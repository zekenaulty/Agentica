using Agentica.Execution;
using Agentica.Planning;
using Agentica.Tools;

internal static class LabSecurityPolicy
{
    public static ToolSecurityPolicy ForPlanner(IWorkflowPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        ToolDataBoundary[] initialBoundaries =
        [
            ToolDataBoundary.UserContent,
            ToolDataBoundary.HostState
        ];

        return planner is IExternalWorkflowPlanner
            ? new ToolSecurityPolicy(
                InitialBoundaries: initialBoundaries,
                ExternalPlannerAllowedBoundaries:
                [
                    ToolDataBoundary.Public,
                    ToolDataBoundary.UserContent,
                    ToolDataBoundary.HostState,
                    ToolDataBoundary.ExternalUntrusted
                ])
            : new ToolSecurityPolicy(InitialBoundaries: initialBoundaries);
    }
}
