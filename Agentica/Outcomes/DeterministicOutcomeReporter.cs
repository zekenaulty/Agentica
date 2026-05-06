using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Runs;
using Agentica.Tools;
using Agentica.Validation;

namespace Agentica.Outcomes;

public sealed class DeterministicOutcomeReporter : IOutcomeReporter
{
    public OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers)
    {
        var claims = new List<ReportClaim>();
        var queryReceipt = run.Receipts.FirstOrDefault(receipt => receipt.ToolId == DemoToolIds.QueryState);
        var actionReceipt = run.Receipts.FirstOrDefault(receipt => receipt.ToolId == DemoToolIds.PerformAction);
        var observation = run.Observations.FirstOrDefault();
        var artifact = run.Artifacts.FirstOrDefault();

        if (queryReceipt is not null && observation is not null)
        {
            claims.Add(new ReportClaim(
                "State was queried before mutation-capable work was attempted.",
                [
                    new EvidenceRef("receipt", queryReceipt.ReceiptId),
                    new EvidenceRef("observation", observation.ObservationId)
                ]));
        }

        if (actionReceipt is not null && artifact is not null)
        {
            claims.Add(new ReportClaim(
                "The action tool completed and produced an action artifact.",
                [
                    new EvidenceRef("receipt", actionReceipt.ReceiptId),
                    new EvidenceRef("artifact", artifact.ArtifactId)
                ]));
        }

        if (validationIssues.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The run stopped before execution because plan validation failed.",
                validationIssues
                    .Select(issue => new EvidenceRef("validationIssue", issue.Code))
                    .ToArray()));
        }

        if (blockers.Count > 0)
        {
            claims.Add(new ReportClaim(
                "The run stopped with blockers instead of inventing success.",
                [
                    new EvidenceRef("stopReason", stopReason.ToString())
                ]));
        }

        if (claims.Count == 0)
        {
            claims.Add(new ReportClaim(
                $"The run stopped with status {status}.",
                [
                    new EvidenceRef("stopReason", stopReason.ToString())
                ]));
        }

        var summary = status switch
        {
            RunOutcomeStatus.Succeeded =>
                "The run queried state, refined the plan from the observation, performed the action, and completed successfully.",
            RunOutcomeStatus.PlanInvalid =>
                "The run stopped because the workflow plan failed validation before execution.",
            RunOutcomeStatus.Blocked =>
                $"The run stopped as blocked. Stop reason: {stopReason}.",
            RunOutcomeStatus.Failed =>
                $"The run failed. Stop reason: {stopReason}.",
            _ =>
                $"The run stopped with status {status}. Stop reason: {stopReason}."
        };

        return new OutcomeReport(
            ReportId: AgenticaIds.New("report"),
            Summary: summary,
            Claims: claims);
    }
}
