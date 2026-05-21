using Agentica;
using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Runs;
using Agentica.Validation;

internal sealed class ChatOutcomeReporter : IOutcomeReporter
{
    public OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers)
    {
        var claims = new List<ReportClaim>();
        var responseArtifact = run.Artifacts.LastOrDefault(artifact => artifact.Kind == ChatArtifactKinds.Response);

        if (responseArtifact is not null)
        {
            claims.Add(new ReportClaim(
                "The chat host emitted an assistant response artifact.",
                [
                    new EvidenceRef("artifact", responseArtifact.ArtifactId),
                    .. responseArtifact.Evidence
                ]));
        }

        foreach (var imageArtifact in run.Artifacts.Where(artifact => artifact.Kind == ChatArtifactKinds.WorkspaceImage))
        {
            claims.Add(new ReportClaim(
                "The chat host generated a workspace image artifact.",
                [
                    new EvidenceRef("artifact", imageArtifact.ArtifactId),
                    .. imageArtifact.Evidence
                ]));
        }

        claims.AddRange(run.Receipts.Select(receipt =>
            new ReportClaim(
                $"{receipt.ToolId} returned {receipt.Status}: {receipt.Message}",
                [new EvidenceRef("receipt", receipt.ReceiptId)])));

        if (validationIssues.Count > 0)
        {
            claims.Add(new ReportClaim(
                "Plan validation failed before the chat turn could complete.",
                validationIssues.Select(issue => new EvidenceRef("validationIssue", issue.Code)).ToArray()));
        }

        if (blockers.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The chat turn stopped with blockers.",
                [new EvidenceRef("stopReason", stopReason.ToString())]));
        }

        if (claims.Count == 0)
        {
            claims.Add(new ReportClaim(
                $"The chat turn stopped with status {status}.",
                [new EvidenceRef("stopReason", stopReason.ToString())]));
        }

        var summary = responseArtifact is not null
            ? "The chat turn completed with a response artifact."
            : $"The chat turn stopped with status {status}. Stop reason: {stopReason}.";

        return new OutcomeReport(AgenticaIds.New("report"), summary, claims);
    }
}
