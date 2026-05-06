using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.MazeQuest;

public sealed class MazeQuestSession
{
    public MazeQuestSession(MazeQuestStage stage)
    {
        Stage = stage;
        State = new MazeQuestSessionState(stage);
    }

    public MazeQuestStage Stage { get; }

    public MazeQuestSessionState State { get; }

    public MazeQuestToolTurn? LastTurn { get; private set; }

    public ToolResult Execute(ToolInvocation invocation)
    {
        var result = ExecuteCore(invocation);
        LastTurn = new MazeQuestToolTurn(invocation, result, CurrentRunState);
        return result;
    }

    private ToolResult ExecuteCore(ToolInvocation invocation)
    {
        return invocation.ToolId switch
        {
            MazeQuestToolIds.GetState => GetState(invocation),
            MazeQuestToolIds.RenderMap => RenderMap(invocation),
            MazeQuestToolIds.Scan => Scan(invocation),
            MazeQuestToolIds.SenseObjective => SenseObjective(invocation),
            MazeQuestToolIds.EvaluateMoves => EvaluateMoves(invocation),
            MazeQuestToolIds.Move => Move(invocation),
            MazeQuestToolIds.Take => Take(invocation),
            MazeQuestToolIds.Use => Use(invocation),
            MazeQuestToolIds.Rest => Rest(invocation),
            MazeQuestToolIds.CompleteObjective => CompleteObjective(invocation),
            _ => Refused(invocation, "unknown_maze_tool", $"Unknown maze tool '{invocation.ToolId}'.")
        };
    }

    public MazeQuestRunState CurrentRunState => State.ToRunState(Stage);

    public string ActiveObjectiveId => State.ActiveObjectiveId(Stage);

    private MazeCell CurrentCell => Stage.Grid[State.Position];

    private MazeQuestObject? CurrentObject => ObjectAt(State.Position);

