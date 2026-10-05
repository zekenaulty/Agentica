using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class XaiResponsesLlmClientTests
{
    [Fact]
    public async Task Grok_streams_without_exposing_raw_reasoning_and_replays_ciphertext()
    {
        var sent = new List<string>();
        var handler = new StubHandler(async request =>
        {
            Assert.Equal("test-xai-key", request.Headers.Authorization?.Parameter);
            sent.Add(await request.Content!.ReadAsStringAsync());
            return sent.Count == 1 ? StreamResponse("""
                event: response.created
                data: {"type":"response.created","response":{"id":"r_1"}}

                event: response.reasoning_text.delta
                data: {"type":"response.reasoning_text.delta","delta":"raw-private-reasoning"}

                event: response.output_text.delta
                data: {"type":"response.output_text.delta","delta":"four"}

                event: response.completed
                data: {"type":"response.completed","response":{"id":"r_1","status":"completed","output":[{"type":"reasoning","encrypted_content":"ciphertext"},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"four"}]}],"usage":{"input_tokens":4,"output_tokens":8,"output_tokens_details":{"reasoning_tokens":6}}}}

                """) : StreamResponse("""
                event: response.completed
                data: {"type":"response.completed","response":{"id":"r_2","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"eight"}]}]}}

                """);
        });
        var client = new XaiResponsesLlmClient(new XaiResponsesClientOptions("test-xai-key",
            Endpoint: new Uri("https://example.test/responses")), new HttpClient(handler));
        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(new LlmRequest("grok-test",
            [new LlmMessage(LlmMessageRole.User, "How many?")]))) events.Add(item);
        var first = Assert.IsType<LlmResponse>(events[^1].Response);
        Assert.Equal("xai", first.ProviderName);
        Assert.Equal("four", first.Text);
        Assert.Equal(6, first.Usage?.ThinkingTokens);
        Assert.NotNull(first.NativeContinuation);
        Assert.Contains(events, item => item is
        { Kind: LlmStreamEventKind.Activity, Text: "reasoning.streaming" });
        Assert.DoesNotContain("raw-private-reasoning", JsonSerializer.Serialize(events));
        Assert.DoesNotContain("ciphertext", JsonSerializer.Serialize(first));

        var next = await client.GenerateAsync(new LlmRequest("grok-test",
            [new LlmMessage(LlmMessageRole.User, "And twice?")],
            NativeContinuation: first.NativeContinuation));
        Assert.Equal("eight", next.Text);
        using var firstBody = JsonDocument.Parse(sent[0]);
        Assert.False(firstBody.RootElement.GetProperty("store").GetBoolean());
        Assert.True(firstBody.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("reasoning.encrypted_content",
            firstBody.RootElement.GetProperty("include")[0].GetString());
        using var nextBody = JsonDocument.Parse(sent[1]);
        Assert.Equal("ciphertext", nextBody.RootElement.GetProperty("input")[1]
            .GetProperty("encrypted_content").GetString());
    }

    [Fact]
    public async Task Grok_continuation_cannot_be_sent_to_OpenAI_adapter()
    {
        var xai = new XaiResponsesLlmClient(new XaiResponsesClientOptions("test-key",
            Endpoint: new Uri("https://example.test/xai")),
            new HttpClient(new StubHandler(_ => Task.FromResult(StreamResponse("""
                event: response.completed
                data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"reasoning","encrypted_content":"secret"},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}}

                """)))));
        var first = await xai.GenerateAsync(new LlmRequest("same-model",
            [new LlmMessage(LlmMessageRole.User, "Start.")]));
        var blockedHandler = new StubHandler(_ => throw new InvalidOperationException("Network called."));
        var openai = new OpenAiResponsesLlmClient(new OpenAiResponsesClientOptions("test-key",
            Endpoint: new Uri("https://example.test/openai")), new HttpClient(blockedHandler));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            openai.GenerateAsync(new LlmRequest("same-model",
                [new LlmMessage(LlmMessageRole.User, "Continue.")],
                NativeContinuation: first.NativeContinuation)));
        Assert.Equal("continuation_binding_mismatch", error.ErrorClass);
        Assert.Equal(0, blockedHandler.CallCount);
    }

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
