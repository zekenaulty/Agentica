using Agentica.Artifacts;
using Agentica.Outcomes;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.Campaign;

public sealed class DungeonCampaignBoard
{
    public IReadOnlyList<CampaignDefinition> ListCampaigns() => [Create()];

    public CampaignDefinition Load(string campaignId)
    {
        var campaign = Create();
        return string.Equals(campaign.CampaignId, campaignId, StringComparison.OrdinalIgnoreCase)
            ? campaign
            : throw new InvalidOperationException($"Unknown campaign '{campaignId}'.");
    }

    public static CampaignDefinition Create()
    {
        CampaignRequiredEvidence Artifact() => new(CampaignEvidenceKind.Artifact, ArtifactKind: "campaign.milestone_completed");
        CampaignRequiredEvidence Receipt(string toolId) => new(CampaignEvidenceKind.Receipt, ToolId: toolId, ReceiptStatus: ReceiptStatus.Succeeded);
        CampaignRequiredEvidence Host(string key) => new(CampaignEvidenceKind.HostState, HostStateKey: key, HostStateValue: true);
        CampaignMilestone Milestone(
            string id,
            string objective,
            string[] dependsOn,
            bool optional,
            int priority,
            IReadOnlyList<CampaignRequiredEvidence> evidence,
            params (string Key, object? Value)[] context) =>
            new(
                id,
                objective,
                dependsOn,
                optional,
                priority,
                RunOutcomeStatus.Succeeded,
                evidence,
                context.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

        return new CampaignDefinition(
            "dungeon_campaign",
            "Dungeon Campaign",
            "Open the final gate by completing a small dependency-shaped dungeon campaign.",
            [
                Milestone(
                    "acquire_lantern",
                    "Acquire the lantern so dark places can be explored.",
                    [],
                    optional: false,
                    priority: 1,
                    [Artifact(), Receipt(DungeonCampaignToolIds.CompleteMilestone), Host("hasLantern")]),
                Milestone(
                    "acquire_bronze_key",
                    "Acquire the bronze key so the bronze vault can be unlocked.",
                    [],
                    optional: false,
                    priority: 1,
                    [Artifact(), Receipt(DungeonCampaignToolIds.CompleteMilestone), Host("hasBronzeKey")]),
                Milestone(
                    "explore_dark_archive",
                    "Use the lantern to explore the dark archive.",
                    ["acquire_lantern"],
                    optional: false,
                    priority: 2,
                    [Artifact(), Receipt(DungeonCampaignToolIds.CompleteMilestone), Host("darkArchiveExplored")]),
                Milestone(
                    "unlock_bronze_vault",
                    "Use the bronze key to unlock the bronze vault.",
                    ["acquire_bronze_key"],
                    optional: false,
                    priority: 2,
                    [Artifact(), Receipt(DungeonCampaignToolIds.CompleteMilestone), Host("bronzeVaultUnlocked")]),
                Milestone(
                    "recover_moon_sigil",
                    "Recover the moon sigil from the explored dark archive.",
                    ["explore_dark_archive"],
                    optional: false,
                    priority: 3,
                    [Artifact(), Receipt(DungeonCampaignToolIds.CompleteMilestone), Host("hasMoonSigil")]),
                Milestone(
                    "recover_sun_sigil",
                    "Recover the sun sigil from the unlocked bronze vault.",
                    ["unlock_bronze_vault"],
                    optional: false,
                    priority: 3,
                    [Artifact(), Receipt(DungeonCampaignToolIds.CompleteMilestone), Host("hasSunSigil")]),
                Milestone(
                    "optional_cache",
                    "Optionally open the lantern-lit side cache.",
                    ["acquire_lantern"],
                    optional: true,
                    priority: 9,
                    [Artifact(), Receipt(DungeonCampaignToolIds.CompleteMilestone), Host("optionalCacheOpened")]),
                Milestone(
                    "open_final_gate",
                    "Open the final gate using the moon sigil and sun sigil.",
                    ["recover_moon_sigil", "recover_sun_sigil"],
                    optional: false,
                    priority: 4,
                    [Artifact(), Receipt(DungeonCampaignToolIds.CompleteMilestone), Host("finalGateOpen")])
            ],
            [new CampaignRequiredEvidence(CampaignEvidenceKind.HostState, HostStateKey: "finalGateOpen", HostStateValue: true)]);
    }
}
