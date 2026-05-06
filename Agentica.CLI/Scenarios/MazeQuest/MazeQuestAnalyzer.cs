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
        var target = TargetFor(stage, state.ActiveObjectiveId);
        var currentDistances = MazePathfinder.Distances(stage.Grid, state.Position);
        var currentDistance = currentDistances.GetValueOrDefault(target, int.MaxValue / 2);

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

            var nextDistances = MazePathfinder.Distances(stage.Grid, next);
            var nextDistance = nextDistances.GetValueOrDefault(target, int.MaxValue / 2);
            var delta = nextDistance < currentDistance
                ? "warmer"
                : nextDistance > currentDistance
                    ? "colder"
                    : "same";
            var frontierGain = MazeVisibility.VisiblePoints(stage.Grid, next, stage.VisibilityRadius)
                .Count(point => !state.Discovered.Contains(point));

            return new MazeMoveEvaluation(
                Direction: direction.Direction,
                To: next,
                Legal: true,
                Reason: "legal",
                TerrainCost: requiredEnergy,
                VisibleRisk: Math.Round(cell.HazardRisk, 2),
                ObjectiveDelta: delta,
                FrontierGain: frontierGain,
                Summary: SummaryFor(cell, delta, frontierGain));
        }).ToArray();
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
            .Where(objective => !string.Equals(objective.ObjectiveId, "complete", StringComparison.Ordinal))
            .Select(objective => new Dictionary<string, object?>
            {
                ["objectiveId"] = objective.ObjectiveId,
                ["kind"] = objective.Kind.ToString(),
                ["description"] = objective.Description,
                ["targetId"] = objective.TargetId
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
                ["targetId"] = objective.TargetId
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
            ["knownBlockers"] = EvaluateMoves(stage, state).Where(move => !move.Legal).ToArray()
        };
    }

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

    private static string SummaryFor(MazeCell cell, string delta, int frontierGain)
    {
        var risk = cell.Hazard == MazeHazard.None
            ? "no visible hazard"
            : $"{cell.Hazard} risk {cell.HazardRisk:0.00}";
        return $"{delta}; cost {cell.TraversalCost}; {risk}; reveals {frontierGain} cells.";
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
