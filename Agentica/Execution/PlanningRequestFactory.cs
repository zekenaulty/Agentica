using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;
using Agentica.Tools;

namespace Agentica.Execution;

internal sealed class PlanningRequestFactory
{
    private readonly ToolCatalog _toolCatalog;
    private readonly ExecutionPolicy _policy;

    public PlanningRequestFactory(ToolCatalog toolCatalog, ExecutionPolicy policy)
    {
        _toolCatalog = toolCatalog;
        _policy = policy;
    }

    public PlanningRequest Create(RunRequest request, AgenticaRun run)
    {
        var context = _policy.EffectivePlanningContext;
        return new PlanningRequest(
            request,
            _toolCatalog.Descriptors,
            Limit(run.Observations, context.MaxRecentObservations),
            Limit(run.Receipts, context.MaxRecentReceipts))
        {
            ExecutionContext = CreateExecutionContext(run)
        };
    }

    private static PlanningExecutionContext CreateExecutionContext(AgenticaRun run) =>
        new(
            run.CompletedSteps.ToArray(),
            run.CompletedSteps.Select(stepId => CreateCompletedStepContext(run, stepId)).ToArray(),
            run.PlanVersions.LastOrDefault()?.PlanId,
            run.PlanVersions.Count);

    private static CompletedStepContext CreateCompletedStepContext(AgenticaRun run, string stepId)
    {
        var planMatch = run.PlanVersions
            .SelectMany(plan => plan.Steps.Select(step => new { Plan = plan, Step = step }))
            .FirstOrDefault(item => string.Equals(item.Step.StepId, stepId, StringComparison.Ordinal));
        var receipt = run.Receipts.LastOrDefault(item => string.Equals(item.StepId, stepId, StringComparison.Ordinal));
        var observation = run.Observations.LastOrDefault(item => string.Equals(item.StepId, stepId, StringComparison.Ordinal));
        var artifact = receipt is null
            ? null
            : run.Artifacts.LastOrDefault(item => item.Evidence.Any(evidence =>
                string.Equals(evidence.Kind, "receipt", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(evidence.RefId, receipt.ReceiptId, StringComparison.Ordinal)));

        return new CompletedStepContext(
            stepId,
            planMatch?.Step.ToolId ?? receipt?.ToolId,
            planMatch?.Plan.PlanId,
            planMatch?.Plan.Version,
            receipt?.ReceiptId,
            receipt?.Status.ToString(),
            observation?.ObservationId,
            artifact?.ArtifactId);
    }

    internal static IReadOnlyList<T> Limit<T>(IReadOnlyList<T> items, int? maxItems) =>
        maxItems switch
        {
            null => items,
            <= 0 => [],
            _ => items.TakeLast(maxItems.Value).ToArray()
        };
}
