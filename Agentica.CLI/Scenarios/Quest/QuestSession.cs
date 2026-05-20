using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.Quest;

public sealed record QuestToolTurn(
    ToolInvocation Invocation,
    ToolResult Result);

public sealed class QuestSession
{
    public QuestSession(QuestDefinition definition)
    {
        Definition = definition;
        State = new QuestRunState(definition.StartLocation);
    }

    public QuestDefinition Definition { get; }

    public QuestRunState State { get; }

    private readonly List<QuestToolTurn> _turns = [];

    public IReadOnlyList<QuestToolTurn> Turns => _turns;

    public QuestToolTurn? LastTurn { get; private set; }

    public ToolResult Execute(ToolInvocation invocation)
    {
        var result = ExecuteCore(invocation);
        LastTurn = new QuestToolTurn(invocation, result);
        _turns.Add(LastTurn);
        return result;
    }

    private ToolResult ExecuteCore(ToolInvocation invocation)
    {
        return invocation.ToolId switch
        {
            QuestToolIds.GetState => GetState(invocation),
            QuestToolIds.ListLegalActions => ListLegalActions(invocation),
            QuestToolIds.Inspect => Inspect(invocation),
            QuestToolIds.Move => Move(invocation),
            QuestToolIds.Take => Take(invocation),
            QuestToolIds.Use => Use(invocation),
            QuestToolIds.Talk => Talk(invocation),
            QuestToolIds.CompleteObjective => CompleteObjective(invocation),
            _ => Refused(invocation, "unknown_quest_tool", $"Unknown quest tool '{invocation.ToolId}'.")
        };
    }

    private QuestRoom CurrentRoom => Definition.Rooms[State.Location];

    private ToolResult GetState(ToolInvocation invocation)
    {
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Quest state returned.", Snapshot("get_state"));
        return new ToolResult(receipt, Observation(invocation, receipt, "Current quest state observed.", Snapshot("get_state")));
    }

    private ToolResult ListLegalActions(ToolInvocation invocation)
    {
        var data = Snapshot("list_legal_actions");
        data["legalActions"] = LegalActions();
        data["blockedActions"] = BlockedActions();
        data["forbiddenActions"] = new[]
        {
            new Dictionary<string, object?>
            {
                ["toolId"] = QuestToolIds.Use,
                ["input"] = new Dictionary<string, object?>
                {
                    ["item"] = "fire",
                    ["target"] = "sun_gate"
                },
                ["reason"] = "destructive_action_blocked"
            }
        };

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Legal quest actions returned.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Legal actions observed.", data));
    }

    private ToolResult Inspect(ToolInvocation invocation)
    {
        var target = ReadString(invocation, "target") ?? "room";
        var data = Snapshot("inspect");
        data["target"] = target;

        if (string.Equals(target, "room", StringComparison.Ordinal) ||
            string.Equals(target, State.Location, StringComparison.Ordinal))
        {
            data["description"] = CurrentRoom.Description;
            data["items"] = VisibleItems();
            data["exits"] = ExitSummaries();
        }
        else if (CurrentRoom.Exits.TryGetValue(target, out var exit))
        {
            data["exit"] = ExitSummary(exit);
        }
        else
        {
            data["description"] = $"No visible quest target named '{target}'.";
        }

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Quest target inspected.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Quest inspection observed.", data));
    }

