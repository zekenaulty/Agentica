using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;
using Agentica.Tools;
using Agentica.Validation;

namespace Agentica.Tests;

public sealed class ProofObserverFailureTests
{
    [Fact]
    public async Task Throwing_outcome_reporter_cannot_erase_a_completed_mutation()
    {
        var tool = new MutationTool();
        var runner = CreateRunner(tool, new ThrowingOutcomeReporter(), new InMemoryEventSink());

        var envelope = await runner.RunAsync(new RunRequest("Preserve proof when reporting fails."));

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(ReceiptStatus.Succeeded, Assert.Single(envelope.Receipts.Items).Status);
        Assert.Contains("configured outcome reporter failed", envelope.Report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            envelope.Details.Events,
            item => item.Type == ExecutionEventType.OutcomeReported.WireName() &&
                item.Diagnostics?.Code == "outcome.reporter.failed");
    }

    [Fact]
    public async Task Throwing_reason_projector_cannot_erase_canonical_events_or_receipts()
    {
        var tool = new MutationTool();
        var events = new InMemoryEventSink();
        var runner = CreateRunner(
            tool,
            new DeterministicOutcomeReporter(),
            events,
            new ThrowingReasonProjector());

        var envelope = await runner.RunAsync(new RunRequest("Preserve proof when event decoration fails."));

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Single(envelope.Receipts.Items);
        Assert.Equal(envelope.Details.Events.Count, events.Events.Count);
        Assert.Contains(
            envelope.Details.Events,
            item => item.Diagnostics?.Code == "event.reason_projection.failed");
        Assert.Contains(
            envelope.Details.Events,
            item => item.Type == ExecutionEventType.RunSucceeded.WireName());
    }

    private static AgenticaRunner CreateRunner(
        MutationTool tool,
        IOutcomeReporter reporter,
        IEventSink eventSink,
        IUserFacingReasonProjector? reasonProjector = null) =>
        new(
            new StaticPlanner(new WorkflowPlan(
                "plan_observer_failure",
                1,
                [
                    new PlanStep(
                        "mutate",
                        "workspace.mutate",
                        ToolKind.Action,
                        ToolEffect.WritesLocalState,
                        new Dictionary<string, object?>())
                ],
                "Proof observer failure plan.")),
            ToolCatalog.Create(TestToolRegistration.Create(
                new ToolDescriptor(
                    "workspace.mutate",
                    "Workspace Mutate",
                    ToolKind.Action,
                    ToolEffect.WritesLocalState),
                tool)),
            eventSink,
            reporter,
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                EffectPolicy: ToolEffectPolicy.AllowKnown),
            PlanExhaustionCompletionEvaluator.Instance,
            userFacingReasonProjector: reasonProjector);

    private sealed class StaticPlanner(WorkflowPlan plan) : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan);

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class MutationTool : ITool
    {
        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(new Receipt(
                AgenticaIds.New("tool_receipt"),
                invocation.StepId,
                invocation.ToolId,
                ReceiptStatus.Succeeded,
                "Mutation completed.",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>())));
        }
    }

    private sealed class ThrowingOutcomeReporter : IOutcomeReporter
    {
        public OutcomeReport BuildReport(
            AgenticaRun run,
            RunOutcomeStatus status,
            StopReason stopReason,
            IReadOnlyList<ValidationIssue> validationIssues,
            IReadOnlyList<string> blockers) =>
            throw new InvalidOperationException("Reporter failed.");
    }

    private sealed class ThrowingReasonProjector : IUserFacingReasonProjector
    {
        public UserFacingReason? Project(UserFacingReasonProjectionRequest request) =>
            throw new InvalidOperationException("Projection failed.");
    }
}
