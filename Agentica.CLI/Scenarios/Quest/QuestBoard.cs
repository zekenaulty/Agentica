namespace Agentica.CLI.Scenarios.Quest;

public interface IQuestBoard
{
    IReadOnlyList<QuestDescriptor> ListQuests();

    QuestDefinition Load(string questId);
}

public sealed class InMemoryQuestBoard : IQuestBoard
{
    private static readonly QuestDescriptor SunGateDescriptor = new(
        QuestId: "sun_gate",
        Title: "The Sun Gate",
        Description: "Recover the sun key and open the north gate.",
        EstimatedSteps: 8,
        Difficulty: "Easy");

    public IReadOnlyList<QuestDescriptor> ListQuests() => [SunGateDescriptor];

    public QuestDefinition Load(string questId)
    {
        if (!string.Equals(questId, SunGateDescriptor.QuestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unknown quest '{questId}'.");
        }

        return new QuestDefinition(
            QuestId: SunGateDescriptor.QuestId,
            Title: SunGateDescriptor.Title,
            Objective: "Open the north gate.",
            StartLocation: "foyer",
            Rooms: new Dictionary<string, QuestRoom>(StringComparer.Ordinal)
            {
                ["foyer"] = new QuestRoom(
                    RoomId: "foyer",
                    Description: "A quiet entry hall. A locked gate stands to the north.",
                    Exits: new Dictionary<string, QuestExit>(StringComparer.Ordinal)
                    {
                        ["east"] = new QuestExit("east", "study"),
                        ["north"] = new QuestExit("north", "hall", "sun_gate")
                    },
                    Items: [],
                    Npcs: new Dictionary<string, string>(StringComparer.Ordinal)),
                ["study"] = new QuestRoom(
                    RoomId: "study",
                    Description: "A dusty study. A brass key rests on the desk.",
                    Exits: new Dictionary<string, QuestExit>(StringComparer.Ordinal)
                    {
                        ["west"] = new QuestExit("west", "foyer")
                    },
                    Items: ["sun_key"],
                    Npcs: new Dictionary<string, string>(StringComparer.Ordinal)),
                ["hall"] = new QuestRoom(
                    RoomId: "hall",
                    Description: "The hall beyond the sun gate.",
                    Exits: new Dictionary<string, QuestExit>(StringComparer.Ordinal),
                    Items: [],
                    Npcs: new Dictionary<string, string>(StringComparer.Ordinal))
            },
            Locks: new Dictionary<string, QuestLock>(StringComparer.Ordinal)
            {
                ["sun_gate"] = new QuestLock("sun_gate", "sun_key")
            });
    }
}
