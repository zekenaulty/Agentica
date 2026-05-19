namespace Agentica.CLI.Scenarios.MazeQuest;

public static class MazeQuestAnalyzer
{
    private static readonly IReadOnlyList<(string Direction, int Dx, int Dy)> Directions =
    [
        ("north", 0, -1),
        ("east", 1, 0),
        ("south", 0, 1),
        ("west", -1, 0)
    ];

    public static MazeObjectiveSignal SenseObjective(MazeQuestStage stage, MazeQuestRunState state)
    {
        var target = TargetFor(stage, state.ActiveObjectiveId);
        var distances = MazePathfinder.Distances(stage.Grid, state.Position);
        var distance = distances.GetValueOrDefault(target, int.MaxValue / 2);
        var maxDistance = Math.Max(1, stage.Grid.Width + stage.Grid.Height);
        var warmth = Math.Round(Math.Clamp(1 - (distance / (double)maxDistance), 0, 1), 2);

        return new MazeObjectiveSignal(
            ObjectiveId: state.ActiveObjectiveId,
            Bearing: Bearing(state.Position, target),
            DistanceBand: DistanceBand(distance),
            Warmth: warmth,
            DeltaFromPrevious: "baseline",
            Confidence: 0.8);
    }

    public static IReadOnlyList<MazeMoveEvaluation> EvaluateMoves(MazeQuestStage stage, MazeQuestRunState state)
    {
        return Directions.Select(direction =>
        {
            var next = state.Position.Translate(direction.Dx, direction.Dy);
            if (!stage.Grid.Contains(next))
            {
                return Blocked(direction.Direction, next, "outside_maze");
            }

            var cell = stage.Grid[next];
            if (cell.Terrain == MazeTerrain.Wall)
            {
                return Blocked(direction.Direction, next, "wall");
            }

            var questObject = ObjectAt(stage, next);
            if (questObject?.Kind == MazeQuestObjectKind.Gate &&
                questObject.RequiredItem is { } requiredItem &&
                !state.Inventory.Contains(requiredItem, StringComparer.Ordinal))
            {
                return Blocked(direction.Direction, next, $"requires_{requiredItem}");
            }

            var requiredEnergy = RequiredEnergyFor(cell, state);
            if (stage.EnergyPolicy.EnforceMoveEnergy && state.Energy < requiredEnergy)
            {
                return Blocked(direction.Direction, next, "insufficient_energy", requiredEnergy);
            }

            var frontierGain = MazeVisibility.VisiblePoints(stage.Grid, next, stage.VisibilityRadius)
                .Count(point => !state.Discovered.Contains(point));
            var objectiveDelta = ObjectiveAffordance(stage, state, next, questObject, frontierGain);

            return new MazeMoveEvaluation(
                Direction: direction.Direction,
                To: next,
                Legal: true,
                Reason: "legal",
                TerrainCost: requiredEnergy,
                VisibleRisk: Math.Round(cell.HazardRisk, 2),
                ObjectiveDelta: objectiveDelta,
                FrontierGain: frontierGain,
                Summary: SummaryFor(cell, objectiveDelta, frontierGain));
        }).ToArray();
    }

    public static IReadOnlyList<MazeKnownTravelOption> KnownTravelOptions(
        MazeQuestStage stage,
        MazeQuestRunState state,
        int maxOptions = 12) =>
        state.Discovered
            .Where(point => point != state.Position)
            .Select(point => EvaluateKnownTravelOption(stage, state, point))
            .Where(option => option.Legal && option.HopCount > 1)
            .OrderByDescending(KnownTravelScore)
            .ThenBy(option => option.HopCount)
            .Take(maxOptions)
            .ToArray();

