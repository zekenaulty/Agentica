using Agentica.Artifacts;
using Agentica.Continuity;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class GoalSpineTests
{
    [Fact]
    public void Initial_goal_spine_preserves_root_goal_and_proof_boundary()
    {
        var compiler = new DefaultGoalSpineCompiler();
        var spine = compiler.CompileInitial(new RunRequest("Restore comms and evacuate survivors."));

        Assert.Equal("agentica.goal_spine", spine.Kind);
        Assert.Equal("Restore comms and evacuate survivors.", spine.RootGoal);
        Assert.Contains(
            spine.ActiveConstraints,
            item => item.Contains("receipts, observations, artifacts, host checks, and verifiers prove reality", StringComparison.Ordinal));
        Assert.Empty(spine.EvidenceRefs);
        Assert.DoesNotContain("proof", spine.ActivePriority, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refused_receipt_updates_spine_without_becoming_completion_proof()
    {
        var compiler = new DefaultGoalSpineCompiler();
        var current = compiler.CompileInitial(new RunRequest("Complete the objective."));
        var receipt = new Receipt(
            "receipt_refused",
            "step_001",
            "demo.action",
            ReceiptStatus.Refused,
            "Input was invalid.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());

        var update = compiler.UpdateFromReceipt(
            current,
            receipt,
            new GoalSpineUpdateContext(
                "run_test",
                1,
                new Dictionary<string, object?>(),
                PlanningExecutionContext.Empty));

        Assert.Equal("receipt", update.UpdateKind);
        Assert.Contains("refused", update.Spine.LatestRealityUpdate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refused", update.Spine.KnownDivergence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            update.Spine.RecentLessons,
            lesson => lesson.Contains("not a committed state mutation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(update.Spine.EvidenceRefs, evidence =>
            evidence.Kind == "receipt" && evidence.RefId == "receipt_refused");
        Assert.DoesNotContain("complete", update.Spine.ActivePriority, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refinement_updates_spine_from_public_evidence_only()
    {
        var compiler = new DefaultGoalSpineCompiler();
        var current = compiler.CompileInitial(new RunRequest("Investigate the blocked route."));
        var refinement = new PlanRefinement(
            "plan_001",
            "plan_002",
            "observation",
            [new EvidenceRef("observation", "observation_001")]);

        var update = compiler.UpdateFromRefinement(
            current,
            refinement,
            new GoalSpineUpdateContext(
                "run_test",
                1,
                new Dictionary<string, object?>(),
                PlanningExecutionContext.Empty));

        Assert.Equal("refinement", update.UpdateKind);
        Assert.Contains("Plan refined", update.Spine.LatestRealityUpdate, StringComparison.Ordinal);
        Assert.Contains(update.Spine.EvidenceRefs, evidence =>
            evidence.Kind == "observation" && evidence.RefId == "observation_001");
        Assert.Contains(
            update.Spine.RecentLessons,
            lesson => lesson.Contains("public observations", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Planning_requests_include_compact_goal_spine_frame()
    {
        var planner = new CapturingPlanner();
        var runner = new AgenticaRunner(
            planner,
            DemoTools.CreateCatalog(),
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 1, MaxRefinements: 0),
            PlanExhaustionCompletionEvaluator.Instance);

        var envelope = await runner.RunAsync(new RunRequest("Query state once."));

        Assert.NotNull(planner.CreateRequest);
        var frame = Assert.Single(planner.CreateRequest!.ContextFrames, item => item.Kind == "agentica.goal_spine");
        var spine = Assert.IsType<GoalSpine>(frame.Payload["goalSpine"]);
        Assert.Equal("Query state once.", spine.RootGoal);
        Assert.Contains("GoalSpine shapes continuity only", Assert.IsType<string>(frame.Payload["proofBoundary"]));
        Assert.Contains(envelope.Details.PlanningFrames, item => item.FrameId == frame.FrameId);
    }

    [Fact]
    public async Task Outcome_details_include_breadcrumb_ledger_without_prompt_bloat()
    {
        var planner = new CapturingPlanner();
        var runner = new AgenticaRunner(
            planner,
            DemoTools.CreateCatalog(),
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 1, MaxRefinements: 0),
            PlanExhaustionCompletionEvaluator.Instance);

        var envelope = await runner.RunAsync(new RunRequest("Query state once."));

        Assert.NotEmpty(envelope.Details.Breadcrumbs.Entries);
        Assert.Contains(envelope.Details.Breadcrumbs.Entries, entry => entry.Kind == "step.started");
        Assert.Contains(envelope.Details.Breadcrumbs.Entries, entry => entry.Kind == "receipt.emitted");
        Assert.Contains(envelope.Details.Breadcrumbs.Entries, entry => entry.Kind == "outcome.reported");
        Assert.All(envelope.Details.Breadcrumbs.Entries, entry =>
        {
            Assert.Equal(envelope.Outcome.RunId, entry.RunId);
            Assert.False(string.IsNullOrWhiteSpace(entry.Summary));
        });
        Assert.DoesNotContain(planner.CreateRequest!.ContextFrames, frame => frame.Kind.Contains("breadcrumb", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(planner.CreateRequest!.ContextFrames, frame => frame.Kind.Contains("divergence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Divergence_ledger_records_refused_receipt_and_blocked_outcome()
    {
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor("refuse.action", "Refuse Action", ToolKind.Action, ToolEffect.WritesLocalState),
            new RefusingTool()));
        var runner = new AgenticaRunner(
            new SingleStepPlanner("refuse.action", ToolKind.Action, ToolEffect.WritesLocalState),
            catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 1, MaxRefinements: 0),
            PlanExhaustionCompletionEvaluator.Instance);

        var envelope = await runner.RunAsync(new RunRequest("Try a refused action."));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.Divergences.Entries, entry =>
            entry.Actual.Contains("Receipt", StringComparison.OrdinalIgnoreCase) &&
            entry.Actual.Contains("Refused", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(envelope.Details.Divergences.Entries, entry =>
            entry.Actual.Contains("Run ended as Blocked", StringComparison.OrdinalIgnoreCase));
        Assert.All(envelope.Details.Divergences.Entries, entry =>
            Assert.Equal(envelope.Outcome.RunId, entry.RunId));
        Assert.True(envelope.Details.Continuity.NarrativeReportRecommended);
        Assert.Contains(envelope.Details.Continuity.RecommendationReasons, reason =>
            reason.Contains("postmortem", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CapturingPlanner : IWorkflowPlanner
    {
        public PlanningRequest? CreateRequest { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateRequest = request;
            return Task.FromResult(new WorkflowPlan(
                "plan_001",
                1,
                [
                    new PlanStep(
                        "step_001",
                        DemoToolIds.QueryState,
                        ToolKind.Query,
                        ToolEffect.ReadOnly,
                        new Dictionary<string, object?>())
                ],
                "Query once."));
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No refinement expected.");
    }

    private sealed class SingleStepPlanner : IWorkflowPlanner
    {
        private readonly string _toolId;
        private readonly ToolKind _kind;
        private readonly ToolEffect _effect;

        public SingleStepPlanner(string toolId, ToolKind kind, ToolEffect effect)
        {
            _toolId = toolId;
            _kind = kind;
            _effect = effect;
        }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan(
                "plan_001",
                1,
                [
                    new PlanStep(
                        "step_001",
                        _toolId,
                        _kind,
                        _effect,
                        new Dictionary<string, object?>())
                    {
                        Reason = "Exercise continuity divergence."
                    }
                ],
                "Single step."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("No refinement expected.");
    }

    private sealed class RefusingTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var receipt = new Receipt(
                "receipt_refused",
                invocation.StepId,
                invocation.ToolId,
                ReceiptStatus.Refused,
                "Host refused the requested mutation.",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>());

            return Task.FromResult(new ToolResult(receipt));
        }
    }
}
