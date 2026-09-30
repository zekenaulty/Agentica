using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Llm;

namespace Agentica.Tests;

public sealed class AnthropicMessagesLlmClientTests
{
    [Fact]
    public async Task Streams_text_and_replays_complete_signed_thinking_blocks()
    {
        var requests = new List<string>();
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("test-key", request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            requests.Add(await request.Content!.ReadAsStringAsync());
            return requests.Count == 1 ? StreamResponse("""
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":8,"cache_read_input_tokens":2}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Checking"}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"private-signature"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: content_block_start
                data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"four"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":1}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":11}}

                event: message_stop
                data: {"type":"message_stop"}

                """) : StreamResponse("""
                event: message_start
                data: {"type":"message_start","message":{"id":"msg_2","usage":{"input_tokens":21}}}

                event: content_block_start
                data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"eight"}}

                event: content_block_stop
                data: {"type":"content_block_stop","index":0}

                event: message_delta
                data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

                event: message_stop
                data: {"type":"message_stop"}

                """);
        });
        var client = CreateClient(handler);
        var firstRequest = new LlmRequest("claude-test",
            [new LlmMessage(LlmMessageRole.System, "Follow policy."),
             new LlmMessage(LlmMessageRole.User, "How many?")],
            new LlmGenerationOptions(MaxOutputTokens: 2048,
                Thinking: LlmThinkingOptions.Dynamic(includeThoughts: true)));
        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(firstRequest)) events.Add(item);
        var first = Assert.IsType<LlmResponse>(events[^1].Response);
        Assert.Equal("four", first.Text);
        Assert.Equal(8, first.Usage?.PromptTokens);
        Assert.Equal(11, first.Usage?.OutputTokens);
        Assert.Equal(2, first.Usage?.CachedPromptTokens);
        Assert.Equal([LlmStreamEventKind.Activity, LlmStreamEventKind.ThoughtSummaryDelta,
            LlmStreamEventKind.TextDelta, LlmStreamEventKind.Completed],
            events.Select(item => item.Kind));
        var continuation = Assert.IsType<LlmNativeContinuation>(first.NativeContinuation);
        Assert.DoesNotContain("private-signature", JsonSerializer.Serialize(first));
        Assert.DoesNotContain("private-signature", continuation.ToString());
        var second = await client.GenerateAsync(new LlmRequest("claude-test",
            [new LlmMessage(LlmMessageRole.System, "Follow policy."),
             new LlmMessage(LlmMessageRole.User, "And twice that?")],
            NativeContinuation: continuation));
        Assert.Equal("eight", second.Text);
        using var firstBody = JsonDocument.Parse(requests[0]);
        Assert.True(firstBody.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("adaptive", firstBody.RootElement.GetProperty("thinking")
            .GetProperty("type").GetString());
        Assert.Equal("summarized", firstBody.RootElement.GetProperty("thinking")
            .GetProperty("display").GetString());
        using var secondBody = JsonDocument.Parse(requests[1]);
        var messages = secondBody.RootElement.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("private-signature", messages[1].GetProperty("content")[0]
            .GetProperty("signature").GetString());
        Assert.Equal("And twice that?", messages[2].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Missing_signature_withholds_continuation_and_tool_block_fails()
    {
        var unsigned = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"m","usage":{"input_tokens":2}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":"ok"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            event: message_stop
            data: {"type":"message_stop"}

            """))));
        var response = await unsigned.GenerateAsync(TextRequest());
        Assert.Equal("ok", response.Text);
        Assert.Null(response.NativeContinuation);

        var tool = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"m"}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","name":"write"}}

            """))));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            tool.GenerateAsync(TextRequest()));
        Assert.Equal("unsupported_content_block", error.ErrorClass);
    }

    [Fact]
    public async Task Missing_message_stop_and_invalid_budget_fail_closed()
    {
        var truncated = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse("""
            event: message_start
            data: {"type":"message_start","message":{"id":"m"}}

            """))));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            truncated.GenerateAsync(TextRequest()));
        Assert.Equal("stream_incomplete", error.ErrorClass);
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network called."));
        var client = CreateClient(handler);
        var request = TextRequest() with
        {
            GenerationOptions = new LlmGenerationOptions(MaxOutputTokens: 1024,
                Thinking: LlmThinkingOptions.Budget(1024))
        };
        var budgetError = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(request));
        Assert.Equal("invalid_thinking_budget", budgetError.ErrorClass);
        Assert.Equal(0, handler.CallCount);
    }

    private static LlmRequest TextRequest() =>
        new("claude-test", [new LlmMessage(LlmMessageRole.User, "hello")]);
    private static AnthropicMessagesLlmClient CreateClient(HttpMessageHandler handler) =>
        new(new AnthropicMessagesClientOptions("test-key", Endpoint:
            new Uri("https://example.test/messages")), new HttpClient(handler));
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
