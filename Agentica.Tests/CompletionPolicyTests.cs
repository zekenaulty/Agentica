using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class CompletionPolicyTests
{
    [Fact]
    public void Runner_rejects_a_missing_completion_evaluator()
    {
        Assert.Throws<ArgumentNullException>(() => new AgenticaRunner(
            new DeterministicWorkflowPlanner(),
            DemoTools.CreateCatalog(),
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(),
            completionEvaluator: null!));
    }

    [Fact]
    public void Completion_evaluator_parameter_is_required_by_the_public_constructor()
    {
        var constructor = Assert.Single(typeof(AgenticaRunner).GetConstructors());
        var parameter = Assert.Single(
            constructor.GetParameters(),
            candidate => candidate.ParameterType == typeof(ICompletionEvaluator));

        Assert.False(parameter.IsOptional);
        Assert.False(parameter.HasDefaultValue);
    }

    [Fact]
    public void Evidence_completion_rejects_an_empty_definition_of_done()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new EvidenceCompletionEvaluator([]));

        Assert.Equal("requirements", exception.ParamName);
    }

    [Fact]
    public void Artifact_completion_requires_a_linked_successful_receipt()
    {
        var receipt = new Receipt(
            "receipt_test",
            "step_test",
            "tool.test",
            ReceiptStatus.Succeeded,
            "Succeeded.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());
        var artifact = new Agentica.Artifacts.Artifact(
            "artifact_test",
            "required.artifact",
            new Dictionary<string, object?>(),
            []);
        var evaluator = EvidenceCompletionEvaluator.ForArtifactKind(
            "required.artifact",
            continueWhenMissing: false);

        var evaluation = evaluator.Evaluate(new CompletionContext(
            "run_test",
            1,
            ["step_test"],
            [receipt],
            [],
            [artifact]));

        Assert.Equal(CompletionDecision.Blocked, evaluation.Decision);
        Assert.Empty(evaluation.EvidenceRefs);
    }

    [Fact]
    public void Evidence_completion_selects_only_the_proof_that_satisfied_it()
    {
        var receipt = new Receipt(
            "receipt_test",
            "step_test",
            "tool.test",
            ReceiptStatus.Succeeded,
            "Succeeded.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());
        var artifact = new Agentica.Artifacts.Artifact(
            "artifact_test",
            "required.artifact",
            new Dictionary<string, object?>(),
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        var evaluator = EvidenceCompletionEvaluator.ForArtifactKind("required.artifact");

        var evaluation = evaluator.Evaluate(new CompletionContext(
            "run_test",
            1,
            ["step_test"],
            [receipt],
            [],
            [artifact]));

        Assert.Equal(CompletionDecision.Complete, evaluation.Decision);
        Assert.Equal([new EvidenceRef("artifact", artifact.ArtifactId)], evaluation.EvidenceRefs);
    }

    [Fact]
    public async Task Throwing_completion_policy_preserves_post_mutation_proof()
    {
        var tool = new CountingMutationTool();
        var plan = new WorkflowPlan(
            "plan_completion_failure",
            1,
            [
                new PlanStep(
                    "mutate",
                    "workspace.mutate",
                    ToolKind.Action,
                    ToolEffect.WritesLocalState,
                    new Dictionary<string, object?>())
            ],
            "Completion policy failure proof.");
        var runner = new AgenticaRunner(
            new StaticPlanner(plan),
            ToolCatalog.Create(TestToolRegistration.Create(
                new ToolDescriptor(
                    "workspace.mutate",
                    "Workspace Mutate",
                    ToolKind.Action,
                    ToolEffect.WritesLocalState),
                tool)),
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                EffectPolicy: ToolEffectPolicy.AllowKnown),
            new ThrowingCompletionEvaluator());

        var envelope = await runner.RunAsync(new RunRequest("Preserve proof after completion failure."));

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        Assert.Equal(StopReason.CompletionEvaluationFailed, envelope.Outcome.StopReason);
        Assert.Equal(ReceiptStatus.Succeeded, Assert.Single(envelope.Receipts.Items).Status);
    }

    private sealed class ThrowingCompletionEvaluator : ICompletionEvaluator
    {
        public CompletionEvaluation Evaluate(CompletionContext context) =>
            throw new InvalidOperationException("Broken completion policy.");
    }

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

    private sealed class CountingMutationTool : ITool
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
}
