using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Planning;
using Agentica.Clients.Llm;
using Agentica.Planning;
using Agentica.Requests;

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
    public async Task Stateless_follow_up_replays_signed_native_steps_without_serializing_them()
    {
        var sent = new List<string>();
        var handler = new StubHandler(async request =>
        {
            sent.Add(await request.Content!.ReadAsStringAsync());
            return sent.Count == 1
                ? StreamResponse(
                    """
                    event: step.start
                    data: {"event_type":"step.start","index":0,"step":{"type":"thought"}}

                    event: step.delta
                    data: {"event_type":"step.delta","index":0,"delta":{"type":"thought_signature","signature":"secret-signature"}}

                    event: step.start
                    data: {"event_type":"step.start","index":1,"step":{"type":"model_output"}}

                    event: step.delta
                    data: {"event_type":"step.delta","index":1,"delta":{"type":"text","text":"eight"}}

                    event: interaction.completed
                    data: {"event_type":"interaction.completed","interaction":{"id":"int_first","status":"completed","steps":[{"type":"thought","signature":"secret-signature","summary":[]},{"type":"model_output","content":[{"type":"text","text":"eight"}]}]}}

                    """)
                : StreamResponse(
                    """
                    event: step.start
                    data: {"event_type":"step.start","index":0,"step":{"type":"model_output"}}

                    event: step.delta
                    data: {"event_type":"step.delta","index":0,"delta":{"type":"text","text":"four"}}

                    event: interaction.completed
                    data: {"event_type":"interaction.completed","interaction":{"id":"int_second","status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"four"}]}]}}

                    """);
        });
        var client = CreateClient(handler);
        var first = await client.GenerateAsync(new LlmRequest("gemini-3.8-flash",
            [new LlmMessage(LlmMessageRole.System, "Keep the same rules."),
             new LlmMessage(LlmMessageRole.User, "How many paws?")]));
        var continuation = Assert.IsType<LlmNativeContinuation>(first.NativeContinuation);
        Assert.Equal("eight", first.Text);
        Assert.DoesNotContain("secret-signature", JsonSerializer.Serialize(first));
        Assert.DoesNotContain("secret-signature", continuation.ToString());

        var followUp = new LlmRequest("gemini-3.8-flash",
            [new LlmMessage(LlmMessageRole.System, "Keep the same rules."),
             new LlmMessage(LlmMessageRole.User, "How many dogs?")],
            NativeContinuation: continuation);
        Assert.DoesNotContain("secret-signature", JsonSerializer.Serialize(followUp));
        var second = await client.GenerateAsync(followUp);

        Assert.Equal("four", second.Text);
        Assert.Equal(2, sent.Count);
        using var next = JsonDocument.Parse(sent[1]);
        var body = next.RootElement;
        Assert.False(body.GetProperty("store").GetBoolean());
        var steps = body.GetProperty("input");
        Assert.Equal(4, steps.GetArrayLength());
        Assert.Equal("user_input", steps[0].GetProperty("type").GetString());
        Assert.Equal("secret-signature", steps[1].GetProperty("signature").GetString());
        Assert.Equal("model_output", steps[2].GetProperty("type").GetString());
        Assert.Equal("How many dogs?", steps[3].GetProperty("content")[0]
            .GetProperty("text").GetString());
    }

    [Fact]
    public async Task Planner_repair_replays_signed_steps_through_stateless_interactions()
    {
        const string invalidJson = "{bad";
        const string repairedJson = """
            {"planId":"repaired","description":"Use the query.","steps":[{"stepId":"step_1","toolId":"query_state","kind":"Query","effect":"ReadOnly","input":{},"reason":"Inspect state."}],"completionCondition":"State was inspected."}
            """;
        static string Event(string name, object payload) =>
            $"event: {name}\ndata: {JsonSerializer.Serialize(payload)}\n\n";
        static object Output(string text) => new
        {
            type = "model_output",
            content = new[] { new { type = "text", text } }
        };
        var first = string.Concat(
            Event("step.start", new
            {
                event_type = "step.start",
                index = 0,
                step = new { type = "thought" }
            }),
            Event("step.delta", new
            {
                event_type = "step.delta",
                index = 0,
                delta = new { type = "thought_signature", signature = "private-signature" }
            }),
            Event("step.start", new
            {
                event_type = "step.start",
                index = 1,
                step = new { type = "model_output" }
            }),
            Event("step.delta", new
            {
                event_type = "step.delta",
                index = 1,
                delta = new { type = "text", text = invalidJson }
            }),
            Event("interaction.completed", new
            {
                event_type = "interaction.completed",
                interaction = new
                {
                    id = "int_invalid",
                    status = "completed",
                    steps = new object[]
                    {
                        new { type = "thought", signature = "private-signature",
                            summary = Array.Empty<object>() },
                        Output(invalidJson)
                    }
                }
            }));
        var second = string.Concat(
            Event("step.start", new
            {
                event_type = "step.start",
                index = 0,
                step = new { type = "model_output" }
            }),
            Event("step.delta", new
            {
                event_type = "step.delta",
                index = 0,
                delta = new { type = "text", text = repairedJson }
            }),
            Event("interaction.completed", new
            {
                event_type = "interaction.completed",
                interaction = new
                {
                    id = "int_repaired",
                    status = "completed",
                    steps = new[] { Output(repairedJson) }
                }
            }));
        var sent = new List<string>();
        var handler = new StubHandler(async request =>
        {
            sent.Add(await request.Content!.ReadAsStringAsync());
            return StreamResponse(sent.Count == 1 ? first : second);
        });
        var planner = new LlmWorkflowPlanner(CreateClient(handler),
            new LlmPlannerOptions(ModelId: "gemini-3.8-flash",
                InvalidJsonRepairAttempts: 1, StatelessRepair: true));

        var plan = await planner.CreatePlanAsync(new PlanningRequest(
            new RunRequest("Inspect state"), [], [], []));

        Assert.Equal("repaired", plan.PlanId);
        Assert.Equal(2, sent.Count);
        using var repair = JsonDocument.Parse(sent[1]);
        var body = repair.RootElement;
        Assert.False(body.GetProperty("store").GetBoolean());
        var input = body.GetProperty("input");
        Assert.Equal(4, input.GetArrayLength());
        Assert.Equal("private-signature", input[1].GetProperty("signature").GetString());
        Assert.Contains("previous Agentica planning response could not be parsed",
            input[3].GetProperty("content")[0].GetProperty("text").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-signature", sent[0],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Continuation_cannot_cross_model_or_instruction_boundary()
    {
        var handler = new StubHandler(_ => Task.FromResult(StreamResponse(
            """
            event: interaction.completed
            data: {"event_type":"interaction.completed","interaction":{"status":"completed","steps":[{"type":"model_output","content":[{"type":"text","text":"hello"}]}]}}

            """)));
        var client = CreateClient(handler);
        var first = await client.GenerateAsync(TextRequest());
        var continuation = Assert.IsType<LlmNativeContinuation>(first.NativeContinuation);
        var modelChanged = new LlmRequest("other-model",
            [new LlmMessage(LlmMessageRole.User, "next")],
            NativeContinuation: continuation);
        var instructionChanged = new LlmRequest("gemini-3.8-flash",
            [new LlmMessage(LlmMessageRole.System, "new instruction"),
             new LlmMessage(LlmMessageRole.User, "next")],
            NativeContinuation: continuation);

        Assert.Equal("continuation_binding_mismatch",
            (await Assert.ThrowsAsync<LlmClientException>(() =>
                client.GenerateAsync(modelChanged))).ErrorClass);
        Assert.Equal("continuation_binding_mismatch",
            (await Assert.ThrowsAsync<LlmClientException>(() =>
                client.GenerateAsync(instructionChanged))).ErrorClass);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Missing_terminal_signature_withholds_native_continuation()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse(
            """
            event: step.start
            data: {"event_type":"step.start","index":0,"step":{"type":"thought"}}

            event: step.delta
            data: {"event_type":"step.delta","index":0,"delta":{"type":"thought_signature","signature":"secret-signature"}}

            event: interaction.completed
            data: {"event_type":"interaction.completed","interaction":{"status":"completed","steps":[{"type":"thought","summary":[]},{"type":"model_output","content":[{"type":"text","text":"hello"}]}]}}

            """))));

        var response = await client.GenerateAsync(TextRequest());

        Assert.Null(response.NativeContinuation);
        Assert.Equal("False", response.Metadata?["gemini.continuation.available"]);
    }

    [Fact]
    public async Task Stream_steps_reconstruct_signed_continuation_when_terminal_omits_steps()
    {
        var client = CreateClient(new StubHandler(_ => Task.FromResult(StreamResponse(
            """
            event: step.start
            data: {"event_type":"step.start","index":0,"step":{"type":"thought","summary":[]}}

            event: step.delta
            data: {"event_type":"step.delta","index":0,"delta":{"type":"thought_signature","signature":"stream-only-signature"}}

            event: step.stop
            data: {"event_type":"step.stop","index":0}

            event: step.start
            data: {"event_type":"step.start","index":1,"step":{"type":"model_output","content":[]}}

            event: step.delta
            data: {"event_type":"step.delta","index":1,"delta":{"type":"text","text":"hello"}}

            event: step.stop
            data: {"event_type":"step.stop","index":1}

            event: interaction.completed
            data: {"event_type":"interaction.completed","interaction":{"status":"completed"}}

            """))));

        var response = await client.GenerateAsync(TextRequest());

        Assert.Equal("hello", response.Text);
        var continuation = Assert.IsType<LlmNativeContinuation>(response.NativeContinuation);
        Assert.Equal("True", response.Metadata?["gemini.continuation.available"]);
        Assert.DoesNotContain("stream-only-signature", JsonSerializer.Serialize(response));

        var body = GeminiInteractionsLlmClient.BuildRequestBody(new LlmRequest(
            "gemini-3.8-flash", [new LlmMessage(LlmMessageRole.User, "next")],
            NativeContinuation: continuation), "gemini-3.8-flash");
        using var sent = JsonDocument.Parse(JsonSerializer.Serialize(body));
        Assert.Equal("stream-only-signature", sent.RootElement.GetProperty("input")[1]
            .GetProperty("signature").GetString());
        Assert.Equal("hello", sent.RootElement.GetProperty("input")[2]
            .GetProperty("content")[0].GetProperty("text").GetString());
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
