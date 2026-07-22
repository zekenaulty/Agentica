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

/// <summary>
/// Marker for planners that transmit planning requests outside the host process or
/// trust boundary. The runner requires an explicit external-planner boundary policy
/// before calling an implementation with this profile.
/// </summary>
public interface IExternalWorkflowPlanner : IWorkflowPlanner
{
}