    public static MazeKnownTravelOption EvaluateKnownTravelOption(
        MazeQuestStage stage,
        MazeQuestRunState state,
        MazePoint destination)
    {
        if (!stage.Grid.Contains(destination))
        {
            return BlockedKnownTravel(destination, "outside_maze", "Destination is outside the maze.");
        }

        if (!state.Discovered.Contains(destination))
        {
            return BlockedKnownTravel(destination, "target_not_discovered", "Destination is not currently exposed by fog-of-war.");
        }

        if (destination == state.Position)
        {
            return BlockedKnownTravel(destination, "already_at_destination", "Already at the requested destination.");
        }

        var path = MazePathfinder.ShortestPath(
            stage.Grid,
            state.Position,
            destination,
            cell => CanEnterKnownTravelCell(stage, state, cell));
        if (path.Count == 0)
        {
            return BlockedKnownTravel(destination, "no_public_path", "No fully exposed traversable path reaches the destination.");
        }

        var triggeredHazards = state.TriggeredHazards.ToHashSet(StringComparer.Ordinal);
        var simulatedDiscovered = state.Discovered.ToHashSet();
        var remainingEnergy = state.Energy;
        var totalTerrainCost = 0;
        var totalFrontierGain = 0;
        var maxVisibleRisk = 0d;
        var guaranteedHazardDamage = 0;
        var directions = new List<string>();

        for (var index = 1; index < path.Count; index++)
        {
            var previous = path[index - 1];
            var point = path[index];
            var cell = stage.Grid[point];
            var simulatedState = state with
            {
                Energy = remainingEnergy,
                TriggeredHazards = triggeredHazards.ToHashSet(StringComparer.Ordinal)
            };
            var cost = RequiredEnergyFor(cell, simulatedState);
            if (stage.EnergyPolicy.EnforceMoveEnergy && remainingEnergy < cost)
            {
                return new MazeKnownTravelOption(
                    Destination: destination,
                    Path: path,
                    Directions: directions,
                    HopCount: path.Count - 1,
                    TotalTerrainCost: totalTerrainCost + cost,
                    FrontierGain: totalFrontierGain,
                    MaxVisibleRisk: Math.Round(maxVisibleRisk, 2),
                    GuaranteedHazardDamage: guaranteedHazardDamage,
                    ObjectiveDelta: "blocked",
                    Legal: false,
                    Reason: "insufficient_energy",
                    Summary: $"Known route needs at least {cost} energy for a later hop; current remaining energy would be {remainingEnergy}.");
            }

            directions.Add(DirectionBetween(previous, point));
            remainingEnergy -= cost;
            totalTerrainCost += cost;
            maxVisibleRisk = Math.Max(maxVisibleRisk, cell.HazardRisk);

            var hazardKey = HazardKey(point, cell.Hazard);
            if (cell.Hazard != MazeHazard.None && triggeredHazards.Add(hazardKey) &&
                cell.Hazard is MazeHazard.Spike or MazeHazard.Trap)
            {
                guaranteedHazardDamage++;
            }

            var visibleFromPoint = MazeVisibility.VisiblePoints(stage.Grid, point, stage.VisibilityRadius).ToArray();
            totalFrontierGain += visibleFromPoint.Count(visiblePoint => !simulatedDiscovered.Contains(visiblePoint));
            simulatedDiscovered.UnionWith(visibleFromPoint);
        }

        if (guaranteedHazardDamage >= state.Health)
        {
            return new MazeKnownTravelOption(
                Destination: destination,
                Path: path,
                Directions: directions,
                HopCount: path.Count - 1,
                TotalTerrainCost: totalTerrainCost,
                FrontierGain: totalFrontierGain,
                MaxVisibleRisk: Math.Round(maxVisibleRisk, 2),
                GuaranteedHazardDamage: guaranteedHazardDamage,
                ObjectiveDelta: "blocked",
                Legal: false,
                Reason: "path_not_survivable",
                Summary: $"Known route crosses {guaranteedHazardDamage} untriggered damaging hazards with current health {state.Health}.");
        }

        var objectiveDelta = KnownTravelObjectiveDelta(stage, state, path, totalFrontierGain);
        return new MazeKnownTravelOption(
            Destination: destination,
            Path: path,
            Directions: directions,
            HopCount: path.Count - 1,
            TotalTerrainCost: totalTerrainCost,
            FrontierGain: totalFrontierGain,
            MaxVisibleRisk: Math.Round(maxVisibleRisk, 2),
            GuaranteedHazardDamage: guaranteedHazardDamage,
            ObjectiveDelta: objectiveDelta,
            Legal: true,
            Reason: "legal_public_path",
            Summary: $"Known public route to ({destination.X},{destination.Y}); {path.Count - 1} hops; cost {totalTerrainCost}; reveals {totalFrontierGain} cells; {objectiveDelta}.");
    }

