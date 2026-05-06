namespace Agentica.Planning;

public sealed record WorkflowPlan(
    string PlanId,
    int Version,
    IReadOnlyList<PlanStep> Steps,
    string Description)
{
    public string? PlanningReason { get; init; }
}
