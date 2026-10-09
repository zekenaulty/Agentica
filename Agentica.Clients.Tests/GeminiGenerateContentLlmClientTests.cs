using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;

namespace Agentica.Tests;

public sealed class GeminiGenerateContentLlmClientTests
{
    private const string TextChunk = """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"hello"}]}}]}""";
    private const string StopChunk = """{"candidates":[{"index":0,"finishReason":"STOP"}]}""";
    private const string SignedStopChunk = """{"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"","thoughtSignature":"opaque-private-signature"}]},"finishReason":"STOP"}]}""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Streams_thought_summaries_only_when_requested_and_keeps_signed_empty_part_private(bool includeThoughts)
    {
        var sse = Event("""{"candidates":[{"content":{"role":"model","parts":[{"thought":true,"text":"Checking the facts"}]}}]}""") +
            Event(TextChunk) + Event(SignedStopChunk) +
            Event("""{"usageMetadata":{"promptTokenCount":11,"candidatesTokenCount":2,"thoughtsTokenCount":3,"totalTokenCount":16,"cachedContentTokenCount":1}}""");
        using var handler = new Handler(_ => Response(sse));
        using var http = new HttpClient(handler);
        var client = new GeminiGenerateContentLlmClient(new("fixture-key"), http);
        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(Request() with
        {
            GenerationOptions = new LlmGenerationOptions(Thinking: LlmThinkingOptions.Dynamic(includeThoughts))
        })) events.Add(item);
        var response = Assert.IsType<LlmResponse>(events[^1].Response);
        using var continuation = Assert.IsType<LlmNativeContinuation>(response.NativeContinuation);
        Assert.Equal("hello", response.Text);
        Assert.Equal(LlmFinishReason.Stop, response.FinishReason);
        Assert.Equal(new LlmUsage(11, 2, 3, 16, 1), response.Usage);
        Assert.Contains(events, item => item.Kind == LlmStreamEventKind.Activity && item.Text == "thinking");
        Assert.Equal(includeThoughts ? 1 : 0, events.Count(item => item.Kind == LlmStreamEventKind.ThoughtSummaryDelta));
        Assert.Equal(includeThoughts ? 1 : 0, response.ThoughtSummaries!.Count);
        Assert.DoesNotContain("opaque-private-signature", JsonSerializer.Serialize(events), StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-private-signature", continuation.ToString(), StringComparison.Ordinal);
        if (!includeThoughts)
            Assert.DoesNotContain("Checking the facts", JsonSerializer.Serialize(events), StringComparison.Ordinal);
        using var history = JsonDocument.Parse(continuation.HistoryStepsJson);
        var parts = history.RootElement[1].GetProperty("parts");
        Assert.Equal(3, parts.GetArrayLength());
        Assert.True(parts[0].GetProperty("thought").GetBoolean());
        Assert.Equal("", parts[2].GetProperty("text").GetString());
        Assert.Equal("opaque-private-signature", parts[2].GetProperty("thoughtSignature").GetString());
    }

    [Fact]
    public async Task Native_followup_replays_exact_parts_and_generation_controls_with_no_remote_history()
    {
        using var handler = new Handler(_ => Response(Event(TextChunk) + Event(SignedStopChunk)));
        using var http = new HttpClient(handler);
        var client = new GeminiGenerateContentLlmClient(new("fixture-key"), http);
        var request = Request() with
        {
            GenerationOptions = new LlmGenerationOptions(0.3, 512, LlmThinkingOptions.Budget(128, true)),
            StructuredOutput = new LlmStructuredOutputOptions(JsonSchema: """{"type":"object"}""")
        };
        var first = await client.GenerateAsync(request);
        using var continuation = Assert.IsType<LlmNativeContinuation>(first.NativeContinuation);
        var second = await client.GenerateAsync(request with { NativeContinuation = continuation });
        second.NativeContinuation?.Dispose();
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-test:streamGenerateContent?alt=sse",
            handler.Endpoint!.AbsoluteUri);
        Assert.Equal("fixture-key", handler.Key);
        using var body = JsonDocument.Parse(handler.Bodies[1]);
        Assert.False(body.RootElement.TryGetProperty("previous_interaction_id", out _));
        var contents = body.RootElement.GetProperty("contents");
        Assert.Equal(3, contents.GetArrayLength());
        Assert.Equal("model", contents[1].GetProperty("role").GetString());
        Assert.Equal("opaque-private-signature", contents[1].GetProperty("parts")[1].GetProperty("thoughtSignature").GetString());
        Assert.Equal("user", contents[2].GetProperty("role").GetString());
        var config = body.RootElement.GetProperty("generationConfig");
        Assert.Equal(1, config.GetProperty("candidateCount").GetInt32());
        Assert.Equal(0.3, config.GetProperty("temperature").GetDouble());
        Assert.Equal(512, config.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(128, config.GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32());
        Assert.True(config.GetProperty("thinkingConfig").GetProperty("includeThoughts").GetBoolean());
        Assert.Equal("object", config.GetProperty("responseJsonSchema").GetProperty("type").GetString());
        Assert.Equal("Policy", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("model")]
    [InlineData("policy")]
    public async Task Wrong_native_binding_is_rejected_before_dispatch(string mismatch)
    {
        using var previous = new LlmNativeContinuation(
            mismatch == "transport" ? GeminiLlmClient.ProviderName : GeminiGenerateContentLlmClient.ProviderName,
            mismatch == "model" ? "other-model" : "gemini-test",
            mismatch == "policy" ? "Other policy" : "Policy",
            """[{"role":"model","parts":[{"text":"prior","thoughtSignature":"private"}]}]""");
        using var handler = new Handler(_ => throw new InvalidOperationException("Unexpected network call."));
        using var http = new HttpClient(handler);
        var client = new GeminiGenerateContentLlmClient(new("fixture-key"), http);
        var error = await Assert.ThrowsAsync<LlmClientException>(() => client.GenerateAsync(Request() with { NativeContinuation = previous }));
        Assert.Equal("continuation_binding_mismatch", error.ErrorClass);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task Text_delta_is_observable_before_terminal_bytes_arrive()
    {
        using var stream = new GatedStream(Event(TextChunk), Event(SignedStopChunk));
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var http = new HttpClient(handler);
        var client = new GeminiGenerateContentLlmClient(new("fixture-key"), http);
        await using var events = client.StreamAsync(Request()).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(LlmStreamEventKind.TextDelta, events.Current.Kind);
        Assert.Equal("hello", events.Current.Text);
        var next = events.MoveNextAsync().AsTask();
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(next.IsCompleted);
        stream.Release.TrySetResult(true);
        Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(LlmStreamEventKind.Completed, events.Current.Kind);
        events.Current.Response?.NativeContinuation?.Dispose();
        Assert.False(await events.MoveNextAsync());
    }

    [Fact]
    public async Task Cancellation_during_stream_never_yields_completion()
    {
        using var stream = new GatedStream(Event(TextChunk), Event(StopChunk));
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var client = new GeminiGenerateContentLlmClient(new("fixture-key"), http);
        await using var events = client.StreamAsync(Request(), cancellation.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        var next = events.MoveNextAsync().AsTask();
        await stream.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Theory]
    [InlineData("missing", "stream_incomplete")]
    [InlineData("malformed", "invalid_event")]
    [InlineData("function", "unsupported_output_part")]
    [InlineData("afterTerminal", "data_after_terminal")]
    [InlineData("providerError", "provider_error")]
    [InlineData("manyCandidates", "unsupported_candidates")]
    [InlineData("oversized", "event_too_large")]
    public async Task Incomplete_or_unsupported_streams_never_report_success(string scenario, string code)
    {
        var sse = scenario switch
        {
            "missing" => Event(TextChunk),
            "malformed" => Event(TextChunk) + Event("{private malformed"),
            "function" => Event("""{"candidates":[{"content":{"parts":[{"functionCall":{"name":"write","args":{}}}]}}]}"""),
            "afterTerminal" => Event(TextChunk) + Event(StopChunk) + Event(TextChunk),
            "providerError" => Event("""{"error":{"message":"private provider data"}}"""),
            "manyCandidates" => Event("""{"candidates":[{"index":0},{"index":1}]}"""),
            "oversized" => Event(new string('x', 1_048_577)),
            _ => throw new InvalidOperationException()
        };
        using var handler = new Handler(_ => Response(sse));
        using var http = new HttpClient(handler);
        var client = new GeminiGenerateContentLlmClient(new("fixture-key"), http);
        var events = new List<LlmStreamEvent>();
        var error = await Assert.ThrowsAsync<LlmClientException>(async () =>
        {
            await foreach (var item in client.StreamAsync(Request())) events.Add(item);
        });
        Assert.Equal(code, error.ErrorClass);
        Assert.DoesNotContain(events, item => item.Kind == LlmStreamEventKind.Completed);
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MAX_TOKENS", LlmFinishReason.MaxTokens)]
    [InlineData("SAFETY", LlmFinishReason.Safety)]
    public async Task Non_success_terminal_reasons_remain_explicit_and_have_no_continuation(string finish, LlmFinishReason expected)
    {
        using var handler = new Handler(_ => Response(Event(TextChunk) +
            Event(JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = finish } } }))));
        using var http = new HttpClient(handler);
        var result = await new GeminiGenerateContentLlmClient(new("fixture-key"), http).GenerateAsync(Request());
        Assert.Equal(expected, result.FinishReason);
        Assert.Null(result.NativeContinuation);
    }

    [Fact]
    public async Task Prompt_block_is_explicit_without_manufactured_output()
    {
        using var handler = new Handler(_ => Response(Event("""{"promptFeedback":{"blockReason":"SAFETY"}}""")));
        using var http = new HttpClient(handler);
        var result = await new GeminiGenerateContentLlmClient(new("fixture-key"), http).GenerateAsync(Request());
        Assert.Equal(LlmFinishReason.Blocked, result.FinishReason);
        Assert.Empty(result.Text);
        Assert.Null(result.NativeContinuation);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmClientErrorKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmClientErrorKind.RateLimited)]
    public async Task Http_failures_preserve_classification_without_response_body(HttpStatusCode status, LlmClientErrorKind expected)
    {
        using var handler = new Handler(_ => new HttpResponseMessage(status) { Content = new StringContent("private response data") });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            new GeminiGenerateContentLlmClient(new("fixture-key"), http).GenerateAsync(Request()));
        Assert.Equal(expected, error.ErrorKind);
        Assert.Equal((int)status, error.StatusCode);
        Assert.DoesNotContain("private response data", error.ToString(), StringComparison.Ordinal);
    }

    private static LlmRequest Request() => new("gemini-test",
        [new(LlmMessageRole.System, "Policy"), new(LlmMessageRole.User, "Say hello")]);

    private static string Event(string data) => "data: " + data + "\n\n";

    private static HttpResponseMessage Response(string sse) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public Uri? Endpoint { get; private set; }
        public string? Key { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Endpoint = request.RequestUri;
            Key = request.Headers.GetValues("x-goog-api-key").Single();
            return respond(request);
        }
    }

    private sealed class GatedStream(string prefix, string suffix) : MemoryStream(Encoding.UTF8.GetBytes(prefix + suffix))
    {
        private readonly int _prefixBytes = Encoding.UTF8.GetByteCount(prefix);
        public TaskCompletionSource<bool> Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= _prefixBytes)
            {
                Waiting.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }
            else
                buffer = buffer[..Math.Min(buffer.Length, _prefixBytes - (int)Position)];
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
