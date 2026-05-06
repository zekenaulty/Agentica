using Agentica.Outcomes;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.Campaign;

public sealed record CampaignAcceptanceResult(
    bool Accepted,
    IReadOnlyList<string> Blockers);

public static class CampaignAcceptance
{
    public static CampaignAcceptanceResult Evaluate(
        CampaignMilestone milestone,
        OutcomeEnvelope envelope,
        IReadOnlyDictionary<string, object?> hostState)
    {
        var blockers = new List<string>();
        if (envelope.Outcome.Status != milestone.RequiredOutcomeStatus)
        {
            blockers.Add($"Outcome status expected {milestone.RequiredOutcomeStatus} actual {envelope.Outcome.Status}.");
        }

        foreach (var requirement in milestone.RequiredEvidence)
        {
            switch (requirement.Kind)
            {
                case CampaignEvidenceKind.Artifact:
                    if (!envelope.Details.Artifacts.Any(artifact =>
                        string.Equals(artifact.Kind, requirement.ArtifactKind, StringComparison.Ordinal)))
                    {
                        blockers.Add($"Missing artifact kind '{requirement.ArtifactKind}'.");
                    }

                    break;

                case CampaignEvidenceKind.Receipt:
                    if (!envelope.Receipts.Items.Any(receipt =>
                        string.Equals(receipt.ToolId, requirement.ToolId, StringComparison.Ordinal) &&
                        (!requirement.ReceiptStatus.HasValue || receipt.Status == requirement.ReceiptStatus.Value)))
                    {
                        blockers.Add($"Missing receipt for tool '{requirement.ToolId}' with status '{requirement.ReceiptStatus}'.");
                    }

                    break;

                case CampaignEvidenceKind.HostState:
                    if (string.IsNullOrWhiteSpace(requirement.HostStateKey) ||
                        !hostState.TryGetValue(requirement.HostStateKey, out var value) ||
                        !HostValueEquals(value, requirement.HostStateValue))
                    {
                        blockers.Add($"Host state '{requirement.HostStateKey}' did not satisfy milestone requirement.");
                    }

                    break;
            }
        }

        return new CampaignAcceptanceResult(blockers.Count == 0, blockers);
    }

    private static bool HostValueEquals(object? actual, object? expected)
    {
        if (actual is null || expected is null)
        {
            return actual is null && expected is null;
        }

        return string.Equals(actual.ToString(), expected.ToString(), StringComparison.Ordinal);
    }
}
