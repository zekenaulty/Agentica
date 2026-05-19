using Agentica.Tools;

namespace Agentica.CLI.Scenarios.MazeQuest;

public sealed record MazeQuestToolTurn(
    ToolInvocation Invocation,
    ToolResult Result,
    MazeQuestRunState RunState)
{
    public MazeQuestRunState? BeforeRunState { get; init; }
}

public enum MazeQuestNarratorKind
{
    Off,
    Deterministic,
    Gemini
}

public sealed record MazeQuestTurnEnvelope(
    int TurnNumber,
    string StepId,
    string ToolId,
    string ReceiptId,
    string ReceiptStatus,
    string ReceiptMessage,
    string? ObservationId,
    string? ArtifactId,
    string? ArtifactKind,
    string ActiveObjectiveId,
    int StepCount,
    MazePoint Position,
    int Health,
    int Energy,
    IReadOnlyList<string> Inventory,
    string? VisibleMapAscii,
    MazeObjectiveSignal? ObjectiveSignal,
    IReadOnlyList<MazeMoveEvaluation> MoveEvaluations,
    IReadOnlyList<MazeKnownTravelOption> KnownTravelOptions,
    string Narration);
