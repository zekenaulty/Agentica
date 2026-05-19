using Agentica.Observations;

namespace Agentica.Orchestration.Acceptance;

public enum TaskAcceptanceStatus
{
    Accepted,
    PartiallyAccepted,
    Rejected,
    Blocked,
    InvalidatedPlan
}

public sealed record TaskAcceptanceResult(
    TaskAcceptanceStatus Status,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<EvidenceRef> EvidenceRefs,
    bool RequiresGraphRefinement = false)
{
    public bool ShouldRefine =>
        RequiresGraphRefinement ||
        Status is TaskAcceptanceStatus.PartiallyAccepted
            or TaskAcceptanceStatus.Blocked
            or TaskAcceptanceStatus.InvalidatedPlan;
}
