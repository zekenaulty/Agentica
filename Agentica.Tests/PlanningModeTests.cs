using Agentica.Artifacts;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class PlanningModeTests
{
    [Fact]
    public async Task Stepwise_mode_refines_after_action_observation()
    {
        var planner = new RefinementProbePlanner(
            initialPlan: Plan("plan_001", 1, Step("step_001", "observing_action", ToolKind.Action, ToolEffect.WritesLocalState)),
            refinedPlan: Plan("plan_002", 2, Step("step_002", "complete_action", ToolKind.Action, ToolEffect.WritesLocalState)) with
            {
                PlanningReason = PlanRefinementReasons.LowConfidence
            });
        var catalog = ToolCatalog.Create(
            Register("observing_action", ToolKind.Action, ToolEffect.WritesLocalState, new ObservationTool()),
            Register("complete_action", ToolKind.Action, ToolEffect.WritesLocalState, new ReceiptOnlyTool()));
        var runner = CreateRunner(planner, catalog, PlanningMode.Stepwise);

        var envelope = await runner.RunAsync(new RunRequest("Stepwise planning mode test"));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(1, planner.RefinementCount);
        Assert.Equal(["step_001", "step_002"], envelope.Outcome.CompletedSteps);
        var refinement = Assert.Single(envelope.Details.PlanRefinements);
        Assert.Equal(PlanRefinementReasons.LowConfidence, refinement.Reason);
    }

    [Fact]
    public async Task Query_and_blocker_mode_does_not_refine_after_normal_action_observation()
    {
        var planner = new RefinementProbePlanner(
            initialPlan: Plan(
                "plan_001",
                1,
                Step("step_001", "observing_action", ToolKind.Action, ToolEffect.WritesLocalState),
                Step("step_002", "complete_action", ToolKind.Action, ToolEffect.WritesLocalState)),
            refinedPlan: Plan("plan_should_not_run", 2, Step("step_refined", "complete_action", ToolKind.Action, ToolEffect.WritesLocalState)));
        var catalog = ToolCatalog.Create(
            Register("observing_action", ToolKind.Action, ToolEffect.WritesLocalState, new ObservationTool()),
            Register("complete_action", ToolKind.Action, ToolEffect.WritesLocalState, new ReceiptOnlyTool()));
        var runner = CreateRunner(planner, catalog, PlanningMode.QueryAndBlockerDriven);

        var envelope = await runner.RunAsync(new RunRequest("Query and blocker planning mode test"));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(0, planner.RefinementCount);
        Assert.Equal(["step_001", "step_002"], envelope.Outcome.CompletedSteps);
        Assert.Empty(envelope.Details.PlanRefinements);
    }

    [Fact]
    public async Task Plan_only_mode_stops_on_refused_observation_without_refining()
    {
        var planner = new RefinementProbePlanner(
            initialPlan: Plan("plan_001", 1, Step("step_001", "refusing_action", ToolKind.Action, ToolEffect.WritesLocalState)),
            refinedPlan: Plan("plan_should_not_run", 2, Step("step_refined", "complete_action", ToolKind.Action, ToolEffect.WritesLocalState)));
        var catalog = ToolCatalog.Create(
            Register("refusing_action", ToolKind.Action, ToolEffect.WritesLocalState, new RefusingObservationTool()),
            Register("complete_action", ToolKind.Action, ToolEffect.WritesLocalState, new ReceiptOnlyTool()));
        var runner = CreateRunner(planner, catalog, PlanningMode.PlanOnly);

        var envelope = await runner.RunAsync(new RunRequest("Plan only planning mode test"));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolRefused, envelope.Outcome.StopReason);
        Assert.Equal(0, planner.RefinementCount);
        Assert.Empty(envelope.Details.PlanRefinements);
    }

    private static AgenticaRunner CreateRunner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        PlanningMode planningMode) =>
        new(
            planner,
            catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 5, PlanningMode: planningMode),
            PlanExhaustionCompletionEvaluator.Instance);

    private static ToolRegistration Register(
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        ITool tool) =>
        TestToolRegistration.Create(new ToolDescriptor(toolId, toolId, kind, effect), tool);

    private static WorkflowPlan Plan(string planId, int version, params PlanStep[] steps) =>
        new(planId, version, steps, "Planning mode test plan.");

    private static PlanStep Step(string stepId, string toolId, ToolKind kind, ToolEffect effect) =>
        new(stepId, toolId, kind, effect, new Dictionary<string, object?>());

    private sealed class RefinementProbePlanner : IWorkflowPlanner
    {
        private readonly WorkflowPlan _initialPlan;
        private readonly WorkflowPlan _refinedPlan;

        public RefinementProbePlanner(WorkflowPlan initialPlan, WorkflowPlan refinedPlan)
        {
            _initialPlan = initialPlan;
            _refinedPlan = refinedPlan;
        }

        public int RefinementCount { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_initialPlan);

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default)
        {
            RefinementCount++;
            return Task.FromResult(_refinedPlan);
        }
    }

    private sealed class ObservationTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Action produced an observation.");
            return Task.FromResult(new ToolResult(
                receipt,
                new Observation(
                    AgenticaIds.New("observation"),
                    invocation.StepId,
                    ObservationKind.ToolResult,
                    "Action observation.",
                    new Dictionary<string, object?>
                    {
                        ["action"] = "observed"
                    },
                    [new EvidenceRef("receipt", receipt.ReceiptId)])));
        }
    }

    private sealed class RefusingObservationTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var receipt = Receipt(invocation, ReceiptStatus.Refused, "Action refused.");
            return Task.FromResult(new ToolResult(
                receipt,
                new Observation(
                    AgenticaIds.New("observation"),
                    invocation.StepId,
                    ObservationKind.ToolResult,
                    "Action refused.",
                    new Dictionary<string, object?>
                    {
                        ["blocker"] = "refused"
                    },
                    [new EvidenceRef("receipt", receipt.ReceiptId)])));
        }
    }

    private sealed class ReceiptOnlyTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResult(Receipt(invocation, ReceiptStatus.Succeeded, "Action completed.")));
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
