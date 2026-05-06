using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Runs;
using Agentica.Validation;

namespace Agentica.CLI.Scenarios.WorkbenchQuest;

public sealed class WorkbenchQuestOutcomeReporter : IOutcomeReporter
{
    public OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers)
    {
        var claims = new List<ReportClaim>();
        var completedArtifact = run.Artifacts.FirstOrDefault(artifact => artifact.Kind == "workbench.objective_completed");
        if (completedArtifact is not null)
        {
            claims.Add(new ReportClaim(
                "The WorkbenchQuest objective was completed by the host completion tool.",
                [new EvidenceRef("artifact", completedArtifact.ArtifactId)]));
        }

        var failedCheck = run.Observations.FirstOrDefault(observation =>
            observation.Data.TryGetValue("status", out var value) &&
            string.Equals(value?.ToString(), "failed", StringComparison.Ordinal));
        if (failedCheck is not null)
        {
            claims.Add(new ReportClaim(
                "The agent captured a failing check before claiming completion.",
                [new EvidenceRef("observation", failedCheck.ObservationId)]));
        }

        var patchReceipt = run.Receipts.FirstOrDefault(receipt =>
            receipt.ToolId == WorkbenchQuestToolIds.ApplyPatch &&
            receipt.Status == ReceiptStatus.Succeeded);
        if (patchReceipt is not null)
        {
            claims.Add(new ReportClaim(
                "The agent changed scenario state through a receipt-backed patch.",
                [new EvidenceRef("receipt", patchReceipt.ReceiptId)]));
        }

        var passedCheck = run.Observations.LastOrDefault(observation =>
            observation.Data.TryGetValue("status", out var value) &&
            string.Equals(value?.ToString(), "passed", StringComparison.Ordinal));
        if (passedCheck is not null)
        {
            claims.Add(new ReportClaim(
                "The agent verified the patched state with a passing check.",
                [new EvidenceRef("observation", passedCheck.ObservationId)]));
        }

        if (validationIssues.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The WorkbenchQuest run stopped before unsafe execution because plan validation failed.",
                validationIssues.Select(issue => new EvidenceRef("validationIssue", issue.Code)).ToArray()));
        }

        if (blockers.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The WorkbenchQuest run stopped with blockers instead of inventing success.",
                [new EvidenceRef("stopReason", stopReason.ToString())]));
        }

        if (claims.Count == 0)
        {
            claims.Add(new ReportClaim(
                $"The WorkbenchQuest run stopped with status {status}.",
                [new EvidenceRef("stopReason", stopReason.ToString())]));
        }

        var summary = status switch
        {
            RunOutcomeStatus.Succeeded =>
                "The agent completed the WorkbenchQuest objective with check-backed and receipt-backed evidence.",
            RunOutcomeStatus.PlanInvalid =>
                "The WorkbenchQuest run stopped because the workflow plan failed validation before execution.",
            RunOutcomeStatus.Blocked =>
                $"The WorkbenchQuest run stopped as blocked. Stop reason: {stopReason}.",
            RunOutcomeStatus.PartiallyComplete =>
                $"The WorkbenchQuest run stopped partially complete. Stop reason: {stopReason}.",
            RunOutcomeStatus.Failed =>
                $"The WorkbenchQuest run failed. Stop reason: {stopReason}.",
            _ =>
                $"The WorkbenchQuest run stopped with status {status}. Stop reason: {stopReason}."
        };

        return new OutcomeReport(
            ReportId: AgenticaIds.New("report"),
            Summary: summary,
            Claims: claims);
    }
}
