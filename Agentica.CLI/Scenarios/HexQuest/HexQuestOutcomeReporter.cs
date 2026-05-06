using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Runs;
using Agentica.Validation;

namespace Agentica.CLI.Scenarios.HexQuest;

public sealed class HexQuestOutcomeReporter : IOutcomeReporter
{
    public OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers)
    {
        var claims = new List<ReportClaim>();
        var completedArtifact = run.Artifacts.FirstOrDefault(artifact => artifact.Kind == "hexquest.objective_completed");
        if (completedArtifact is not null)
        {
            claims.Add(new ReportClaim(
                "The HexQuest objective was completed by the host commit tool.",
                [new EvidenceRef("artifact", completedArtifact.ArtifactId)]));
        }

        var commitReceipt = run.Receipts.FirstOrDefault(receipt =>
            receipt.ToolId == HexQuestToolIds.CommitPatch &&
            receipt.Status == ReceiptStatus.Succeeded);
        if (commitReceipt is not null)
        {
            claims.Add(new ReportClaim(
                "The winning mutation was an encoded payload patch.",
                [new EvidenceRef("receipt", commitReceipt.ReceiptId)]));
        }

        var validation = run.Observations.FirstOrDefault(observation =>
            observation.Data.TryGetValue("action", out var action) &&
            string.Equals(action?.ToString(), "validate_patch", StringComparison.Ordinal));
        if (validation is not null)
        {
            claims.Add(new ReportClaim(
                "The agent dry-ran the encoded patch before commit.",
                [new EvidenceRef("observation", validation.ObservationId)]));
        }

        if (validationIssues.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The HexQuest run stopped before unsafe execution because plan validation failed.",
                validationIssues.Select(issue => new EvidenceRef("validationIssue", issue.Code)).ToArray()));
        }

        if (blockers.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The HexQuest run stopped with blockers instead of inventing success.",
                [new EvidenceRef("stopReason", stopReason.ToString())]));
        }

        if (claims.Count == 0)
        {
            claims.Add(new ReportClaim(
                $"The HexQuest run stopped with status {status}.",
                [new EvidenceRef("stopReason", stopReason.ToString())]));
        }

        var summary = status == RunOutcomeStatus.Succeeded
            ? "The agent completed HexQuest by committing a receipt-backed encoded payload patch."
            : $"The HexQuest run stopped with status {status}. Stop reason: {stopReason}.";

        return new OutcomeReport(AgenticaIds.New("report"), summary, claims);
    }
}
