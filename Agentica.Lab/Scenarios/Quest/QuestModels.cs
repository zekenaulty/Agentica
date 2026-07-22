namespace Agentica.Lab.Scenarios.Quest;

public sealed record QuestDescriptor(
    string QuestId,
    string Title,
    string Description,
    int EstimatedSteps,
    string Difficulty);

public sealed record QuestDefinition(
    string QuestId,
    string Title,
    string Objective,
    string StartLocation,
    IReadOnlyDictionary<string, QuestRoom> Rooms,
    IReadOnlyDictionary<string, QuestLock> Locks);

public sealed record QuestRoom(
    string RoomId,
    string Description,
    IReadOnlyDictionary<string, QuestExit> Exits,
    IReadOnlyList<string> Items,
    IReadOnlyDictionary<string, string> Npcs);

public sealed record QuestExit(
    string Direction,
    string To,
    string? LockId = null);

public sealed record QuestLock(
    string LockId,
    string RequiredItem);

public sealed class QuestRunState
{
    public QuestRunState(string startLocation)
    {
        Location = startLocation;
        VisitedRooms.Add(startLocation);
    }

    public string Location { get; set; }

    public List<string> Inventory { get; } = [];

    public HashSet<string> OpenedLocks { get; } = new(StringComparer.Ordinal);

    public HashSet<string> VisitedRooms { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

    public HashSet<string> CollectedItems { get; } = new(StringComparer.Ordinal);

    public bool ObjectiveCompleted { get; set; }
}
