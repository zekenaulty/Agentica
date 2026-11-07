using System.Net;
using System.Text;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class ProviderAttemptRetentionTests
{
    public static TheoryData<string> Protocols => ["openai", "xai", "anthropic", "interactions", "generate", "ollama"];

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Success_retains_observed_identity_usage_and_single_dispatch(string protocol)
    {
        var handler = new FixtureHandler(() => Response(Success(protocol)));
        using var http = new HttpClient(handler);
        var request = Request();
        var response = await Client(protocol, http).GenerateAsync(request);
        Assert.Equal("ok", response.Text);
        var evidence = Assert.IsType<LlmAttemptEvidence>(response.AttemptEvidence);
        Assert.Same(evidence, request.AttemptCapture!.Latest);
        Assert.Equal(LlmAttemptOutcome.Succeeded, evidence.Outcome);
        Assert.Equal(LlmDispatchCertainty.ResponseObserved, evidence.DispatchCertainty);
        Assert.Equal(200, evidence.HttpStatusCode);
        Assert.Equal("request-1", evidence.ProviderRequestId);
        Assert.Equal("reported", evidence.ProviderReportedModelId);
        Assert.Equal(7, evidence.Usage?.PromptTokens);
        Assert.Equal(3, evidence.Usage?.OutputTokens);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Invalid_output_preserves_usage_received_in_same_event(string protocol)
    {
        var handler = new FixtureHandler(() => Response(Invalid(protocol)));
        using var http = new HttpClient(handler);
        var request = Request();
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => Client(protocol, http).GenerateAsync(request));
        Assert.Same(request.AttemptCapture!.Latest, failure.AttemptEvidence);
        Assert.Equal(LlmAttemptOutcome.Failed, failure.AttemptEvidence!.Outcome);
        Assert.Equal(7, failure.AttemptEvidence.Usage?.PromptTokens);
        Assert.Equal(3, failure.AttemptEvidence.Usage?.OutputTokens);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Late_malformed_data_does_not_deliver_a_premature_completed_event(string protocol)
    {
        var suffix = protocol == "ollama" ? "{malformed}\n" : "data: {malformed}\n\n";
        var handler = new FixtureHandler(() => Response(Success(protocol) + suffix));
        using var http = new HttpClient(handler);
        var request = Request();
        var events = new List<LlmStreamEvent>();
        await Assert.ThrowsAsync<LlmClientException>(async () =>
        {
            await foreach (var item in Client(protocol, http).StreamAsync(request)) events.Add(item);
        });
        Assert.DoesNotContain(events, item => item.Kind == LlmStreamEventKind.Completed);
        Assert.Equal(7, request.AttemptCapture!.Latest!.Usage?.PromptTokens);
        Assert.Equal(LlmAttemptOutcome.Failed, request.AttemptCapture.Latest.Outcome);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Disconnect_after_terminal_facts_retains_them_without_success(string protocol)
    {
        var handler = new FixtureHandler(() => Response(new EndFailureStream(Success(protocol), null)));
        using var http = new HttpClient(handler);
        var request = Request();
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => Client(protocol, http).GenerateAsync(request));
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Same(request.AttemptCapture!.Latest, failure.AttemptEvidence);
        var evidence = request.AttemptCapture!.Latest!;
        Assert.Equal(7, evidence.Usage?.PromptTokens);
        Assert.Equal(LlmAttemptOutcome.Failed, evidence.Outcome);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Cancellation_after_terminal_facts_keeps_ordinary_cancellation_and_usage(string protocol)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FixtureHandler(() => Response(new EndFailureStream(Success(protocol), cancellation)));
        using var http = new HttpClient(handler);
        var request = Request();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(protocol, http).GenerateAsync(request, cancellation.Token));
        Assert.Equal(LlmAttemptOutcome.Cancelled, request.AttemptCapture!.Latest!.Outcome);
        Assert.Equal(7, request.AttemptCapture.Latest.Usage?.PromptTokens);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Http_failure_retains_headers_and_unknown_usage_without_retry(string protocol)
    {
        var handler = new FixtureHandler(() => Response("", HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var request = Request();
        var exception = await Assert.ThrowsAsync<LlmClientException>(() => Client(protocol, http).GenerateAsync(request));
        Assert.Equal(503, exception.AttemptEvidence?.HttpStatusCode);
        Assert.Equal("request-1", exception.AttemptEvidence?.ProviderRequestId);
        Assert.Null(exception.AttemptEvidence?.Usage);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Early_disposal_retains_observed_facts_without_completed_delivery(string protocol)
    {
        var handler = new FixtureHandler(() => Response(Prefix(protocol)));
        using var http = new HttpClient(handler);
        var request = Request();
        await using (var stream = Client(protocol, http).StreamAsync(request).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.NotEqual(LlmStreamEventKind.Completed, stream.Current.Kind);
        }
        Assert.Equal(LlmAttemptOutcome.Abandoned, request.AttemptCapture!.Latest!.Outcome);
        Assert.Equal(7, request.AttemptCapture.Latest.Usage?.PromptTokens);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Cancellation_before_transport_sends_nothing(string protocol)
    {
        var handler = new FixtureHandler(() => Response(Success(protocol)));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var request = Request();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(protocol, http).GenerateAsync(request, cancellation.Token));
        Assert.Equal(LlmAttemptOutcome.Cancelled, request.AttemptCapture!.Latest!.Outcome);
        Assert.Equal(LlmDispatchCertainty.NotDispatched, request.AttemptCapture.Latest.DispatchCertainty);
        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Truncated_terminal_preserves_usage_and_truthful_finish(string protocol)
    {
        var body = Success(protocol);
        body = protocol switch
        {
            "openai" or "xai" => body.Replace("response.completed", "response.incomplete", StringComparison.Ordinal)
                .Replace("\"status\":\"completed\"", "\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}", StringComparison.Ordinal),
            "anthropic" => body.Replace("end_turn", "max_tokens", StringComparison.Ordinal),
            "interactions" => body.Replace("\"status\":\"completed\"", "\"status\":\"incomplete\"", StringComparison.Ordinal),
            "generate" => body.Replace("STOP", "MAX_TOKENS", StringComparison.Ordinal),
            "ollama" => body.Replace("\"done_reason\":\"stop\"", "\"done_reason\":\"length\"", StringComparison.Ordinal),
            _ => body
        };
        var handler = new FixtureHandler(() => Response(body));
        using var http = new HttpClient(handler);
        var request = Request();
        if (protocol is "openai" or "xai")
            await Assert.ThrowsAsync<LlmClientException>(() => Client(protocol, http).GenerateAsync(request));
        else
        {
            var response = await Client(protocol, http).GenerateAsync(request);
            Assert.Equal(LlmFinishReason.MaxTokens, response.FinishReason);
        }
        Assert.Equal(LlmFinishReason.MaxTokens, request.AttemptCapture!.Latest!.FinishReason);
        Assert.Equal(7, request.AttemptCapture.Latest.Usage?.PromptTokens);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Legacy_sdk_has_one_observed_request_even_for_retryable_status(HttpStatusCode status)
    {
        var handler = new FixtureHandler(() => Response("{\"error\":{\"message\":\"fixture\"}}", status));
        using var http = new HttpClient(handler);
        var request = Request();
        var client = new GeminiLlmClient(new GeminiClientOptions(ApiKey: "test-key"), http);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.GenerateAsync(request));
        Assert.Equal(1, handler.Count);
        Assert.Equal((int)status, error.AttemptEvidence?.HttpStatusCode);
        Assert.Null(error.AttemptEvidence?.Usage);
    }

    [Fact]
    public async Task Legacy_sdk_retains_reported_error_body_usage_without_claiming_success()
    {
        var body = """{"error":{"message":"fixture"},"responseId":"response-1","usageMetadata":{"promptTokenCount":7,"candidatesTokenCount":3}}""";
        var handler = new FixtureHandler(() => Response(body, HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var request = Request();
        var error = await Assert.ThrowsAsync<LlmClientException>(() => new GeminiLlmClient(new GeminiClientOptions(ApiKey: "test-key"), http).GenerateAsync(request));
        Assert.Equal(503, error.AttemptEvidence?.HttpStatusCode);
        Assert.Equal(7, error.AttemptEvidence?.Usage?.PromptTokens);
        Assert.Equal(LlmAttemptOutcome.Failed, error.AttemptEvidence?.Outcome);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Legacy_sdk_mapping_error_retains_received_usage_and_identity()
    {
        var body = """{"responseId":"response-1","modelVersion":"reported","usageMetadata":{"promptTokenCount":7,"candidatesTokenCount":3},"candidates":"invalid"}""";
        var handler = new FixtureHandler(() => Response(body));
        using var http = new HttpClient(handler);
        var request = Request();
        var error = await Assert.ThrowsAsync<LlmClientException>(() => new GeminiLlmClient(new GeminiClientOptions(ApiKey: "test-key"), http).GenerateAsync(request));
        Assert.Equal(7, error.AttemptEvidence?.Usage?.PromptTokens);
        Assert.Equal("response-1", error.AttemptEvidence?.ProviderResponseId);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Legacy_sdk_success_uses_observed_transport_and_preserves_exclusive_thinking_arithmetic()
    {
        var body = """{"responseId":"response-1","modelVersion":"reported","usageMetadata":{"promptTokenCount":7,"candidatesTokenCount":3,"thoughtsTokenCount":2,"totalTokenCount":12},"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}""";
        var handler = new FixtureHandler(() => Response(body));
        using var http = new HttpClient(handler);
        var result = await new GeminiLlmClient(new GeminiClientOptions(ApiKey: "test-key"), http).GenerateAsync(Request());
        Assert.Equal("ok", result.Text);
        Assert.Equal(5, result.AttemptEvidence?.Usage?.GeneratedOutputTokens);
        Assert.Equal(LlmAttemptOutcome.Succeeded, result.AttemptEvidence?.Outcome);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Retry_wrapper_rejects_captured_multi_attempt_call_before_dispatch()
    {
        var handler = new FixtureHandler(() => Response("", HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var client = new RetryingLlmClient(Client("openai", http), new LlmRetryOptions(MaxAttempts: 3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GenerateAsync(Request()));
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task One_attempt_retry_wrapper_preserves_failed_observation_on_outer_exception()
    {
        var handler = new FixtureHandler(() => Response("", HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var request = Request();
        var client = new RetryingLlmClient(Client("openai", http), new LlmRetryOptions(MaxAttempts: 1));
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.GenerateAsync(request));
        Assert.Same(request.AttemptCapture!.Latest, error.AttemptEvidence);
        Assert.Equal(503, error.AttemptEvidence?.HttpStatusCode);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task Uncaptured_disconnect_returns_retained_evidence_on_client_exception(string protocol)
    {
        var handler = new FixtureHandler(() => Response(new EndFailureStream(Success(protocol), null)));
        using var http = new HttpClient(handler);
        var request = Request() with { AttemptCapture = null };
        var failure = await Assert.ThrowsAsync<LlmClientException>(() => Client(protocol, http).GenerateAsync(request));
        Assert.Equal(7, failure.AttemptEvidence?.Usage?.PromptTokens);
        Assert.Equal(LlmAttemptOutcome.Failed, failure.AttemptEvidence?.Outcome);
        Assert.Equal(1, handler.Count);
    }

    private static LlmRequest Request() => new("fixture-model", [new(LlmMessageRole.User, "fixture")])
    { AttemptCapture = new(new("attempt:" + Guid.NewGuid().ToString("N"))) };

    private static ILlmStreamingClient Client(string protocol, HttpClient http) => protocol switch
    {
        "openai" => new OpenAiResponsesLlmClient(new(ApiKey: "test-key"), http),
        "xai" => new XaiResponsesLlmClient(new(ApiKey: "test-key"), http),
        "anthropic" => new AnthropicMessagesLlmClient(new(ApiKey: "test-key"), http),
        "interactions" => new GeminiInteractionsLlmClient(new(ApiKey: "test-key"), http),
        "generate" => new GeminiGenerateContentLlmClient(new(ApiKey: "test-key"), http),
        "ollama" => new OllamaLlmClient(new(DefaultModelId: "fixture-model"), http),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private static string Event(string json) => "data: " + json + "\n\n";
    private static string Success(string protocol) => protocol switch
    {
        "openai" or "xai" => Event("""{"type":"response.completed","response":{"id":"response-1","model":"reported","status":"completed","usage":{"input_tokens":7,"output_tokens":3,"output_tokens_details":{"reasoning_tokens":1}},"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}}"""),
        "anthropic" => Event("""{"type":"message_start","message":{"id":"response-1","model":"reported","usage":{"input_tokens":7,"output_tokens":0}}}""") + Event("""{"type":"content_block_start","index":0,"content_block":{"type":"text","text":"ok"}}""") + Event("""{"type":"content_block_stop","index":0}""") + Event("""{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":3}}""") + Event("""{"type":"message_stop"}"""),
        "interactions" => Event("""{"event_type":"interaction.completed","interaction":{"id":"response-1","model":"reported","status":"completed","usage":{"total_input_tokens":7,"total_output_tokens":3,"total_thought_tokens":1},"steps":[{"type":"model_output","content":[{"type":"text","text":"ok"}]}]}}"""),
        "generate" => Event("""{"responseId":"response-1","modelVersion":"reported","usageMetadata":{"promptTokenCount":7,"candidatesTokenCount":3,"thoughtsTokenCount":1},"candidates":[{"content":{"role":"model","parts":[{"text":"ok"}]},"finishReason":"STOP"}]}"""),
        "ollama" => """{"model":"reported","message":{"content":"ok"},"done":true,"done_reason":"stop","prompt_eval_count":7,"eval_count":3}""" + "\n",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private static string Prefix(string protocol) => protocol switch
    {
        "openai" or "xai" => Event("""{"type":"response.created","response":{"id":"response-1","model":"reported","usage":{"input_tokens":7}}}"""),
        "anthropic" => Event("""{"type":"message_start","message":{"id":"response-1","model":"reported","usage":{"input_tokens":7}}}"""),
        "interactions" => Event("""{"event_type":"interaction.created","interaction":{"id":"response-1","model":"reported","usage":{"total_input_tokens":7}}}"""),
        "generate" => Event("""{"responseId":"response-1","usageMetadata":{"promptTokenCount":7},"candidates":[{"content":{"parts":[{"text":"partial"}]}}]}"""),
        "ollama" => """{"message":{"content":"partial"},"prompt_eval_count":7,"done":false}""" + "\n",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private static string Invalid(string protocol) => protocol switch
    {
        "openai" or "xai" => Success(protocol).Replace("\"type\":\"output_text\",\"text\":\"ok\"", "\"type\":\"refusal\",\"refusal\":\"fixture\"", StringComparison.Ordinal),
        "anthropic" => Success(protocol).Replace("end_turn", "refusal", StringComparison.Ordinal),
        "interactions" => Success(protocol).Replace("\"type\":\"model_output\"", "\"type\":\"function_call\"", StringComparison.Ordinal),
        "generate" => Success(protocol).Replace("\"text\":\"ok\"", "\"functionCall\":{}", StringComparison.Ordinal),
        "ollama" => Success(protocol).Replace("\"content\":\"ok\"", "\"tool_calls\":[{}]", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    private static HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        Response(new MemoryStream(Encoding.UTF8.GetBytes(text)), status);
    private static HttpResponseMessage Response(Stream stream, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StreamContent(stream) };
        response.Headers.Add("x-request-id", "request-1");
        return response;
    }

    private sealed class FixtureHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return Task.FromResult(response()); }
    }

    private sealed class EndFailureStream(string prefix, CancellationTokenSource? cancellation) : MemoryStream(Encoding.UTF8.GetBytes(prefix))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position != Length) return base.ReadAsync(buffer, cancellationToken);
            if (cancellation is not null)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new IOException("Fixture disconnected after observed facts.");
        }
    }
}
