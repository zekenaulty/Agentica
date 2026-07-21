using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Tools;

namespace Agentica.Lab.Scenarios.Campaign;

public sealed record CampaignAcceptanceResult(
    bool Accepted,
    IReadOnlyList<string> Blockers)
{
    public IReadOnlyList<EvidenceRef> Evidence { get; init; } = [];
}

public static class CampaignAcceptance
{
    public static CampaignAcceptanceResult Evaluate(
        CampaignMilestone milestone,
        OutcomeEnvelope envelope,
        IReadOnlyDictionary<string, object?> hostState)
    {
        var blockers = new List<string>();
        var evidence = new List<EvidenceRef>();
        if (envelope.Outcome.Status != milestone.RequiredOutcomeStatus)
        {
            blockers.Add($"Outcome status expected {milestone.RequiredOutcomeStatus} actual {envelope.Outcome.Status}.");
        }

        foreach (var requirement in milestone.RequiredEvidence)
        {
            switch (requirement.Kind)
            {
                case CampaignEvidenceKind.Artifact:
                    var artifact = envelope.Details.Artifacts.FirstOrDefault(candidate =>
                        string.Equals(candidate.Kind, requirement.ArtifactKind, StringComparison.Ordinal));
                    if (artifact is null)
                    {
                        blockers.Add($"Missing artifact kind '{requirement.ArtifactKind}'.");
                    }
                    else
                    {
                        evidence.Add(new EvidenceRef("artifact", artifact.ArtifactId));
                    }

                    break;

                case CampaignEvidenceKind.Receipt:
                    var receipt = envelope.Receipts.Items.FirstOrDefault(candidate =>
                        string.Equals(candidate.ToolId, requirement.ToolId, StringComparison.Ordinal) &&
                        (!requirement.ReceiptStatus.HasValue || candidate.Status == requirement.ReceiptStatus.Value));
                    if (receipt is null)
                    {
                        blockers.Add($"Missing receipt for tool '{requirement.ToolId}' with status '{requirement.ReceiptStatus}'.");
                    }
                    else
                    {
                        evidence.Add(new EvidenceRef("receipt", receipt.ReceiptId));
                    }

                    break;

                case CampaignEvidenceKind.HostState:
                    if (string.IsNullOrWhiteSpace(requirement.HostStateKey) ||
                        !hostState.TryGetValue(requirement.HostStateKey, out var value) ||
                        !HostValueEquals(value, requirement.HostStateValue))
                    {
                        blockers.Add($"Host state '{requirement.HostStateKey}' did not satisfy milestone requirement.");
                    }
                    else
                    {
                        evidence.Add(new EvidenceRef("hostState", requirement.HostStateKey));
                    }

                    break;
            }
        }

        return new CampaignAcceptanceResult(blockers.Count == 0, blockers)
        {
            Evidence = evidence.Distinct().ToArray()
        };
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
