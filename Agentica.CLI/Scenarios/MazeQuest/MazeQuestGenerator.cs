namespace Agentica.CLI.Scenarios.MazeQuest;

public sealed class MazeQuestGenerator
{
    private static readonly (int Dx, int Dy)[] CarveDirections =
    [
        (0, -2),
        (2, 0),
        (0, 2),
        (-2, 0)
    ];

    public MazeQuestStage Generate(MazeQuestDescriptor descriptor, MazeQuestGenerationOptions options)
    {
        options = options.Normalize();
        var random = new Random(options.Seed);
        var open = CarveMaze(options.Width, options.Height, random);
        var baseGrid = BuildBaseGrid(open);
        var start = new MazePoint(1, 1);
        var distances = MazePathfinder.Distances(baseGrid, start);
        var exit = distances.OrderByDescending(pair => pair.Value).First().Key;
        var path = MazePathfinder.ShortestPath(baseGrid, start, exit);
        var weights = BuildWeights(baseGrid, distances);
        var decorators = new MazeQuestDecorators(random);
        var template = new MazeQuestTemplateGenerator(random, decorators)
            .Generate(descriptor, path, options.QuestType);
        var reservedPoints = template.Objects.Values
            .Select(item => item.Point)
            .Append(template.Placements.Start)
            .ToHashSet();
        var hazards = PickHazards(baseGrid, weights, template.Placements.Start, reservedPoints, random);
        var rewards = PickRewards(baseGrid, weights, reservedPoints, hazards.Keys, random);
        var grid = BuildDecoratedGrid(baseGrid, template.Objects, hazards, rewards, decorators);
        var energyPolicy = BuildEnergyPolicy(grid, template.Quest, template.Placements, template.Objects);

        return new MazeQuestStage(
            Quest: template.Quest,
            Grid: grid,
            Placements: template.Placements,
            Objects: template.Objects,
            Weights: weights,
            EnergyPolicy: energyPolicy,
            VisibilityRadius: options.VisibilityRadius,
            Seed: options.Seed);
    }

    private static MazeQuestEnergyPolicy BuildEnergyPolicy(
        MazeGrid grid,
        MazeQuestDefinition quest,
        MazeQuestPlacements placements,
        IReadOnlyDictionary<string, MazeQuestObject> objects)
    {
        var route = new List<MazePoint> { placements.Start };
        var cursor = placements.Start;
        foreach (var objective in quest.Objectives
            .Where(objective => objective.Kind != MazeObjectiveKind.Complete)
            .Where(objective => objective.Required))
        {
            var target = objects.TryGetValue(objective.TargetId, out var questObject)
                ? questObject.Point
                : objective.ObjectiveId == "reach_exit"
                    ? placements.Exit
                    : cursor;
            var segment = MazePathfinder.ShortestPath(grid, cursor, target);
            if (segment.Count > 1)
            {
                route.AddRange(segment.Skip(1));
                cursor = target;
            }
        }

        var triggeredHazards = new HashSet<string>(StringComparer.Ordinal);
        var perfectRouteCost = route
            .Skip(1)
            .Sum(point => MoveEnergyCost(grid[point], triggeredHazards));
        var padding = 5;
        var initialEnergy = Math.Max(8, perfectRouteCost + padding);

        return new MazeQuestEnergyPolicy(
            InitialEnergy: initialEnergy,
            MaxEnergy: initialEnergy,
            RestEnergyGain: 2,
            RestHealthGain: 1,
            RestCharges: 2,
            EnforceMoveEnergy: true,
            PerfectRouteCost: perfectRouteCost,
            Padding: padding);
    }

    private static int MoveEnergyCost(MazeCell cell, ISet<string> triggeredHazards)
    {
        var cost = Math.Max(1, cell.TraversalCost);
        var hazardKey = $"{cell.Point.X},{cell.Point.Y}:{cell.Hazard}";
        if (cell.Hazard == MazeHazard.Darkness && triggeredHazards.Add(hazardKey))
        {
            cost++;
        }

        return cost;
    }

