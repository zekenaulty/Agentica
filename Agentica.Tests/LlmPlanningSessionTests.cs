using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Planning;
using Agentica.Clients.Xai;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class LlmPlanningSessionTests
{
    private const string Model = "fixture-model";
    private const string Secret = "private-native-carrier";
    private const string InitialPlan = """
        {"planId":"initial","description":"Inspect the host state.","steps":[{"stepId":"inspect","toolId":"query_state","kind":"Query","effect":"ReadOnly","input":{},"reason":"Learn state."}],"completionCondition":"The mark exists."}
        """;
    private const string RefinedPlan = """
        {"fromPlanId":"initial","reason":"observation","evidence":[],"refinedPlan":{"planId":"refined","description":"Mark the inspected state.","steps":[{"stepId":"mark","toolId":"mark_state","kind":"Action","effect":"WritesLocalState","input":{},"reason":"State is ready."}],"completionCondition":"The mark exists."}}
        """;

    [Theory]
    [InlineData("gemini")]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    public async Task Runner_preserves_native_reasoning_through_governed_work_and_disposes_it(string provider)
    {
        var sent = new List<string>();
        using var http = new HttpClient(new Handler(async message =>
        {
            sent.Add(await message.Content!.ReadAsStringAsync());
            return Response(provider, sent.Count == 1 ? InitialPlan : RefinedPlan);
        }));
        ILlmClient adapter = provider switch
        {
            "gemini" => new GeminiInteractionsLlmClient(new("key", Model), http),
            "openai" => new OpenAiResponsesLlmClient(new("key", Model), http),
            "xai" => new XaiResponsesLlmClient(new("key", Model), http),
            "anthropic" => new AnthropicMessagesLlmClient(new("key", Model), http),
            "ollama" => new OllamaLlmClient(new(DefaultModelId: Model), http),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var client = new RecordingClient(adapter);
        var action = new MarkTool();
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(new("query_state", "Inspect", ToolKind.Query,
                ToolEffect.ReadOnly), new QueryStateTool()),
            TestToolRegistration.Create(new("mark_state", "Mark", ToolKind.Action,
                ToolEffect.WritesLocalState), action));
        var planner = new LlmWorkflowPlanner(client, Options());
        var runner = new AgenticaRunner(planner, catalog, new InMemoryEventSink(),
            new DeterministicOutcomeReporter(), new ExecutionPolicy(MaxBlockedRetries: 0,
                SecurityPolicy: new ToolSecurityPolicy(ExternalPlannerAllowedBoundaries:
                    [ToolDataBoundary.UserContent, ToolDataBoundary.HostState])),
            new MarkCompletion(action));

        var envelope = await runner.RunAsync(new RunRequest("Inspect and mark one state.",
            AuthorizationScopeId: "scope-one"));

        Assert.True(envelope.Outcome.Status == RunOutcomeStatus.Succeeded,
            JsonSerializer.Serialize(envelope.Outcome));
        Assert.Equal(1, action.Count);
        Assert.Equal(2, sent.Count);
        Assert.DoesNotContain(Secret, sent[0]);
        Assert.Contains(Secret, sent[1]);
        var receipt = Assert.Single(envelope.Receipts.Items, item => item.ToolId == "query_state");
        var observation = Assert.Single(envelope.Details.Observations);
        using var body = JsonDocument.Parse(sent[1]);
        var input = body.RootElement.GetProperty(provider is "anthropic" or "ollama" ? "messages" : "input");
        var latest = input[input.GetArrayLength() - 1].GetProperty("content");
        var latestText = provider == "gemini" ? latest[0].GetProperty("text").GetString() : latest.GetString();
        Assert.Contains(receipt.ReceiptId, latestText);
        Assert.Contains(observation.ObservationId, latestText);
        Assert.Contains("stateReady", latestText);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(envelope));
        Assert.All(client.Carriers, carrier => Assert.Throws<ObjectDisposedException>(() => carrier.HistoryStepsJson));
        Assert.Equal(2, client.Carriers.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_history_exceeding_character_or_token_budget_recompiles_cleanly(bool tokens)
    {
        var client = new FixtureClient(tokens ? 100 : 17_000);
        var options = Options() with { MaxInputCharacters = 20_000 };
        if (tokens)
            options = options with
            {
                ContextWindowBudget = new LlmContextWindowBudget(1000, 100, 0, 0, 0),
                InputTokenEstimator = new NativeTokenEstimator()
            };
        var planner = new LlmWorkflowPlanner(client, options);
        var context = Context("run-one");
        using var session = planner.BeginSession(context);
        await session.CreatePlanAsync(Request(context));
        var first = client.Carriers[0];
        await session.CreatePlanAsync(Request(context));

        Assert.Null(client.Requests[1].NativeContinuation);
        Assert.Equal("budget_reset", client.Requests[1].Metadata!["agentica.planner.continuation"]);
        Assert.Throws<ObjectDisposedException>(() => first.HistoryStepsJson);
        Assert.Contains(client.Requests[1].InputCompilationReceipt!.Decisions,
            decision => decision.Kind == "nativeContinuation" && !decision.Included);
    }

    [Fact]
    public async Task Separate_sessions_never_share_carriers_and_binding_mismatches_never_dispatch()
    {
        var client = new FixtureClient();
        var planner = new LlmWorkflowPlanner(client, Options());
        var firstContext = Context("run-one");
        var secondContext = Context("run-two");
        using var first = planner.BeginSession(firstContext);
        using var second = planner.BeginSession(secondContext);
        Assert.IsAssignableFrom<IExternalWorkflowPlanner>(first);
        await first.CreatePlanAsync(Request(firstContext));
        await second.CreatePlanAsync(Request(secondContext));
        Assert.All(client.Requests, request => Assert.Null(request.NativeContinuation));
        await Assert.ThrowsAsync<LlmPlannerException>(() => first.CreatePlanAsync(Request(secondContext)));
        await Assert.ThrowsAsync<LlmPlannerException>(() => first.CreatePlanAsync(Request(firstContext) with
        {
            Request = new RunRequest("A different objective", AuthorizationScopeId: "scope-one")
        }));
        await Assert.ThrowsAsync<LlmPlannerException>(() => first.CreatePlanAsync(Request(firstContext) with
        {
            Request = new RunRequest(firstContext.Objective, AuthorizationScopeId: "scope-two")
        }));
        Assert.Equal(2, client.Requests.Count);
        await first.CreatePlanAsync(Request(firstContext));
        Assert.Same(client.Carriers[0], client.Requests[2].NativeContinuation);
        Assert.Throws<ObjectDisposedException>(() => client.Carriers[0].HistoryStepsJson);
        Assert.NotEmpty(client.Carriers[1].HistoryStepsJson);
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => client.Carriers[2].HistoryStepsJson);
        Assert.NotEmpty(client.Carriers[1].HistoryStepsJson);
    }

    [Fact]
    public async Task Cancellation_releases_the_retained_carrier()
    {
        var client = new FixtureClient();
        var context = Context("run-one");
        using var session = new LlmWorkflowPlanner(client, Options()).BeginSession(context);
        await session.CreatePlanAsync(Request(context));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.CreatePlanAsync(Request(context), cancelled.Token));
        Assert.Throws<ObjectDisposedException>(() => client.Carriers[0].HistoryStepsJson);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Successful_repair_replaces_the_session_carrier_for_the_next_real_turn()
    {
        var client = new FixtureClient { InvalidFirst = true };
        var context = Context("run-one");
        using var session = new LlmWorkflowPlanner(client,
            Options() with { InvalidJsonRepairAttempts = 1 }).BeginSession(context);
        await session.CreatePlanAsync(Request(context));
        Assert.Throws<ObjectDisposedException>(() => client.Carriers[0].HistoryStepsJson);
        Assert.NotEmpty(client.Carriers[1].HistoryStepsJson);
        await session.CreatePlanAsync(Request(context));
        Assert.Same(client.Carriers[1], client.Requests[2].NativeContinuation);
        Assert.Throws<ObjectDisposedException>(() => client.Carriers[1].HistoryStepsJson);
        Assert.NotEmpty(client.Carriers[2].HistoryStepsJson);
    }

    [Fact]
    public async Task External_session_cannot_hide_behind_a_local_factory()
    {
        var factory = new ExternalSessionFactory();
        var runner = new AgenticaRunner(factory, DemoTools.CreateCatalog(), new InMemoryEventSink(),
            new DeterministicOutcomeReporter(), new ExecutionPolicy(MaxBlockedRetries: 0),
            PlanExhaustionCompletionEvaluator.Instance);

        var outcome = await runner.RunAsync(new RunRequest("Inspect state."));

        Assert.Equal(StopReason.PlannerDataBoundaryDenied, outcome.Outcome.StopReason);
        Assert.True(factory.Session.Disposed);
        Assert.False(factory.Session.Called);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("instruction")]
    public async Task Foreign_response_carrier_never_reaches_the_next_dispatch(string mismatch)
    {
        var client = new FixtureClient { Mismatch = mismatch };
        var context = Context("run-one");
        using var session = new LlmWorkflowPlanner(client, Options()).BeginSession(context);
        await session.CreatePlanAsync(Request(context));
        Assert.Throws<ObjectDisposedException>(() => client.Carriers[0].HistoryStepsJson);
        await session.CreatePlanAsync(Request(context));
        Assert.Null(client.Requests[1].NativeContinuation);
    }

    private static LlmPlannerOptions Options() => new(ModelId: Model,
        GenerationOptions: new LlmGenerationOptions(MaxOutputTokens: 4096),
        InvalidJsonRepairAttempts: 0, StatelessRepair: true);

    private static PlanningSessionContext Context(string id) =>
        new(id, "Inspect state", RequestOrigin.User, "scope-one");

    private static PlanningRequest Request(PlanningSessionContext context) =>
        new(new RunRequest(context.Objective, context.Origin,
            AuthorizationScopeId: context.AuthorizationScopeId), [], [], [])
        { SessionContext = context };

    private sealed class FixtureClient(int padding = 50) : ILlmClient
    {
        public string? Mismatch { get; init; }
        public bool InvalidFirst { get; init; }
        public List<LlmRequest> Requests { get; } = [];
        public List<LlmNativeContinuation> Carriers { get; } = [];
        public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var instruction = request.Messages.Single(message => message.Role == LlmMessageRole.System).Content;
            var carrier = new LlmNativeContinuation(Mismatch == "provider" ? "foreign" : "fixture",
                Mismatch == "model" ? "foreign-model" : Model,
                Mismatch == "instruction" ? "foreign instruction" : instruction,
                JsonSerializer.Serialize(new[] { new { type = "private", data = new string('x', padding) } }));
            Carriers.Add(carrier);
            return Task.FromResult(new LlmResponse("fixture", Model,
                InvalidFirst && Requests.Count == 1 ? "{invalid" : InitialPlan,
                FinishReason: LlmFinishReason.Stop, NativeContinuation: carrier));
        }
    }

    private sealed class NativeTokenEstimator : ILlmInputTokenEstimator
    {
        public string Name => "native-sensitive-fixture";
        public long EstimateTokens(LlmRequest request) => request.NativeContinuation is null ? 100 : 2000;
    }

    private sealed class ExternalSessionFactory : IWorkflowPlanner, IWorkflowPlannerSessionFactory
    {
        public ExternalSession Session { get; } = new();
        public IWorkflowPlannerSession BeginSession(PlanningSessionContext context) => Session;
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Runner must use the session.");
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Runner must use the session.");
    }

    private sealed class ExternalSession : IWorkflowPlannerSession, IExternalWorkflowPlanner
    {
        public bool Disposed { get; private set; }
        public bool Called { get; private set; }
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
        {
            Called = true;
            throw new InvalidOperationException("External dispatch must be denied.");
        }
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation,
            CancellationToken cancellationToken = default) => CreatePlanAsync(request, cancellationToken);
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingClient(ILlmClient inner) : ILlmClient
    {
        public List<LlmNativeContinuation> Carriers { get; } = [];
        public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            var result = await inner.GenerateAsync(request, cancellationToken);
            if (result.NativeContinuation is { } carrier) Carriers.Add(carrier);
            return result;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private sealed class MarkTool : ITool
    {
        public int Count { get; private set; }
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new ToolResult(new Receipt("mark-receipt", invocation.StepId,
                invocation.ToolId, ReceiptStatus.Succeeded, "Host mark created.", DateTimeOffset.UtcNow,
                new Dictionary<string, object?> { ["marks"] = Count })));
        }
    }

    private sealed class MarkCompletion(MarkTool tool) : ICompletionEvaluator
    {
        public CompletionEvaluation Evaluate(CompletionContext context)
        {
            var receipt = context.Receipts.SingleOrDefault(value => value.ToolId == "mark_state");
            return tool.Count == 1 && receipt?.Status == ReceiptStatus.Succeeded
                ? CompletionEvaluation.Complete(new EvidenceRef("receipt", receipt.ReceiptId))
                : CompletionEvaluation.Continue("Host mark is missing.");
        }
    }

    private static string Event(string name, object payload) =>
        $"event: {name}\ndata: {JsonSerializer.Serialize(payload)}\n\n";

    private static HttpResponseMessage Response(string provider, string text)
    {
        string stream;
        if (provider is "openai" or "xai")
            stream = Event("response.completed", new
            {
                type = "response.completed",
                response = new
                {
                    status = "completed",
                    output = new object[]
                    {
                        new { type = "reasoning", encrypted_content = Secret },
                        new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text } } }
                    }
                }
            });
        else if (provider == "gemini")
            stream = Event("step.start", new
            {
                event_type = "step.start",
                index = 1,
                step = new { type = "model_output" }
            }) +
                Event("step.delta", new
                {
                    event_type = "step.delta",
                    index = 1,
                    delta = new { type = "text", text }
                }) +
                Event("interaction.completed", new
                {
                    event_type = "interaction.completed",
                    interaction = new
                    {
                        status = "completed",
                        steps = new object[]
                        {
                            new { type = "thought", signature = Secret },
                            new { type = "model_output", content = new[] { new { type = "text", text } } }
                        }
                    }
                });
        else if (provider == "ollama")
            stream = JsonSerializer.Serialize(new { model = Model, message = new { content = text, thinking = Secret }, done = true, done_reason = "stop" }) + "\n";
        else
            stream = Event("message_start", new { type = "message_start", message = new { id = "message" } }) +
                Event("content_block_start", new
                {
                    type = "content_block_start",
                    index = 0,
                    content_block = new { type = "thinking", thinking = "", signature = Secret }
                }) +
                Event("content_block_stop", new { type = "content_block_stop", index = 0 }) +
                Event("content_block_start", new
                {
                    type = "content_block_start",
                    index = 1,
                    content_block = new { type = "text", text = "" }
                }) +
                Event("content_block_delta", new
                {
                    type = "content_block_delta",
                    index = 1,
                    delta = new { type = "text_delta", text }
                }) +
                Event("content_block_stop", new { type = "content_block_stop", index = 1 }) +
                Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" } }) +
                Event("message_stop", new { type = "message_stop" });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, provider == "ollama" ? "application/x-ndjson" : "text/event-stream")
        };
    }
}