    private ToolResult Move(ToolInvocation invocation)
    {
        var direction = ReadString(invocation, "direction");
        if (string.IsNullOrWhiteSpace(direction))
        {
            return Refused(invocation, "missing_direction", "Move requires a direction.");
        }

        if (!CurrentRoom.Exits.TryGetValue(direction, out var exit))
        {
            return Refused(invocation, "invalid_exit", $"There is no '{direction}' exit from {State.Location}.");
        }

        if (exit.LockId is { } lockId && !State.OpenedLocks.Contains(lockId))
        {
            var requiredItem = Definition.Locks[lockId].RequiredItem;
            return Refused(
                invocation,
                "locked_exit",
                $"The {direction} exit is locked by {lockId} and requires {requiredItem}.",
                extraData: new Dictionary<string, object?>
                {
                    ["direction"] = direction,
                    ["lockId"] = lockId,
                    ["requiredItem"] = requiredItem
                });
        }

        State.Location = exit.To;
        State.VisitedRooms.Add(State.Location);

        var data = Snapshot("move");
        data["direction"] = direction;
        data["to"] = State.Location;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Moved {direction} to {State.Location}.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, $"Location changed to {State.Location}.", data));
    }

    private ToolResult Take(ToolInvocation invocation)
    {
        var item = ReadString(invocation, "item");
        if (string.IsNullOrWhiteSpace(item))
        {
            return Refused(invocation, "missing_item", "Take requires an item.");
        }

        if (!VisibleItems().Contains(item, StringComparer.Ordinal))
        {
            return Refused(invocation, "item_unavailable", $"Item '{item}' is not available in {State.Location}.");
        }

        State.Inventory.Add(item);
        State.CollectedItems.Add(item);

        var data = Snapshot("take");
        data["item"] = item;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Took {item}.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, $"{item} added to inventory.", data));
    }

    private ToolResult Use(ToolInvocation invocation)
    {
        var item = ReadString(invocation, "item");
        var target = ReadString(invocation, "target");
        if (string.IsNullOrWhiteSpace(item) || string.IsNullOrWhiteSpace(target))
        {
            return Refused(invocation, "missing_use_input", "Use requires item and target.");
        }

        if (string.Equals(item, "fire", StringComparison.Ordinal))
        {
            return Refused(invocation, "destructive_action_blocked", "Quest policy refuses destructive shortcuts.");
        }

        if (!State.Inventory.Contains(item, StringComparer.Ordinal))
        {
            return Refused(invocation, "item_not_in_inventory", $"Item '{item}' is not in inventory.");
        }

        if (!Definition.Locks.TryGetValue(target, out var questLock))
        {
            return Refused(invocation, "target_unusable", $"Target '{target}' cannot be used that way.");
        }

        if (!string.Equals(questLock.RequiredItem, item, StringComparison.Ordinal))
        {
            return Refused(invocation, "wrong_item", $"{target} requires {questLock.RequiredItem}.");
        }

        State.OpenedLocks.Add(target);

        var data = Snapshot("use");
        data["item"] = item;
        data["target"] = target;
        data["openedLock"] = target;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Used {item} on {target}.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, $"{target} opened.", data));
    }

    private ToolResult Talk(ToolInvocation invocation)
    {
        var npc = ReadString(invocation, "npc");
        if (string.IsNullOrWhiteSpace(npc))
        {
            return Refused(invocation, "missing_npc", "Talk requires an npc.");
        }

        if (!CurrentRoom.Npcs.TryGetValue(npc, out var flag))
        {
            return Refused(invocation, "npc_unavailable", $"No npc named '{npc}' is available in {State.Location}.");
        }

        State.Flags.Add(flag);

        var data = Snapshot("talk");
        data["npc"] = npc;
        data["discoveredFlag"] = flag;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Talked to {npc}.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, $"{npc} revealed {flag}.", data));
    }

    private ToolResult CompleteObjective(ToolInvocation invocation)
    {
        if (!string.Equals(State.Location, "hall", StringComparison.Ordinal) ||
            !State.OpenedLocks.Contains("sun_gate"))
        {
            return Refused(invocation, "objective_not_satisfied", "The north gate objective is not satisfied.");
        }

        State.ObjectiveCompleted = true;

        var data = Snapshot("complete_objective");
        data["objective"] = Definition.Objective;
        data["objectiveCompleted"] = true;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Quest objective completed.", data);
        var artifact = new Artifact(
            ArtifactId: AgenticaIds.New("artifact"),
            Kind: "quest.objective_completed",
            Payload: data,
            Evidence:
            [
                new EvidenceRef("receipt", receipt.ReceiptId)
            ]);

        return new ToolResult(receipt, Artifact: artifact);
    }

    private ToolResult Refused(
        ToolInvocation invocation,
        string reason,
        string message,
        IReadOnlyDictionary<string, object?>? extraData = null)
    {
        var data = Snapshot("refused");
        data["reason"] = reason;
        data["blocker"] = reason;

        if (extraData is not null)
        {
            foreach (var pair in extraData)
            {
                data[pair.Key] = pair.Value;
            }
        }

        var receipt = Receipt(invocation, ReceiptStatus.Refused, message, data);
        return new ToolResult(receipt, Observation(invocation, receipt, message, data));
    }

    private Dictionary<string, object?> Snapshot(string action) =>
        new(StringComparer.Ordinal)
        {
            ["questId"] = Definition.QuestId,
            ["objective"] = Definition.Objective,
            ["action"] = action,
            ["location"] = State.Location,
            ["inventory"] = State.Inventory.Order(StringComparer.Ordinal).ToArray(),
            ["openedLocks"] = State.OpenedLocks.Order(StringComparer.Ordinal).ToArray(),
            ["visitedRooms"] = State.VisitedRooms.Order(StringComparer.Ordinal).ToArray(),
            ["flags"] = State.Flags.Order(StringComparer.Ordinal).ToArray(),
            ["visibleItems"] = VisibleItems(),
            ["objectiveCompleted"] = State.ObjectiveCompleted
        };

    private IReadOnlyList<Dictionary<string, object?>> LegalActions()
    {
        var actions = new List<Dictionary<string, object?>>
        {
            Action(QuestToolIds.GetState),
            Action(QuestToolIds.ListLegalActions),
            Action(QuestToolIds.Inspect, ("target", "room"))
        };

        foreach (var exit in CurrentRoom.Exits.Values.Where(IsOpen))
        {
            actions.Add(Action(QuestToolIds.Move, ("direction", exit.Direction)));
        }

        foreach (var item in VisibleItems())
        {
            actions.Add(Action(QuestToolIds.Take, ("item", item)));
        }

        foreach (var questLock in Definition.Locks.Values
            .Where(questLock => State.Inventory.Contains(questLock.RequiredItem, StringComparer.Ordinal) &&
                                !State.OpenedLocks.Contains(questLock.LockId)))
        {
            actions.Add(Action(
                QuestToolIds.Use,
                ("item", questLock.RequiredItem),
                ("target", questLock.LockId)));
        }

        if (State.Location == "hall" && State.OpenedLocks.Contains("sun_gate"))
        {
            actions.Add(Action(QuestToolIds.CompleteObjective));
        }

        return actions;
    }

    private IReadOnlyList<Dictionary<string, object?>> BlockedActions() =>
        CurrentRoom.Exits.Values
            .Where(exit => !IsOpen(exit))
            .Select(exit =>
            {
                var questLock = Definition.Locks[exit.LockId!];
                return Action(
                    QuestToolIds.Move,
                    ("direction", exit.Direction),
                    ("reason", "locked_exit"),
                    ("lockId", questLock.LockId),
                    ("requiredItem", questLock.RequiredItem));
            })
            .ToArray();

    private IReadOnlyList<string> VisibleItems() =>
        CurrentRoom.Items
            .Where(item => !State.CollectedItems.Contains(item))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private IReadOnlyList<Dictionary<string, object?>> ExitSummaries() =>
        CurrentRoom.Exits.Values.Select(ExitSummary).ToArray();

    private Dictionary<string, object?> ExitSummary(QuestExit exit)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["direction"] = exit.Direction,
            ["to"] = exit.To,
            ["open"] = IsOpen(exit)
        };

        if (exit.LockId is { } lockId)
        {
            data["lockId"] = lockId;
            data["requiredItem"] = Definition.Locks[lockId].RequiredItem;
        }

        return data;
    }

    private bool IsOpen(QuestExit exit) =>
        exit.LockId is null || State.OpenedLocks.Contains(exit.LockId);

    private static Dictionary<string, object?> Action(string toolId, params (string Key, object? Value)[] input)
    {
        var action = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["toolId"] = toolId
        };

        if (input.Length > 0)
        {
            action["input"] = input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        return action;
    }

    private static string? ReadString(ToolInvocation invocation, string key) =>
        invocation.Input.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static Receipt Receipt(
        ToolInvocation invocation,
        ReceiptStatus status,
        string message,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            ReceiptId: AgenticaIds.New("receipt"),
            StepId: invocation.StepId,
            ToolId: invocation.ToolId,
            Status: status,
            Message: message,
            At: DateTimeOffset.UtcNow,
            Data: data);

    private static Observation Observation(
        ToolInvocation invocation,
        Receipt receipt,
        string summary,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            ObservationId: AgenticaIds.New("observation"),
            StepId: invocation.StepId,
            Kind: ObservationKind.ToolResult,
            Summary: summary,
            Data: data,
            Evidence:
            [
                new EvidenceRef("receipt", receipt.ReceiptId)
            ]);
}
