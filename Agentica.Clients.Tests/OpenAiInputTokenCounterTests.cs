using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;

namespace Agentica.Tests;

public sealed class OpenAiInputTokenCounterTests
{
    [Fact]
    public async Task Native_count_is_separate_admitted_work_bound_to_the_complete_prepared_input()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(CountResponse(321)));
        using var http = new HttpClient(handler);
        var request = LlmPreparedCallTests.Request() with
        {
            Messages = [new(LlmMessageRole.System, "private policy"), new(LlmMessageRole.User, "private input")],
            GenerationOptions = new(MaxOutputTokens: 256,
                Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High, includeThoughts: true)),
            StructuredOutput = new(JsonSchema: """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"],"additionalProperties":false}""")
        };
        using var prepared = LlmPreparedCallTests.Prepare("openai", request, LlmPreparedCallTests.References(), http);
        var counter = Counter(http);
        var capture = Capture();
        var admission = new LlmPreparedCallTests.Admission(descriptor =>
        {
            Assert.Equal(0, handler.Sends);
            Assert.Equal(LlmProviderWorkKind.InputCount, descriptor.WorkKind);
            Assert.Same(capture.References, descriptor.References);
        });

        var measurement = await counter.CountAsync(prepared, capture, admission);

        Assert.Equal(321, measurement.Tokens);
        Assert.Equal("openai.responses.input_tokens", measurement.Method);
        Assert.Equal(prepared.Descriptor.RequestIdentity, measurement.GenerationRequestIdentity);
        Assert.Equal(prepared.Descriptor.BodyHash, measurement.GenerationBodyHash);
        Assert.NotEqual(prepared.Descriptor.RequestIdentity, measurement.CountRequest.RequestIdentity);
        Assert.NotEqual(prepared.Descriptor.BodyHash, measurement.CountRequest.BodyHash);
        Assert.Equal("scope-1", measurement.CountRequest.HostScopeId);
        Assert.Equal("responses_input_tokens", measurement.CountRequest.ApiMode);
        Assert.Equal(1, admission.Calls);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(1, counter.Requests);
        Assert.EndsWith("/responses/input_tokens", handler.Uris.Single().AbsoluteUri, StringComparison.Ordinal);
        Assert.Same(capture.Latest, measurement.AttemptEvidence);
        Assert.Equal(LlmAttemptOutcome.Succeeded, capture.Latest!.Outcome);
        Assert.Equal(LlmDispatchCertainty.ResponseObserved, capture.Latest.DispatchCertainty);
        Assert.Null(capture.Latest.Usage); // measured input is not billed usage of the counting operation
        Assert.Null(prepared.Capture.Latest); // no inference was sent or consumed
        using var generation = JsonDocument.Parse(prepared.CopySensitiveRequestBody());
        using var counted = JsonDocument.Parse(handler.Bodies.Single());
        foreach (var field in new[] { "model", "input", "instructions", "reasoning", "text" })
            Assert.Equal(generation.RootElement.GetProperty(field).GetRawText(), counted.RootElement.GetProperty(field).GetRawText());
        foreach (var generationOnly in new[] { "stream", "store", "include", "max_output_tokens" })
            Assert.False(counted.RootElement.TryGetProperty(generationOnly, out _));
    }

    [Fact]
    public void Count_serializer_preserves_all_input_fields_including_tools_and_native_items()
    {
        var bytes = Encoding.UTF8.GetBytes("""
            {"model":"fixture-model","input":[{"type":"reasoning","encrypted_content":"private-cipher"},{"role":"user","content":"question"}],
             "instructions":"policy","reasoning":{"effort":"high","summary":"auto"},
             "text":{"format":{"type":"json_schema","name":"answer","strict":true,"schema":{"type":"object"}}},
             "tools":[{"type":"web_search","filters":{"allowed_domains":["example.com"]}}],
             "tool_choice":"auto","parallel_tool_calls":false,"truncation":"disabled",
             "max_output_tokens":8000,"store":false,"stream":true,"include":["reasoning.encrypted_content"],
             "metadata":{"must_not_count":"secret"}}
            """);

        var body = OpenAiInputTokenCounter.BuildCountRequest(bytes);

        using var original = JsonDocument.Parse(bytes);
        using var count = JsonDocument.Parse(body);
        Assert.Equal(9, count.RootElement.EnumerateObject().Count());
        foreach (var field in new[] { "model", "input", "instructions", "reasoning", "text", "tools", "tool_choice", "parallel_tool_calls", "truncation" })
            Assert.Equal(original.RootElement.GetProperty(field).GetRawText(), count.RootElement.GetProperty(field).GetRawText());
        Assert.False(count.RootElement.TryGetProperty("metadata", out _));
    }

    [Theory]
    [InlineData("{\"model\":\"m\",\"input\":[],\"previous_response_id\":\"r\"}")]
    [InlineData("{\"model\":\"m\",\"input\":[],\"conversation\":\"c\"}")]
    [InlineData("{\"model\":\"m\",\"input\":{}}")]
    [InlineData("{\"model\":\"\",\"input\":[]}")]
    public void Count_serializer_rejects_stateful_or_incomplete_inputs(string json)
    {
        var error = Assert.Throws<LlmClientException>(() => OpenAiInputTokenCounter.BuildCountRequest(Encoding.UTF8.GetBytes(json)));
        Assert.Equal("input_count_invalid_request", error.ErrorClass);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("malformed")]
    [InlineData("wrong-object")]
    [InlineData("negative")]
    [InlineData("oversized")]
    public async Task Unavailable_count_never_uses_character_heuristics_or_sends_inference(string failure)
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(failure switch
        {
            "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") },
            "malformed" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") },
            "wrong-object" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"object\":\"response\",\"input_tokens\":7}") },
            "negative" => CountResponse(-1),
            "oversized" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 65_537)) },
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        }));
        using var http = new HttpClient(handler);
        using var prepared = LlmPreparedCallTests.Prepare("openai", LlmPreparedCallTests.Request(), LlmPreparedCallTests.References(), http);
        var capture = Capture();

        var error = await Assert.ThrowsAsync<LlmClientException>(() => Counter(http).CountAsync(prepared, capture, new LlmPreparedCallTests.Admission()));

        Assert.Equal("input_count_unavailable", error.ErrorClass);
        Assert.Equal(1, handler.Sends);
        Assert.All(handler.Uris, uri => Assert.EndsWith("/input_tokens", uri.AbsoluteUri, StringComparison.Ordinal));
        Assert.Null(prepared.Capture.Latest);
        Assert.Equal(LlmAttemptOutcome.Failed, capture.Latest!.Outcome);
        Assert.Null(capture.Latest.Usage);
        Assert.Same(capture.Latest, error.AttemptEvidence);
    }

    [Fact]
    public async Task Host_denial_does_not_spend_local_capacity_and_a_new_capture_can_retry_admission()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(CountResponse(12)));
        using var http = new HttpClient(handler);
        using var prepared = LlmPreparedCallTests.Prepare("openai", LlmPreparedCallTests.Request(), LlmPreparedCallTests.References(), http);
        var counter = Counter(http, maximumRequests: 1);
        var rejected = Capture();
        await Assert.ThrowsAsync<LlmClientException>(() => counter.CountAsync(prepared, rejected,
            new LlmPreparedCallTests.Admission(_ => throw new InvalidOperationException("denied"))));
        Assert.Equal(0, counter.Requests);
        Assert.Equal(0, handler.Sends);
        Assert.Equal(LlmAttemptOutcome.Rejected, rejected.Latest!.Outcome);

        var result = await counter.CountAsync(prepared, Capture("count-2"), new LlmPreparedCallTests.Admission());

        Assert.Equal(12, result.Tokens);
        Assert.Equal(1, handler.Sends);
        var limited = Capture("count-3");
        var error = await Assert.ThrowsAsync<LlmClientException>(() => counter.CountAsync(prepared, limited, new LlmPreparedCallTests.Admission()));
        Assert.Equal("input_count_limit", error.ErrorClass);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(1, counter.Requests);
        Assert.Equal(LlmDispatchCertainty.NotDispatched, limited.Latest!.DispatchCertainty);
    }

    [Fact]
    public async Task Reusing_a_count_capture_cannot_send_again_or_overwrite_its_owner_evidence()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(CountResponse(12)));
        using var http = new HttpClient(handler);
        using var prepared = LlmPreparedCallTests.Prepare("openai", LlmPreparedCallTests.Request(), LlmPreparedCallTests.References(), http);
        var counter = Counter(http);
        var capture = Capture();
        var result = await counter.CountAsync(prepared, capture, new LlmPreparedCallTests.Admission());

        await Assert.ThrowsAsync<InvalidOperationException>(() => counter.CountAsync(prepared, capture, new LlmPreparedCallTests.Admission()));

        Assert.Same(result.AttemptEvidence, capture.Latest);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(1, counter.Requests);
    }

    [Fact]
    public async Task Counter_endpoint_or_attempt_identity_mismatch_rejects_before_admission()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler();
        using var http = new HttpClient(handler);
        using var prepared = LlmPreparedCallTests.Prepare("openai", LlmPreparedCallTests.Request(), LlmPreparedCallTests.References(), http);
        var wrongRoute = new OpenAiInputTokenCounter(http,
            new OpenAiResponsesClientOptions("fixture-key", Endpoint: new Uri("https://fixture.invalid/other")), 1);
        var admission = new LlmPreparedCallTests.Admission();
        await Assert.ThrowsAsync<LlmClientException>(() => wrongRoute.CountAsync(prepared, Capture(), admission));
        await Assert.ThrowsAsync<LlmClientException>(() => Counter(http).CountAsync(prepared,
            new LlmAttemptCapture(prepared.Descriptor.References), admission));
        Assert.Equal(0, admission.Calls);
        Assert.Equal(0, handler.Sends);
    }

    [Fact]
    public async Task Count_identity_includes_parent_configuration_even_when_counted_input_bytes_are_equal()
    {
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) => Task.FromResult(CountResponse(12)));
        using var http = new HttpClient(handler);
        using var first = LlmPreparedCallTests.Prepare("openai", LlmPreparedCallTests.Request() with
        { GenerationOptions = new(MaxOutputTokens: 128) }, LlmPreparedCallTests.References(), http);
        using var second = LlmPreparedCallTests.Prepare("openai", LlmPreparedCallTests.Request() with
        { GenerationOptions = new(MaxOutputTokens: 256) }, LlmPreparedCallTests.References("attempt-2"), http);
        var counter = Counter(http);

        var firstCount = await counter.CountAsync(first, Capture(), new LlmPreparedCallTests.Admission());
        var secondCount = await counter.CountAsync(second, Capture("count-2"), new LlmPreparedCallTests.Admission());

        Assert.Equal(firstCount.CountRequest.BodyHash, secondCount.CountRequest.BodyHash);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.NotEqual(firstCount.GenerationRequestIdentity, secondCount.GenerationRequestIdentity);
        Assert.NotEqual(firstCount.CountRequest.RequestIdentity, secondCount.CountRequest.RequestIdentity);
    }

    [Fact]
    public async Task Precancelled_count_retains_cancelled_capture_without_admission_or_capacity_spend()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new LlmPreparedCallTests.CaptureHandler();
        using var http = new HttpClient(handler);
        using var prepared = LlmPreparedCallTests.Prepare("openai", LlmPreparedCallTests.Request(), LlmPreparedCallTests.References(), http);
        var capture = Capture();
        var admission = new LlmPreparedCallTests.Admission();
        var counter = Counter(http);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => counter.CountAsync(prepared, capture, admission, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, handler.Sends);
        Assert.Equal(0, admission.Calls);
        Assert.Equal(0, counter.Requests);
        Assert.Equal(LlmAttemptOutcome.Cancelled, capture.Latest!.Outcome);
        Assert.Equal(LlmDispatchCertainty.NotDispatched, capture.Latest.DispatchCertainty);
    }

    [Fact]
    public async Task Count_cancellation_preserves_original_token_and_observed_response_in_caller_capture()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new LlmPreparedCallTests.CaptureHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new CancelOnReadStream(cancellation)) };
            response.Headers.Add("x-request-id", "count-response-before-cancel");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var prepared = LlmPreparedCallTests.Prepare("openai", LlmPreparedCallTests.Request(), LlmPreparedCallTests.References(), http);
        var capture = Capture();
        var counter = Counter(http);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            counter.CountAsync(prepared, capture, new LlmPreparedCallTests.Admission(), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, handler.Sends);
        Assert.Equal(1, counter.Requests);
        Assert.Equal(LlmAttemptOutcome.Cancelled, capture.Latest!.Outcome);
        Assert.Equal(LlmDispatchCertainty.ResponseObserved, capture.Latest.DispatchCertainty);
        Assert.Equal(200, capture.Latest.HttpStatusCode);
        Assert.Equal("count-response-before-cancel", capture.Latest.ProviderRequestId);
        Assert.Null(capture.Latest.Usage);
        Assert.Null(prepared.Capture.Latest);
    }

    private static OpenAiInputTokenCounter Counter(HttpClient http, int maximumRequests = 4) =>
        new(http, new OpenAiResponsesClientOptions("fixture-secret-key", Endpoint: LlmPreparedCallTests.Endpoint), maximumRequests);
    private static LlmAttemptCapture Capture(string attempt = "count-1") =>
        new(new(attempt, "chat.count-input", "count", "route-1", "bundle-1"));
    private static HttpResponseMessage CountResponse(int tokens) => new(HttpStatusCode.OK)
    { Content = new StringContent($"{{\"object\":\"response.input_tokens\",\"input_tokens\":{tokens}}}", Encoding.UTF8, "application/json") };

    private sealed class CancelOnReadStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellationToken);
        }
    }
}
