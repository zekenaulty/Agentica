using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Outcomes;

namespace Agentica.Orchestration.Planning;

public interface ITaskPlanner
{
    Task<TaskGraphPlan> CreatePlanAsync(
        TaskPlanningRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskGraphRefinement> RefinePlanAsync(
        TaskRefinementRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record TaskPlanningRequest(
    LargeTaskRequest Request,
    OrchestrationPolicy Policy);

public sealed record TaskRefinementRequest(
    LargeTaskRequest Request,
    TaskGraphPlan CurrentPlan,
    OrchestrationState State,
    TaskNode ActiveTask,
    OutcomeEnvelope LatestOutcome,
    TaskAcceptanceResult Acceptance,
    WorkContextSnapshot WorkingContext,
    OrchestrationPolicy Policy);

public sealed record TaskGraphRefinement(
    string Reason,
    IReadOnlyList<TaskGraphMutation> Mutations,
    IReadOnlyList<string> Blockers,
    bool RequiresUserInput);
