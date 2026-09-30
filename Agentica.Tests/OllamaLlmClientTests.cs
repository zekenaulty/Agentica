using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;

namespace Agentica.Tests;

public sealed class OllamaLlmClientTests
{
    [Fact]
    public async Task Streams_text_and_reasoning_activity_without_exposing_raw_thinking()
    {
        string? requestJson = null;
        var handler = new StubHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return StreamResponse(
                """
                {"model":"qwen3","message":{"thinking":"private trace"},"done":false}
                {"model":"qwen3","message":{"content":"hel"},"done":false}
                {"model":"qwen3","message":{"content":"lo"},"done":false}
                {"model":"qwen3","message":{},"done":true,"done_reason":"stop","prompt_eval_count":7,"eval_count":2,"prompt_eval_cached_count":3}

                """);
        });
        var client = CreateClient(handler);
        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(Request()))
        {
            events.Add(item);
        }
        var completed = Assert.Single(events, item =>
            item.Kind == LlmStreamEventKind.Completed);
        Assert.Equal("hello", completed.Response!.Text);
        Assert.Equal(7, completed.Response.Usage?.PromptTokens);
        Assert.Equal(2, completed.Response.Usage?.OutputTokens);
        Assert.Equal("13", completed.Response.Metadata?["ollama.thinkingCharacters"]);
        Assert.Contains(events, item => item.Kind == LlmStreamEventKind.Activity &&
            item.Text == "thinking.started");
        Assert.DoesNotContain(events, item => item.Text?.Contains("private trace",
            StringComparison.Ordinal) == true);

        using var body = JsonDocument.Parse(requestJson!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("qwen3", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("system", body.RootElement.GetProperty("messages")[0]
            .GetProperty("role").GetString());
        Assert.Equal(256, body.RootElement.GetProperty("options")
            .GetProperty("num_predict").GetInt32());
        Assert.Equal("object", body.RootElement.GetProperty("format")
            .GetProperty("type").GetString());
    }

    [Fact]
    public async Task Truncated_stream_fails_without_terminal_result()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse(
            "{\"message\":{\"content\":\"partial\"},\"done\":false}\n"))));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(Request()));
        Assert.Equal("stream_incomplete", error.ErrorClass);
    }

    [Fact]
    public async Task Native_history_and_numeric_thinking_budget_fail_before_network()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException(
            "Network should not be called."));
        var client = CreateClient(handler);
        var history = Request() with
        {
            Messages =
            [
                new LlmMessage(LlmMessageRole.User, "one"),
                new LlmMessage(LlmMessageRole.Assistant, "two")
            ]
        };
        var budget = Request() with
        {
            GenerationOptions = new LlmGenerationOptions(
                Thinking: LlmThinkingOptions.Budget(512))
        };
        Assert.Equal("native_history_required",
            (await Assert.ThrowsAsync<LlmClientException>(() =>
                client.GenerateAsync(history))).ErrorClass);
        Assert.Equal("unsupported_thinking_budget",
            (await Assert.ThrowsAsync<LlmClientException>(() =>
                client.GenerateAsync(budget))).ErrorClass);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Tool_call_is_not_treated_as_text_completion()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse(
            "{\"message\":{\"tool_calls\":[{\"function\":{\"name\":\"write\"}}]},\"done\":true}\n"))));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(Request()));
        Assert.Equal("unexpected_tool_call", error.ErrorClass);
    }

    private static LlmRequest Request() => new(
        "qwen3",
        [new LlmMessage(LlmMessageRole.System, "Plan safely."),
         new LlmMessage(LlmMessageRole.User, "Plan this.")],
        new LlmGenerationOptions(MaxOutputTokens: 256,
            Thinking: LlmThinkingOptions.Dynamic()),
        new LlmStructuredOutputOptions(JsonSchema: "{\"type\":\"object\"}"));

    private static OllamaLlmClient CreateClient(HttpMessageHandler handler) =>
        new(new OllamaClientOptions(
            new Uri("http://127.0.0.1:11434/api/chat")), new HttpClient(handler));

    private static HttpResponseMessage StreamResponse(string ndjson) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(ndjson, Encoding.UTF8,
                "application/x-ndjson")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return responseFactory(request);
        }
    }
}
