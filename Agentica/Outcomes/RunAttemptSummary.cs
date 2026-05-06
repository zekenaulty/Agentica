namespace Agentica.Outcomes;

public sealed record RunAttemptSummary(
    int AttemptNumber,
    string RunId,
    RunOutcomeStatus Status,
    StopReason StopReason,
    IReadOnlyList<string> CompletedSteps,
    IReadOnlyList<string> Blockers)
{
    public static RunAttemptSummary From(int attemptNumber, OutcomeEnvelope envelope) =>
        new(
            attemptNumber,
            envelope.Outcome.RunId,
            envelope.Outcome.Status,
            envelope.Outcome.StopReason,
            envelope.Outcome.CompletedSteps,
            envelope.Outcome.Blockers);
}
