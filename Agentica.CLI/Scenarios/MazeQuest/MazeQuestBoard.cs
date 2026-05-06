namespace Agentica.CLI.Scenarios.MazeQuest;

public interface IMazeQuestBoard
{
    IReadOnlyList<MazeQuestDescriptor> ListQuests();

    MazeQuestDescriptor GetQuest(string questId);
}

public sealed class MazeQuestBoard : IMazeQuestBoard
{
    private static readonly IReadOnlyList<MazeQuestDescriptor> Quests =
    [
        new MazeQuestDescriptor(
            QuestId: "sun_gate_maze",
            Title: "The Sun Gate Maze",
            Description: "Find the sun key, open the gate, and reach the exit through a fogged maze.",
            Difficulty: "Easy",
            EstimatedSteps: 24,
            DefaultSeed: 173)
    ];

    public IReadOnlyList<MazeQuestDescriptor> ListQuests() => Quests;

    public MazeQuestDescriptor GetQuest(string questId) =>
        Quests.FirstOrDefault(quest => string.Equals(quest.QuestId, questId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Unknown maze quest '{questId}'.");
}
