using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Artifacts;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class RuntimeInvariantTests
{
    [Fact]
    public async Task Validation_failure_prevents_all_tool_execution()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(new ToolRegistration(
            new ToolDescriptor("known.read", "Known Read", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var runner = CreateRunner(new StaticPlanner(Plan(
            new PlanStep("duplicate", "missing.tool", ToolKind.Query, ToolEffect.ReadOnly, new Dictionary<string, object?>()),
            new PlanStep("duplicate", "known.read", ToolKind.Action, ToolEffect.ReadOnly, new Dictionary<string, object?>()))), catalog);

        var envelope = await runner.RunAsync(new RunRequest("Invalid plans must fail closed."));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.unknown_tool");
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.duplicate_id");
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.kind_mismatch");
        Assert.Empty(envelope.Receipts.Items);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Every_completed_step_has_exactly_one_receipt()
    {
        var catalog = ToolCatalog.Create(
            Registration("read.a"),
            Registration("read.b"));
        var runner = CreateRunner(new StaticPlanner(Plan(
            Step("step_a", "read.a") with { BatchId = "reads" },
            Step("step_b", "read.b") with { BatchId = "reads" })), catalog);

        var envelope = await runner.RunAsync(new RunRequest("Receipt invariant."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.All(envelope.Outcome.CompletedSteps, stepId =>
            Assert.Single(envelope.Receipts.Items, receipt => receipt.StepId == stepId));
    }

    [Fact]
    public async Task Success_requires_completion_evaluator_satisfaction()
    {
        var catalog = ToolCatalog.Create(Registration("read.state"));
        var runner = CreateRunner(
            new StaticPlanner(Plan(Step("step_001", "read.state"))),
            catalog,
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("required.artifact", continueWhenMissing: false));

        var envelope = await runner.RunAsync(new RunRequest("Completion gate invariant."));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.CompletionNotSatisfied, envelope.Outcome.StopReason);
        Assert.DoesNotContain("succeeded", envelope.Report.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Readonly_batch_failure_does_not_invent_partial_success()
    {
        var catalog = ToolCatalog.Create(
            new ToolRegistration(
                new ToolDescriptor("read.good", "Read Good", ToolKind.Query, ToolEffect.ReadOnly),
                new StatusTool(ReceiptStatus.Succeeded)),
            new ToolRegistration(
                new ToolDescriptor("read.bad", "Read Bad", ToolKind.Query, ToolEffect.ReadOnly),
                new StatusTool(ReceiptStatus.Failed)));
        var runner = CreateRunner(new StaticPlanner(Plan(
            Step("step_good", "read.good") with { BatchId = "reads" },
            Step("step_bad", "read.bad") with { BatchId = "reads" })), catalog);

        var envelope = await runner.RunAsync(new RunRequest("Batch failure invariant."));

        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolFailure, envelope.Outcome.StopReason);
        Assert.Equal(["step_good", "step_bad"], envelope.Outcome.CompletedSteps);
        Assert.Equal(2, envelope.Receipts.Items.Count);
        Assert.Contains(envelope.Receipts.Items, receipt => receipt.Status == ReceiptStatus.Failed);
        Assert.Single(envelope.Details.Batches);
    }

    [Fact]
    public async Task Outcome_report_is_not_completion_proof()
    {
        var catalog = ToolCatalog.Create(Registration("read.state"));
        var runner = new AgenticaRunner(
            new StaticPlanner(Plan(Step("step_001", "read.state"))),
            catalog,
            new InMemoryEventSink(),
            new MisleadingOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 4, MaxRefinements: 0, PlanningMode: PlanningMode.PlanOnly),
            EvidenceCompletionEvaluator.ForArtifactKind("required.artifact", continueWhenMissing: false));

        var envelope = await runner.RunAsync(new RunRequest("Reports cannot prove completion."));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Contains("succeeded", envelope.Report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(envelope.Outcome.CompletionEvidence, evidence => evidence.Kind == "artifact");
    }

    [Fact]
    public async Task Outcome_envelope_serializes_batch_and_plan_dependency_fields()
    {
        var catalog = ToolCatalog.Create(
            Registration("read.a"),
            Registration("read.b"),
            Registration("read.c"));
        var runner = CreateRunner(new StaticPlanner(Plan(
            Step("step_a", "read.a") with { BatchId = "reads" },
            Step("step_b", "read.b") with { BatchId = "reads" },
            Step("step_c", "read.c") with { DependsOn = ["step_a", "step_b"] })), catalog);

        var envelope = await runner.RunAsync(new RunRequest("Serialization invariant."));
        var json = JsonSerializer.Serialize(envelope, JsonOptions());
        using var document = JsonDocument.Parse(json);

        var details = document.RootElement.GetProperty("details");
        Assert.Equal(1, details.GetProperty("batches").GetArrayLength());
        var firstBatch = details.GetProperty("batches")[0];
        Assert.Equal("reads", firstBatch.GetProperty("batchId").GetString());
        Assert.Equal(2, firstBatch.GetProperty("stepIds").GetArrayLength());

        var steps = details.GetProperty("planVersions")[0].GetProperty("steps").EnumerateArray().ToArray();
        Assert.Equal("reads", steps[0].GetProperty("batchId").GetString());
        Assert.Equal(2, steps[2].GetProperty("dependsOn").GetArrayLength());
    }

    [Fact]
    public void Execution_policy_defaults_are_bounded_and_local_first()
    {
        var policy = ExecutionPolicy.Default;

        Assert.True(policy.MaxSteps > 0);
        Assert.True(policy.MaxRefinements >= 0);
        Assert.True(policy.MaxBatchSize <= 8);
        Assert.True(policy.MaxParallelism <= 8);
        Assert.True(policy.AllowReadOnlyParallelBatches);
        Assert.True(policy.EffectiveEffectPolicy.Allows(ToolEffect.ReadOnly));
        Assert.True(policy.EffectiveEffectPolicy.Allows(ToolEffect.WritesLocalState));
        Assert.False(policy.EffectiveEffectPolicy.Allows(ToolEffect.ExternalSideEffect));
        Assert.False(policy.EffectiveEffectPolicy.Allows(ToolEffect.Destructive));
    }

    private static AgenticaRunner CreateRunner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        ICompletionEvaluator? completionEvaluator = null) =>
        new(
            planner,
            catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 8, MaxRefinements: 0, PlanningMode: PlanningMode.PlanOnly),
            completionEvaluator);

    private static WorkflowPlan Plan(params PlanStep[] steps) =>
        new("plan_test", 1, steps, "Runtime invariant test plan.");

    private static PlanStep Step(string stepId, string toolId) =>
        new(stepId, toolId, ToolKind.Query, ToolEffect.ReadOnly, new Dictionary<string, object?>());

    private static ToolRegistration Registration(string toolId) =>
        new(
            new ToolDescriptor(toolId, toolId, ToolKind.Query, ToolEffect.ReadOnly),
            new StatusTool(ReceiptStatus.Succeeded));

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

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

    private sealed class CountingTool : ITool
    {
        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(Receipt(invocation, ReceiptStatus.Succeeded)));
        }
    }

    private sealed class StatusTool : ITool
    {
        private readonly ReceiptStatus _status;

        public StatusTool(ReceiptStatus status)
        {
            _status = status;
        }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResult(Receipt(invocation, _status)));
    }

    private sealed class MisleadingOutcomeReporter : IOutcomeReporter
    {
        public OutcomeReport BuildReport(
            Runs.AgenticaRun run,
            RunOutcomeStatus status,
            StopReason stopReason,
            IReadOnlyList<Validation.ValidationIssue> validationIssues,
            IReadOnlyList<string> blockers) =>
            new(
                "report_misleading",
                "This report says the run succeeded, but it is not proof.",
                [new ReportClaim("Misleading narrative claim.", [new EvidenceRef("stopReason", stopReason.ToString())])]);
    }

    private static Receipt Receipt(ToolInvocation invocation, ReceiptStatus status) =>
        new(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            status,
            $"Tool returned {status}.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());
}
