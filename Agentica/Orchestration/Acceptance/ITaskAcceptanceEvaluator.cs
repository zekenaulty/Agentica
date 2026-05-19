using Agentica.Orchestration.Context;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;

namespace Agentica.Orchestration.Acceptance;

public interface ITaskAcceptanceEvaluator
{
    Task<TaskAcceptanceResult> EvaluateAsync(
        TaskNode task,
        OutcomeEnvelope outcome,
        TaskAcceptanceContext context,
        CancellationToken cancellationToken = default);
}

public sealed record TaskAcceptanceContext(
    TaskGraphPlan Plan,
    OrchestrationState State,
    WorkContextSnapshot WorkingContext,
    IReadOnlyDictionary<string, object?> HostState);
