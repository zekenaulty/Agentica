using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Planning;
using Agentica.Clients.Llm;

namespace Agentica.Tests;

public sealed class GeminiInteractionsLlmClientTests
{
    [Fact]
    public async Task Streams_stateless_text_and_requires_terminal_receipt()
    {
        string? requestJson = null;
        string? requestKey = null;
        var handler = new StubHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            requestKey = request.Headers.GetValues("x-goog-api-key").Single();
            return StreamResponse(
                """
                event: interaction.created
                data: {"event_type":"interaction.created","interaction":{"id":"int_1"}}

                event: step.start
                data: {"event_type":"step.start","index":0,"step":{"type":"thought"}}

                event: step.delta
                data: {"event_type":"step.delta","index":0,"delta":{"type":"thought_summary","content":{"type":"text","text":"Checking"}}}

                event: step.delta
                data: {"event_type":"step.delta","index":0,"delta":{"type":"thought_signature","signature":"opaque"}}

                event: step.start
                data: {"event_type":"step.start","index":1,"step":{"type":"model_output"}}

                event: step.delta
                data: {"event_type":"step.delta","index":1,"delta":{"type":"text","text":"hel"}}

                event: step.delta
                data: {"event_type":"step.delta","index":1,"delta":{"type":"text","text":"lo"}}

                event: interaction.completed
                data: {"event_type":"interaction.completed","interaction":{"id":"int_1","status":"completed","usage":{"total_input_tokens":3,"total_output_tokens":2,"total_thought_tokens":1,"total_tokens":6}}}

                event: done
                data: [DONE]

                """);
        });
        var client = CreateClient(handler);
        var request = new LlmRequest(
            "gemini-3.8-flash",
            [
                new LlmMessage(LlmMessageRole.System, "Follow policy."),
                new LlmMessage(LlmMessageRole.User, "Say hello.")
            ],
            new LlmGenerationOptions(MaxOutputTokens: 64,
                Thinking: LlmThinkingOptions.Dynamic(includeThoughts: true)),
            new LlmStructuredOutputOptions(JsonSchema: "{\"type\":\"object\"}"));

        var events = new List<LlmStreamEvent>();
        await foreach (var item in client.StreamAsync(request))
        {
            events.Add(item);
        }

        Assert.Equal(
            [LlmStreamEventKind.Activity, LlmStreamEventKind.Activity,
                LlmStreamEventKind.ThoughtSummaryDelta, LlmStreamEventKind.Activity,
                LlmStreamEventKind.TextDelta, LlmStreamEventKind.TextDelta,
                LlmStreamEventKind.Completed],
            events.Select(item => item.Kind));
        var result = Assert.IsType<LlmResponse>(events[^1].Response);
        Assert.Equal("hello", result.Text);
        Assert.Equal("hello", result.StructuredJson);
        Assert.Equal(3, result.Usage?.PromptTokens);
        Assert.Equal(1, result.Usage?.ThinkingTokens);
        Assert.Equal("int_1", result.Metadata?["gemini.interaction.id"]);
        Assert.DoesNotContain("opaque", JsonSerializer.Serialize(result));
        Assert.Equal("test-key", requestKey);
        using var sent = JsonDocument.Parse(Assert.IsType<string>(requestJson));
        var body = sent.RootElement;
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal("Follow policy.", body.GetProperty("system_instruction").GetString());
        Assert.Equal("Say hello.", body.GetProperty("input").GetString());
        Assert.Equal(64, body.GetProperty("generation_config")
            .GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("auto", body.GetProperty("generation_config")
            .GetProperty("thinking_summaries").GetString());
        Assert.Equal("text", body.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Truncated_stream_cannot_become_a_successful_response()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse(
            """
            event: step.start
            data: {"event_type":"step.start","index":0,"step":{"type":"model_output"}}

            event: step.delta
            data: {"event_type":"step.delta","index":0,"delta":{"type":"text","text":"partial"}}

            """))));

        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(TextRequest()));

        Assert.Equal("stream_incomplete", error.ErrorClass);
    }

    [Fact]
    public async Task Incomplete_interaction_reports_max_tokens()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse(
            """
            event: interaction.completed
            data: {"event_type":"interaction.completed","interaction":{"id":"int_2","status":"incomplete"}}

            """))));

        var result = await client.GenerateAsync(TextRequest());

        Assert.Equal(LlmFinishReason.MaxTokens, result.FinishReason);
    }

    [Fact]
    public async Task Native_history_and_unsupported_budget_fail_before_network()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network should not be called."));
        var client = CreateClient(handler);
        var history = TextRequest() with
        {
            Messages =
            [
                new LlmMessage(LlmMessageRole.User, "one"),
                new LlmMessage(LlmMessageRole.Assistant, "two"),
                new LlmMessage(LlmMessageRole.User, "three")
            ]
        };
        var budget = TextRequest() with
        {
            GenerationOptions = new LlmGenerationOptions(
                Thinking: LlmThinkingOptions.Budget(1024))
        };

        Assert.Equal("native_history_required",
            (await Assert.ThrowsAsync<LlmClientException>(() => client.GenerateAsync(history)))
            .ErrorClass);
        Assert.Equal("unsupported_thinking_budget",
            (await Assert.ThrowsAsync<LlmClientException>(() => client.GenerateAsync(budget)))
            .ErrorClass);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Unexpected_function_call_is_not_treated_as_text_success()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse(
            """
            event: step.start
            data: {"event_type":"step.start","index":0,"step":{"type":"function_call","name":"write"}}

            """))));

        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(TextRequest()));

        Assert.Equal("unexpected_function_call", error.ErrorClass);
    }

    [Fact]
    public void Stateless_repair_quotes_invalid_output_as_data_without_assistant_history()
    {
        var repair = WorkflowPlanPromptBuilder.BuildInitialPlanRepairRequest(
            TextRequest(), "{bad-json", "invalid JSON", 1,
            new LlmPlannerOptions(StatelessRepair: true));

        Assert.All(repair.Messages, message =>
            Assert.NotEqual(LlmMessageRole.Assistant, message.Role));
        Assert.Contains("{bad-json", repair.Messages[^1].Content, StringComparison.Ordinal);
        Assert.Contains("quoted data", repair.Messages[^1].Content, StringComparison.Ordinal);
    }

    private static LlmRequest TextRequest() =>
        new("gemini-3.8-flash", [new LlmMessage(LlmMessageRole.User, "hello")]);

    private static GeminiInteractionsLlmClient CreateClient(HttpMessageHandler handler) =>
        new(new GeminiInteractionsClientOptions(
            ApiKey: "test-key",
            Endpoint: new Uri("https://example.test/interactions")),
            new HttpClient(handler));

    private static HttpResponseMessage StreamResponse(string sse) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };

    private sealed class StubHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return await responseFactory(request);
        }
    }
}
