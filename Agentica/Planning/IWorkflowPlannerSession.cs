using Agentica.Requests;

namespace Agentica.Planning;

/// <summary>Host-owned identity for one run attempt. It grants no capabilities.</summary>
public sealed record PlanningSessionContext(
    string RunId,
    string Objective,
    RequestOrigin Origin,
    string? AuthorizationScopeId = null);

/// <summary>Optional planner lifetime owned and disposed by the runner for each attempt.</summary>
public interface IWorkflowPlannerSession : IWorkflowPlanner, IDisposable
{
}

public interface IWorkflowPlannerSessionFactory
{
    IWorkflowPlannerSession BeginSession(PlanningSessionContext context);
}
