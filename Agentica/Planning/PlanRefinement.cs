using Agentica.Observations;

namespace Agentica.Planning;

public sealed record PlanRefinement(
    string FromPlanId,
    string ToPlanId,
    string Reason,
    IReadOnlyList<EvidenceRef> Evidence);
