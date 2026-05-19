using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.Planning;

public sealed class DeterministicWorkflowPlanner : IWorkflowPlanner
{
    public Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new WorkflowPlan(
            PlanId: "plan_001",
            Version: 1,
            Steps:
            [
                new PlanStep(
                    StepId: "step_001",
                    ToolId: DemoToolIds.QueryState,
                    Kind: ToolKind.Query,
                    Effect: ToolEffect.ReadOnly,
                    Input: new Dictionary<string, object?>
                    {
                        ["query"] = "current_state"
                })
                {
                    Reason = "Query current state before selecting an action."
                }
            ],
            Description: "Initial deterministic plan: query state before action."));
    }

    public Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new WorkflowPlan(
            PlanId: "plan_002",
            Version: 2,
            Steps:
            [
                new PlanStep(
                    StepId: "step_002",
                    ToolId: DemoToolIds.PerformAction,
                    Kind: ToolKind.Action,
                    Effect: ToolEffect.WritesLocalState,
                    Input: new Dictionary<string, object?>
                    {
                        ["action"] = "write_marker",
                        ["basedOnObservation"] = observation.ObservationId
                })
                {
                    Reason = "Act only after the state query produced receipt-backed evidence."
                }
            ],
            Description: "Refined deterministic plan: act after state query observation."));
    }
}