    public static int RequiredEnergyFor(MazeCell cell, MazeQuestRunState state)
    {
        var requiredEnergy = Math.Max(1, cell.TraversalCost);
        if (cell.Hazard == MazeHazard.Darkness &&
            !state.TriggeredHazards.Contains(HazardKey(cell.Point, cell.Hazard)))
        {
            requiredEnergy++;
        }

        return requiredEnergy;
    }

    public static IReadOnlyList<Dictionary<string, object?>> VisibleCells(MazeQuestStage stage, MazeQuestRunState state)
    {
        return state.Discovered
            .OrderBy(point => point.Y)
            .ThenBy(point => point.X)
            .Select(point =>
            {
                var cell = stage.Grid[point];
                var weights = stage.Weights[point];
                var questObject = ObjectAt(stage, point);
                return new Dictionary<string, object?>
                {
                    ["x"] = point.X,
                    ["y"] = point.Y,
                    ["terrain"] = cell.Terrain.ToString(),
                    ["cost"] = cell.TraversalCost,
                    ["hazard"] = cell.Hazard.ToString(),
                    ["hazardRisk"] = cell.HazardRisk,
                    ["reward"] = cell.Reward.ToString(),
                    ["objectiveItem"] = cell.ObjectiveItem.ToString(),
                    ["lockId"] = cell.LockId,
                    ["displayName"] = cell.DisplayName,
                    ["questObject"] = questObject is null
                        ? null
                        : new Dictionary<string, object?>
                        {
                            ["objectId"] = questObject.ObjectId,
                            ["kind"] = questObject.Kind.ToString(),
                            ["displayName"] = questObject.DisplayName,
                            ["requiredItem"] = questObject.RequiredItem,
                            ["coverageTags"] = questObject.CoverageTags
                        },
                    ["weights"] = new Dictionary<string, object?>
                    {
                        ["depth"] = weights.Depth,
                        ["hazard"] = weights.HazardWeight,
                        ["reward"] = weights.RewardWeight,
                        ["frontier"] = weights.FrontierWeight
                    }
                };
            })
            .ToArray();
    }

