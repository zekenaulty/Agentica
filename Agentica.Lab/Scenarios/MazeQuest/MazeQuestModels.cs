namespace Agentica.Lab.Scenarios.MazeQuest;

public sealed record MazeQuestDescriptor(
    string QuestId,
    string Title,
    string Description,
    string Difficulty,
    int EstimatedSteps,
    int DefaultSeed);

public sealed record MazeQuestGenerationOptions(
    string QuestId,
    int Seed,
    int Width = 11,
    int Height = 11,
    int VisibilityRadius = 2,
    MazeQuestArchetype? QuestType = null)
{
    public MazeQuestGenerationOptions Normalize()
    {
        var width = NormalizeOdd(Width, min: 7, max: 15);
        var height = NormalizeOdd(Height, min: 7, max: 15);
        var visibilityRadius = Math.Clamp(VisibilityRadius, 1, 4);
        return this with
        {
            Width = width,
            Height = height,
            VisibilityRadius = visibilityRadius
        };
    }

    private static int NormalizeOdd(int value, int min, int max)
    {
        var normalized = Math.Clamp(value, min, max);
        return normalized % 2 == 0 ? normalized - 1 : normalized;
    }
}

public sealed record MazeQuestStage(
    MazeQuestDefinition Quest,
    MazeGrid Grid,
    MazeQuestPlacements Placements,
    IReadOnlyDictionary<string, MazeQuestObject> Objects,
    IReadOnlyDictionary<MazePoint, MazeCellWeights> Weights,
    MazeQuestEnergyPolicy EnergyPolicy,
    int VisibilityRadius,
    int Seed);

public sealed record MazeQuestEnergyPolicy(
    int InitialEnergy,
    int MaxEnergy,
    int RestEnergyGain,
    int RestHealthGain,
    int RestCharges,
    bool EnforceMoveEnergy,
    int PerfectRouteCost,
    int Padding);

public sealed record MazeQuestDefinition(
    string QuestId,
    string Title,
    string Objective,
    MazeQuestArchetype QuestType,
    IReadOnlyList<string> CoverageTags,
    IReadOnlyList<MazeQuestObjective> Objectives);

public sealed record MazeQuestObjective(
    string ObjectiveId,
    string Description,
    MazeObjectiveKind Kind,
    string TargetId)
{
    public bool Required { get; init; } = true;

    public int Priority { get; init; } = 100;
}

public sealed record MazeQuestPlacements(
    MazePoint Start,
    MazePoint Key,
    MazePoint Gate,
    MazePoint Exit);

public sealed record MazeQuestObject(
    string ObjectId,
    MazeQuestObjectKind Kind,
    MazePoint Point,
    string DisplayName,
    string? RequiredItem,
    IReadOnlyList<string> CoverageTags);

public sealed record MazeGrid(
    int Width,
    int Height,
    IReadOnlyDictionary<MazePoint, MazeCell> Cells)
{
    public MazeCell this[MazePoint point] => Cells[point];

    public bool Contains(MazePoint point) =>
        point.X >= 0 && point.X < Width && point.Y >= 0 && point.Y < Height;

    public IEnumerable<MazeCell> AllCells() => Cells.Values;
}

public sealed record MazeCell(
    MazePoint Point,
    MazeTerrain Terrain,
    int TraversalCost,
    MazeHazard Hazard,
    double HazardRisk,
    MazeReward Reward,
    MazeObjectiveItem ObjectiveItem,
    string? LockId,
    string DisplayName);

public sealed record MazeCellWeights(
    double Depth,
    double HazardWeight,
    double RewardWeight,
    double FrontierWeight);

public sealed record MazeQuestRunState(
    MazePoint Position,
    int Health,
    int Energy,
    int RestCharges,
    int StepCount,
    IReadOnlyList<string> Inventory,
    IReadOnlySet<MazePoint> Discovered,
    IReadOnlySet<string> TriggeredHazards,
    IReadOnlySet<string> CompletedObjectives,
    string ActiveObjectiveId)
{
    public static MazeQuestRunState Create(MazeQuestStage stage)
    {
        var discovered = MazeVisibility.VisiblePoints(
                stage.Grid,
                stage.Placements.Start,
                stage.VisibilityRadius)
            .ToHashSet();

        return new MazeQuestRunState(
            Position: stage.Placements.Start,
            Health: 8,
            Energy: stage.EnergyPolicy.InitialEnergy,
            RestCharges: stage.EnergyPolicy.RestCharges,
            StepCount: 0,
            Inventory: [],
            Discovered: discovered,
            TriggeredHazards: new HashSet<string>(StringComparer.Ordinal),
            CompletedObjectives: new HashSet<string>(StringComparer.Ordinal),
            ActiveObjectiveId: stage.Quest.Objectives[0].ObjectiveId);
    }
}

