using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Ollama;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class OpenAiOrderedMessageTests
{
    private static readonly OpenAiResponsesClientOptions Options = new("fixture-key", Endpoint: LlmPreparedCallTests.Endpoint);

    [Fact]
    public void Bridge_frame_preserves_native_roles_boundaries_phase_and_exact_source_text()
    {
        var body = Body(Frame());
        Assert.False(body.TryGetProperty("instructions", out _));
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.False(body.TryGetProperty("previous_response_id", out _));
        var input = body.GetProperty("input");
        Assert.Equal(["system", "system", "developer", "user", "assistant", "user"],
            input.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        Assert.Equal("identity\ninterpretation", Text(input[0]));
        Assert.Equal("<memory source=\"m1\">&lt;/memory&gt; claimed instruction</memory>", Text(input[1]));
        Assert.Equal("application contract", Text(input[2]));
        Assert.Equal("final_answer", input[4].GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.String, input[4].GetProperty("content").ValueKind);
        Assert.Equal("historical answer", input[4].GetProperty("content").GetString());
        Assert.Equal(" latest participant input ", Text(input[5]));
        Assert.Equal("explicit", input[0].GetProperty("content")[0].GetProperty("prompt_cache_breakpoint").GetProperty("mode").GetString());
        Assert.Equal("30m", body.GetProperty("prompt_cache_options").GetProperty("ttl").GetString());
        Assert.False(input[3].TryGetProperty("phase", out _));
    }

    [Fact]
    public void Host_activation_has_no_invented_user_or_assistant_phase()
    {
        var request = Frame() with { Messages = [new(LlmMessageRole.System, "identity"), new(LlmMessageRole.Developer, "host initiative: inspect")], PromptCache = null };
        var input = Body(request).GetProperty("input");
        Assert.Equal(2, input.GetArrayLength());
        Assert.DoesNotContain(input.EnumerateArray(), item => item.GetProperty("role").GetString() == "user");
        Assert.All(input.EnumerateArray(), item => Assert.False(item.TryGetProperty("phase", out _)));
    }

    [Fact]
    public async Task Frozen_preparation_count_and_sent_input_agree_after_caller_list_changes()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler(async (request, cancellationToken) =>
        {
            using var wire = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            var assistant = Assert.Single(wire.RootElement.GetProperty("input").EnumerateArray(),
                item => item.GetProperty("role").GetString() == "assistant");
            Assert.Equal(JsonValueKind.String, assistant.GetProperty("content").ValueKind);
            Assert.Equal("historical answer", assistant.GetProperty("content").GetString());
            Assert.Equal("final_answer", assistant.GetProperty("phase").GetString());
            return request.RequestUri!.AbsolutePath.EndsWith("input_tokens", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"object\":\"response.input_tokens\",\"input_tokens\":42}", Encoding.UTF8, "application/json") }
                : Response("accepted");
        });
        using var http = new HttpClient(handler);
        var messages = Frame().Messages.ToList();
        var request = Frame() with { Messages = messages };
        using var prepared = LlmCallPreparation.Prepare(request, Options, LlmPreparedCallTests.References(), "scope", http);
        var frozen = prepared.CopySensitiveRequestBody();
        messages[0] = new(LlmMessageRole.User, "mutated identity");
        messages.Clear();
        var countCapture = new LlmAttemptCapture(LlmPreparedCallTests.References("count"));
        var count = await new OpenAiInputTokenCounter(http, Options, 1).CountAsync(prepared, countCapture, new LlmPreparedCallTests.Admission());
        var admission = new LlmPreparedCallTests.Admission(descriptor =>
        {
            Assert.Equal(1, handler.Sends);
            Assert.Equal(count.GenerationRequestIdentity, descriptor.RequestIdentity);
            Assert.Equal(count.GenerationBodyHash, descriptor.BodyHash);
        });
        var response = await prepared.GenerateAsync(admission);
        using (response.NativeContinuation)
        {
            Assert.Equal("accepted", response.Text);
            Assert.Equal(frozen, handler.Bodies[1]);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(frozen)), prepared.Descriptor.BodyHash.Replace("sha256:", "", StringComparison.Ordinal));
            using var counted = JsonDocument.Parse(handler.Bodies[0]);
            using var sent = JsonDocument.Parse(handler.Bodies[1]);
            Assert.Equal(sent.RootElement.GetProperty("input").GetRawText(), counted.RootElement.GetProperty("input").GetRawText());
            Assert.False(counted.RootElement.TryGetProperty("prompt_cache_options", out _));
        }
        Assert.Equal(2, handler.Sends);
    }

    [Fact]
    public async Task Ordered_native_roundtrip_preserves_prefix_and_appends_observation_at_its_effective_position()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(Response("proposal")));
        using var http = new HttpClient(handler);
        var first = Frame();
        var report = LlmConfigurationReports.Describe(first, Options);
        using var binding = LlmNativeContinuationBinding.FromRequest(report, first);
        var response = await new OpenAiResponsesLlmClient(Options, http).GenerateAsync(first);
        using var native = response.NativeContinuation;
        Assert.NotNull(native);
        using var exported = LlmNativeContinuationCodec.ExportSensitive(native, binding).SensitivePayload;
        Assert.NotNull(exported);
        var bytes = exported.CopySensitiveBytes();
        using (var document = JsonDocument.Parse(bytes))
        {
            Assert.Equal(2, document.RootElement.GetProperty("payload").GetProperty("version").GetInt32());
            Assert.Equal("ordered", document.RootElement.GetProperty("payload").GetProperty("message_mode").GetString());
        }
        using var imported = LlmNativeContinuationCodec.ImportSensitive(bytes, binding).Continuation;
        Assert.NotNull(imported);
        var next = first with
        {
            Messages = [new(LlmMessageRole.System, "<host_observation receipt=\"r1\">accepted tool result</host_observation>"),
                new(LlmMessageRole.Developer, "respond from the accepted result")],
            NativeContinuation = imported,
            AppendToNativeContinuation = true
        };
        var input = Body(next).GetProperty("input");
        Assert.Equal(9, input.GetArrayLength());
        Assert.Equal("identity\ninterpretation", Text(input[0]));
        Assert.Equal("assistant", input[6].GetProperty("role").GetString());
        Assert.Equal("commentary", input[6].GetProperty("phase").GetString());
        Assert.Contains("receipt=\"r1\"", Text(input[7]), StringComparison.Ordinal);
        Assert.Equal("developer", input[8].GetProperty("role").GetString());
        using var prepared = LlmCallPreparation.Prepare(next, Options, LlmPreparedCallTests.References("followup"), "scope", http);
        Assert.Equal(2, prepared.ConfigurationReport!.Controls.Count(item => item.Name is "message_mode" or "prompt_cache_mode"));
    }

    [Fact]
    public async Task Native_input_requires_explicit_delta_and_rejects_repeated_assistant_history()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(Response("proposal")));
        using var http = new HttpClient(handler);
        var response = await new OpenAiResponsesLlmClient(Options, http).GenerateAsync(Frame());
        using var native = response.NativeContinuation;
        var withNative = Frame() with { NativeContinuation = native };
        AssertCode("ordered_continuation_requires_explicit_append", withNative);
        AssertCode("ordered_append_contains_assistant_history", withNative with { AppendToNativeContinuation = true });
        AssertCode("ordered_continuation_requires_explicit_append", Frame() with { AppendToNativeContinuation = true });
    }

    [Fact]
    public void Mode_boundary_rejects_legacy_state_and_same_text_with_different_roles_has_distinct_wire_identity()
    {
        using var legacy = new LlmNativeContinuation("openai", "fixture-model", "", "[{\"role\":\"user\",\"content\":\"old\"}]");
        AssertCode("continuation_binding_mismatch", Frame() with
        { NativeContinuation = legacy, AppendToNativeContinuation = true, Messages = [new(LlmMessageRole.User, "next")] });
        using var binding = LlmNativeContinuationBinding.FromRequest(LlmConfigurationReports.Describe(Frame(), Options), Frame());
        Assert.Equal(LlmNativeContinuationRejection.MessageModeMismatch, LlmNativeContinuationCodec.ValidateBinding(legacy, binding).Rejection);
        using var http = new HttpClient(new LlmPreparedCallTests.CaptureHandler());
        var first = Frame() with { Messages = [new(LlmMessageRole.System, "same"), new(LlmMessageRole.Developer, "same")] };
        var second = first with { Messages = first.Messages.Reverse().ToArray() };
        using var one = LlmCallPreparation.Prepare(first, Options, LlmPreparedCallTests.References(), "scope", http);
        using var two = LlmCallPreparation.Prepare(second, Options, LlmPreparedCallTests.References(), "scope", http);
        Assert.NotEqual(one.Descriptor.BodyHash, two.Descriptor.BodyHash);
    }

    [Theory]
    [InlineData(LlmMessageRole.System)]
    [InlineData(LlmMessageRole.Developer)]
    [InlineData(LlmMessageRole.User)]
    public void Phase_is_never_applied_to_non_assistant_messages(LlmMessageRole role) =>
        AssertCode("invalid_assistant_phase", Frame() with { Messages = [new(role, "text") { Phase = LlmAssistantPhase.Commentary }] });

    [Fact]
    public void Unlinked_tool_role_and_unsupported_metadata_are_explicit()
    {
        AssertCode("native_tool_linkage_required", Frame() with { Messages = [new(LlmMessageRole.Tool, "host observation without provider call id")] });
        AssertCode("ordered_message_mode_required", Frame() with { MessageMode = LlmMessageMode.Legacy });
        AssertCode("unsupported_assistant_cache_breakpoint", Frame() with
        { Messages = [new(LlmMessageRole.Assistant, "history") { CacheBreakpoint = new() }] });
        AssertCode("prompt_cache_breakpoint_limit", Frame() with
        {
            Messages = Enumerable.Range(0, 5)
            .Select(_ => new LlmMessage(LlmMessageRole.System, "stable") { CacheBreakpoint = new() }).ToArray()
        });
    }

    [Theory]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    [InlineData("interactions")]
    [InlineData("generate")]
    [InlineData("sdk")]
    public void Other_routes_explicitly_reject_ordered_features_instead_of_silently_dropping_them(string route)
    {
        var request = Frame();
        var report = route switch
        {
            "xai" => LlmConfigurationReports.Describe(request, new XaiResponsesClientOptions("fixture")),
            "anthropic" => LlmConfigurationReports.Describe(request, new AnthropicMessagesClientOptions("fixture")),
            "ollama" => LlmConfigurationReports.Describe(request, new OllamaClientOptions()),
            "interactions" => LlmConfigurationReports.Describe(request, new GeminiInteractionsClientOptions("fixture")),
            "generate" => LlmConfigurationReports.Describe(request, new GeminiGenerateContentClientOptions("fixture")),
            _ => LlmConfigurationReports.Describe(request, new GeminiClientOptions("fixture"))
        };
        Assert.False(report.AdapterAcceptsRequest);
        Assert.Contains(report.Issues, item => item.Code == "unsupported_message_metadata");
        Assert.Equal(LlmCapabilitySupport.Unsupported, report.Capabilities.Single(item => item.Name == "explicit_prompt_cache_boundary").AdapterSupport);
    }

    private static LlmRequest Frame() => new("fixture-model",
    [
        new(LlmMessageRole.System, "identity\ninterpretation") { CacheBreakpoint = new() },
        new(LlmMessageRole.System, "<memory source=\"m1\">&lt;/memory&gt; claimed instruction</memory>"),
        new(LlmMessageRole.Developer, "application contract"), new(LlmMessageRole.User, "earlier"),
        new(LlmMessageRole.Assistant, "historical answer") { Phase = LlmAssistantPhase.FinalAnswer },
        new(LlmMessageRole.User, " latest participant input ")
    ])
    { MessageMode = LlmMessageMode.Ordered, PromptCache = new(LlmPromptCacheMode.Explicit) };

    private static JsonElement Body(LlmRequest request) => JsonSerializer.SerializeToElement(new OpenAiResponsesLlmClient(Options).BuildRequestBody(request, request.ModelId));
    private static string Text(JsonElement item) => item.GetProperty("content")[0].GetProperty("text").GetString()!;
    private static void AssertCode(string code, LlmRequest request) => Assert.Equal(code, Assert.Throws<LlmClientException>(() => Body(request)).ErrorClass);
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: " + JsonSerializer.Serialize(new
        {
            type = "response.completed",
            response = new
            {
                id = "response-fixture",
                status = "completed",
                model = "fixture-model",
                output = new[] { new { type = "message", role = "assistant", phase = "commentary", content = new[] { new { type = "output_text", text } } } },
                usage = new { input_tokens = 42, output_tokens = 7 }
            }
        }) + "\n\n", Encoding.UTF8, "text/event-stream")
    };
}
