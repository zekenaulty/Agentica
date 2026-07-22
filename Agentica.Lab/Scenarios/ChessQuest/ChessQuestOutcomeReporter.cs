using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Runs;
using Agentica.Validation;

namespace Agentica.Lab.Scenarios.ChessQuest;

public sealed class ChessQuestOutcomeReporter : IOutcomeReporter
{
    public OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers)
    {
        var claims = new List<ReportClaim>();
        var objectiveArtifact = run.Artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Kind, "chessquest.objective_completed", StringComparison.Ordinal));
        if (objectiveArtifact is not null)
        {
            claims.Add(new ReportClaim(
                "The ChessQuest objective was completed by verified board state.",
                [new EvidenceRef("artifact", objectiveArtifact.ArtifactId)]));
        }

        foreach (var receipt in run.Receipts.Where(receipt =>
                     string.Equals(receipt.ToolId, ChessQuestToolIds.PlayMove, StringComparison.Ordinal) &&
                     receipt.Status == ReceiptStatus.Succeeded))
        {
            claims.Add(new ReportClaim(
                "The agent committed a ChessQuest move through the strict tool surface.",
                [new EvidenceRef("receipt", receipt.ReceiptId)]));
        }

        if (validationIssues.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The ChessQuest run stopped before execution because plan validation failed.",
                validationIssues.Select(issue => new EvidenceRef("validationIssue", issue.Code)).ToArray()));
        }

        if (blockers.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The ChessQuest run stopped with blockers instead of inventing success.",
                [new EvidenceRef("stopReason", stopReason.ToString())]));
        }

        if (claims.Count == 0)
        {
            claims.Add(new ReportClaim(
                $"The ChessQuest run stopped with status {status}.",
                [new EvidenceRef("stopReason", stopReason.ToString())]));
        }

        var summary = status switch
        {
            RunOutcomeStatus.Succeeded =>
                "The agent completed ChessQuest using receipt-backed board verification.",
            RunOutcomeStatus.PlanInvalid =>
                "The ChessQuest run stopped because the workflow plan failed validation before execution.",
            RunOutcomeStatus.Blocked =>
                $"The ChessQuest run stopped as blocked. Stop reason: {stopReason}.",
            RunOutcomeStatus.Failed =>
                $"The ChessQuest run failed. Stop reason: {stopReason}.",
            _ =>
                $"The ChessQuest run stopped with status {status}. Stop reason: {stopReason}."
        };

        return new OutcomeReport(
            ReportId: AgenticaIds.New("report"),
            Summary: summary,
            Claims: claims);
    }
}
