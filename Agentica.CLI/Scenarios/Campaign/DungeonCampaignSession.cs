using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.Campaign;

public sealed class DungeonCampaignSession
{
    private readonly CampaignDefinition _definition;

    public DungeonCampaignSession(CampaignDefinition definition)
    {
        _definition = definition;
    }

    public DungeonCampaignState State { get; } = new();

    public IReadOnlyDictionary<string, object?> PublicSnapshot() => State.PublicSnapshot();

    public ToolResult Execute(ToolInvocation invocation)
    {
        return invocation.ToolId switch
        {
            DungeonCampaignToolIds.GetState => Observed(invocation, ReceiptStatus.Succeeded, "Dungeon state returned.", "Dungeon state observed.", Snapshot("get_state")),
            DungeonCampaignToolIds.AcquireItem => AcquireItem(invocation),
            DungeonCampaignToolIds.Explore => Explore(invocation),
            DungeonCampaignToolIds.Unlock => Unlock(invocation),
            DungeonCampaignToolIds.OpenFinalGate => OpenFinalGate(invocation),
            DungeonCampaignToolIds.CompleteMilestone => CompleteMilestone(invocation),
            _ => Refused(invocation, "unknown_dungeon_tool", $"Unknown dungeon campaign tool '{invocation.ToolId}'.")
        };
    }

    private ToolResult AcquireItem(ToolInvocation invocation)
    {
        var item = ReadString(invocation, "item");
        if (string.IsNullOrWhiteSpace(item))
        {
            return Refused(invocation, "missing_item", "Acquire item requires item.");
        }

        if (item is "moon_sigil" && !State.ExploredAreas.Contains("dark_archive"))
        {
            return Refused(invocation, "archive_not_explored", "The moon sigil is only available after exploring the dark archive.");
        }

        if (item is "sun_sigil" && !State.OpenedGates.Contains("bronze_vault"))
        {
            return Refused(invocation, "vault_locked", "The sun sigil is only available after unlocking the bronze vault.");
        }

        State.Inventory.Add(item);
        var data = Snapshot("acquire_item");
        data["item"] = item;
        return Observed(invocation, ReceiptStatus.Succeeded, $"Acquired {item}.", $"{item} acquired.", data);
    }

    private ToolResult Explore(ToolInvocation invocation)
    {
        var area = ReadString(invocation, "area");
        if (area != "dark_archive")
        {
            return Refused(invocation, "unknown_area", $"Unknown dungeon area '{area}'.");
        }

        if (!State.Inventory.Contains("lantern"))
        {
            return Refused(invocation, "lantern_required", "The dark archive requires the lantern.");
        }

        State.ExploredAreas.Add(area);
        var data = Snapshot("explore");
        data["area"] = area;
        return Observed(invocation, ReceiptStatus.Succeeded, "Explored dark archive.", "Dark archive explored.", data);
    }

    private ToolResult Unlock(ToolInvocation invocation)
    {
        var gate = ReadString(invocation, "gate");
        if (gate == "bronze_vault")
        {
            if (!State.Inventory.Contains("bronze_key"))
            {
                return Refused(invocation, "bronze_key_required", "The bronze vault requires the bronze key.");
            }

            State.OpenedGates.Add(gate);
            var data = Snapshot("unlock");
            data["gate"] = gate;
            return Observed(invocation, ReceiptStatus.Succeeded, "Unlocked bronze vault.", "Bronze vault unlocked.", data);
        }

        if (gate == "optional_cache")
        {
            if (!State.Inventory.Contains("lantern"))
            {
                return Refused(invocation, "lantern_required", "The optional cache requires the lantern.");
            }

            State.OpenedGates.Add(gate);
            var data = Snapshot("unlock");
            data["gate"] = gate;
            return Observed(invocation, ReceiptStatus.Succeeded, "Unlocked optional cache.", "Optional cache unlocked.", data);
        }

        return Refused(invocation, "unknown_gate", $"Unknown dungeon gate '{gate}'.");
    }

    private ToolResult OpenFinalGate(ToolInvocation invocation)
    {
        if (!State.Inventory.Contains("moon_sigil") || !State.Inventory.Contains("sun_sigil"))
        {
            return Refused(invocation, "sigils_required", "The final gate requires the moon sigil and sun sigil.");
        }

        State.FinalGateOpen = true;
        var data = Snapshot("open_final_gate");
        data["gate"] = "final_gate";
        return Observed(invocation, ReceiptStatus.Succeeded, "Opened final gate.", "Final gate opened.", data);
    }

    private ToolResult CompleteMilestone(ToolInvocation invocation)
    {
        var milestoneId = ReadString(invocation, "milestoneId");
        if (string.IsNullOrWhiteSpace(milestoneId) ||
            !_definition.Milestones.Any(milestone => milestone.MilestoneId == milestoneId))
        {
            return Refused(invocation, "unknown_milestone", $"Unknown campaign milestone '{milestoneId}'.");
        }

        if (!MilestoneSatisfied(milestoneId))
        {
            return Refused(invocation, "milestone_not_satisfied", $"Milestone '{milestoneId}' is not satisfied by dungeon state.");
        }

        State.CompletedMilestones.Add(milestoneId);
        var data = Snapshot("complete_milestone");
        data["milestoneId"] = milestoneId;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Completed dungeon milestone {milestoneId}.", data);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            "campaign.milestone_completed",
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return new ToolResult(receipt, Artifact: artifact);
    }

    private bool MilestoneSatisfied(string milestoneId) =>
        milestoneId switch
        {
            "acquire_lantern" => State.Inventory.Contains("lantern"),
            "acquire_bronze_key" => State.Inventory.Contains("bronze_key"),
            "explore_dark_archive" => State.ExploredAreas.Contains("dark_archive"),
            "unlock_bronze_vault" => State.OpenedGates.Contains("bronze_vault"),
            "recover_moon_sigil" => State.Inventory.Contains("moon_sigil"),
            "recover_sun_sigil" => State.Inventory.Contains("sun_sigil"),
            "optional_cache" => State.OpenedGates.Contains("optional_cache"),
            "open_final_gate" => State.FinalGateOpen,
            _ => false
        };

    private ToolResult Refused(
        ToolInvocation invocation,
        string reason,
        string message)
    {
        var data = Snapshot("refused");
        data["reason"] = reason;
        data["blocker"] = reason;
        return Observed(invocation, ReceiptStatus.Refused, message, message, data);
    }

    private ToolResult Observed(
        ToolInvocation invocation,
        ReceiptStatus status,
        string receiptMessage,
        string observationSummary,
        IReadOnlyDictionary<string, object?> data)
    {
        var receipt = Receipt(invocation, status, receiptMessage, data);
        return new ToolResult(receipt, Observation(invocation, receipt, observationSummary, data));
    }

    private Dictionary<string, object?> Snapshot(string action)
    {
        var data = new Dictionary<string, object?>(State.PublicSnapshot(), StringComparer.Ordinal)
        {
            ["campaignId"] = _definition.CampaignId,
            ["action"] = action
        };
        return data;
    }

    private static string? ReadString(ToolInvocation invocation, string key) =>
        invocation.Input.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static Receipt Receipt(
        ToolInvocation invocation,
        ReceiptStatus status,
        string message,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            status,
            message,
            DateTimeOffset.UtcNow,
            data);

    private static Observation Observation(
        ToolInvocation invocation,
        Receipt receipt,
        string summary,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.ToolResult,
            summary,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
}
