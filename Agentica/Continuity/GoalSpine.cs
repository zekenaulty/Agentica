using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Continuity;

public sealed record GoalSpine(
    string Kind,
    string Version,
    DateTimeOffset UpdatedAt,
    string RootGoal,
    string CurrentReality,
    string ActivePriority,
    string LatestRealityUpdate,
    string? KnownDivergence,
    string NextDecisionPressure,
    IReadOnlyList<string> ActiveConstraints,
    IReadOnlyList<string> RecentLessons,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record GoalSpineUpdate(
    GoalSpine Spine,
    string UpdateKind,
    string Summary,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record GoalSpineUpdateContext(
    string RunId,
    int AttemptNumber,
    IReadOnlyDictionary<string, object?> RequestContext,
    PlanningExecutionContext ExecutionContext);

public sealed record GoalSpineOptions(
    int MaxConstraints = 8,
    int MaxLessons = 8,
    int MaxOpenQuestions = 6,
    int MaxEvidenceRefs = 16,
    int MaxTextLength = 240);

public interface IGoalSpineCompiler
{
    GoalSpine CompileInitial(RunRequest request);

    GoalSpineUpdate UpdateFromReceipt(
        GoalSpine current,
        Receipt receipt,
        GoalSpineUpdateContext context);

    GoalSpineUpdate UpdateFromRefinement(
        GoalSpine current,
        PlanRefinement refinement,
        GoalSpineUpdateContext context);
}

public sealed class DefaultGoalSpineCompiler : IGoalSpineCompiler
{
    public DefaultGoalSpineCompiler(GoalSpineOptions? options = null)
    {
        Options = options ?? new GoalSpineOptions();
    }

    public GoalSpineOptions Options { get; }

    public GoalSpine CompileInitial(RunRequest request)
    {
        var constraints = new[]
        {
            "GoalSpine shapes continuity only; receipts, observations, artifacts, host checks, and verifiers prove reality.",
            "Do not store or reconstruct hidden chain-of-thought.",
            "Keep the next plan slice bounded and evidence-grounded.",
            "Treat model intent, status prose, and continuity notes as guidance, not completion proof."
        };
        var openQuestions = new[]
        {
            "What public evidence is still needed to satisfy the objective?"
        };

        return Bound(new GoalSpine(
            Kind: "agentica.goal_spine",
            Version: "1.0",
            UpdatedAt: DateTimeOffset.UtcNow,
            RootGoal: request.Objective,
            CurrentReality: "Run accepted; no receipt-backed execution has been observed yet.",
            ActivePriority: "Establish public state and make the next bounded progress step.",
            LatestRealityUpdate: "Initial run request accepted.",
            KnownDivergence: null,
            NextDecisionPressure:
                "Use request context, tool descriptors, observations, and receipts; do not treat intent narration as proof.",
            ActiveConstraints: constraints,
            RecentLessons: [],
            OpenQuestions: openQuestions,
            EvidenceRefs: []));
    }

    public GoalSpineUpdate UpdateFromReceipt(
        GoalSpine current,
        Receipt receipt,
        GoalSpineUpdateContext context)
    {
        var evidenceRefs = MergeEvidence(
            current.EvidenceRefs,
            new EvidenceRef("receipt", receipt.ReceiptId));
        var latestReality = ReceiptReality(receipt);
        var currentReality = ReceiptCurrentReality(receipt);
        var activePriority = ReceiptActivePriority(receipt, current.ActivePriority);
        var knownDivergence = ReceiptDivergence(receipt, current.KnownDivergence);
        var nextDecisionPressure = ReceiptDecisionPressure(receipt);
        var lessons = Merge(
            current.RecentLessons,
            ReceiptLessons(receipt),
            Options.MaxLessons);

        var updated = Bound(current with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentReality = currentReality,
            ActivePriority = activePriority,
            LatestRealityUpdate = latestReality,
            KnownDivergence = knownDivergence,
            NextDecisionPressure = nextDecisionPressure,
            RecentLessons = lessons,
            EvidenceRefs = evidenceRefs
        });

        return new GoalSpineUpdate(
            updated,
            "receipt",
            latestReality,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
    }

    public GoalSpineUpdate UpdateFromRefinement(
        GoalSpine current,
        PlanRefinement refinement,
        GoalSpineUpdateContext context)
    {
        var evidenceRefs = MergeEvidence(current.EvidenceRefs, refinement.Evidence);
        var latestReality = Compact(
            $"Plan refined from {refinement.FromPlanId} to {refinement.ToPlanId}: {refinement.Reason}");
        var knownDivergence = RefinementDivergence(refinement, current.KnownDivergence);
        var lessons = Merge(
            current.RecentLessons,
            RefinementLessons(refinement),
            Options.MaxLessons);

        var updated = Bound(current with
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            CurrentReality = "The workflow plan was refined using public observations, receipts, or validation context.",
            ActivePriority = "Continue from the latest refined plan while preserving receipt-backed constraints.",
            LatestRealityUpdate = latestReality,
            KnownDivergence = knownDivergence,
            NextDecisionPressure =
                "Use the latest observations, receipts, and completed-step context; avoid repeating a step already completed.",
            RecentLessons = lessons,
            EvidenceRefs = evidenceRefs
        });

        return new GoalSpineUpdate(
            updated,
            "refinement",
            latestReality,
            refinement.Evidence);
    }

    private string ReceiptReality(Receipt receipt) =>
        Compact(receipt.Status switch
        {
            ReceiptStatus.Succeeded =>
                $"Receipt {receipt.ReceiptId} succeeded for {receipt.ToolId}: {receipt.Message}",
            ReceiptStatus.Accepted =>
                $"Receipt {receipt.ReceiptId} was accepted for {receipt.ToolId}: {receipt.Message}",
            ReceiptStatus.Partial =>
                $"Receipt {receipt.ReceiptId} was partial for {receipt.ToolId}: {receipt.Message}",
            ReceiptStatus.Refused =>
                $"Receipt {receipt.ReceiptId} refused {receipt.ToolId}: {receipt.Message}",
            ReceiptStatus.Unavailable =>
                $"Receipt {receipt.ReceiptId} reports {receipt.ToolId} unavailable: {receipt.Message}",
            ReceiptStatus.TimedOut =>
                $"Receipt {receipt.ReceiptId} timed out for {receipt.ToolId}: {receipt.Message}",
            ReceiptStatus.Cancelled =>
                $"Receipt {receipt.ReceiptId} cancelled {receipt.ToolId}: {receipt.Message}",
            ReceiptStatus.WaitingForApproval =>
                $"Receipt {receipt.ReceiptId} is waiting for approval for {receipt.ToolId}: {receipt.Message}",
            _ =>
                $"Receipt {receipt.ReceiptId} failed for {receipt.ToolId}: {receipt.Message}"
        });

    private static string ReceiptCurrentReality(Receipt receipt) =>
        receipt.Status switch
        {
            ReceiptStatus.Succeeded =>
                "Latest receipt succeeded; it is execution evidence, not a blanket proof of objective completion.",
            ReceiptStatus.Accepted or ReceiptStatus.Partial =>
                "Latest receipt made partial/accepted progress; verify remaining objective conditions separately.",
            ReceiptStatus.Refused or ReceiptStatus.Unavailable =>
                "Latest tool attempt did not commit the requested progress.",
            ReceiptStatus.Failed or ReceiptStatus.TimedOut or ReceiptStatus.Cancelled =>
                "Latest tool attempt failed before completing the intended progress.",
            ReceiptStatus.WaitingForApproval =>
                "Latest tool attempt is waiting for approval and should not be treated as completed.",
            _ =>
                "Latest receipt updated the run state."
        };

    private static string ReceiptActivePriority(Receipt receipt, string fallback) =>
        receipt.Status switch
        {
            ReceiptStatus.Succeeded =>
                "Use the receipt-backed result as current public reality and choose the next bounded step.",
            ReceiptStatus.Accepted or ReceiptStatus.Partial =>
                "Continue from partial progress and verify what remains unsatisfied.",
            ReceiptStatus.Refused =>
                "Repair the refused action or choose another valid path; do not treat the refused attempt as committed.",
            ReceiptStatus.Unavailable =>
                "Handle the unavailable tool/state surface or choose a different public evidence path.",
            ReceiptStatus.Failed or ReceiptStatus.TimedOut or ReceiptStatus.Cancelled =>
                "Recover from the tool failure with a bounded plan or surface a blocker.",
            ReceiptStatus.WaitingForApproval =>
                "Wait for approval or choose a safe non-mutating path when policy allows.",
            _ => fallback
        };

    private static string? ReceiptDivergence(Receipt receipt, string? fallback) =>
        receipt.Status switch
        {
            ReceiptStatus.Refused =>
                $"Expected tool progress diverged from reality: {receipt.ToolId} was refused.",
            ReceiptStatus.Unavailable =>
                $"Expected tool progress diverged from reality: {receipt.ToolId} was unavailable.",
            ReceiptStatus.Failed =>
                $"Expected tool progress diverged from reality: {receipt.ToolId} failed.",
            ReceiptStatus.TimedOut =>
                $"Expected tool progress diverged from reality: {receipt.ToolId} timed out.",
            ReceiptStatus.Cancelled =>
                $"Expected tool progress diverged from reality: {receipt.ToolId} was cancelled.",
            _ => fallback
        };

    private static string ReceiptDecisionPressure(Receipt receipt) =>
        receipt.Status switch
        {
            ReceiptStatus.Succeeded =>
                "Plan from receipt-backed state. If claiming completion, cite objective-verifier evidence rather than continuity notes.",
            ReceiptStatus.Accepted or ReceiptStatus.Partial =>
                "Identify the remaining unsatisfied conditions before claiming task or run completion.",
            ReceiptStatus.Refused =>
                "Use the refusal reason as reality feedback; repair inputs or switch paths instead of repeating the same invalid action.",
            ReceiptStatus.Unavailable =>
                "Treat the missing surface as a blocker unless another available tool can provide the required public evidence.",
            ReceiptStatus.Failed or ReceiptStatus.TimedOut or ReceiptStatus.Cancelled =>
                "Avoid recursive retries without new evidence; choose a bounded recovery step or report the blocker.",
            ReceiptStatus.WaitingForApproval =>
                "Do not continue as though the action completed; wait for or request the required approval.",
            _ =>
                "Continue with evidence-grounded planning."
        };

    private static IReadOnlyList<string> ReceiptLessons(Receipt receipt) =>
        receipt.Status switch
        {
            ReceiptStatus.Refused =>
            [
                "A refused receipt is not a committed state mutation.",
                "Repair should address the specific refusal reason instead of repeating the same action."
            ],
            ReceiptStatus.Unavailable =>
            [
                "Tool or state unavailability is a blocker unless another public evidence path exists."
            ],
            ReceiptStatus.Failed or ReceiptStatus.TimedOut or ReceiptStatus.Cancelled =>
            [
                "Tool failure is reality feedback; retries need a changed condition or explicit recovery reason."
            ],
            ReceiptStatus.WaitingForApproval =>
            [
                "Approval-gated actions remain uncompleted until an approval-backed receipt exists."
            ],
            ReceiptStatus.Succeeded =>
            [
                "Successful tool receipts prove their scoped result only; objective completion still requires matching completion evidence."
            ],
            _ => []
        };

    private static string? RefinementDivergence(PlanRefinement refinement, string? fallback)
    {
        var reason = refinement.Reason.ToLowerInvariant();
        if (reason.Contains("blocked", StringComparison.Ordinal) ||
            reason.Contains("invalid", StringComparison.Ordinal) ||
            reason.Contains("refused", StringComparison.Ordinal) ||
            reason.Contains("failed", StringComparison.Ordinal))
        {
            return $"Plan changed after a blocking or invalid condition: {refinement.Reason}.";
        }

        return fallback;
    }

    private static IReadOnlyList<string> RefinementLessons(PlanRefinement refinement)
    {
        var reason = refinement.Reason.ToLowerInvariant();
        return reason.Contains("observation", StringComparison.Ordinal)
            ? ["Refinement should preserve new public observations as constraints for the next plan slice."]
            : ["Refinement updates continuity, but refined intent is not execution proof."];
    }

    private GoalSpine Bound(GoalSpine spine) =>
        spine with
        {
            RootGoal = Compact(spine.RootGoal),
            CurrentReality = Compact(spine.CurrentReality),
            ActivePriority = Compact(spine.ActivePriority),
            LatestRealityUpdate = Compact(spine.LatestRealityUpdate),
            KnownDivergence = spine.KnownDivergence is null ? null : Compact(spine.KnownDivergence),
            NextDecisionPressure = Compact(spine.NextDecisionPressure),
            ActiveConstraints = LimitStrings(spine.ActiveConstraints, Options.MaxConstraints),
            RecentLessons = LimitStrings(spine.RecentLessons, Options.MaxLessons),
            OpenQuestions = LimitStrings(spine.OpenQuestions, Options.MaxOpenQuestions),
            EvidenceRefs = spine.EvidenceRefs.Take(Options.MaxEvidenceRefs).ToArray()
        };

    private IReadOnlyList<string> LimitStrings(IReadOnlyList<string> values, int limit) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Compact)
            .Distinct(StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .ToArray();

    private IReadOnlyList<string> Merge(
        IReadOnlyList<string> current,
        IReadOnlyList<string> next,
        int limit) =>
        current
            .Concat(next)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Compact)
            .Distinct(StringComparer.Ordinal)
            .TakeLast(Math.Max(0, limit))
            .ToArray();

    private IReadOnlyList<EvidenceRef> MergeEvidence(
        IReadOnlyList<EvidenceRef> current,
        params EvidenceRef[] next) =>
        MergeEvidence(current, (IReadOnlyList<EvidenceRef>)next);

    private IReadOnlyList<EvidenceRef> MergeEvidence(
        IReadOnlyList<EvidenceRef> current,
        IReadOnlyList<EvidenceRef> next) =>
        current
            .Concat(next)
            .Where(item => !string.IsNullOrWhiteSpace(item.Kind) && !string.IsNullOrWhiteSpace(item.RefId))
            .DistinctBy(item => $"{item.Kind}:{item.RefId}")
            .TakeLast(Math.Max(0, Options.MaxEvidenceRefs))
            .ToArray();

    private string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (Options.MaxTextLength <= 0 || compact.Length <= Options.MaxTextLength)
        {
            return compact;
        }

        return compact[..Math.Max(0, Options.MaxTextLength - 3)] + "...";
    }
}
