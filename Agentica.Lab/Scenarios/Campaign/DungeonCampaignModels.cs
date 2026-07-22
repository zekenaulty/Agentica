namespace Agentica.Lab.Scenarios.Campaign;

public sealed class DungeonCampaignState
{
    public HashSet<string> Inventory { get; } = new(StringComparer.Ordinal);

    public HashSet<string> ExploredAreas { get; } = new(StringComparer.Ordinal);

    public HashSet<string> OpenedGates { get; } = new(StringComparer.Ordinal);

    public HashSet<string> CompletedMilestones { get; } = new(StringComparer.Ordinal);

    public bool FinalGateOpen { get; set; }

    public IReadOnlyDictionary<string, object?> PublicSnapshot() =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["hasLantern"] = Inventory.Contains("lantern"),
            ["hasBronzeKey"] = Inventory.Contains("bronze_key"),
            ["darkArchiveExplored"] = ExploredAreas.Contains("dark_archive"),
            ["bronzeVaultUnlocked"] = OpenedGates.Contains("bronze_vault"),
            ["hasMoonSigil"] = Inventory.Contains("moon_sigil"),
            ["hasSunSigil"] = Inventory.Contains("sun_sigil"),
            ["optionalCacheOpened"] = OpenedGates.Contains("optional_cache"),
            ["finalGateOpen"] = FinalGateOpen,
            ["completedDungeonMilestones"] = CompletedMilestones.Order(StringComparer.Ordinal).ToArray()
        };
}
