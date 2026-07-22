namespace Agentica.Lab.Scenarios.ChessQuest;

public interface IChessQuestBoard
{
    IReadOnlyList<ChessQuestScenarioDescriptor> ListScenarios();

    ChessQuestScenario Load(string scenarioId);

    IChessOpponent CreateOpponent(string scenarioId, int seed);
}

public sealed record ChessQuestScenarioDescriptor(
    string ScenarioId,
    string Title,
    string Objective,
    string Description,
    string Difficulty,
    string Surface,
    string Opponent,
    int EstimatedSteps);

public sealed class ChessQuestBoard : IChessQuestBoard
{
    public const string DefaultScenarioId = "fools_mate_finish";

    private const string StandardStartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string FoolsMateBlackToMoveFen = "rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq g3 0 2";

    private readonly ChessQuestScenarioDescriptor[] _descriptors =
    [
        new(
            ScenarioId: DefaultScenarioId,
            Title: "Fool's Mate Finish",
            Objective: "Win the game as Black from a mate-in-one position. Draw is not success.",
            Description: "A deterministic smoke scenario for the strict referee tool surface.",
            Difficulty: "Intro",
            Surface: "StrictRefereeProjected",
            Opponent: "none-after-terminal",
            EstimatedSteps: 6),
        new(
            ScenarioId: "standard_start_random",
            Title: "Standard Start vs Random",
            Objective: "Win the game as White from the standard starting position. Draw is not success.",
            Description: "A longer benchmark-style session against a seeded random legal-move opponent.",
            Difficulty: "Open",
            Surface: "StrictRefereeProjected",
            Opponent: "random-legal",
            EstimatedSteps: 80),
        new(
            ScenarioId: "standard_start_heuristic",
            Title: "Standard Start vs Heuristic",
            Objective: "Win the game as White from the standard starting position. Draw is not success.",
            Description: "A longer benchmark-style session against a pure managed heuristic opponent.",
            Difficulty: "Open",
            Surface: "StrictRefereeThreatAware",
            Opponent: "heuristic-club",
            EstimatedSteps: 120),
        new(
            ScenarioId: "standard_start_agent",
            Title: "Standard Start vs Agent Opponent",
            Objective: "Win the game as White from the standard starting position. Draw is not success.",
            Description: "A benchmark-style session where a second bounded Agentica planner chooses the opponent replies.",
            Difficulty: "Open",
            Surface: "StrictRefereeThreatAware",
            Opponent: "agent-opponent",
            EstimatedSteps: 80)
    ];

    public IReadOnlyList<ChessQuestScenarioDescriptor> ListScenarios() => _descriptors;

    public ChessQuestScenario Load(string scenarioId)
    {
        var descriptor = _descriptors.FirstOrDefault(item =>
            string.Equals(item.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
        {
            throw new InvalidOperationException($"Unknown ChessQuest scenario '{scenarioId}'.");
        }

        return descriptor.ScenarioId switch
        {
            DefaultScenarioId => new ChessQuestScenario(
                ScenarioId: descriptor.ScenarioId,
                Title: descriptor.Title,
                InitialFen: FoolsMateBlackToMoveFen,
                AgentColor: ChessQuestColor.Black,
                ObjectiveKind: ChessQuestObjectiveKind.WinGame,
                PublicObjective: descriptor.Objective,
                Difficulty: new ChessQuestDifficulty(
                    Scenario: descriptor.Difficulty.ToLowerInvariant(),
                    Surface: "strict_projected",
                    Opponent: descriptor.Opponent),
                DisclosurePolicy: ChessQuestDisclosurePolicy.StrictRefereeProjected,
                HiddenSolutionLine: ["d8h4"]),
            "standard_start_random" => new ChessQuestScenario(
                ScenarioId: descriptor.ScenarioId,
                Title: descriptor.Title,
                InitialFen: StandardStartFen,
                AgentColor: ChessQuestColor.White,
                ObjectiveKind: ChessQuestObjectiveKind.WinGame,
                PublicObjective: descriptor.Objective,
                Difficulty: new ChessQuestDifficulty(
                    Scenario: descriptor.Difficulty.ToLowerInvariant(),
                    Surface: "strict_projected",
                    Opponent: descriptor.Opponent),
                DisclosurePolicy: ChessQuestDisclosurePolicy.StrictRefereeProjected),
            "standard_start_heuristic" or "standard_start_agent" => new ChessQuestScenario(
                ScenarioId: descriptor.ScenarioId,
                Title: descriptor.Title,
                InitialFen: StandardStartFen,
                AgentColor: ChessQuestColor.White,
                ObjectiveKind: ChessQuestObjectiveKind.WinGame,
                PublicObjective: descriptor.Objective,
                Difficulty: new ChessQuestDifficulty(
                    Scenario: descriptor.Difficulty.ToLowerInvariant(),
                    Surface: "strict_threat_aware",
                    Opponent: descriptor.Opponent),
                DisclosurePolicy: ChessQuestDisclosurePolicy.StrictRefereeThreatAware),
            _ => throw new InvalidOperationException($"ChessQuest scenario '{scenarioId}' is not implemented.")
        };
    }

    public IChessOpponent CreateOpponent(string scenarioId, int seed) =>
        string.Equals(scenarioId, DefaultScenarioId, StringComparison.OrdinalIgnoreCase)
            ? new ScriptedChessOpponent([], fallbackToFirstLegalMove: false)
            : string.Equals(scenarioId, "standard_start_heuristic", StringComparison.OrdinalIgnoreCase)
                ? new HeuristicChessOpponent(seed, "club")
            : new RandomLegalMoveOpponent(seed);
}
