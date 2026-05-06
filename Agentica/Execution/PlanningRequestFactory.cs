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
            Limit(run.Receipts, context.MaxRecentReceipts));
    }

    internal static IReadOnlyList<T> Limit<T>(IReadOnlyList<T> items, int? maxItems) =>
        maxItems switch
        {
            null => items,
            <= 0 => [],
            _ => items.TakeLast(maxItems.Value).ToArray()
        };
}
