using Agentica.Observations;

namespace Agentica.Outcomes;

public sealed record RunOutcome(
    string RunId,
    RunOutcomeStatus Status,
    StopReason StopReason,
    IReadOnlyList<string> CompletedSteps,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<EvidenceRef> CompletionEvidence);
