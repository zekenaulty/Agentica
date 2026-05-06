using Agentica.Artifacts;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class RuntimeHarnessGapTests
{
    [Fact]
    public async Task Tool_input_schema_rejects_missing_required_input_before_execution()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor(
                "maze.move",
                "Maze Move",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                InputSchema: ToolInputSchema.Create(new ToolInputField(
                    "direction",
                    Required: true,
                    AllowedValues: ["north", "south", "east", "west"]))),
            tool));
        var runner = CreateRunner(new StaticPlanner(Plan(Step("step_001", "maze.move", ToolKind.Action, ToolEffect.WritesLocalState))), catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.input.required");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Tool_input_schema_rejects_invalid_enum_extra_input_and_range_before_execution()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor(
                "maze.sense_objective",
                "Maze Sense Objective",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                InputSchema: new ToolInputSchema(
                [
                    new ToolInputField("objectiveId", Required: true, AllowedValues: ["sun_key", "sun_gate"]),
                    new ToolInputField("radius", ToolInputValueType.Integer, Required: true, Minimum: 1, Maximum: 4)
                ])),
            tool));
        var runner = CreateRunner(new StaticPlanner(Plan(Step(
            "step_001",
            "maze.sense_objective",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            ("objectiveId", "exit"),
            ("radius", 9),
            ("path", "north,east,east")))), catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.input.enum");
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.input.range");
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.input.unknown");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Default_effect_policy_rejects_destructive_tool_effect_before_execution()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("maze.burn_gate", "Burn Gate", ToolKind.Action, ToolEffect.Destructive),
            tool));
        var runner = CreateRunner(new StaticPlanner(Plan(Step(
            "step_001",
            "maze.burn_gate",
            ToolKind.Action,
            ToolEffect.Destructive))), catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.effect_not_allowed");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Completion_evidence_gate_blocks_plan_exhaustion_without_required_artifact()
    {
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("maze.move", "Maze Move", ToolKind.Action, ToolEffect.WritesLocalState),
            new CountingTool()));
        var runner = CreateRunner(
            new StaticPlanner(Plan(Step("step_001", "maze.move", ToolKind.Action, ToolEffect.WritesLocalState))),
            catalog,
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("maze.objective_completed", continueWhenMissing: false));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.CompletionNotSatisfied, envelope.Outcome.StopReason);
        Assert.DoesNotContain("succeeded", envelope.Report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Single(envelope.Receipts.Items);
    }

    [Fact]
    public async Task Plan_exhaustion_can_request_bounded_continuation_until_completion_evidence_exists()
    {
        var planner = new ContinuationPlanner();
        var catalog = ToolCatalog.Create(
            new ToolRegistration(
                new ToolDescriptor("maze.scan", "Maze Scan", ToolKind.Action, ToolEffect.WritesLocalState),
                new ObservationTool()),
            new ToolRegistration(
                new ToolDescriptor("maze.complete_objective", "Maze Complete Objective", ToolKind.Action, ToolEffect.WritesLocalState),
                new ArtifactTool("maze.objective_completed")));
        var runner = CreateRunner(
            planner,
            catalog,
            policy: new ExecutionPolicy(
                MaxSteps: 5,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxPlanContinuations: 1,
                PlanningContext: new PlanningContextOptions(MaxRecentObservations: 1, MaxRecentReceipts: 1)),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("maze.objective_completed"));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(2, envelope.Details.PlanVersions.Count);
        Assert.Equal(2, planner.CreatePlanCount);
        Assert.Equal(1, planner.SecondRequestObservationCount);
        Assert.Equal(1, planner.SecondRequestReceiptCount);
        Assert.Contains(envelope.Details.Artifacts, artifact => artifact.Kind == "maze.objective_completed");
    }

    [Fact]
    public async Task Blocked_run_retries_with_blocked_context_and_can_succeed()
    {
        var planner = new RetryAwarePlanner();
        var blockedTool = new CountingBlockedTool("Initial blocker.");
        var completeTool = new ArtifactTool("maze.objective_completed");
        var catalog = ToolCatalog.Create(
            new ToolRegistration(
                new ToolDescriptor("maze.probe", "Maze Probe", ToolKind.Query, ToolEffect.ReadOnly),
                blockedTool),
            new ToolRegistration(
                new ToolDescriptor("maze.complete_objective", "Maze Complete Objective", ToolKind.Action, ToolEffect.WritesLocalState),
                completeTool));
        var runner = CreateRunner(
            planner,
            catalog,
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("maze.objective_completed", continueWhenMissing: false));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(2, planner.CreatePlanCount);
        Assert.Equal(1, blockedTool.ExecutionCount);
        Assert.Equal(2, envelope.Details.RunAttempts.Count);
        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Details.RunAttempts[0].Status);
        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Details.RunAttempts[1].Status);
        Assert.Equal(RequestOrigin.Agent, envelope.Details.Request.Origin);

        var retryContext = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(planner.LastRetryContext);
        Assert.Equal(2, retryContext["attemptNumber"]);
        Assert.Equal(2, retryContext["maxBlockedRetries"]);

        var previousAttempt = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(retryContext["previousAttempt"]);
        Assert.Equal(nameof(RunOutcomeStatus.Blocked), previousAttempt["status"]);
        Assert.Equal(nameof(StopReason.ToolUnavailable), previousAttempt["stopReason"]);
        Assert.Contains("Initial blocker.", Assert.IsAssignableFrom<IReadOnlyList<string>>(previousAttempt["blockers"]));
    }

    [Fact]
    public async Task Blocked_retry_context_uses_the_latest_blocker_when_retry_blocks_again()
    {
        var planner = new AlwaysProbePlanner();
        var blockedTool = new SequenceBlockedTool("First blocker.", "Second blocker.", "Third blocker.");
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("maze.probe", "Maze Probe", ToolKind.Query, ToolEffect.ReadOnly),
            blockedTool));
        var runner = CreateRunner(planner, catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(3, planner.CreatePlanCount);
        Assert.Equal(3, envelope.Details.RunAttempts.Count);

        var finalRetryContext = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(planner.RetryContexts[^1]);
        var previousAttempt = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(finalRetryContext["previousAttempt"]);
        var blockers = Assert.IsAssignableFrom<IReadOnlyList<string>>(previousAttempt["blockers"]);

        Assert.Contains("Second blocker.", blockers);
        Assert.DoesNotContain("First blocker.", blockers);
    }

    private static RunRequest Request() =>
        new("Navigate the host-provided test surface.", RequestOrigin.User);

    private static AgenticaRunner CreateRunner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        ExecutionPolicy? policy = null,
        ICompletionEvaluator? completionEvaluator = null) =>
        new(
            planner,
            catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            policy ?? new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 2),
            completionEvaluator);

    private static WorkflowPlan Plan(params PlanStep[] steps) =>
        new($"plan_{Guid.NewGuid():N}", 1, steps, "Runtime harness gap test plan.");

    private static PlanStep Step(
        string stepId,
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        params (string Key, object? Value)[] input) =>
        new(
            stepId,
            toolId,
            kind,
            effect,
            input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private sealed class StaticPlanner : IWorkflowPlanner
    {
        private readonly WorkflowPlan _plan;

        public StaticPlanner(WorkflowPlan plan)
        {
            _plan = plan;
        }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_plan);

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class ContinuationPlanner : IWorkflowPlanner
    {
        public int CreatePlanCount { get; private set; }

        public int SecondRequestObservationCount { get; private set; }

        public int SecondRequestReceiptCount { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatePlanCount++;
            if (CreatePlanCount == 1)
            {
                return Task.FromResult(Plan(
                    Step("step_001", "maze.scan", ToolKind.Action, ToolEffect.WritesLocalState),
                    Step("step_002", "maze.scan", ToolKind.Action, ToolEffect.WritesLocalState)));
            }

            SecondRequestObservationCount = request.Observations.Count;
            SecondRequestReceiptCount = request.Receipts.Count;
            return Task.FromResult(Plan(Step(
                "step_003",
                "maze.complete_objective",
                ToolKind.Action,
                ToolEffect.WritesLocalState)));
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class RetryAwarePlanner : IWorkflowPlanner
    {
        public int CreatePlanCount { get; private set; }

        public object? LastRetryContext { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatePlanCount++;
            if (request.Request.Context?.TryGetValue("agentica.retry", out var retryContext) == true)
            {
                LastRetryContext = retryContext;
                return Task.FromResult(Plan(Step(
                    "step_retry_complete",
                    "maze.complete_objective",
                    ToolKind.Action,
                    ToolEffect.WritesLocalState)));
            }

            return Task.FromResult(Plan(Step(
                "step_initial_probe",
                "maze.probe",
                ToolKind.Query,
                ToolEffect.ReadOnly)));
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class AlwaysProbePlanner : IWorkflowPlanner
    {
        public int CreatePlanCount { get; private set; }

        public List<object?> RetryContexts { get; } = [];

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatePlanCount++;
            if (request.Request.Context?.TryGetValue("agentica.retry", out var retryContext) == true)
            {
                RetryContexts.Add(retryContext);
            }

            return Task.FromResult(Plan(Step(
                $"step_probe_{CreatePlanCount}",
                "maze.probe",
                ToolKind.Query,
                ToolEffect.ReadOnly)));
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class CountingTool : ITool
    {
        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(Receipt(invocation, ReceiptStatus.Succeeded, "Tool executed.")));
        }
    }

    private sealed class ObservationTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Observation tool executed.");
            return Task.FromResult(new ToolResult(
                receipt,
                new Observation(
                    AgenticaIds.New("observation"),
                    invocation.StepId,
                    ObservationKind.ToolResult,
                    "Observed host state.",
                    new Dictionary<string, object?>
                    {
                        ["step"] = invocation.StepId
                    },
                    [new EvidenceRef("receipt", receipt.ReceiptId)])));
        }
    }

    private sealed class ArtifactTool : ITool
    {
        private readonly string _artifactKind;

        public ArtifactTool(string artifactKind)
        {
            _artifactKind = artifactKind;
        }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Objective completed.");
            return Task.FromResult(new ToolResult(
                receipt,
                Artifact: new Artifact(
                    AgenticaIds.New("artifact"),
                    _artifactKind,
                    new Dictionary<string, object?>
                    {
                        ["completed"] = true
                    },
                    [new EvidenceRef("receipt", receipt.ReceiptId)])));
        }
    }

    private sealed class CountingBlockedTool : ITool
    {
        private readonly string _message;

        public CountingBlockedTool(string message)
        {
            _message = message;
        }

        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(Receipt(invocation, ReceiptStatus.Unavailable, _message)));
        }
    }

    private sealed class SequenceBlockedTool : ITool
    {
        private readonly Queue<string> _messages;

        public SequenceBlockedTool(params string[] messages)
        {
            _messages = new Queue<string>(messages);
        }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var message = _messages.Count == 0 ? "Still blocked." : _messages.Dequeue();
            return Task.FromResult(new ToolResult(Receipt(invocation, ReceiptStatus.Unavailable, message)));
        }
    }

    private static Receipt Receipt(ToolInvocation invocation, ReceiptStatus status, string message) =>
        new(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            status,
            message,
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());
}
