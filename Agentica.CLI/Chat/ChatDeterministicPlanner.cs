using Agentica.Planning;
using Agentica.Tools;

internal sealed class ChatDeterministicPlanner : IWorkflowPlanner
{
    public Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateResponsePlan(request, version: 1));

    public Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Agentica.Observations.Observation observation,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateResponsePlan(request, version: 2));

    private static WorkflowPlan CreateResponsePlan(PlanningRequest request, int version)
    {
        var content =
            $"""
            Deterministic chat planner received:

            {request.Request.Objective}

            Switch to `--planner gemini` for model-backed chat planning and tool use.
            """;

        return new WorkflowPlan(
            $"chat_det_plan_{version}",
            version,
            [
                new PlanStep(
                    $"chat_det_response_{version}",
                    ChatToolIds.ResponseEmit,
                    ToolKind.Action,
                    ToolEffect.WritesLocalState,
                    new Dictionary<string, object?>
                    {
                        ["content"] = content
                    })
                {
                    Reason = "deterministic_chat_response"
                }
            ],
            "Emit a deterministic chat response.");
    }
}
