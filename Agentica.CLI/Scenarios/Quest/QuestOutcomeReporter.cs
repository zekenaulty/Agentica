using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Runs;
using Agentica.Validation;

namespace Agentica.CLI.Scenarios.Quest;

public sealed class QuestOutcomeReporter : IOutcomeReporter
{
    public OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers)
    {
        var claims = new List<ReportClaim>();
        AddReceiptClaim(
            claims,
            run,
            QuestToolIds.Take,
            receipt => string.Equals(receipt.Data.GetValueOrDefault("item")?.ToString(), "sun_key", StringComparison.Ordinal),
            "The agent recovered the sun key.");

        AddReceiptClaim(
            claims,
            run,
            QuestToolIds.Use,
            receipt => string.Equals(receipt.Data.GetValueOrDefault("openedLock")?.ToString(), "sun_gate", StringComparison.Ordinal),
            "The agent opened the sun gate with the sun key.");

        var objectiveArtifact = run.Artifacts.FirstOrDefault(artifact => artifact.Kind == "quest.objective_completed");
        if (objectiveArtifact is not null)
        {
            claims.Add(new ReportClaim(
                "The quest objective was completed.",
                [
                    new EvidenceRef("artifact", objectiveArtifact.ArtifactId)
                ]));
        }

        var refusedReceipt = run.Receipts.FirstOrDefault(receipt => receipt.Status == ReceiptStatus.Refused);
        if (refusedReceipt is not null && status == RunOutcomeStatus.Succeeded)
        {
            claims.Add(new ReportClaim(
                "The run recovered from a refused quest action instead of treating narration as success.",
                [
                    new EvidenceRef("receipt", refusedReceipt.ReceiptId)
                ]));
        }

        if (validationIssues.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The quest run stopped before execution because plan validation failed.",
                validationIssues.Select(issue => new EvidenceRef("validationIssue", issue.Code)).ToArray()));
        }

        if (blockers.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The quest run stopped with blockers instead of inventing success.",
                [
                    new EvidenceRef("stopReason", stopReason.ToString())
                ]));
        }

        if (claims.Count == 0)
        {
            claims.Add(new ReportClaim(
                $"The quest run stopped with status {status}.",
                [
                    new EvidenceRef("stopReason", stopReason.ToString())
                ]));
        }

        var summary = status switch
        {
            RunOutcomeStatus.Succeeded =>
                "The agent completed the quest using receipt-backed tool execution.",
            RunOutcomeStatus.PlanInvalid =>
                "The quest run stopped because the workflow plan failed validation before execution.",
            RunOutcomeStatus.Blocked =>
                $"The quest run stopped as blocked. Stop reason: {stopReason}.",
            RunOutcomeStatus.Failed =>
                $"The quest run failed. Stop reason: {stopReason}.",
            _ =>
                $"The quest run stopped with status {status}. Stop reason: {stopReason}."
        };

        return new OutcomeReport(
            ReportId: AgenticaIds.New("report"),
            Summary: summary,
            Claims: claims);
    }

    private static void AddReceiptClaim(
        ICollection<ReportClaim> claims,
        AgenticaRun run,
        string toolId,
        Func<Receipt, bool> predicate,
        string text)
    {
        var receipt = run.Receipts.FirstOrDefault(receipt =>
            receipt.ToolId == toolId &&
            receipt.Status == ReceiptStatus.Succeeded &&
            predicate(receipt));

        if (receipt is not null)
        {
            claims.Add(new ReportClaim(
                text,
                [
                    new EvidenceRef("receipt", receipt.ReceiptId)
                ]));
        }
    }
}
