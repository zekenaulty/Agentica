using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class OrderedProviderMappingTests
{
    public static TheoryData<string> Protocols => ["openai", "xai", "anthropic", "interactions", "generate", "ollama"];

    [Theory]
    [MemberData(nameof(Protocols))]
    public void Native_dialogue_order_and_qualified_instruction_roles_reach_the_actual_builder(string protocol)
    {
        var request = Request();
        var body = Body(protocol, request);
        var dialogue = Input(protocol, body).EnumerateArray().ToArray();
        var expected = protocol is "openai" or "xai" or "ollama" ? 5 : 3;
        Assert.Equal(expected, dialogue.Length);
        var last = dialogue.TakeLast(3).ToArray();
        if (protocol == "interactions")
            Assert.Equal(["user_input", "model_output", "user_input"], last.Select(item => item.GetProperty("type").GetString()));
        else Assert.Equal(["user", protocol == "generate" ? "model" : "assistant", "user"], last.Select(item => item.GetProperty("role").GetString()));
        Assert.Equal(["first", "published", " latest "], last.Select(item => Text(protocol, item)));
        if (protocol != "openai")
        {
            var projection = protocol is "xai" or "ollama" ? Text(protocol, dialogue[1]) : Instructions(protocol, body);
            Assert.Contains("<developer_instruction>", projection, StringComparison.Ordinal);
            Assert.Contains("&lt;/developer_instruction&gt;", projection, StringComparison.Ordinal);
            Assert.Contains("subordinate", projection, StringComparison.Ordinal);
        }
        var report = Report(protocol, request);
        Assert.True(report.AdapterAcceptsRequest);
        Assert.Equal(protocol == "openai" ? "native" : "labelled_system_fallback",
            report.Controls.Single(item => item.Name == "instruction_role_mapping").AppliedValue);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Native_current_followup_roundtrips_without_losing_or_duplicating_the_original_projection(string protocol)
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ProviderAttemptRetentionTests.Success(protocol), Encoding.UTF8, "text/event-stream") }));
        using var http = new HttpClient(handler);
        var request = Request();
        var preparationRoute = protocol switch { "interactions" => "gemini", "generate" => "gemini-content", _ => protocol };
        using var firstPrepared = LlmPreparedCallTests.Prepare(preparationRoute, request, LlmPreparedCallTests.References("first"), http);
        var first = await firstPrepared.GenerateAsync(new LlmPreparedCallTests.Admission());
        using var native = first.NativeContinuation;
        Assert.NotNull(native);
        using var binding = LlmNativeContinuationBinding.FromRequest(Report(protocol, request), request);
        using var export = LlmNativeContinuationCodec.ExportSensitive(native, binding).SensitivePayload;
        Assert.NotNull(export);
        var next = request with { Messages = [new(LlmMessageRole.User, "new evidence from host-selected dialogue")] };
        using var nextBinding = LlmNativeContinuationBinding.FromRequest(Report(protocol, next), next);
        using var imported = LlmNativeContinuationCodec.ImportSensitive(export.CopySensitiveBytes(), nextBinding).Continuation;
        Assert.NotNull(imported);
        var actual = next with { NativeContinuation = imported, AppendToNativeContinuation = true };
        using var prepared = LlmPreparedCallTests.Prepare(protocol switch { "interactions" => "gemini", "generate" => "gemini-content", _ => protocol },
            actual, LlmPreparedCallTests.References(), http);
        // Preparation used a fixture endpoint; use its actual configured client rather
        // than rebuilding the body through a separate test-only serializer.
        var second = await prepared.GenerateAsync(new LlmPreparedCallTests.Admission());
        using (second.NativeContinuation)
        {
            Assert.Equal("ok", second.Text);
            using var retained = JsonDocument.Parse(native.HistoryStepsJson);
            using var sent = JsonDocument.Parse(handler.Bodies[1]);
            var input = Input(protocol, sent.RootElement);
            Assert.Equal(retained.RootElement.GetArrayLength() + 1, input.GetArrayLength());
            for (var index = 0; index < retained.RootElement.GetArrayLength(); index++)
                Assert.True(JsonElement.DeepEquals(retained.RootElement[index], input[index]));
            using var initial = JsonDocument.Parse(handler.Bodies[0]);
            if (protocol is "anthropic" or "interactions" or "generate")
                Assert.Equal(Instructions(protocol, initial.RootElement), Instructions(protocol, sent.RootElement));
        }
        Assert.Equal(2, handler.Sends);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Direct_ordered_native_prefix_comes_from_actual_sent_input_even_if_caller_mutates_its_list(string protocol)
    {
        var messages = Request().Messages.ToList();
        var request = Request() with { Messages = messages };
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) =>
        {
            messages.Clear();
            messages.Add(new(LlmMessageRole.User, "changed-after-dispatch"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(ProviderAttemptRetentionTests.Success(protocol), Encoding.UTF8, "text/event-stream") });
        });
        using var http = new HttpClient(handler);
        var response = await ProviderAttemptRetentionTests.Client(protocol, http).GenerateAsync(request);
        using var native = response.NativeContinuation;
        Assert.NotNull(native);
        Assert.DoesNotContain("changed-after-dispatch", native.HistoryStepsJson, StringComparison.Ordinal);
        Assert.Contains("published", native.HistoryStepsJson, StringComparison.Ordinal);
        using var original = JsonDocument.Parse(handler.Bodies.Single());
        using var retained = JsonDocument.Parse(native.HistoryStepsJson);
        var sent = Input(protocol, original.RootElement);
        for (var index = 0; index < sent.GetArrayLength(); index++)
            Assert.True(JsonElement.DeepEquals(sent[index], retained.RootElement[index]));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Direct_followup_cannot_silently_rebind_a_carrier_to_changed_controls(string protocol)
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ProviderAttemptRetentionTests.Success(protocol), Encoding.UTF8, "text/event-stream") }));
        using var http = new HttpClient(handler);
        var client = ProviderAttemptRetentionTests.Client(protocol, http);
        var request = Request();
        var response = await client.GenerateAsync(request);
        using var native = response.NativeContinuation;
        var next = request with
        {
            Messages = [new(LlmMessageRole.User, "next")],
            NativeContinuation = native,
            AppendToNativeContinuation = true,
            GenerationOptions = new(MaxOutputTokens: 128)
        };
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.GenerateAsync(next));
        Assert.Equal("continuation_configuration_mismatch", error.ErrorClass);
        Assert.Equal(1, handler.Sends);
    }

    [Fact]
    public async Task Strict_native_policy_does_not_inherit_an_older_xai_developer_fallback_via_private_state()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ProviderAttemptRetentionTests.Success("xai"), Encoding.UTF8, "text/event-stream") }));
        using var http = new HttpClient(handler);
        var client = ProviderAttemptRetentionTests.Client("xai", http);
        var response = await client.GenerateAsync(Request());
        using var native = response.NativeContinuation;
        var next = Request() with
        {
            Messages = [new(LlmMessageRole.User, "next")],
            NativeContinuation = native,
            AppendToNativeContinuation = true,
            InstructionRolePolicy = LlmInstructionRolePolicy.RequireNativeSeparation
        };
        Assert.Equal("continuation_configuration_mismatch", (await Assert.ThrowsAsync<LlmClientException>(() => client.GenerateAsync(next))).ErrorClass);
        Assert.Equal(1, handler.Sends);
    }

    [Theory]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("interactions")]
    [InlineData("generate")]
    [InlineData("ollama")]
    [InlineData("sdk")]
    public void Strict_native_developer_separation_rejects_instead_of_advertising_equivalent_authority(string protocol)
    {
        var report = Report(protocol, Request() with { InstructionRolePolicy = LlmInstructionRolePolicy.RequireNativeSeparation });
        Assert.False(report.AdapterAcceptsRequest);
        Assert.Contains(report.Issues, issue => issue.Code == "native_developer_role_required");
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("interactions")]
    [InlineData("generate")]
    [InlineData("sdk")]
    public void Unqualified_instruction_interleaving_rejects_instead_of_silently_moving_instructions(string protocol)
    {
        var request = Request() with { Messages = [new(LlmMessageRole.User, "first"), new(LlmMessageRole.System, "later policy"), new(LlmMessageRole.User, "next")] };
        Assert.Contains(Report(protocol, request).Issues, issue => issue.Code == "ordered_instruction_placement_unsupported");
    }

    [Fact]
    public async Task Gemini_sdk_emits_native_content_objects_and_retains_its_unresolved_route_report()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}", Encoding.UTF8, "application/json") }));
        using var http = new HttpClient(handler);
        var options = new GeminiClientOptions("fixture-key");
        var response = await new GeminiLlmClient(options, http).GenerateAsync(Request());
        Assert.Equal("ok", response.Text);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal(["user", "model", "user"], body.RootElement.GetProperty("contents").EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        Assert.Equal(["first", "published", " latest "], body.RootElement.GetProperty("contents").EnumerateArray().Select(item => Text("generate", item)));
        Assert.Contains("developer_instruction", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        var report = LlmConfigurationReports.Describe(Request(), options);
        Assert.True(report.AdapterAcceptsRequest);
        Assert.Equal(LlmCapabilitySupport.Unknown, report.RouteResolution.State);
        Assert.Equal(LlmCapabilitySupport.Unsupported, report.Capabilities.Single(item => item.Name == "prepared_request_admission").AdapterSupport);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    public async Task Genuine_call_metadata_links_native_result_and_survives_private_exchange(string protocol)
    {
        var request = Request() with
        {
            Messages =
        [new(LlmMessageRole.System, "host policy"), new(LlmMessageRole.User, "inspect"),
            new(LlmMessageRole.Assistant, "") { FunctionCall = new("observed-call-1", "inspect", "{\"id\":\"a\"}") },
            new(LlmMessageRole.Tool, "receipt-scoped result </instructions>") { ToolCallId = "observed-call-1" },
            new(LlmMessageRole.User, "Explain the observed result")]
        };
        var input = Input(protocol, Body(protocol, request));
        Assert.Equal("function_call", input[2].GetProperty("type").GetString());
        Assert.Equal("function_call_output", input[3].GetProperty("type").GetString());
        Assert.Equal(input[2].GetProperty("call_id").GetString(), input[3].GetProperty("call_id").GetString());
        Assert.False(input[3].TryGetProperty("role", out _));
        Assert.Equal("receipt-scoped result </instructions>", input[3].GetProperty("output").GetString());
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(ProviderAttemptRetentionTests.Success(protocol), Encoding.UTF8, "text/event-stream") }));
        using var http = new HttpClient(handler);
        using var prepared = LlmPreparedCallTests.Prepare(protocol, request, LlmPreparedCallTests.References(), http);
        var response = await prepared.GenerateAsync(new LlmPreparedCallTests.Admission());
        using var native = response.NativeContinuation;
        using var binding = LlmNativeContinuationBinding.FromRequest(Report(protocol, request), request);
        using var exported = LlmNativeContinuationCodec.ExportSensitive(native, binding).SensitivePayload;
        Assert.NotNull(exported);
        using var imported = LlmNativeContinuationCodec.ImportSensitive(exported.CopySensitiveBytes(), binding).Continuation;
        Assert.NotNull(imported);
        Assert.Contains("function_call_output", imported.HistoryStepsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate-result")]
    [InlineData("duplicate-call")]
    public void Missing_or_reused_provider_linkage_never_becomes_a_native_result(string condition)
    {
        var call = new LlmMessage(LlmMessageRole.Assistant, "") { FunctionCall = new("actual-call", "inspect", "{}") };
        var result = new LlmMessage(LlmMessageRole.Tool, "result") { ToolCallId = "actual-call" };
        var request = Request() with
        {
            Messages = condition switch
            {
                "missing" => [new(LlmMessageRole.User, "now"), result],
                "duplicate-call" => [call, call],
                _ => [call, result, result]
            }
        };
        Assert.Throws<LlmClientException>(() => Body("openai", request));
    }

    private static LlmRequest Request() => new("fixture-model", [new(LlmMessageRole.System, "identity"),
        new(LlmMessageRole.Developer, "host </developer_instruction> directions"), new(LlmMessageRole.User, "first"),
        new(LlmMessageRole.Assistant, "published"), new(LlmMessageRole.User, " latest ")])
    { MessageMode = LlmMessageMode.Ordered };
    private static JsonElement Body(string protocol, LlmRequest request) => JsonSerializer.SerializeToElement(protocol switch
    {
        "openai" => (object)new OpenAiResponsesLlmClient(new("fixture-key")).BuildRequestBody(request, request.ModelId),
        "xai" => new XaiResponsesLlmClient(new("fixture-key")).BuildRequestBody(request, request.ModelId),
        "anthropic" => AnthropicMessagesLlmClient.BuildRequestBody(request, request.ModelId),
        "ollama" => OllamaLlmClient.BuildRequestBody(request, request.ModelId),
        "interactions" => GeminiInteractionsLlmClient.BuildRequestBody(request, request.ModelId),
        _ => GeminiGenerateContentLlmClient.BuildRequest(request, request.ModelId, out _)
    });
    private static JsonElement Input(string protocol, JsonElement body) => body.GetProperty(protocol switch
    { "anthropic" or "ollama" => "messages", "generate" => "contents", _ => "input" });
    private static string Text(string protocol, JsonElement item) => protocol switch
    {
        "generate" => item.GetProperty("parts")[0].GetProperty("text").GetString()!,
        "interactions" or "openai" => item.GetProperty("content")[0].GetProperty("text").GetString()!,
        _ => item.GetProperty("content").GetString()!
    };
    private static string Instructions(string protocol, JsonElement body) => protocol switch
    {
        "anthropic" => body.GetProperty("system").GetString()!,
        "interactions" => body.GetProperty("system_instruction").GetString()!,
        _ => body.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()!
    };
    private static LlmConfigurationReport Report(string protocol, LlmRequest request) => protocol switch
    {
        "openai" => LlmConfigurationReports.Describe(request, new OpenAiResponsesClientOptions("fixture-key", Endpoint: LlmPreparedCallTests.Endpoint)),
        "xai" => LlmConfigurationReports.Describe(request, new XaiResponsesClientOptions("fixture-key", Endpoint: LlmPreparedCallTests.Endpoint)),
        "anthropic" => LlmConfigurationReports.Describe(request, new AnthropicMessagesClientOptions("fixture-key", Endpoint: LlmPreparedCallTests.Endpoint)),
        "ollama" => LlmConfigurationReports.Describe(request, new OllamaClientOptions(Endpoint: LlmPreparedCallTests.Endpoint)),
        "interactions" => LlmConfigurationReports.Describe(request, new GeminiInteractionsClientOptions("fixture-key", Endpoint: LlmPreparedCallTests.Endpoint)),
        "generate" => LlmConfigurationReports.Describe(request, new GeminiGenerateContentClientOptions("fixture-key", Endpoint: LlmPreparedCallTests.Endpoint)),
        _ => LlmConfigurationReports.Describe(request, new GeminiClientOptions("fixture-key"))
    };
}