    public static IReadOnlyDictionary<string, object?> BuildPublicSnapshot(MazeQuestStage stage, MazeQuestRunState state)
    {
        var signal = SenseObjective(stage, state);
        var remainingObjectives = stage.Quest.Objectives
            .Where(objective => objective.Kind != MazeObjectiveKind.Complete)
            .Where(objective => objective.Required)
            .Where(objective => !string.Equals(objective.ObjectiveId, "complete", StringComparison.Ordinal))
            .Select(objective => new Dictionary<string, object?>
            {
                ["objectiveId"] = objective.ObjectiveId,
                ["kind"] = objective.Kind.ToString(),
                ["description"] = objective.Description,
                ["targetId"] = objective.TargetId,
                ["required"] = objective.Required,
                ["priority"] = objective.Priority
            })
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["questId"] = stage.Quest.QuestId,
            ["questType"] = stage.Quest.QuestType.ToString(),
            ["objective"] = stage.Quest.Objective,
            ["coverageTags"] = stage.Quest.CoverageTags,
            ["objectiveChain"] = stage.Quest.Objectives.Select(objective => new Dictionary<string, object?>
            {
                ["objectiveId"] = objective.ObjectiveId,
                ["kind"] = objective.Kind.ToString(),
                ["description"] = objective.Description,
                ["targetId"] = objective.TargetId,
                ["required"] = objective.Required,
                ["priority"] = objective.Priority
            }).ToArray(),
            ["seed"] = stage.Seed,
            ["position"] = new Dictionary<string, object?>
            {
                ["x"] = state.Position.X,
                ["y"] = state.Position.Y
            },
            ["health"] = state.Health,
            ["energy"] = state.Energy,
            ["resources"] = new Dictionary<string, object?>
            {
                ["health"] = state.Health,
                ["maxHealth"] = 8,
                ["energy"] = state.Energy,
                ["maxEnergy"] = stage.EnergyPolicy.MaxEnergy,
                ["restCharges"] = state.RestCharges,
                ["restEnergyGain"] = stage.EnergyPolicy.RestEnergyGain,
                ["restHealthGain"] = stage.EnergyPolicy.RestHealthGain,
                ["enforceMoveEnergy"] = stage.EnergyPolicy.EnforceMoveEnergy,
                ["perfectRouteCost"] = stage.EnergyPolicy.PerfectRouteCost,
                ["energyPadding"] = stage.EnergyPolicy.Padding
            },
            ["stepCount"] = state.StepCount,
            ["inventory"] = state.Inventory,
            ["activeObjectiveId"] = state.ActiveObjectiveId,
            ["objectiveBoard"] = ObjectiveBoard(stage, state),
            ["remainingObjectives"] = remainingObjectives,
            ["objectiveProgress"] = new Dictionary<string, object?>
            {
                ["completedCount"] = 0,
                ["remainingCount"] = remainingObjectives.Length,
                ["canCompleteObjective"] = false
            },
            ["visibleMapAscii"] = MazeQuestRenderer.RenderFog(stage, state),
            ["visibleCells"] = VisibleCells(stage, state),
            ["objectiveSignal"] = signal,
            ["moveEvaluations"] = EvaluateMoves(stage, state),
            ["knownTravelOptions"] = KnownTravelOptions(stage, state),
            ["knownBlockers"] = EvaluateMoves(stage, state).Where(move => !move.Legal).ToArray()
        };
    }

    public static IReadOnlyList<Dictionary<string, object?>> ObjectiveBoard(MazeQuestStage stage, MazeQuestRunState state) =>
        stage.Quest.Objectives
            .Where(objective => objective.Kind != MazeObjectiveKind.Complete)
            .Select((objective, index) => ObjectiveBoardEntry(stage, state, objective, index))
            .ToArray();

    public static string HazardKey(MazePoint point, MazeHazard hazard) =>
        $"{point.X},{point.Y}:{hazard}";

    private static MazeMoveEvaluation Blocked(string direction, MazePoint to, string reason, int terrainCost = 0) =>
        new(
            Direction: direction,
            To: to,
            Legal: false,
            Reason: reason,
            TerrainCost: terrainCost,
            VisibleRisk: 0,
            ObjectiveDelta: "blocked",
            FrontierGain: 0,
            Summary: $"Blocked by {reason}.");

    private static MazeKnownTravelOption BlockedKnownTravel(
        MazePoint destination,
        string reason,
        string summary) =>
        new(
            Destination: destination,
            Path: [],
            Directions: [],
            HopCount: 0,
            TotalTerrainCost: 0,
            FrontierGain: 0,
            MaxVisibleRisk: 0,
            GuaranteedHazardDamage: 0,
            ObjectiveDelta: "blocked",
            Legal: false,
            Reason: reason,
            Summary: summary);

    private static bool CanEnterKnownTravelCell(
        MazeQuestStage stage,
        MazeQuestRunState state,
        MazeCell cell)
    {
        if (!state.Discovered.Contains(cell.Point) || cell.Terrain == MazeTerrain.Wall)
        {
            return false;
        }

        var questObject = ObjectAt(stage, cell.Point);
        return questObject?.Kind != MazeQuestObjectKind.Gate ||
            questObject.RequiredItem is not { } requiredItem ||
            state.Inventory.Contains(requiredItem, StringComparer.Ordinal);
    }

    private static int KnownTravelScore(MazeKnownTravelOption option)
    {
        var score = option.FrontierGain * 10;
        if (option.ObjectiveDelta.Contains("active_objective", StringComparison.Ordinal))
        {
            score += 1000;
        }
        else if (option.ObjectiveDelta.Contains("known_objective", StringComparison.Ordinal))
        {
            score += 500;
        }

        score -= option.HopCount;
        score -= option.GuaranteedHazardDamage * 50;
        score -= (int)Math.Round(option.MaxVisibleRisk * 25);
        return score;
    }

    private static string KnownTravelObjectiveDelta(
        MazeQuestStage stage,
        MazeQuestRunState state,
        IReadOnlyList<MazePoint> path,
        int frontierGain)
    {
        var objectiveObject = path
            .Skip(1)
            .Select(point => ObjectAt(stage, point))
            .FirstOrDefault(questObject =>
                questObject is not null &&
                stage.Quest.Objectives.Any(objective =>
                    !string.Equals(objective.ObjectiveId, "complete", StringComparison.Ordinal) &&
                    string.Equals(objective.TargetId, questObject.ObjectId, StringComparison.Ordinal)));

        if (objectiveObject is not null)
        {
            return string.Equals(objectiveObject.ObjectId, ActiveTargetId(stage, state), StringComparison.Ordinal)
                ? "reaches_active_objective_object"
                : "reaches_known_objective_object";
        }

        return frontierGain > 0 ? "expands_frontier" : "known_reposition";
    }

    private static string ObjectiveAffordance(
        MazeQuestStage stage,
        MazeQuestRunState state,
        MazePoint next,
        MazeQuestObject? questObject,
        int frontierGain)
    {
        if (questObject is not null &&
            stage.Quest.Objectives.Any(objective =>
                !string.Equals(objective.ObjectiveId, "complete", StringComparison.Ordinal) &&
                string.Equals(objective.TargetId, questObject.ObjectId, StringComparison.Ordinal)))
        {
            return string.Equals(questObject.ObjectId, ActiveTargetId(stage, state), StringComparison.Ordinal)
                ? "reaches_active_objective_object"
                : "reaches_known_objective_object";
        }

        if (frontierGain > 0)
        {
            return "expands_frontier";
        }

        return "local_traversal";
    }

    private static Dictionary<string, object?> ObjectiveBoardEntry(
        MazeQuestStage stage,
        MazeQuestRunState state,
        MazeQuestObjective objective,
        int index)
    {
        var priorRequiredIncomplete = stage.Quest.Objectives
            .Where(item => item.Kind != MazeObjectiveKind.Complete)
            .Take(index)
            .Any(item => item.Required && !state.CompletedObjectives.Contains(item.ObjectiveId));
        var status = state.CompletedObjectives.Contains(objective.ObjectiveId)
            ? "completed"
            : string.Equals(state.ActiveObjectiveId, objective.ObjectiveId, StringComparison.Ordinal)
                ? "active"
                : priorRequiredIncomplete
                    ? "waiting"
                    : "open";

        if (stage.Objects.TryGetValue(objective.TargetId, out var target) &&
            state.Inventory.Contains(target.ObjectId, StringComparer.Ordinal) &&
            objective.Kind is MazeObjectiveKind.FindItem or MazeObjectiveKind.CollectItem or MazeObjectiveKind.RescueTarget)
        {
            status = "completed";
        }

        var distance = stage.Objects.TryGetValue(objective.TargetId, out var questObject)
            ? MazePathfinder.Distances(stage.Grid, state.Position).GetValueOrDefault(questObject.Point, int.MaxValue / 2)
            : int.MaxValue / 2;

        return new Dictionary<string, object?>
        {
            ["objectiveId"] = objective.ObjectiveId,
            ["description"] = objective.Description,
            ["kind"] = objective.Kind.ToString(),
            ["targetId"] = objective.TargetId,
            ["required"] = objective.Required,
            ["priority"] = objective.Priority,
            ["status"] = status,
            ["distanceBand"] = DistanceBand(distance),
            ["knownCostBand"] = CostBand(distance),
            ["reward"] = RewardFor(stage, objective.TargetId)
        };
    }

    private static string SummaryFor(MazeCell cell, string objectiveDelta, int frontierGain)
    {
        var risk = cell.Hazard == MazeHazard.None
            ? "no visible hazard"
            : $"{cell.Hazard} risk {cell.HazardRisk:0.00}";
        return $"{objectiveDelta}; cost {cell.TraversalCost}; {risk}; reveals {frontierGain} cells.";
    }

    private static string? ActiveTargetId(MazeQuestStage stage, MazeQuestRunState state) =>
        stage.Quest.Objectives.FirstOrDefault(objective => objective.ObjectiveId == state.ActiveObjectiveId)?.TargetId;

    private static string DirectionBetween(MazePoint from, MazePoint to)
    {
        if (to.X > from.X)
        {
            return "east";
        }

        if (to.X < from.X)
        {
            return "west";
        }

        return to.Y > from.Y ? "south" : "north";
    }

    private static string CostBand(int distance) =>
        distance switch
        {
            <= 2 => "short",
            <= 6 => "medium",
            < int.MaxValue / 4 => "long",
            _ => "unknown"
        };

    private static Dictionary<string, object?>? RewardFor(MazeQuestStage stage, string targetId)
    {
        if (!stage.Objects.TryGetValue(targetId, out var questObject))
        {
            return null;
        }

        var cell = stage.Grid[questObject.Point];
        if (questObject.Kind == MazeQuestObjectKind.ResourceCache || cell.Reward == MazeReward.Energy)
        {
            return new Dictionary<string, object?> { ["energy"] = 4 };
        }

        if (cell.Reward == MazeReward.Health)
        {
            return new Dictionary<string, object?> { ["health"] = 2 };
        }

        return null;
    }

    private static MazePoint TargetFor(MazeQuestStage stage, string objectiveId)
    {
        var objective = stage.Quest.Objectives.FirstOrDefault(item =>
            string.Equals(item.ObjectiveId, objectiveId, StringComparison.Ordinal));

        if (objective is not null &&
            stage.Objects.TryGetValue(objective.TargetId, out var targetObject))
        {
            return targetObject.Point;
        }

        return objectiveId switch
        {
            "find_sun_key" => stage.Placements.Key,
            "unlock_sun_gate" => stage.Placements.Gate,
            "reach_exit" => stage.Placements.Exit,
            _ => stage.Placements.Exit
        };
    }

    private static MazeQuestObject? ObjectAt(MazeQuestStage stage, MazePoint point) =>
        stage.Objects.Values.FirstOrDefault(item => item.Point == point);

    private static string Bearing(MazePoint from, MazePoint to)
    {
        var eastWest = to.X > from.X ? "east" : to.X < from.X ? "west" : string.Empty;
        var northSouth = to.Y > from.Y ? "south" : to.Y < from.Y ? "north" : string.Empty;

        if (northSouth.Length > 0 && eastWest.Length > 0)
        {
            return $"{northSouth}_{eastWest}";
        }

        return northSouth.Length > 0 ? northSouth : eastWest.Length > 0 ? eastWest : "here";
    }

    private static string DistanceBand(int distance)
    {
        return distance switch
        {
            <= 0 => "here",
            <= 3 => "near",
            <= 7 => "medium",
            _ => "far"
        };
    }
}
