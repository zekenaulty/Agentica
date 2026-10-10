using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;

namespace Agentica.Tests;

public sealed class OpenAiResponsesLlmClientTests
{
    [Fact]
    public async Task Streams_text_usage_and_private_encrypted_reasoning_replay()
    {
        var sent = new List<string>();
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            sent.Add(await request.Content!.ReadAsStringAsync());
            return sent.Count == 1
                ? StreamResponse("""
                    event: response.created
                    data: {"type":"response.created","response":{"id":"resp_1"}}

                    event: response.reasoning_summary_text.delta
                    data: {"type":"response.reasoning_summary_text.delta","delta":"Checking"}

                    event: response.output_text.delta
                    data: {"type":"response.output_text.delta","delta":"four"}

                    event: response.completed
                    data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","output":[{"id":"rs_1","type":"reasoning","encrypted_content":"opaque-secret","summary":[]},{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"four","annotations":[]}]}],"usage":{"input_tokens":8,"output_tokens":10,"total_tokens":18,"input_tokens_details":{"cached_tokens":2},"output_tokens_details":{"reasoning_tokens":6}}}}

                    data: [DONE]

                    """)
                : StreamResponse("""
                    event: response.output_text.delta
                    data: {"type":"response.output_text.delta","delta":"eight"}

                    event: response.completed
                    data: {"type":"response.completed","response":{"id":"resp_2","status":"completed","output":[{"id":"msg_2","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"eight","annotations":[]}]}],"usage":{"input_tokens":19,"output_tokens":2,"total_tokens":21}}}

                    """);
        });
        var client = CreateClient(handler);
        var firstRequest = new LlmRequest("gpt-6-luna",
            [new LlmMessage(LlmMessageRole.System, "Answer as JSON."),
             new LlmMessage(LlmMessageRole.User, "How many?")],
            new LlmGenerationOptions(MaxOutputTokens: 128,
                Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High, includeThoughts: true)),
            new LlmStructuredOutputOptions(JsonSchema: "{\"type\":\"object\"}"));
        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(firstRequest)) events.Add(item);
        var first = Assert.IsType<LlmResponse>(events[^1].Response);
        Assert.Equal("four", first.Text);
        Assert.Equal(6, first.Usage?.ThinkingTokens);
        Assert.Equal(2, first.Usage?.CachedPromptTokens);
        Assert.Equal([LlmStreamEventKind.Activity,
            LlmStreamEventKind.ThoughtSummaryDelta, LlmStreamEventKind.TextDelta,
            LlmStreamEventKind.Completed], events.Select(item => item.Kind));
        var continuation = Assert.IsType<LlmNativeContinuation>(first.NativeContinuation);
        Assert.DoesNotContain("opaque-secret", JsonSerializer.Serialize(first));
        Assert.DoesNotContain("opaque-secret", JsonSerializer.Serialize(firstRequest));
        Assert.DoesNotContain("opaque-secret", JsonSerializer.Serialize(events));
        Assert.DoesNotContain("opaque-secret", continuation.ToString());

        var second = await client.GenerateAsync(new LlmRequest("gpt-6-luna",
            [new LlmMessage(LlmMessageRole.System, "Answer as JSON."),
             new LlmMessage(LlmMessageRole.User, "And twice that?")],
            GenerationOptions: firstRequest.GenerationOptions,
            NativeContinuation: continuation));
        Assert.Equal("eight", second.Text);
        Assert.Equal(2, sent.Count);
        foreach (var body in sent)
        {
            using var requestBody = JsonDocument.Parse(body);
            Assert.Equal("gpt-6-luna", requestBody.RootElement.GetProperty("model").GetString());
            Assert.Equal("high", requestBody.RootElement.GetProperty("reasoning")
                .GetProperty("effort").GetString());
            Assert.False(requestBody.RootElement.GetProperty("store").GetBoolean());
            Assert.True(requestBody.RootElement.GetProperty("stream").GetBoolean());
            Assert.False(requestBody.RootElement.TryGetProperty("previous_response_id", out _));
        }
        using var firstBody = JsonDocument.Parse(sent[0]);
        Assert.Equal("json_object", firstBody.RootElement.GetProperty("text")
            .GetProperty("format").GetProperty("type").GetString());
        Assert.Equal("auto", firstBody.RootElement.GetProperty("reasoning")
            .GetProperty("summary").GetString());
        using var secondBody = JsonDocument.Parse(sent[1]);
        var input = secondBody.RootElement.GetProperty("input");
        Assert.Equal(4, input.GetArrayLength());
        Assert.Equal("How many?", input[0].GetProperty("content").GetString());
        Assert.Equal("opaque-secret", input[1].GetProperty("encrypted_content").GetString());
        Assert.Equal("message", input[2].GetProperty("type").GetString());
        Assert.Equal("And twice that?", input[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Missing_encrypted_reasoning_withholds_continuation()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse("""
            event: response.completed
            data: {"type":"response.completed","response":{"id":"r","status":"completed","output":[{"type":"reasoning","summary":[]},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}}

            """))));
        var response = await client.GenerateAsync(TextRequest());
        Assert.Equal("ok", response.Text);
        Assert.Null(response.NativeContinuation);
    }

    [Theory]
    [InlineData("response.incomplete", "response.incomplete")]
    [InlineData("response.failed", "response.failed")]
    public async Task Nonterminal_success_is_rejected(string eventType, string errorClass)
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse(
            $"event: {eventType}\ndata: {{\"type\":\"{eventType}\",\"response\":{{\"status\":\"incomplete\"}}}}\n\n"))));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(TextRequest()));
        Assert.Equal(errorClass, error.ErrorClass);
    }

    [Fact]
    public async Task Truncated_stream_and_native_tool_call_cannot_complete()
    {
        var truncated = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse("""
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"partial"}

            """))));
        var incomplete = await Assert.ThrowsAsync<LlmClientException>(() =>
            truncated.GenerateAsync(TextRequest()));
        Assert.Equal("stream_incomplete", incomplete.ErrorClass);
        var toolCall = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse("""
            event: response.completed
            data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"function_call","name":"write","arguments":"{}"}]}}

            """))));
        var rejected = await Assert.ThrowsAsync<LlmClientException>(() =>
            toolCall.GenerateAsync(TextRequest()));
        Assert.Equal("unsupported_output_item", rejected.ErrorClass);
    }

    [Fact]
    public async Task Continuation_binding_and_assistant_history_fail_before_network()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network called."));
        var client = CreateClient(handler);
        var history = TextRequest() with
        {
            Messages = [new LlmMessage(LlmMessageRole.User, "one"),
                new LlmMessage(LlmMessageRole.Assistant, "two")]
        };
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(history));
        Assert.Equal("native_history_required", error.ErrorClass);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Replay_rejects_changed_model_or_instructions_before_network()
    {
        var firstHandler = new StubHandler(_ => Task.FromResult(StreamResponse("""
            event: response.completed
            data: {"type":"response.completed","response":{"id":"r","status":"completed","output":[{"type":"reasoning","encrypted_content":"opaque"},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}}

            """)));
        var first = await CreateClient(firstHandler).GenerateAsync(new LlmRequest("gpt-test",
            [new LlmMessage(LlmMessageRole.System, "Follow policy."),
             new LlmMessage(LlmMessageRole.User, "Start.")]));
        var continuation = Assert.IsType<LlmNativeContinuation>(first.NativeContinuation);
        var blockedHandler = new StubHandler(_ => throw new InvalidOperationException("Network called."));
        var client = CreateClient(blockedHandler);
        foreach (var changed in new[]
        {
            new LlmRequest("other-model", [new LlmMessage(LlmMessageRole.System, "Follow policy."),
                new LlmMessage(LlmMessageRole.User, "Continue.")], NativeContinuation: continuation),
            new LlmRequest("gpt-test", [new LlmMessage(LlmMessageRole.System, "Changed policy."),
                new LlmMessage(LlmMessageRole.User, "Continue.")], NativeContinuation: continuation)
        })
        {
            var error = await Assert.ThrowsAsync<LlmClientException>(() =>
                client.GenerateAsync(changed));
            Assert.Equal("continuation_binding_mismatch", error.ErrorClass);
        }
        Assert.Equal(0, blockedHandler.CallCount);
    }

    [Fact]
    public async Task Complete_output_item_events_supply_terminal_output_when_omitted()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse("""
            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","encrypted_content":"opaque"}}

            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":1,"item":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"r","status":"completed"}}

            """))));
        var response = await client.GenerateAsync(TextRequest());
        Assert.Equal("ok", response.Text);
        Assert.NotNull(response.NativeContinuation);
    }

    private static LlmRequest TextRequest() =>
        new("gpt-test", [new LlmMessage(LlmMessageRole.User, "hello")]);
    private static OpenAiResponsesLlmClient CreateClient(HttpMessageHandler handler) =>
        new(new OpenAiResponsesClientOptions("test-key", Endpoint:
            new Uri("https://example.test/responses")), new HttpClient(handler));
    private static HttpResponseMessage StreamResponse(string sse) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> factory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return await factory(request);
        }
    }
}