    private static bool[,] CarveMaze(int width, int height, Random random)
    {
        var open = new bool[width, height];
        var visited = new bool[width, height];
        var stack = new Stack<MazePoint>();
        var start = new MazePoint(1, 1);

        open[start.X, start.Y] = true;
        visited[start.X, start.Y] = true;
        stack.Push(start);

        while (stack.Count > 0)
        {
            var current = stack.Peek();
            var choices = CarveDirections
                .Select(direction => current.Translate(direction.Dx, direction.Dy))
                .Where(point => point.X > 0 &&
                                point.Y > 0 &&
                                point.X < width - 1 &&
                                point.Y < height - 1 &&
                                !visited[point.X, point.Y])
                .OrderBy(_ => random.Next())
                .ToArray();

            if (choices.Length == 0)
            {
                stack.Pop();
                continue;
            }

            var next = choices[0];
            var wall = new MazePoint(
                current.X + Math.Sign(next.X - current.X),
                current.Y + Math.Sign(next.Y - current.Y));

            open[wall.X, wall.Y] = true;
            open[next.X, next.Y] = true;
            visited[next.X, next.Y] = true;
            stack.Push(next);
        }

        return open;
    }

    private static MazeGrid BuildBaseGrid(bool[,] open)
    {
        var width = open.GetLength(0);
        var height = open.GetLength(1);
        var cells = new Dictionary<MazePoint, MazeCell>();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var point = new MazePoint(x, y);
                var terrain = open[x, y] ? MazeTerrain.Floor : MazeTerrain.Wall;
                cells[point] = new MazeCell(
                    Point: point,
                    Terrain: terrain,
                    TraversalCost: terrain == MazeTerrain.Wall ? 0 : 1,
                    Hazard: MazeHazard.None,
                    HazardRisk: 0,
                    Reward: MazeReward.None,
                    ObjectiveItem: MazeObjectiveItem.None,
                    LockId: null,
                    DisplayName: terrain == MazeTerrain.Wall ? "wall" : "floor");
            }
        }

        return new MazeGrid(width, height, cells);
    }

    private static IReadOnlyDictionary<MazePoint, MazeCellWeights> BuildWeights(
        MazeGrid grid,
        IReadOnlyDictionary<MazePoint, int> distances)
    {
        var maxDistance = Math.Max(1, distances.Values.Max());
        return grid.AllCells().ToDictionary(
            cell => cell.Point,
            cell =>
            {
                if (cell.Terrain == MazeTerrain.Wall || !distances.TryGetValue(cell.Point, out var distance))
                {
                    return new MazeCellWeights(0, 0, 0, 0);
                }

                var depth = distance / (double)maxDistance;
                var openNeighbors = MazePathfinder.Neighbors(cell.Point)
                    .Count(point => grid.Contains(point) && grid[point].Terrain != MazeTerrain.Wall);
                var branchiness = openNeighbors / 4.0;

                return new MazeCellWeights(
                    Depth: depth,
                    HazardWeight: Math.Round((depth * 0.65) + (branchiness * 0.35), 3),
                    RewardWeight: Math.Round(((1 - Math.Abs(depth - 0.45)) * 0.6) + (branchiness * 0.4), 3),
                    FrontierWeight: Math.Round((1 - depth) * 0.5 + branchiness * 0.5, 3));
            });
    }

    private static IReadOnlyDictionary<MazePoint, MazeHazard> PickHazards(
        MazeGrid grid,
        IReadOnlyDictionary<MazePoint, MazeCellWeights> weights,
        MazePoint start,
        IReadOnlySet<MazePoint> reservedPoints,
        Random random)
    {
        var candidates = grid.AllCells()
            .Where(cell => cell.Terrain == MazeTerrain.Floor &&
                           !reservedPoints.Contains(cell.Point) &&
                           Math.Abs(cell.Point.X - start.X) + Math.Abs(cell.Point.Y - start.Y) > 2)
            .ToList();

        var hazards = new Dictionary<MazePoint, MazeHazard>();
        for (var index = 0; index < 3 && candidates.Count > 0; index++)
        {
            var selected = WeightedPick(candidates, cell => weights[cell.Point].HazardWeight + 0.05, random);
            hazards[selected.Point] = (index % 3) switch
            {
                0 => MazeHazard.Spike,
                1 => MazeHazard.Darkness,
                _ => MazeHazard.Trap
            };
            candidates.Remove(selected);
        }

        return hazards;
    }

    private static IReadOnlyDictionary<MazePoint, MazeReward> PickRewards(
        MazeGrid grid,
        IReadOnlyDictionary<MazePoint, MazeCellWeights> weights,
        IReadOnlySet<MazePoint> reservedPoints,
        IEnumerable<MazePoint> hazardPoints,
        Random random)
    {
        var excluded = new HashSet<MazePoint>(reservedPoints);
        excluded.UnionWith(hazardPoints);

        var candidates = grid.AllCells()
            .Where(cell => cell.Terrain == MazeTerrain.Floor && !excluded.Contains(cell.Point))
            .ToList();

        var rewards = new Dictionary<MazePoint, MazeReward>();
        var rewardTypes = new[] { MazeReward.Health, MazeReward.Energy };
        for (var index = 0; index < rewardTypes.Length && candidates.Count > 0; index++)
        {
            var selected = WeightedPick(candidates, cell => weights[cell.Point].RewardWeight + 0.05, random);
            rewards[selected.Point] = rewardTypes[index];
            candidates.Remove(selected);
        }

        return rewards;
    }

    private static MazeGrid BuildDecoratedGrid(
        MazeGrid baseGrid,
        IReadOnlyDictionary<string, MazeQuestObject> objects,
        IReadOnlyDictionary<MazePoint, MazeHazard> hazards,
        IReadOnlyDictionary<MazePoint, MazeReward> rewards,
        MazeQuestDecorators decorators)
    {
        var cells = new Dictionary<MazePoint, MazeCell>();
        foreach (var cell in baseGrid.AllCells())
        {
            var questObject = objects.Values.FirstOrDefault(item => item.Point == cell.Point);
            var terrain = questObject?.Kind == MazeQuestObjectKind.Gate
                ? MazeTerrain.Door
                : questObject?.Kind == MazeQuestObjectKind.Exit
                    ? MazeTerrain.Exit
                    : cell.Terrain;

            var hazard = hazards.GetValueOrDefault(cell.Point, MazeHazard.None);
            var reward = rewards.GetValueOrDefault(cell.Point, MazeReward.None);
            var objectiveItem = questObject?.Kind == MazeQuestObjectKind.Key
                ? MazeObjectiveItem.SunKey
                : MazeObjectiveItem.None;

            var traversalCost = terrain switch
            {
                MazeTerrain.Wall => 0,
                MazeTerrain.Door => 2,
                MazeTerrain.Exit => 1,
                _ => hazard == MazeHazard.Darkness ? 2 : 1
            };

            var hazardRisk = hazard switch
            {
                MazeHazard.Spike => 0.35,
                MazeHazard.Darkness => 0.25,
                MazeHazard.Trap => 0.45,
                _ => 0
            };

            var displayName = questObject?.DisplayName ?? DisplayName(terrain, hazard, reward, objectiveItem, decorators);
            cells[cell.Point] = cell with
            {
                Terrain = terrain,
                TraversalCost = traversalCost,
                Hazard = hazard,
                HazardRisk = hazardRisk,
                Reward = reward,
                ObjectiveItem = objectiveItem,
                LockId = questObject?.Kind == MazeQuestObjectKind.Gate ? questObject.ObjectId : null,
                DisplayName = displayName
            };
        }

        return new MazeGrid(baseGrid.Width, baseGrid.Height, cells);
    }

    private static string DisplayName(
        MazeTerrain terrain,
        MazeHazard hazard,
        MazeReward reward,
        MazeObjectiveItem objectiveItem,
        MazeQuestDecorators decorators)
    {
        if (objectiveItem != MazeObjectiveItem.None)
        {
            return decorators.DecorateObjectiveItem(objectiveItem);
        }

        if (reward != MazeReward.None)
        {
            return decorators.DecorateReward(reward);
        }

        if (terrain == MazeTerrain.Door)
        {
            return "sun gate";
        }

        if (terrain == MazeTerrain.Exit)
        {
            return "north exit";
        }

        return hazard switch
        {
            MazeHazard.Spike => "spike-marked floor",
            MazeHazard.Darkness => "dark floor",
            MazeHazard.Trap => "trapped floor",
            _ => terrain == MazeTerrain.Wall ? "wall" : "floor"
        };
    }

    private static T WeightedPick<T>(IReadOnlyList<T> items, Func<T, double> weightSelector, Random random)
    {
        var total = items.Sum(weightSelector);
        var roll = random.NextDouble() * total;
        var running = 0.0;

        foreach (var item in items)
        {
            running += weightSelector(item);
            if (roll <= running)
            {
                return item;
            }
        }

        return items[^1];
    }
}
