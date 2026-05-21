using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Execution;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Orchestration.Tests;

public sealed class TaskOrchestratorTests
{
    [Fact]
    public async Task Orchestrator_executes_single_node_pass_through_graph()
    {
        var task = Task("direct", "Do the direct task.");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor([Envelope("run_direct", RunOutcomeStatus.Succeeded)]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(TaskAcceptanceStatus.Accepted, [], [new EvidenceRef("artifact", "artifact_run_direct")]));
        var orchestrator = CreateOrchestrator(planner, executor, evaluator);

        var outcome = await orchestrator.RunAsync(Request("Do a small thing."));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Complete, outcome.StopReason);
        Assert.Equal(["direct"], outcome.State.CompletedTaskIds);
        Assert.Single(executor.Requests);
        Assert.Equal("Do the direct task.", executor.Requests[0].Objective);
        Assert.NotNull(executor.Requests[0].Context);
        Assert.True(executor.Requests[0].Context!.ContainsKey("orchestration.workingContext"));
    }

    [Fact]
    public void Graph_validator_rejects_cycles_and_dangling_dependencies()
    {
        var cyclic = Plan(
        [
            Task("a", dependsOn: ["b"]),
            Task("b", dependsOn: ["a"])
        ]);
        var dangling = Plan([Task("a", dependsOn: ["missing"])]);

        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(cyclic));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(dangling));
    }

    [Fact]
    public async Task Orchestrator_refines_graph_when_successful_run_invalidates_plan()
    {
        var inspect = Task("inspect", "Inspect the model.");
        var implement = Task("implement", "Implement persistence.", dependsOn: ["inspect"]);
        var initialPlan = Plan([inspect, implement]);
        var design = Task("design_attempts", "Design execution attempt model.", dependsOn: ["inspect"], priority: 2);
        var revisedImplement = implement with
        {
            DependsOn = ["inspect", "design_attempts"],
            Priority = 3,
            Objective = "Implement persistence after execution attempts are modeled."
        };
        var refinement = new TaskGraphRefinement(
            "Execution attempts must be modeled first.",
            [
                new TaskGraphMutation(TaskGraphMutationKind.AddTask, design.TaskId, Task: design),
                new TaskGraphMutation(TaskGraphMutationKind.ReplaceTask, implement.TaskId, Task: revisedImplement)
            ],
            [],
            RequiresUserInput: false);
        var planner = new ScriptedTaskPlanner(initialPlan, [refinement]);
        var executor = new ScriptedRunExecutor(
        [
            Envelope("run_inspect", RunOutcomeStatus.Succeeded),
            Envelope("run_implement_invalidated", RunOutcomeStatus.Succeeded),
            Envelope("run_design", RunOutcomeStatus.Succeeded),
            Envelope("run_implement", RunOutcomeStatus.Succeeded)
        ]);
        var invalidatedImplement = false;
        var evaluator = new ScriptedAcceptanceEvaluator(task =>
        {
            if (task.TaskId == "implement" && !invalidatedImplement)
            {
                invalidatedImplement = true;
                return new TaskAcceptanceResult(
                    TaskAcceptanceStatus.InvalidatedPlan,
                    ["Persistence schema depends on execution attempt modeling."],
                    [new EvidenceRef("artifact", "artifact_run_implement_invalidated")]);
            }

            return new TaskAcceptanceResult(
                TaskAcceptanceStatus.Accepted,
                [],
                [new EvidenceRef("artifact", $"artifact_{task.TaskId}")]);
        });
        var orchestrator = CreateOrchestrator(planner, executor, evaluator);

        var outcome = await orchestrator.RunAsync(Request("Build durable run persistence."));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Equal(1, planner.RefineCalls);
        Assert.Equal(["inspect", "design_attempts", "implement"], outcome.State.CompletedTaskIds);
        Assert.Equal(["Inspect the model.", "Implement persistence.", "Design execution attempt model.", "Implement persistence after execution attempts are modeled."],
            executor.Requests.Select(request => request.Objective));
        Assert.Contains(outcome.WorkingContext.PlanImpacts, impact =>
            impact.Kind == PlanImpactKind.NewDependencyDiscovered &&
            impact.Summary.Contains("execution attempt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_blocks_task_after_per_task_max_runs()
    {
        var task = Task("retry", "Attempt bounded work.", maxRuns: 1);
        var followUp = Task("follow_up", "Follow up after retry succeeds.", dependsOn: ["retry"]);
        var refinement = new TaskGraphRefinement(
            "The first attempt produced partial evidence but still needs another run.",
            [new TaskGraphMutation(TaskGraphMutationKind.AddTask, followUp.TaskId, Task: followUp)],
            [],
            RequiresUserInput: false);
        var planner = new ScriptedTaskPlanner(Plan([task]), [refinement]);
        var executor = new ScriptedRunExecutor([Envelope("run_retry_first", RunOutcomeStatus.Succeeded)]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                ["More evidence is needed."],
                [new EvidenceRef("artifact", "artifact_run_retry_first")]));
        var orchestrator = CreateOrchestrator(planner, executor, evaluator);

        var outcome = await orchestrator.RunAsync(Request("Respect per-task run budget."));

        Assert.Equal(OrchestrationStatus.Blocked, outcome.Status);
        Assert.Equal(OrchestrationStopReason.MaxRunsReached, outcome.StopReason);
        Assert.Equal(1, planner.RefineCalls);
        Assert.Single(executor.Requests);
        Assert.Equal(1, outcome.State.TaskRunCounts["retry"]);
        Assert.Contains("retry", outcome.State.BlockedTaskIds);
        Assert.Contains(outcome.WorkingContext.KnownBlockers, blocker =>
            blocker.Contains("maxRuns", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_preserves_child_plan_invalid_when_refinement_budget_is_exhausted()
    {
        var task = Task("phase", "Run a child phase.");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor(
        [
            Envelope("run_phase_invalid", RunOutcomeStatus.PlanInvalid, StopReason.PlanInvalid)
        ]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                ["Child run produced an invalid plan and would need repair."],
                [],
                RequiresGraphRefinement: true));
        var orchestrator = new TaskOrchestrator(
            planner,
            executor,
            evaluator,
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>(),
            new OrchestrationPolicy(MaxRuns: 1, MaxRefinements: 0));

        var outcome = await orchestrator.RunAsync(Request("Preserve child plan-invalid failure."));

        Assert.Equal(OrchestrationStatus.PlanInvalid, outcome.Status);
        Assert.Equal(OrchestrationStopReason.PlanInvalid, outcome.StopReason);
        Assert.NotEqual(OrchestrationStopReason.MaxRefinementsReached, outcome.StopReason);
        Assert.Single(outcome.RunOutcomes);
        Assert.Equal(RunOutcomeStatus.PlanInvalid, outcome.RunOutcomes[0].Outcome.Status);
    }

    [Fact]
    public async Task Orchestrator_preserves_terminal_loss_child_stop_reason()
    {
        var task = Task("phase", "Run a terminal child phase.");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor(
        [
            Envelope("run_terminal_loss", RunOutcomeStatus.Failed, StopReason.TerminalLoss)
        ]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.Rejected,
                ["Child run reached terminal loss."],
                []));
        var orchestrator = CreateOrchestrator(planner, executor, evaluator);

        var outcome = await orchestrator.RunAsync(Request("Preserve terminal loss."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.TerminalLoss, outcome.StopReason);
    }

    [Fact]
    public async Task Orchestrator_preserves_child_planner_unavailable_when_refinement_budget_is_exhausted()
    {
        var task = Task("phase", "Run a child phase.");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor(
        [
            Envelope("run_planner_unavailable", RunOutcomeStatus.Blocked, StopReason.PlannerUnavailable)
        ]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                ["Child planner was unavailable and would need retry."],
                [],
                RequiresGraphRefinement: true));
        var orchestrator = new TaskOrchestrator(
            planner,
            executor,
            evaluator,
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>(),
            new OrchestrationPolicy(MaxRuns: 1, MaxRefinements: 0));

        var outcome = await orchestrator.RunAsync(Request("Preserve child planner-unavailable failure."));

        Assert.Equal(OrchestrationStatus.Blocked, outcome.Status);
        Assert.Equal(OrchestrationStopReason.PlannerUnavailable, outcome.StopReason);
        Assert.NotEqual(OrchestrationStopReason.MaxRefinementsReached, outcome.StopReason);
    }

    [Fact]
    public void Graph_validator_rejects_rewriting_completed_tasks()
    {
        var original = Plan([Task("done"), Task("next", dependsOn: ["done"])]);
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        state.CompletedTaskIds.Add("done");
        var rewritten = original with
        {
            Tasks =
            [
                Task("done", "A rewritten objective."),
                Task("next", dependsOn: ["done"])
            ]
        };

        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(rewritten, state, original));
    }

    [Fact]
    public async Task Evidence_acceptance_evaluator_uses_receipts_artifacts_status_and_host_state()
    {
        var task = Task("accepted") with
        {
            AcceptanceRequirements =
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Succeeded),
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "test.artifact"),
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Receipt, ToolId: "tool.test"),
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.HostState, HostStateKey: "hostReady", HostStateValue: true)
            ]
        };
        var plan = Plan([task]);
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        var context = new TaskAcceptanceContext(
            plan,
            state,
            state.WorkingContext,
            new Dictionary<string, object?> { ["hostReady"] = true });

        var result = await new EvidenceTaskAcceptanceEvaluator().EvaluateAsync(
            task,
            Envelope("run_acceptance", RunOutcomeStatus.Succeeded),
            context);

        Assert.Equal(TaskAcceptanceStatus.Accepted, result.Status);
        Assert.Contains(result.EvidenceRefs, evidence => evidence.Kind == "artifact");
        Assert.Contains(result.EvidenceRefs, evidence => evidence.Kind == "receipt");
    }

    [Fact]
    public async Task Evidence_acceptance_evaluator_does_not_accept_report_prose_as_proof()
    {
        var task = Task("accepted") with
        {
            AcceptanceRequirements =
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "missing.kind")
            ]
        };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        var result = await new EvidenceTaskAcceptanceEvaluator().EvaluateAsync(
            task,
            Envelope("run_claims_success", RunOutcomeStatus.Succeeded) with
            {
                Report = new OutcomeReport("report_claims_success", "The missing.kind artifact exists and proves completion.", [])
            },
            new TaskAcceptanceContext(Plan([task]), state, state.WorkingContext, new Dictionary<string, object?>()));

        Assert.NotEqual(TaskAcceptanceStatus.Accepted, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("Missing artifact kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Work_context_compiler_keeps_report_claims_out_of_proven_facts_and_records_plan_impacts()
    {
        var task = Task("implement");
        var plan = Plan([task]);
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        var acceptance = new TaskAcceptanceResult(
            TaskAcceptanceStatus.InvalidatedPlan,
            ["Report prose says success, but evidence shows a new dependency is required."],
            [new EvidenceRef("artifact", "artifact_run_context")]);

        var context = new DeterministicWorkContextCompiler().Compile(new WorkContextCompilationRequest(
            plan,
            state,
            task,
            Envelope("run_context", RunOutcomeStatus.Succeeded) with
            {
                Report = new OutcomeReport("report_context", "Unsupported claim should not become a proven fact.", [])
            },
            acceptance,
            null,
            new Dictionary<string, object?>()));

        Assert.DoesNotContain(context.ProvenFacts, fact =>
            fact.Summary.Contains("Unsupported claim", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(context.PlanImpacts, impact =>
            impact.Kind == PlanImpactKind.NewDependencyDiscovered);
    }

    private static TaskOrchestrator CreateOrchestrator(
        ITaskPlanner planner,
        IRunExecutor executor,
        ITaskAcceptanceEvaluator evaluator) =>
        new(
            planner,
            executor,
            evaluator,
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?> { ["hostReady"] = true },
            new OrchestrationPolicy(MaxRuns: 8, MaxRefinements: 4, MaxGraphMutationsPerRefinement: 4));

    private static LargeTaskRequest Request(string objective) =>
        new(objective, RequestOrigin.User, new Dictionary<string, object?>());

    private static TaskGraphPlan Plan(IReadOnlyList<TaskNode> tasks) =>
        new(
            "plan_test",
            "Test objective.",
            tasks,
            [],
            DateTimeOffset.UtcNow);

    private static TaskNode Task(
        string taskId,
        string? objective = null,
        IReadOnlyList<string>? dependsOn = null,
        int priority = 1,
        int maxRuns = 1) =>
        new(
            taskId,
            objective ?? $"Objective for {taskId}.",
            dependsOn ?? [],
            Optional: false,
            priority,
            maxRuns,
            new Dictionary<string, object?>(),
            [new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Succeeded)]);

    private static OutcomeEnvelope Envelope(
        string runId,
        RunOutcomeStatus status,
        StopReason? stopReason = null) =>
        new(
            new RunOutcome(
                runId,
                status,
                stopReason ?? (status == RunOutcomeStatus.Succeeded ? StopReason.Complete : StopReason.ToolFailure),
                [],
                [],
                [new EvidenceRef("artifact", $"artifact_{runId}")]),
            new OutcomeReport($"report_{runId}", $"Report for {runId}.", []),
            new ReceiptEnvelope([Receipt(runId)]),
            new DetailEnvelope(
                new RunRequest($"Objective for {runId}."),
                [],
                [],
                [],
                [Artifact(runId)],
                [],
                [],
                []));

    private static Receipt Receipt(string runId) =>
        new(
            $"receipt_{runId}",
            "step_test",
            "tool.test",
            ReceiptStatus.Succeeded,
            "Receipt.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());

    private static Artifact Artifact(string runId) =>
        new(
            $"artifact_{runId}",
            "test.artifact",
            new Dictionary<string, object?>(),
            []);

    private sealed class ScriptedTaskPlanner : ITaskPlanner
    {
        private readonly Queue<TaskGraphRefinement> _refinements;

        public ScriptedTaskPlanner(TaskGraphPlan plan, IReadOnlyList<TaskGraphRefinement>? refinements = null)
        {
            Plan = plan;
            _refinements = new Queue<TaskGraphRefinement>(refinements ?? []);
        }

        public TaskGraphPlan Plan { get; }

        public int RefineCalls { get; private set; }

        public Task<TaskGraphPlan> CreatePlanAsync(
            TaskPlanningRequest request,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(Plan);

        public Task<TaskGraphRefinement> RefinePlanAsync(
            TaskRefinementRequest request,
            CancellationToken cancellationToken = default)
        {
            RefineCalls++;
            return System.Threading.Tasks.Task.FromResult(_refinements.Dequeue());
        }
    }

    private sealed class ScriptedRunExecutor : IRunExecutor
    {
        private readonly Queue<OutcomeEnvelope> _outcomes;

        public ScriptedRunExecutor(IReadOnlyList<OutcomeEnvelope> outcomes)
        {
            _outcomes = new Queue<OutcomeEnvelope>(outcomes);
        }

        public List<RunRequest> Requests { get; } = [];

        public Task<OutcomeEnvelope> RunAsync(
            RunRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return System.Threading.Tasks.Task.FromResult(_outcomes.Dequeue());
        }
    }

    private sealed class ScriptedAcceptanceEvaluator : ITaskAcceptanceEvaluator
    {
        private readonly Func<TaskNode, TaskAcceptanceResult> _evaluate;

        public ScriptedAcceptanceEvaluator(Func<TaskNode, TaskAcceptanceResult> evaluate)
        {
            _evaluate = evaluate;
        }

        public Task<TaskAcceptanceResult> EvaluateAsync(
            TaskNode task,
            OutcomeEnvelope outcome,
            TaskAcceptanceContext context,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(_evaluate(task));
    }
}
