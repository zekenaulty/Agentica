using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Tools;

namespace Agentica.Lab.Scenarios.Campaign;

public static class CampaignProgressSnapshotCompiler
{
    public static CampaignProgressSnapshot Initial(
        CampaignDefinition definition,
        IReadOnlyDictionary<string, object?> hostStateProjection) =>
        new(
            definition.CampaignId,
            definition.Goal,
            CurrentMilestoneId: null,
            CompletedMilestones: [],
            ProvenFacts: [],
            OutstandingFacts: definition.Milestones
                .Where(milestone => !milestone.Optional)
                .Select(milestone => milestone.Objective)
                .ToArray(),
            ArtifactRefs: [],
            ReceiptRefs: [],
            Blockers: [],
            HostStateProjection: Compact(hostStateProjection));

    public static CampaignProgressSnapshot Compile(
        CampaignDefinition definition,
        CampaignState state,
        CampaignMilestone? currentMilestone,
        OutcomeEnvelope? envelope,
        IReadOnlyDictionary<string, object?> hostStateProjection,
        IReadOnlyList<string>? acceptanceBlockers = null)
    {
        var artifactRefs = envelope?.Details.Artifacts
            .Select(artifact => new EvidenceRef("artifact", artifact.ArtifactId))
            .ToArray() ?? [];
        var receiptRefs = envelope?.Receipts.Items
            .Where(receipt => receipt.Status == ReceiptStatus.Succeeded)
            .Select(receipt => new EvidenceRef("receipt", receipt.ReceiptId))
            .ToArray() ?? [];
        var facts = state.CompletedMilestones
            .Select(milestoneId =>
            {
                var milestone = definition.Milestones.First(item => item.MilestoneId == milestoneId);
                var evidence = state.PriorRunRefs
                    .Where(run => run.MilestoneId == milestoneId)
                    .SelectMany(run => run.Evidence)
                    .ToArray();
                return new CampaignFact(
                    $"milestone.{milestoneId}.completed",
                    $"Milestone '{milestoneId}' completed: {milestone.Objective}",
                    evidence);
            })
            .Concat(hostStateProjection
                .Where(pair => pair.Value is bool boolValue && boolValue)
                .Select(pair => new CampaignFact(
                    $"host.{pair.Key}",
                    $"Host state '{pair.Key}' is true.",
                    [])))
            .ToArray();

        var completed = state.CompletedMilestones.ToHashSet(StringComparer.Ordinal);
        var outstanding = definition.Milestones
            .Where(milestone => !milestone.Optional && !completed.Contains(milestone.MilestoneId))
            .Select(milestone => milestone.Objective)
            .Concat(acceptanceBlockers ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new CampaignProgressSnapshot(
            definition.CampaignId,
            definition.Goal,
            currentMilestone?.MilestoneId,
            state.CompletedMilestones.ToArray(),
            facts,
            outstanding,
            artifactRefs,
            receiptRefs,
            acceptanceBlockers ?? envelope?.Outcome.Blockers ?? [],
            Compact(hostStateProjection));
    }

    private static IReadOnlyDictionary<string, object?> Compact(IReadOnlyDictionary<string, object?> source)
    {
        var compact = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in source)
        {
            if (TryCompactValue(pair.Value, out var value))
            {
                compact[pair.Key] = value;
            }
        }

        return compact;
    }

    private static bool TryCompactValue(object? source, out object? value)
    {
        switch (source)
        {
            case null:
            case bool:
            case int:
            case long:
            case double:
            case decimal:
                value = source;
                return true;
            case string text when text.Length <= 128:
                value = text;
                return true;
            case string:
                value = null;
                return false;
            case IEnumerable<string> strings:
                value = strings.Take(16).ToArray();
                return true;
            case IEnumerable<bool> bools:
                value = bools.Take(16).ToArray();
                return true;
            case IEnumerable<int> ints:
                value = ints.Take(16).ToArray();
                return true;
            default:
                value = null;
                return false;
        }
    }
}