public sealed class MazeQuestSessionState
{
    public MazeQuestSessionState(MazeQuestStage stage)
    {
        Position = stage.Placements.Start;
        Energy = stage.EnergyPolicy.InitialEnergy;
        RestCharges = stage.EnergyPolicy.RestCharges;
        Discovered.UnionWith(MazeVisibility.VisiblePoints(stage.Grid, Position, stage.VisibilityRadius));
    }

    public MazePoint Position { get; set; }

    public int Health { get; set; } = 8;

    public int Energy { get; set; } = 8;

    public int RestCharges { get; set; }

    public int StepCount { get; set; }

    public List<string> Inventory { get; } = [];

    public HashSet<MazePoint> Discovered { get; } = [];

    public HashSet<string> CompletedObjectives { get; } = new(StringComparer.Ordinal);

    public HashSet<string> TakenObjects { get; } = new(StringComparer.Ordinal);

    public HashSet<string> ActivatedObjects { get; } = new(StringComparer.Ordinal);

    public HashSet<string> OpenedObjects { get; } = new(StringComparer.Ordinal);

    public HashSet<string> TriggeredHazards { get; } = new(StringComparer.Ordinal);

    public bool ObjectiveCompleted { get; set; }

    public MazeQuestRunState ToRunState(MazeQuestStage stage) =>
        new(
            Position,
            Health,
            Energy,
            RestCharges,
            StepCount,
            Inventory.ToArray(),
            Discovered.ToHashSet(),
            TriggeredHazards.ToHashSet(StringComparer.Ordinal),
            CompletedObjectives.ToHashSet(StringComparer.Ordinal),
            ActiveObjectiveId(stage));

    public string ActiveObjectiveId(MazeQuestStage stage) =>
        stage.Quest.Objectives
            .FirstOrDefault(objective => objective.Required && !CompletedObjectives.Contains(objective.ObjectiveId))
            ?.ObjectiveId ?? "complete";
}

public sealed record MazeObjectiveSignal(
    string ObjectiveId,
    string Bearing,
    string DistanceBand,
    double Warmth,
    string DeltaFromPrevious,
    double Confidence);

public sealed record MazeMoveEvaluation(
    string Direction,
    MazePoint To,
    bool Legal,
    string Reason,
    int TerrainCost,
    double VisibleRisk,
    string ObjectiveDelta,
    int FrontierGain,
    string Summary);

public sealed record MazeKnownTravelOption(
    MazePoint Destination,
    IReadOnlyList<MazePoint> Path,
    IReadOnlyList<string> Directions,
    int HopCount,
    int TotalTerrainCost,
    int FrontierGain,
    double MaxVisibleRisk,
    int GuaranteedHazardDamage,
    string ObjectiveDelta,
    bool Legal,
    string Reason,
    string Summary);

public sealed record MazeKnownTravelHop(
    int Index,
    string Direction,
    MazePoint From,
    MazePoint To,
    int TerrainCost,
    double VisibleRisk,
    int FrontierGain,
    string ObjectiveDelta,
    int EnergyBefore,
    int EnergyAfter,
    int HealthBefore,
    int HealthAfter,
    string ActiveObjectiveIdAfter);

public enum MazeObjectiveKind
{
    FindItem,
    CollectItem,
    DeliverItem,
    DiscoverLocation,
    ActivateObject,
    RescueTarget,
    UnlockGate,
    ReachExit,
    Complete
}

public enum MazeQuestArchetype
{
    Unlock,
    Collect,
    Delivery,
    Explore,
    Activate,
    PuzzleSequence,
    Rescue,
    ResourceRoute
}

public enum MazeQuestObjectKind
{
    Key,
    Gate,
    Exit,
    Collectible,
    DeliveryPickup,
    DeliveryDropoff,
    DiscoveryMarker,
    Activator,
    RescueTarget,
    Refuge,
    PuzzleRune,
    ResourceCache
}

public enum MazeTerrain
{
    Wall,
    Floor,
    Door,
    Exit
}

public enum MazeHazard
{
    None,
    Spike,
    Darkness,
    Trap
}

public enum MazeReward
{
    None,
    Health,
    Energy,
    Clue
}

public enum MazeObjectiveItem
{
    None,
    SunKey
}

public readonly record struct MazePoint(int X, int Y)
{
    public MazePoint Translate(int dx, int dy) => new(X + dx, Y + dy);
}
