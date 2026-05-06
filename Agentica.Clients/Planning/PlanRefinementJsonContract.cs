namespace Agentica.Clients.Planning;

public sealed record PlanRefinementJsonContract(
    string? FromPlanId,
    string? Reason,
    IReadOnlyList<PlanRefinementEvidenceJsonContract>? Evidence,
    WorkflowPlanJsonContract? RefinedPlan);

public sealed record PlanRefinementEvidenceJsonContract(
    string? Kind,
    string? RefId);
