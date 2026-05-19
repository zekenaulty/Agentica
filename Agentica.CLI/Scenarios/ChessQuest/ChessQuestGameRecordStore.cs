using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentica.CLI.Scenarios.ChessQuest;

public static class ChessQuestGameRecordStore
{
    public const string GameRecordFileName = "chessquest-game.json";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions JsonLineOptions = CreateJsonOptions(writeIndented: false);

    public static ChessQuestGameRecord FromSession(
        ChessQuestSession session,
        DateTimeOffset? createdAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        var state = session.CurrentState;
        return new ChessQuestGameRecord(
            Kind: "chessquest.game_record",
            Version: CurrentVersion,
            ScenarioId: session.Scenario.ScenarioId,
            Title: session.Scenario.Title,
            InitialFen: session.Scenario.InitialFen,
            AgentColor: session.Scenario.AgentColor,
            Difficulty: session.Scenario.Difficulty,
            CreatedAt: createdAt ?? now,
            UpdatedAt: now,
            CurrentFen: state.Fen,
            Ply: state.Ply,
            Terminal: state.IsTerminal,
            TerminalState: state.TerminalState,
            Plies: session.CommittedPlies.ToArray());
    }

    public static string WriteDirectory(string directoryPath, ChessQuestSession session)
    {
        Directory.CreateDirectory(directoryPath);
        var path = Path.Combine(directoryPath, GameRecordFileName);
        ChessQuestGameRecord? existing = File.Exists(path)
            ? ReadRecord(path)
            : null;
        var record = FromSession(session, existing?.CreatedAt);
        File.WriteAllText(path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
        return path;
    }

    public static ChessQuestGameRecord Load(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            var recordPath = Path.Combine(fullPath, GameRecordFileName);
            return File.Exists(recordPath)
                ? ReadRecord(recordPath)
                : LoadFromLegacyRunDirectory(fullPath);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"ChessQuest replay source '{path}' was not found.", fullPath);
        }

        return ReadRecord(fullPath);
    }

    public static void ReplayIntoSession(ChessQuestSession session, ChessQuestGameRecord record)
    {
        session.ReplayCommittedPlies(record.Plies);
        if (!string.Equals(session.CurrentState.Fen, record.CurrentFen, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ChessQuest replay validation failed: expected final FEN '{record.CurrentFen}', but got '{session.CurrentState.Fen}'.");
        }
    }

    private static ChessQuestGameRecord ReadRecord(string path)
    {
        var record = JsonSerializer.Deserialize<ChessQuestGameRecord>(
            File.ReadAllText(path),
            JsonOptions);
        return record ?? throw new InvalidOperationException($"ChessQuest game record '{path}' was empty.");
    }

    private static ChessQuestGameRecord LoadFromLegacyRunDirectory(string directoryPath)
    {
        var scenarioPath = Path.Combine(directoryPath, "chessquest-scenario.json");
        if (!File.Exists(scenarioPath))
        {
            throw new FileNotFoundException(
                $"ChessQuest run directory '{directoryPath}' does not contain '{GameRecordFileName}' or 'chessquest-scenario.json'.",
                scenarioPath);
        }

        var scenario = JsonSerializer.Deserialize<ChessQuestScenario>(
            File.ReadAllText(scenarioPath),
            JsonOptions) ?? throw new InvalidOperationException($"ChessQuest scenario file '{scenarioPath}' was empty.");

        var turnsPath = Path.Combine(directoryPath, "chessquest-turns.jsonl");
        var plies = new List<ChessQuestRecordedPly>();
        var rules = new GeraChessRulesEngine(scenario.InitialFen);
        var createdAt = File.GetCreationTimeUtc(scenarioPath);

        if (File.Exists(turnsPath))
        {
            foreach (var line in File.ReadLines(turnsPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var turn = JsonSerializer.Deserialize<ChessQuestCockpitTurnEnvelope>(line, JsonLineOptions);
                if (turn is null)
                {
                    continue;
                }

                ApplyLoggedMove(rules, plies, turn.SelectedMove, "agent", createdAt);
                if (turn.OpponentMoveApplied && !string.IsNullOrWhiteSpace(turn.OpponentMove))
                {
                    ApplyLoggedMove(rules, plies, turn.OpponentMove, "opponent", createdAt);
                }

                if (!string.Equals(rules.GetFen(), turn.FenAfter, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"ChessQuest turn log replay failed at turn {turn.TurnNumber}: expected FEN '{turn.FenAfter}', but got '{rules.GetFen()}'.");
                }
            }
        }

        var finalState = rules.GetState();
        return new ChessQuestGameRecord(
            Kind: "chessquest.game_record",
            Version: CurrentVersion,
            ScenarioId: scenario.ScenarioId,
            Title: scenario.Title,
            InitialFen: scenario.InitialFen,
            AgentColor: scenario.AgentColor,
            Difficulty: scenario.Difficulty,
            CreatedAt: createdAt,
            UpdatedAt: File.Exists(turnsPath) ? File.GetLastWriteTimeUtc(turnsPath) : createdAt,
            CurrentFen: finalState.Fen,
            Ply: finalState.Ply,
            Terminal: finalState.IsTerminal,
            TerminalState: finalState.TerminalState,
            Plies: plies);
    }

    private static void ApplyLoggedMove(
        GeraChessRulesEngine rules,
        List<ChessQuestRecordedPly> plies,
        string move,
        string source,
        DateTimeOffset at)
    {
        var before = rules.GetState();
        var result = rules.TryPlayMove(move);
        if (!result.Accepted || result.Move is null)
        {
            throw new InvalidOperationException(
                $"ChessQuest turn log replay failed at ply {plies.Count + 1}: move '{move}' was refused as {result.RefusalReason ?? "illegal_move"}.");
        }

        plies.Add(new ChessQuestRecordedPly(
            Ply: plies.Count + 1,
            Move: result.Move,
            Color: before.SideToMove,
            Source: source,
            FenBefore: result.FenBefore,
            FenAfter: result.FenAfter,
            At: at));
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = writeIndented
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
