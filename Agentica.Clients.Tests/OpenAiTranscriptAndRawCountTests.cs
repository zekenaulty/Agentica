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

public sealed class OpenAiTranscriptAndRawCountTests
{
    [Fact]
    public async Task Transcript_mode_preserves_author_order_and_whitespace_instructions_without_native_state()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(TextResponse()));
        using var http = new HttpClient(handler);
        var request = new LlmRequest("fixture-model", [new(LlmMessageRole.System, "   "),
            new(LlmMessageRole.User, "first"), new(LlmMessageRole.Assistant, "assistant **text**"),
            new(LlmMessageRole.User, " latest ")])
        { OpenAiHistoryMode = OpenAiHistoryMode.TranscriptOnly };
        var response = await new OpenAiResponsesLlmClient(new(ApiKey: "fixture-key"), http).GenerateAsync(request);
        using var body = JsonDocument.Parse(handler.Bodies.Single());
        Assert.Equal("   ", body.RootElement.GetProperty("instructions").GetString());
        var input = body.RootElement.GetProperty("input");
        Assert.Equal(["user", "assistant", "user"], input.EnumerateArray().Select(item => item.GetProperty("role").GetString()));
        Assert.Equal(["first", "assistant **text**", " latest "], input.EnumerateArray().Select(item => item.GetProperty("content").GetString()));
        Assert.False(body.RootElement.TryGetProperty("include", out _));
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Null(response.NativeContinuation);
        Assert.Equal("False", response.Metadata!["openai.continuation.available"]);
    }

    [Fact]
    public void Transcript_mode_is_explicit_rejects_tools_and_native_carriers_and_preserves_default_guards()
    {
        var client = new OpenAiResponsesLlmClient(new(ApiKey: "fixture-key"));
        var request = Request() with { Messages = [new(LlmMessageRole.Assistant, "history"), new(LlmMessageRole.User, "now")] };
        Assert.Equal("native_history_required", Assert.Throws<LlmClientException>(() => client.BuildRequestBody(request, "fixture-model")).ErrorClass);
        using var native = new LlmNativeContinuation("openai", "fixture-model", "", "[{\"role\":\"user\",\"content\":\"earlier\"}]");
        Assert.Equal("transcript_continuation_conflict", Assert.Throws<LlmClientException>(() => client.BuildRequestBody(
            request with { OpenAiHistoryMode = OpenAiHistoryMode.TranscriptOnly, NativeContinuation = native }, "fixture-model")).ErrorClass);
        Assert.Equal("native_history_required", Assert.Throws<LlmClientException>(() => client.BuildRequestBody(
            request with { OpenAiHistoryMode = OpenAiHistoryMode.TranscriptOnly, Messages = [new(LlmMessageRole.Tool, "tool"), new(LlmMessageRole.User, "now")] }, "fixture-model")).ErrorClass);
    }

    [Fact]
    public void Interpretation_policy_changes_configuration_identity_even_when_wire_body_is_identical()
    {
        var options = new OpenAiResponsesClientOptions(ApiKey: "fixture-key") { IncludeEncryptedReasoning = false };
        var native = LlmConfigurationReports.Describe(Request(), options);
        var transcriptRequest = Request() with { OpenAiHistoryMode = OpenAiHistoryMode.TranscriptOnly };
        var transcript = LlmConfigurationReports.Describe(transcriptRequest, options);
        Assert.NotEqual(native.ConfigurationIdentity, transcript.ConfigurationIdentity);
        Assert.Equal("TranscriptOnly", transcript.Controls.Single(item => item.Name == "history_mode").AppliedValue);
        var client = new OpenAiResponsesLlmClient(options);
        Assert.Equal(JsonSerializer.Serialize(client.BuildRequestBody(Request(), "fixture-model")),
            JsonSerializer.Serialize(client.BuildRequestBody(transcriptRequest, "fixture-model")));
        var scalar = LlmConfigurationReports.Describe(Request() with { WebSearch = new(citationIndexUnit: LlmCitationIndexUnit.UnicodeScalars) }, options);
        var utf16 = LlmConfigurationReports.Describe(Request() with { WebSearch = new(citationIndexUnit: LlmCitationIndexUnit.Utf16CodeUnits) }, options);
        Assert.NotEqual(scalar.ConfigurationIdentity, utf16.ConfigurationIdentity);
    }

    [Theory]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    [InlineData("interactions")]
    [InlineData("generate")]
    [InlineData("sdk")]
    public void Non_OpenAI_actual_builders_and_reports_explicitly_reject_transcript_mode(string provider)
    {
        var request = Request() with { OpenAiHistoryMode = OpenAiHistoryMode.TranscriptOnly };
        var error = Assert.Throws<LlmClientException>(() => Build(provider, request));
        Assert.Equal("unsupported_history_mode", error.ErrorClass);
        var report = Report(provider, request);
        Assert.False(report.AdapterAcceptsRequest);
        Assert.Contains(report.Issues, item => item.Code == "unsupported_history_mode");
    }

    [Fact]
    public async Task Raw_count_binds_full_generation_bytes_and_host_context_while_sending_only_count_fields()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(CountResponse()));
        using var http = new HttpClient(handler);
        var options = new OpenAiResponsesClientOptions("fixture-key", Endpoint: LlmPreparedCallTests.Endpoint);
        var counter = new OpenAiInputTokenCounter(http, options, 4);
        var first = Generation(128);
        var second = Generation(256);
        var generation = new LlmAttemptReferences("generation-1", "operation", "generate", "route", "bundle");
        var capture1 = CountCapture("count-1");
        var admission1 = new LlmPreparedCallTests.Admission(descriptor =>
        {
            Assert.Equal(0, handler.Sends);
            Assert.Equal("scope", descriptor.HostScopeId);
            Assert.Same(capture1.References, descriptor.References);
        });
        var one = await counter.CountAsync(first, generation, "scope", capture1, admission1);
        var two = await counter.CountAsync(second, generation, "scope", CountCapture("count-2"), new LlmPreparedCallTests.Admission());
        Assert.Equal(15, one.Tokens);
        Assert.Equal(LlmPreparedIdentity.Hash(first), one.GenerationBodyHash);
        Assert.Equal(LlmPreparedIdentity.Hash(second), two.GenerationBodyHash);
        Assert.NotEqual(one.GenerationRequestIdentity, two.GenerationRequestIdentity);
        Assert.NotEqual(one.CountRequest.RequestIdentity, two.CountRequest.RequestIdentity);
        Assert.Equal(one.CountRequest.BodyHash, two.CountRequest.BodyHash);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.All(handler.Uris, uri => Assert.EndsWith("/responses/input_tokens", uri.AbsoluteUri, StringComparison.Ordinal));
        Assert.Same(capture1.Latest, one.AttemptEvidence);
        Assert.Null(one.AttemptEvidence.Usage);
        Assert.Equal(2, handler.Sends);
        Assert.DoesNotContain("private-user-text", JsonSerializer.Serialize(one));
    }

    [Theory]
    [InlineData("{malformed}")]
    [InlineData("{\"model\":\"fixture\",\"input\":[],\"response_id\":\"state\"}")]
    [InlineData("{\"model\":\"fixture\",\"input\":[],\"previous_response_id\":null,\"previous_response_id\":\"state\"}")]
    [InlineData("{\"model\":{\"dynamic\":true},\"input\":[]}")]
    [InlineData("{\"model\":\"fixture\",\"input\":[{\"type\":\"item_reference\",\"id\":\"stored-item\"}]}")]
    [InlineData("{\"model\":\"fixture\",\"input\":[{\"id\":\"stored-item\"}]}")]
    [InlineData("{\"model\":\"fixture\",\"input\":[{\"type\":null,\"id\":\"stored-item\"}]}")]
    public async Task Raw_validation_rejects_before_admission_or_transport_and_preserves_stable_error(string body)
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler();
        using var http = new HttpClient(handler);
        var counter = new OpenAiInputTokenCounter(http, new("fixture-key"), 1);
        var admission = new LlmPreparedCallTests.Admission();
        var bytes = Encoding.UTF8.GetBytes(body);
        Assert.Equal("input_count_invalid_request", Assert.Throws<LlmClientException>(() => OpenAiInputTokenCounter.ValidateGenerationBody(bytes)).ErrorClass);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => counter.CountAsync(bytes,
            new("generation"), "scope", CountCapture(), admission));
        Assert.Equal("input_count_invalid_request", error.ErrorClass);
        Assert.Equal(0, admission.Calls);
        Assert.Equal(0, handler.Sends);
        Assert.Equal(0, counter.Requests);
    }

    [Fact]
    public async Task Raw_count_cancellation_survives_on_capture_and_same_attempt_identity_is_rejected()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(CountResponse()));
        using var http = new HttpClient(handler);
        var counter = new OpenAiInputTokenCounter(http, new("fixture-key"), 1);
        var admission = new LlmPreparedCallTests.Admission();
        var capture = CountCapture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => counter.CountAsync(Generation(128),
            new("generation"), "scope", capture, admission, cancellation.Token));
        Assert.Equal(LlmAttemptOutcome.Cancelled, capture.Latest?.Outcome);
        Assert.Equal(LlmDispatchCertainty.NotDispatched, capture.Latest?.DispatchCertainty);
        var reused = new LlmAttemptCapture(new("generation"));
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => counter.CountAsync(Generation(128),
            new("generation"), "scope", reused, admission));
        Assert.Equal("input_count_binding_mismatch", failure.ErrorClass);
        Assert.Equal(0, handler.Sends);
        Assert.Equal(0, admission.Calls);
        Assert.Equal(0, counter.Requests);
    }

    private static LlmRequest Request() => new("fixture-model", [new(LlmMessageRole.User, "fixture")]);
    private static byte[] Generation(int output) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        model = "fixture-model",
        input = new[] { new { role = "user", content = "private-user-text" } },
        stream = true,
        store = false,
        max_output_tokens = output
    });
    private static LlmAttemptCapture CountCapture(string id = "count") => new(new(id, "operation", "count", "route", "bundle"));
    private static HttpResponseMessage CountResponse() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"object\":\"response.input_tokens\",\"input_tokens\":15}", Encoding.UTF8, "application/json") };
    private static HttpResponseMessage TextResponse() => new(HttpStatusCode.OK)
    { Content = new StringContent("data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"encrypted_content\":\"opaque\"},{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"answer\"}]}]}}\n\n", Encoding.UTF8, "text/event-stream") };

    private static object Build(string provider, LlmRequest request) => provider switch
    {
        "xai" => new XaiResponsesLlmClient(new(ApiKey: "fixture-key")).BuildRequestBody(request, "fixture-model"),
        "anthropic" => AnthropicMessagesLlmClient.BuildRequestBody(request, "fixture-model"),
        "ollama" => OllamaLlmClient.BuildRequestBody(request, "fixture-model"),
        "interactions" => GeminiInteractionsLlmClient.BuildRequestBody(request, "fixture-model"),
        "generate" => GeminiGenerateContentLlmClient.BuildRequest(request, "fixture-model", out _),
        "sdk" => GeminiLlmClient.CreateConfig(request),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
    private static LlmConfigurationReport Report(string provider, LlmRequest request) => provider switch
    {
        "xai" => LlmConfigurationReports.Describe(request, new XaiResponsesClientOptions()),
        "anthropic" => LlmConfigurationReports.Describe(request, new AnthropicMessagesClientOptions()),
        "ollama" => LlmConfigurationReports.Describe(request, new OllamaClientOptions()),
        "interactions" => LlmConfigurationReports.Describe(request, new GeminiInteractionsClientOptions()),
        "generate" => LlmConfigurationReports.Describe(request, new GeminiGenerateContentClientOptions()),
        "sdk" => LlmConfigurationReports.Describe(request, new GeminiClientOptions()),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}
