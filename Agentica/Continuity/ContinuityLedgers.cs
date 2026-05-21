using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Runs;
using Agentica.Validation;

namespace Agentica.Continuity;

public sealed record BreadcrumbLedger(IReadOnlyList<BreadcrumbEntry> Entries);

public sealed record BreadcrumbEntry(
    string EntryId,
    string RunId,
    int Sequence,
    string Kind,
    string Summary,
    string? StepId,
    string? ToolId,
    string? ReceiptId,
    string? ObservationId,
    string? PlanId,
    string? PhaseId,
    DateTimeOffset At,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record DivergenceLedger(IReadOnlyList<DivergenceEntry> Entries);

public sealed record DivergenceEntry(
    string DivergenceId,
    string RunId,
    int Sequence,
    string Expected,
    string Actual,
    string Severity,
    string Interpretation,
    string RecommendedAdjustment,
    DateTimeOffset At,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record ContinuitySummary(
    int BreadcrumbCount,
    int DivergenceCount,
    int PlanVersionCount,
    int PlanRefinementCount,
    int ReceiptCount,
    int RunAttemptCount,
    bool NarrativeReportRecommended,
    IReadOnlyList<string> RecommendationReasons);

public interface IDivergenceDetector
{
    IReadOnlyList<DivergenceEntry> Detect(
        AgenticaRun run,
        IReadOnlyList<ValidationIssue> validationIssues,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<string> blockers);
}

public sealed class RuleBasedDivergenceDetector : IDivergenceDetector
{
    public IReadOnlyList<DivergenceEntry> Detect(
        AgenticaRun run,
        IReadOnlyList<ValidationIssue> validationIssues,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<string> blockers)
    {
        var entries = new List<DivergenceEntry>();
        var sequence = 0;

        foreach (var issue in validationIssues)
        {
            entries.Add(Entry(
                run,
                ++sequence,
                Expected: "Submitted plans should satisfy execution validation before tool execution.",
                Actual: $"Validation issue {issue.Code}: {issue.Message}",
                Severity: "high",
                Interpretation: "The runtime rejected the plan before treating it as executable truth.",
                RecommendedAdjustment: "Repair the plan shape, dependencies, tool ids, and step scopes before retrying.",
                EvidenceRefs: [new EvidenceRef("validationIssue", issue.Code)]));
        }

        foreach (var receipt in run.Receipts)
        {
            if (receipt.Status is ReceiptStatus.Refused or ReceiptStatus.Unavailable or ReceiptStatus.Failed or
                ReceiptStatus.TimedOut or ReceiptStatus.Cancelled)
            {
                entries.Add(Entry(
                    run,
                    ++sequence,
                    Expected: $"Tool {receipt.ToolId} would complete its scoped step.",
                    Actual: $"Receipt {receipt.ReceiptId} status was {receipt.Status}: {receipt.Message}",
                    Severity: ReceiptSeverity(receipt.Status),
                    Interpretation: "The attempted tool result is reality feedback, not committed success.",
                    RecommendedAdjustment: ReceiptAdjustment(receipt.Status),
                    EvidenceRefs: [new EvidenceRef("receipt", receipt.ReceiptId)]));
            }

            if (receipt.Status == ReceiptStatus.Succeeded && IndicatesNoMutation(receipt))
            {
                entries.Add(Entry(
                    run,
                    ++sequence,
                    Expected: $"Mutation-capable step {receipt.StepId} would change state.",
                    Actual: $"Receipt {receipt.ReceiptId} succeeded but reported unchanged state.",
                    Severity: "medium",
                    Interpretation: "A successful receipt can still have narrower scope than the planner expected.",
                    RecommendedAdjustment: "Bind future claims to the receipt's actual state delta instead of the intended mutation.",
                    EvidenceRefs: [new EvidenceRef("receipt", receipt.ReceiptId)]));
            }
        }

        foreach (var refinement in run.PlanRefinements)
        {
            if (LooksLikeBlockingRefinement(refinement.Reason))
            {
                entries.Add(Entry(
                    run,
                    ++sequence,
                    Expected: $"Plan {refinement.FromPlanId} would remain executable.",
                    Actual: $"Plan refined to {refinement.ToPlanId} because {refinement.Reason}.",
                    Severity: "medium",
                    Interpretation: "The run had to revise its intended path after public feedback.",
                    RecommendedAdjustment: "Preserve the triggering evidence as a constraint in the next plan.",
                    EvidenceRefs: refinement.Evidence));
            }
        }

        foreach (var executionEvent in run.Events)
        {
            if (executionEvent.Diagnostics is { } diagnostics && IsDiagnosticDivergence(diagnostics))
            {
                entries.Add(Entry(
                    run,
                    ++sequence,
                    Expected: $"{executionEvent.Type} would complete without runtime diagnostics.",
                    Actual: $"{diagnostics.Code}: {diagnostics.Message}",
                    Severity: DiagnosticSeverity(diagnostics),
                    Interpretation: "Runtime diagnostics are expected-vs-actual evidence and should not be hidden by retries or summaries.",
                    RecommendedAdjustment: DiagnosticAdjustment(diagnostics),
                    EvidenceRefs: executionEvent.EvidenceRefs));
            }
        }

        if (status is RunOutcomeStatus.Failed or RunOutcomeStatus.PlanInvalid or RunOutcomeStatus.Blocked or RunOutcomeStatus.Cancelled)
        {
            entries.Add(Entry(
                run,
                ++sequence,
                Expected: "The run would satisfy its objective.",
                Actual: $"Run ended as {status} with stop reason {stopReason}.",
                Severity: status == RunOutcomeStatus.Blocked ? "medium" : "high",
                Interpretation: "The terminal outcome is verifier/runtime truth regardless of planner narration.",
                RecommendedAdjustment: blockers.Count == 0
                    ? "Use terminal outcome evidence for postmortem and next-run setup."
                    : $"Resolve blocker: {Compact(blockers[0])}",
                EvidenceRefs: stopReason == StopReason.PlanInvalid
                    ? validationIssues.Select(issue => new EvidenceRef("validationIssue", issue.Code)).ToArray()
                    : []));
        }

        return entries;
    }

    private static DivergenceEntry Entry(
        AgenticaRun run,
        int sequence,
        string Expected,
        string Actual,
        string Severity,
        string Interpretation,
        string RecommendedAdjustment,
        IReadOnlyList<EvidenceRef> EvidenceRefs) =>
        new(
            DivergenceId: $"divergence_{sequence:0000}",
            RunId: run.RunId,
            Sequence: sequence,
            Expected: Compact(Expected),
            Actual: Compact(Actual),
            Severity: Severity,
            Interpretation: Compact(Interpretation),
            RecommendedAdjustment: Compact(RecommendedAdjustment),
            At: DateTimeOffset.UtcNow,
            EvidenceRefs: EvidenceRefs);

    private static bool IndicatesNoMutation(Receipt receipt) =>
        (receipt.Data.TryGetValue("fenUnchanged", out var fenUnchanged) && IsTrue(fenUnchanged)) ||
        (receipt.Data.TryGetValue("stateChanged", out var stateChanged) && IsFalse(stateChanged)) ||
        (receipt.Data.TryGetValue("mutationApplied", out var mutationApplied) && IsFalse(mutationApplied));

    private static bool IsTrue(object? value) =>
        value switch
        {
            bool boolValue => boolValue,
            string stringValue when bool.TryParse(stringValue, out var parsed) => parsed,
            _ => false
        };

    private static bool IsFalse(object? value) =>
        value switch
        {
            bool boolValue => !boolValue,
            string stringValue when bool.TryParse(stringValue, out var parsed) => !parsed,
            _ => false
        };

    private static bool LooksLikeBlockingRefinement(string reason) =>
        ContainsAny(reason, "blocked", "invalid", "refused", "failed", "stale", "repair", "conflict");

    private static bool IsDiagnosticDivergence(ExecutionDiagnostics diagnostics) =>
        ContainsAny(diagnostics.Code, "failed", "invalid", "cancelled", "timeout", "truncated", "unavailable") ||
        ContainsAny(diagnostics.Message, "failed", "invalid", "cancelled", "timeout", "truncated", "MaxTokens", "unavailable");

    private static string DiagnosticSeverity(ExecutionDiagnostics diagnostics) =>
        ContainsAny(diagnostics.Message, "MaxTokens", "truncated", "invalid") ? "high" : "medium";

    private static string DiagnosticAdjustment(ExecutionDiagnostics diagnostics) =>
        ContainsAny(diagnostics.Message, "MaxTokens", "truncated")
            ? "Increase output budget or reduce requested structure before retry; preserve truncation as diagnostic truth."
            : "Repair the failing planner/tool condition before retrying.";

    private static string ReceiptSeverity(ReceiptStatus status) =>
        status switch
        {
            ReceiptStatus.Failed or ReceiptStatus.TimedOut or ReceiptStatus.Cancelled => "high",
            _ => "medium"
        };

    private static string ReceiptAdjustment(ReceiptStatus status) =>
        status switch
        {
            ReceiptStatus.Refused => "Repair the refused inputs or choose another valid path.",
            ReceiptStatus.Unavailable => "Treat unavailable capability as a blocker unless another public evidence path exists.",
            ReceiptStatus.TimedOut => "Reduce scope or increase timeout before retrying.",
            ReceiptStatus.Cancelled => "Confirm cancellation source before continuing.",
            _ => "Recover from the tool failure with new evidence or report the blocker."
        };

    private static bool ContainsAny(string? value, params string[] needles) =>
        !string.IsNullOrWhiteSpace(value) &&
        needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 320 ? compact : compact[..317] + "...";
    }
}

public sealed class ContinuityLedgerCompiler
{
    private readonly IDivergenceDetector _divergenceDetector;

    public ContinuityLedgerCompiler(IDivergenceDetector? divergenceDetector = null)
    {
        _divergenceDetector = divergenceDetector ?? new RuleBasedDivergenceDetector();
    }

    public BreadcrumbLedger CompileBreadcrumbLedger(AgenticaRun run)
    {
        var sequence = 0;
        var entries = run.Events
            .OrderBy(executionEvent => executionEvent.Sequence ?? long.MaxValue)
            .ThenBy(executionEvent => executionEvent.At)
            .Select(executionEvent => BreadcrumbFromEvent(run, executionEvent, ++sequence))
            .ToArray();

        return new BreadcrumbLedger(entries);
    }

    public DivergenceLedger CompileDivergenceLedger(
        AgenticaRun run,
        IReadOnlyList<ValidationIssue> validationIssues,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<string> blockers) =>
        new(_divergenceDetector.Detect(run, validationIssues, status, stopReason, blockers));

    public ContinuitySummary CompileSummary(
        AgenticaRun run,
        BreadcrumbLedger breadcrumbs,
        DivergenceLedger divergences,
        IReadOnlyList<ValidationIssue> validationIssues,
        RunOutcomeStatus status)
    {
        var reasons = new List<string>();
        if (run.AttemptNumber >= 5)
        {
            reasons.Add("run attempt count reached checkpoint threshold");
        }

        if (run.PlanVersions.Count >= 5)
        {
            reasons.Add("plan version count reached checkpoint threshold");
        }

        if (run.PlanRefinements.Count >= 5)
        {
            reasons.Add("plan refinement count reached checkpoint threshold");
        }

        if (run.Receipts.Count >= 10)
        {
            reasons.Add("receipt count indicates a high-detail execution trace");
        }

        if (divergences.Entries.Count >= 3)
        {
            reasons.Add("multiple divergences need synthesis");
        }

        if (validationIssues.Count > 0 || status != RunOutcomeStatus.Succeeded)
        {
            reasons.Add("terminal outcome needs postmortem interpretation");
        }

        return new ContinuitySummary(
            BreadcrumbCount: breadcrumbs.Entries.Count,
            DivergenceCount: divergences.Entries.Count,
            PlanVersionCount: run.PlanVersions.Count,
            PlanRefinementCount: run.PlanRefinements.Count,
            ReceiptCount: run.Receipts.Count,
            RunAttemptCount: run.AttemptNumber,
            NarrativeReportRecommended: reasons.Count > 0,
            RecommendationReasons: reasons.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static BreadcrumbEntry BreadcrumbFromEvent(
        AgenticaRun run,
        ExecutionEvent executionEvent,
        int sequence)
    {
        var context = executionEvent.Context;
        return new BreadcrumbEntry(
            EntryId: $"breadcrumb_{sequence:0000}",
            RunId: run.RunId,
            Sequence: sequence,
            Kind: executionEvent.Type,
            Summary: Summary(executionEvent),
            StepId: context?.StepId,
            ToolId: context?.ToolId,
            ReceiptId: context?.ReceiptId,
            ObservationId: context?.ObservationId,
            PlanId: context?.PlanId ?? context?.ToPlanId ?? context?.FromPlanId,
            PhaseId: ReadPhaseId(executionEvent),
            At: executionEvent.At,
            EvidenceRefs: executionEvent.EvidenceRefs);
    }

    private static string Summary(ExecutionEvent executionEvent)
    {
        if (executionEvent.UserFacingReason is { } userReason)
        {
            return Compact(userReason.Detail is null
                ? userReason.Summary
                : $"{userReason.Summary} {userReason.Detail}");
        }

        if (executionEvent.Intent is { } intent)
        {
            return Compact(string.Join(
                " ",
                new[] { intent.Action, intent.Rationale, intent.ExpectedOutcome }
                    .Where(item => !string.IsNullOrWhiteSpace(item))));
        }

        if (executionEvent.Diagnostics is { } diagnostics)
        {
            return Compact($"{diagnostics.Code}: {diagnostics.Message}");
        }

        if (executionEvent.Data.Count > 0)
        {
            return Compact($"{executionEvent.Type}: {string.Join("; ", executionEvent.Data.Select(item => $"{item.Key}={item.Value}"))}");
        }

        return executionEvent.Type;
    }

    private static string? ReadPhaseId(ExecutionEvent executionEvent)
    {
        if (executionEvent.Payload.TryGetValue("phaseRunId", out var phaseRunId) && phaseRunId is not null)
        {
            return Convert.ToString(phaseRunId);
        }

        if (executionEvent.Payload.TryGetValue("phaseId", out var phaseId) && phaseId is not null)
        {
            return Convert.ToString(phaseId);
        }

        return null;
    }

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 320 ? compact : compact[..317] + "...";
    }
}
