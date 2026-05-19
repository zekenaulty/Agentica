using Agentica.Outcomes;

namespace Agentica.Execution;

public sealed record CompletionEvaluation(
    CompletionDecision Decision,
    StopReason StopReason,
    IReadOnlyList<string> Blockers)
{
    public static CompletionEvaluation Complete() =>
        new(CompletionDecision.Complete, StopReason.Complete, []);

    public static CompletionEvaluation Continue(params string[] blockers) =>
        new(CompletionDecision.Continue, StopReason.CompletionNotSatisfied, blockers);

    public static CompletionEvaluation Blocked(StopReason stopReason, params string[] blockers) =>
        new(CompletionDecision.Blocked, stopReason, blockers);

    public static CompletionEvaluation Partial(params string[] blockers) =>
        new(CompletionDecision.Partial, StopReason.Partial, blockers);

    public static CompletionEvaluation Failed(StopReason stopReason, params string[] blockers) =>
        new(CompletionDecision.Failed, stopReason, blockers);
}