    private ToolResult GetState(ToolInvocation invocation)
    {
        var data = Snapshot("get_state");
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "MazeQuest state returned.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Current MazeQuest state observed.", data));
    }

    private ToolResult RenderMap(ToolInvocation invocation)
    {
        var data = Snapshot("render_map");
        data["visibleMapAscii"] = MazeQuestRenderer.RenderFog(Stage, CurrentRunState);
        data["legend"] = " @ agent, ? unknown, # wall, . floor, ! hazard, + reward/cache, objective symbols shown only when discovered";

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Fog-of-war map rendered.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Visible maze map rendered.", data));
    }

    private ToolResult Scan(ToolInvocation invocation)
    {
        var before = State.Discovered.Count;
        State.Discovered.UnionWith(MazeVisibility.VisiblePoints(Stage.Grid, State.Position, Stage.VisibilityRadius));

        var data = Snapshot("scan");
        data["newCellsDiscovered"] = State.Discovered.Count - before;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Local maze scan completed.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Local maze cells scanned.", data));
    }

    private ToolResult SenseObjective(ToolInvocation invocation)
    {
        var data = Snapshot("sense_objective");
        data["objectiveSignal"] = MazeQuestAnalyzer.SenseObjective(Stage, CurrentRunState);

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Objective signal returned.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Objective signal observed.", data));
    }

    private ToolResult EvaluateMoves(ToolInvocation invocation)
    {
        var data = Snapshot("evaluate_moves");
        data["moveEvaluations"] = MazeQuestAnalyzer.EvaluateMoves(Stage, CurrentRunState);
        data["legalActions"] = LegalActions();

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Local move options evaluated.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Local move evaluations observed.", data));
    }

    private ToolResult Move(ToolInvocation invocation)
    {
        var direction = ReadString(invocation, "direction");
        if (string.IsNullOrWhiteSpace(direction))
        {
            return Refused(invocation, "missing_direction", "Move requires a direction.");
        }

        var move = MazeQuestAnalyzer.EvaluateMoves(Stage, CurrentRunState)
            .FirstOrDefault(item => string.Equals(item.Direction, direction, StringComparison.Ordinal));
        if (move is null)
        {
            return Refused(invocation, "invalid_direction", $"Unknown direction '{direction}'.");
        }

        if (!move.Legal)
        {
            var legalAlternatives = MazeQuestAnalyzer.EvaluateMoves(Stage, CurrentRunState)
                .Where(item => item.Legal)
                .Select(item => new Dictionary<string, object?>
                {
                    ["toolId"] = MazeQuestToolIds.Move,
                    ["input"] = new Dictionary<string, object?>
                    {
                        ["direction"] = item.Direction
                    },
                    ["summary"] = item.Summary
                })
                .ToArray();

            return Refused(
                invocation,
                move.Reason,
                $"Cannot move {direction}: {move.Reason}.",
                new Dictionary<string, object?>
                {
                    ["direction"] = direction,
                    ["to"] = Point(move.To),
                    ["currentEnergy"] = State.Energy,
                    ["requiredEnergy"] = move.TerrainCost,
                    ["restAvailable"] = CanRest(),
                    ["restCharges"] = State.RestCharges,
                    ["legalAlternatives"] = legalAlternatives
                });
        }

        State.Position = move.To;
        State.StepCount++;
        State.Energy = Math.Max(0, State.Energy - move.TerrainCost);
        State.Discovered.UnionWith(MazeVisibility.VisiblePoints(Stage.Grid, State.Position, Stage.VisibilityRadius));

        ApplyHazard();
        UpdateProgressFromLocation();

        var data = Snapshot("move");
        data["direction"] = direction;
        data["to"] = Point(State.Position);
        data["terrainCost"] = move.TerrainCost;
        data["hazard"] = CurrentCell.Hazard.ToString();
        data["hazardRisk"] = CurrentCell.HazardRisk;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Moved {direction}.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, $"Moved {direction} to ({State.Position.X}, {State.Position.Y}).", data));
    }

    private ToolResult Take(ToolInvocation invocation)
    {
        var objectId = ReadString(invocation, "objectId");
        if (string.IsNullOrWhiteSpace(objectId))
        {
            return Refused(invocation, "missing_object_id", "Take requires objectId.");
        }

        var questObject = RequireCurrentObject(objectId);
        if (questObject is null)
        {
            return Refused(
                invocation,
                "object_not_here",
                $"Object '{objectId}' is not at the current cell.",
                new Dictionary<string, object?>
                {
                    ["expectedActionSource"] = "Use objectId exactly as provided by legalActions for maze.take."
                });
        }

        if (!IsTakeable(questObject))
        {
            return Refused(invocation, "object_not_takeable", $"Object '{objectId}' cannot be taken.");
        }

        if (!State.TakenObjects.Add(questObject.ObjectId))
        {
            return Refused(invocation, "object_already_taken", $"Object '{objectId}' was already taken.");
        }

        if (!State.Inventory.Contains(questObject.ObjectId, StringComparer.Ordinal))
        {
            State.Inventory.Add(questObject.ObjectId);
        }

        UpdateProgressForObject(questObject, interaction: "take");

        var data = Snapshot("take");
        data["objectId"] = questObject.ObjectId;
        data["displayName"] = questObject.DisplayName;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Took {questObject.DisplayName}.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, $"{questObject.DisplayName} added to inventory.", data));
    }

    private ToolResult Use(ToolInvocation invocation)
    {
        var targetId = ReadString(invocation, "targetId") ?? ReadString(invocation, "objectId");
        var item = ReadString(invocation, "item");
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return Refused(invocation, "missing_target_id", "Use requires targetId.");
        }

        var questObject = RequireCurrentObject(targetId);
        if (questObject is null)
        {
            return Refused(
                invocation,
                "target_not_here",
                $"Target '{targetId}' is not at the current cell.",
                new Dictionary<string, object?>
                {
                    ["expectedActionSource"] = "Use targetId exactly as provided by legalActions for maze.use."
                });
        }

        if (questObject.RequiredItem is { } requiredItem &&
            !State.Inventory.Contains(requiredItem, StringComparer.Ordinal))
        {
            return Refused(
                invocation,
                "missing_required_item",
                $"Target '{targetId}' requires {requiredItem}.",
                new Dictionary<string, object?>
                {
                    ["targetId"] = targetId,
                    ["requiredItem"] = requiredItem,
                    ["item"] = item
                });
        }

        State.ActivatedObjects.Add(questObject.ObjectId);
        if (questObject.Kind == MazeQuestObjectKind.Gate)
        {
            State.OpenedObjects.Add(questObject.ObjectId);
        }

        ApplyUseSideEffects(questObject);
        UpdateProgressForObject(questObject, interaction: "use");

        var data = Snapshot("use");
        data["targetId"] = questObject.ObjectId;
        data["item"] = item;
        data["displayName"] = questObject.DisplayName;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Used {questObject.DisplayName}.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, $"{questObject.DisplayName} used.", data));
    }

    private ToolResult Rest(ToolInvocation invocation)
    {
        if (!CanRest())
        {
            return Refused(
                invocation,
                "rest_not_available",
                "Rest is not available from the current resource state.",
                new Dictionary<string, object?>
                {
                    ["restCharges"] = State.RestCharges,
                    ["energy"] = State.Energy,
                    ["maxEnergy"] = Stage.EnergyPolicy.MaxEnergy,
                    ["health"] = State.Health,
                    ["maxHealth"] = 8
                });
        }

        var previousEnergy = State.Energy;
        var previousHealth = State.Health;
        var previousRestCharges = State.RestCharges;

        State.StepCount++;
        State.RestCharges--;
        State.Health = Math.Min(8, State.Health + Stage.EnergyPolicy.RestHealthGain);
        State.Energy = Math.Min(Stage.EnergyPolicy.MaxEnergy, State.Energy + Stage.EnergyPolicy.RestEnergyGain);

        var data = Snapshot("rest");
        data["previousEnergy"] = previousEnergy;
        data["newEnergy"] = State.Energy;
        data["previousHealth"] = previousHealth;
        data["newHealth"] = State.Health;
        data["restChargesBefore"] = previousRestCharges;
        data["restChargesAfter"] = State.RestCharges;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Rested briefly.", data);
        return new ToolResult(receipt, Observation(invocation, receipt, "Health and energy recovered slightly.", data));
    }

    private ToolResult CompleteObjective(ToolInvocation invocation)
    {
        var incomplete = Stage.Quest.Objectives
            .Where(objective => objective.Kind != MazeObjectiveKind.Complete)
            .Where(objective => !State.CompletedObjectives.Contains(objective.ObjectiveId))
            .ToArray();

        if (incomplete.Length > 0)
        {
            return Refused(
                invocation,
                "objective_not_satisfied",
                "MazeQuest objective chain is not satisfied.",
                new Dictionary<string, object?>
                {
                    ["incompleteObjectives"] = incomplete.Select(item => item.ObjectiveId).ToArray()
                });
        }

        State.ObjectiveCompleted = true;
        State.CompletedObjectives.Add("complete");

        var data = Snapshot("complete_objective");
        data["objectiveCompleted"] = true;

        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "MazeQuest objective completed.", data);
        var artifact = new Artifact(
            ArtifactId: AgenticaIds.New("artifact"),
            Kind: "mazequest.objective_completed",
            Payload: data,
            Evidence:
            [
                new EvidenceRef("receipt", receipt.ReceiptId)
            ]);

        return new ToolResult(receipt, Artifact: artifact);
    }

    private void ApplyHazard()
    {
        if (CurrentCell.Hazard == MazeHazard.None)
        {
            return;
        }

        var hazardKey = MazeQuestAnalyzer.HazardKey(State.Position, CurrentCell.Hazard);
        if (!State.TriggeredHazards.Add(hazardKey))
        {
            return;
        }

        if (CurrentCell.Hazard is MazeHazard.Spike or MazeHazard.Trap)
        {
            State.Health = Math.Max(0, State.Health - 1);
        }
    }

    private void UpdateProgressFromLocation()
    {
        var questObject = CurrentObject;
        if (questObject is null)
        {
            return;
        }

        foreach (var objective in Stage.Quest.Objectives.Where(objective => objective.TargetId == questObject.ObjectId))
        {
            if (objective.Kind is MazeObjectiveKind.DiscoverLocation or MazeObjectiveKind.ReachExit)
            {
                State.CompletedObjectives.Add(objective.ObjectiveId);
            }
        }
    }

    private void UpdateProgressForObject(MazeQuestObject questObject, string interaction)
    {
        foreach (var objective in Stage.Quest.Objectives.Where(objective => objective.TargetId == questObject.ObjectId))
        {
            var complete = objective.Kind switch
            {
                MazeObjectiveKind.FindItem => interaction == "take",
                MazeObjectiveKind.CollectItem => interaction == "take",
                MazeObjectiveKind.RescueTarget => interaction == "take",
                MazeObjectiveKind.DeliverItem => interaction == "use",
                MazeObjectiveKind.ActivateObject => interaction == "use",
                MazeObjectiveKind.UnlockGate => interaction == "use" && questObject.Kind == MazeQuestObjectKind.Gate,
                _ => false
            };

            if (complete)
            {
                State.CompletedObjectives.Add(objective.ObjectiveId);
            }
        }
    }

    private void ApplyUseSideEffects(MazeQuestObject questObject)
    {
        if (Stage.Quest.QuestType != MazeQuestArchetype.Activate ||
            questObject.Kind != MazeQuestObjectKind.Activator)
        {
            return;
        }

        var requiredActivators = Stage.Quest.Objectives
            .Where(objective => objective.Kind == MazeObjectiveKind.ActivateObject)
            .Select(objective => objective.TargetId)
            .Where(targetId =>
                Stage.Objects.TryGetValue(targetId, out var target) &&
                target.Kind == MazeQuestObjectKind.Activator)
            .ToArray();

        if (requiredActivators.Length > 0 &&
            requiredActivators.All(targetId => State.ActivatedObjects.Contains(targetId)) &&
            !State.Inventory.Contains("activator_charge", StringComparer.Ordinal))
        {
            State.Inventory.Add("activator_charge");
        }
    }

    private Dictionary<string, object?> Snapshot(string action)
    {
        var runState = CurrentRunState;
        var completedObjectives = State.CompletedObjectives.Order(StringComparer.Ordinal).ToArray();
        var remainingObjectives = Stage.Quest.Objectives
            .Where(objective => objective.Kind != MazeObjectiveKind.Complete)
            .Where(objective => !State.CompletedObjectives.Contains(objective.ObjectiveId))
            .Select(objective => new Dictionary<string, object?>
            {
                ["objectiveId"] = objective.ObjectiveId,
                ["description"] = objective.Description,
                ["kind"] = objective.Kind.ToString(),
                ["targetId"] = objective.TargetId
            })
            .ToArray();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["questId"] = Stage.Quest.QuestId,
            ["questType"] = Stage.Quest.QuestType.ToString(),
            ["objective"] = Stage.Quest.Objective,
            ["coverageTags"] = Stage.Quest.CoverageTags,
            ["action"] = action,
            ["position"] = Point(State.Position),
            ["health"] = State.Health,
            ["energy"] = State.Energy,
            ["resources"] = Resources(),
            ["stepCount"] = State.StepCount,
            ["inventory"] = State.Inventory.Order(StringComparer.Ordinal).ToArray(),
            ["completedObjectives"] = completedObjectives,
            ["remainingObjectives"] = remainingObjectives,
            ["activeObjectiveId"] = runState.ActiveObjectiveId,
            ["activeObjective"] = Stage.Quest.Objectives.FirstOrDefault(item => item.ObjectiveId == runState.ActiveObjectiveId),
            ["objectiveProgress"] = new Dictionary<string, object?>
            {
                ["completedCount"] = completedObjectives.Length,
                ["remainingCount"] = remainingObjectives.Length,
                ["canCompleteObjective"] = remainingObjectives.Length == 0
            },
            ["currentCell"] = Cell(CurrentCell),
            ["currentObject"] = CurrentObject,
            ["visibleMapAscii"] = MazeQuestRenderer.RenderFog(Stage, runState),
            ["visibleCells"] = MazeQuestAnalyzer.VisibleCells(Stage, runState),
            ["objectiveSignal"] = MazeQuestAnalyzer.SenseObjective(Stage, runState),
            ["moveEvaluations"] = MazeQuestAnalyzer.EvaluateMoves(Stage, runState),
            ["legalActions"] = LegalActions(),
            ["objectiveCompleted"] = State.ObjectiveCompleted
        };
    }

    private IReadOnlyList<Dictionary<string, object?>> LegalActions()
    {
        var actions = new List<Dictionary<string, object?>>
        {
            Action(MazeQuestToolIds.GetState),
            Action(MazeQuestToolIds.RenderMap),
            Action(MazeQuestToolIds.Scan),
            Action(MazeQuestToolIds.SenseObjective),
            Action(MazeQuestToolIds.EvaluateMoves)
        };

        if (CanRest())
        {
            actions.Add(Action(MazeQuestToolIds.Rest));
        }

        foreach (var move in MazeQuestAnalyzer.EvaluateMoves(Stage, CurrentRunState).Where(move => move.Legal))
        {
            actions.Add(Action(MazeQuestToolIds.Move, ("direction", move.Direction)));
        }

        if (CurrentObject is { } currentObject)
        {
            if (IsTakeable(currentObject) && !State.TakenObjects.Contains(currentObject.ObjectId))
            {
                actions.Add(Action(MazeQuestToolIds.Take, ("objectId", currentObject.ObjectId)));
            }

            if (IsUsable(currentObject))
            {
                var inputs = new List<(string Key, object? Value)> { ("targetId", currentObject.ObjectId) };
                if (currentObject.RequiredItem is not null)
                {
                    inputs.Add(("item", currentObject.RequiredItem));
                }

                actions.Add(Action(MazeQuestToolIds.Use, inputs.ToArray()));
            }
        }

        if (Stage.Quest.Objectives
            .Where(objective => objective.Kind != MazeObjectiveKind.Complete)
            .All(objective => State.CompletedObjectives.Contains(objective.ObjectiveId)))
        {
            actions.Add(Action(MazeQuestToolIds.CompleteObjective));
        }

        return actions;
    }

    private bool IsUsable(MazeQuestObject questObject) =>
        questObject.Kind is MazeQuestObjectKind.Gate
            or MazeQuestObjectKind.DeliveryDropoff
            or MazeQuestObjectKind.Activator
            or MazeQuestObjectKind.Refuge
            or MazeQuestObjectKind.PuzzleRune
            or MazeQuestObjectKind.DiscoveryMarker;

    private static bool IsTakeable(MazeQuestObject questObject) =>
        questObject.Kind is MazeQuestObjectKind.Key
            or MazeQuestObjectKind.Collectible
            or MazeQuestObjectKind.DeliveryPickup
            or MazeQuestObjectKind.RescueTarget
            or MazeQuestObjectKind.ResourceCache;

    private bool CanRest() =>
        State.RestCharges > 0 &&
        (State.Energy < Stage.EnergyPolicy.MaxEnergy || State.Health < 8);

    private MazeQuestObject? RequireCurrentObject(string objectId) =>
        CurrentObject is { } currentObject &&
        string.Equals(currentObject.ObjectId, objectId, StringComparison.Ordinal)
            ? currentObject
            : null;

    private MazeQuestObject? ObjectAt(MazePoint point) =>
        Stage.Objects.Values.FirstOrDefault(item => item.Point == point);

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

    private static string? ReadString(ToolInvocation invocation, string key) =>
        invocation.Input.TryGetValue(key, out var value) ? value?.ToString() : null;

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

    private static Dictionary<string, object?> Point(MazePoint point) =>
        new(StringComparer.Ordinal)
        {
            ["x"] = point.X,
            ["y"] = point.Y
        };

    private static Dictionary<string, object?> Cell(MazeCell cell) =>
        new(StringComparer.Ordinal)
        {
            ["x"] = cell.Point.X,
            ["y"] = cell.Point.Y,
            ["terrain"] = cell.Terrain.ToString(),
            ["cost"] = cell.TraversalCost,
            ["hazard"] = cell.Hazard.ToString(),
            ["hazardRisk"] = cell.HazardRisk,
            ["reward"] = cell.Reward.ToString(),
            ["objectiveItem"] = cell.ObjectiveItem.ToString(),
            ["lockId"] = cell.LockId,
            ["displayName"] = cell.DisplayName
        };

    private Dictionary<string, object?> Resources() =>
        new(StringComparer.Ordinal)
        {
            ["health"] = State.Health,
            ["maxHealth"] = 8,
            ["energy"] = State.Energy,
            ["maxEnergy"] = Stage.EnergyPolicy.MaxEnergy,
            ["restCharges"] = State.RestCharges,
            ["restEnergyGain"] = Stage.EnergyPolicy.RestEnergyGain,
            ["restHealthGain"] = Stage.EnergyPolicy.RestHealthGain,
            ["enforceMoveEnergy"] = Stage.EnergyPolicy.EnforceMoveEnergy,
            ["perfectRouteCost"] = Stage.EnergyPolicy.PerfectRouteCost,
            ["energyPadding"] = Stage.EnergyPolicy.Padding
        };

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
