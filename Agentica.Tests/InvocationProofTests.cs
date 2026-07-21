using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class InvocationProofTests
{
    [Fact]
    public async Task Mutation_that_throws_cancellation_still_has_a_terminal_receipt()
    {
        var tool = new CancelAfterMutationTool();
        var envelope = await CreateRunner(
            Plan(Step("mutate", "workspace.mutate", ToolKind.Action, ToolEffect.WritesLocalState)),
            TestToolRegistration.Create(
                new ToolDescriptor(
                    "workspace.mutate",
                    "Workspace Mutate",
                    ToolKind.Action,
                    ToolEffect.WritesLocalState),
                tool)).RunAsync(new RunRequest("Prove cancelled mutation dispatch."));

        Assert.Equal(1, tool.MutationCount);
        Assert.Equal(RunOutcomeStatus.Cancelled, envelope.Outcome.Status);
        var receipt = Assert.Single(envelope.Receipts.Items);
        Assert.Equal(ReceiptStatus.Cancelled, receipt.Status);
        Assert.Equal("mutate", receipt.StepId);
        Assert.Equal("workspace.mutate", receipt.ToolId);
        Assert.Contains(
            envelope.Details.Events,
            item => item.Type == ExecutionEventType.ReceiptEmitted.WireName() &&
                item.Context?.ReceiptId == receipt.ReceiptId);
    }

    [Fact]
    public async Task Parallel_cancellation_preserves_every_dispatched_result()
    {
        var success = new SuccessTool();
        var cancelled = new CancelAfterDispatchTool();
        var plan = Plan(
            Step("read", "workspace.read", ToolKind.Query, ToolEffect.ReadOnly) with { BatchId = "batch" },
            Step("cancelled_read", "workspace.cancelled_read", ToolKind.Query, ToolEffect.ReadOnly) with { BatchId = "batch" });
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(
                new ToolDescriptor("workspace.read", "Workspace Read", ToolKind.Query, ToolEffect.ReadOnly),
                success),
            TestToolRegistration.Create(
                new ToolDescriptor(
                    "workspace.cancelled_read",
                    "Workspace Cancelled Read",
                    ToolKind.Query,
                    ToolEffect.ReadOnly),
                cancelled));
        var runner = new AgenticaRunner(
            new StaticPlanner(plan),
            catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly),
            PlanExhaustionCompletionEvaluator.Instance);

        var envelope = await runner.RunAsync(new RunRequest("Preserve the whole dispatched batch."));

        Assert.Equal(1, success.ExecutionCount);
        Assert.Equal(1, cancelled.ExecutionCount);
        Assert.Equal(RunOutcomeStatus.Cancelled, envelope.Outcome.Status);
        Assert.Equal(2, envelope.Receipts.Items.Count);
        Assert.Contains(envelope.Receipts.Items, item => item.Status == ReceiptStatus.Succeeded);
        Assert.Contains(envelope.Receipts.Items, item => item.Status == ReceiptStatus.Cancelled);
    }

    private static AgenticaRunner CreateRunner(WorkflowPlan plan, ToolRegistration registration) =>
        new(
            new StaticPlanner(plan),
            ToolCatalog.Create(registration),
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                EffectPolicy: ToolEffectPolicy.AllowKnown),
            PlanExhaustionCompletionEvaluator.Instance);

    private static WorkflowPlan Plan(params PlanStep[] steps) =>
        new("plan_invocation_proof", 1, steps, "Invocation proof test plan.");

    private static PlanStep Step(
        string stepId,
        string toolId,
        ToolKind kind,
        ToolEffect effect) =>
        new(stepId, toolId, kind, effect, new Dictionary<string, object?>());

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

    private sealed class CancelAfterMutationTool : ITool
    {
        public int MutationCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            MutationCount++;
            throw new OperationCanceledException("Cancellation after mutation.");
        }
    }

    private sealed class SuccessTool : ITool
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
                "Read completed.",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>())));
        }
    }

    private sealed class CancelAfterDispatchTool : ITool
    {
        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            throw new OperationCanceledException("Cancellation after dispatch.");
        }
    }
}
