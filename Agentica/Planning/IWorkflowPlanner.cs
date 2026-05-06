using Agentica.Observations;

namespace Agentica.Planning;

public interface IWorkflowPlanner
{
    Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default);
}
