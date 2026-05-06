using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Runs;
using Agentica.Validation;

namespace Agentica.CLI.Scenarios.MazeQuest;

public sealed class MazeQuestOutcomeReporter : IOutcomeReporter
{
    public OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers)
    {
        var claims = new List<ReportClaim>();
        var completedArtifact = run.Artifacts.FirstOrDefault(artifact => artifact.Kind == "mazequest.objective_completed");
        if (completedArtifact is not null)
        {
            claims.Add(new ReportClaim(
                "The MazeQuest objective chain was completed.",
                [
                    new EvidenceRef("artifact", completedArtifact.ArtifactId)
                ]));
        }

        var moveReceipts = run.Receipts
            .Where(receipt => receipt.ToolId == MazeQuestToolIds.Move && receipt.Status == ReceiptStatus.Succeeded)
            .ToArray();
        if (moveReceipts.Length > 0)
        {
            claims.Add(new ReportClaim(
                "The agent traversed the maze using receipt-backed move actions.",
                moveReceipts.Take(3)
                    .Select(receipt => new EvidenceRef("receipt", receipt.ReceiptId))
                    .ToArray()));
        }

        var takeReceipt = run.Receipts.FirstOrDefault(receipt =>
            receipt.ToolId == MazeQuestToolIds.Take && receipt.Status == ReceiptStatus.Succeeded);
        if (takeReceipt is not null)
        {
            claims.Add(new ReportClaim(
                "The agent acquired a quest object through the host tool surface.",
                [
                    new EvidenceRef("receipt", takeReceipt.ReceiptId)
                ]));
        }

        if (validationIssues.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The MazeQuest run stopped before unsafe execution because plan validation failed.",
                validationIssues.Select(issue => new EvidenceRef("validationIssue", issue.Code)).ToArray()));
        }

        if (blockers.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The MazeQuest run stopped with blockers instead of inventing success.",
                [
                    new EvidenceRef("stopReason", stopReason.ToString())
                ]));
        }

        if (claims.Count == 0)
        {
            claims.Add(new ReportClaim(
                $"The MazeQuest run stopped with status {status}.",
                [
                    new EvidenceRef("stopReason", stopReason.ToString())
                ]));
        }

        var summary = status switch
        {
            RunOutcomeStatus.Succeeded =>
                "The agent completed the MazeQuest objective chain with receipt-backed evidence.",
            RunOutcomeStatus.PlanInvalid =>
                "The MazeQuest run stopped because the workflow plan failed validation before execution.",
            RunOutcomeStatus.Blocked =>
                $"The MazeQuest run stopped as blocked. Stop reason: {stopReason}.",
            RunOutcomeStatus.PartiallyComplete =>
                $"The MazeQuest run stopped partially complete. Stop reason: {stopReason}.",
            RunOutcomeStatus.Failed =>
                $"The MazeQuest run failed. Stop reason: {stopReason}.",
            _ =>
                $"The MazeQuest run stopped with status {status}. Stop reason: {stopReason}."
        };

        return new OutcomeReport(
            ReportId: AgenticaIds.New("report"),
            Summary: summary,
            Claims: claims);
    }
}
