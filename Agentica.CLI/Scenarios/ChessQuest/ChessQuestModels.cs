using Agentica.Tools;

namespace Agentica.CLI.Scenarios.ChessQuest;

public enum ChessQuestColor
{
    White,
    Black
}

public enum ChessQuestMode
{
    StrictRefereeHard,
    StrictRefereeProjected,
    StrictRefereeThreatAware,
    ActorProbe,
    StrictRefereeAssisted,
    Coach
}

public enum ChessQuestObjectiveKind
{
    WinGame
}

public enum ChessQuestOpponentMode
{
    Random,
    Heuristic,
    Agent
}

public sealed record ChessQuestDifficulty(
    string Scenario,
    string Surface,
    string Opponent);

public sealed record ChessQuestScenario(
    string ScenarioId,
    string Title,
    string InitialFen,
    ChessQuestColor AgentColor,
    ChessQuestObjectiveKind ObjectiveKind,
    string PublicObjective,
    ChessQuestDifficulty Difficulty,
    ChessQuestDisclosurePolicy DisclosurePolicy,
    IReadOnlyList<string>? HiddenSolutionLine = null);

public sealed record ChessQuestSessionContext(
    string Kind,
    string SessionId,
    ChessQuestColor AgentColor,
    ChessQuestColor OpponentColor,
    ChessQuestColor SideToMove,
    bool AgentToMove,
    string SurfaceMode,
    ChessQuestObjective Objective,
    ChessQuestResultPolicy ResultPolicy,
    ChessQuestDifficulty Difficulty,
    int Ply,
    bool SideToMoveInCheck,
    bool Terminal);

public sealed record ChessQuestLegalMoveObservation(
    string ObservationId,
    string ReceiptId,
    string Fen,
    int Ply,
    ChessQuestColor SideToMove,
    IReadOnlyList<string> LegalMoves);

public sealed record ChessQuestObjective(
    string Kind,
    string PublicDescription);

public sealed record ChessQuestResultPolicy(
    bool WinRequired,
    string DrawCountsAs,
    string LossCountsAs,
    bool ResignationAllowed);

public sealed record ChessLegalMove(string Uci);

public sealed record ChessTerminalState(
    string Result,
    string Reason,
    ChessQuestColor? Winner);

public sealed record ChessPublicState(
    string Fen,
    ChessQuestColor SideToMove,
    bool IsTerminal,
    ChessTerminalState? TerminalState,
    int Ply,
    IReadOnlyList<string> RecentMovesUci);

public sealed record ChessMoveResult(
    bool Accepted,
    string? Move,
    string FenBefore,
    string FenAfter,
    ChessTerminalState? TerminalState,
    IReadOnlyList<ChessProjectedCapture> Captures,
    string? RefusalReason = null);

public sealed record ChessLineProjectionRequest(
    IReadOnlyList<string> Line,
    int MaxPlies,
    IReadOnlyList<string>? Claims = null);

public sealed record ChessLineProjection(
    IReadOnlyList<string> AcceptedPrefix,
    ChessLineRejection? RejectedAt,
    bool ReadOnly,
    bool SessionFenUnchanged,
    bool LegalProjectionOnly,
    bool MoveQualityKnown,
    bool SafetyKnown,
    bool OpponentReplyModeled,
    string FenAfter,
    IReadOnlyList<string> BoardAfter,
    ChessQuestColor SideToMoveAfter,
    bool SideToMoveInCheckAfter,
    bool LastAcceptedMoveGivesCheck,
    bool LastAcceptedMoveGivesCheckmate,
    bool Terminal,
    ChessTerminalState? TerminalState,
    IReadOnlyList<ChessProjectedCapture> Captures,
    IReadOnlyDictionary<string, bool> ClaimVerification,
    string? Note = null);

public sealed record ChessLineRejection(
    int PlyOffset,
    string Move,
    string Reason);

public sealed record ChessProjectedCapture(
    int PlyOffset,
    string Move,
    string Piece);

public sealed record ChessAttackInspection(
    string Kind,
    ChessQuestColor AgentColor,
    ChessQuestColor OpponentColor,
    ChessQuestColor SideToMove,
    bool AgentKingInCheck,
    IReadOnlyList<ChessPublicCapture> OpponentLegalCaptures,
    IReadOnlyList<ChessAttackedPiece> AttackedAgentPieces,
    bool ReadOnly,
    bool EvaluationIncluded,
    bool GuidanceIncluded,
    string? Note = null);

public sealed record ChessPublicCapture(
    string Move,
    string From,
    string To,
    string CapturedPiece);

public sealed record ChessAttackedPiece(
    string Square,
    string Piece,
    IReadOnlyList<string> Attackers,
    IReadOnlyList<string> CaptureMoves);

public sealed record ChessOpponentRequest(
    string Fen,
    ChessQuestColor OpponentColor,
    string Difficulty,
    int Ply,
    int Seed,
    IReadOnlyList<string> LegalMoves);

public sealed record ChessOpponentMove(
    string Move,
    string Policy);

public sealed record ChessQuestRecordedPly(
    int Ply,
    string Move,
    ChessQuestColor Color,
    string Source,
    string FenBefore,
    string FenAfter,
    DateTimeOffset At);

public sealed record ChessQuestGameRecord(
    string Kind,
    int Version,
    string ScenarioId,
    string Title,
    string InitialFen,
    ChessQuestColor AgentColor,
    ChessQuestDifficulty Difficulty,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CurrentFen,
    int Ply,
    bool Terminal,
    ChessTerminalState? TerminalState,
    IReadOnlyList<ChessQuestRecordedPly> Plies);

public interface IChessOpponent
{
    Task<ChessOpponentMove?> ChooseMoveAsync(
        ChessOpponentRequest request,
        CancellationToken cancellationToken);
}

public sealed class ScriptedChessOpponent : IChessOpponent
{
    private readonly Queue<string> _moves;
    private readonly bool _fallbackToFirstLegalMove;

    public ScriptedChessOpponent(
        IEnumerable<string> moves,
        bool fallbackToFirstLegalMove = true)
    {
        _moves = new Queue<string>(moves);
        _fallbackToFirstLegalMove = fallbackToFirstLegalMove;
    }

    public Task<ChessOpponentMove?> ChooseMoveAsync(
        ChessOpponentRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_moves.Count > 0)
        {
            return Task.FromResult<ChessOpponentMove?>(new ChessOpponentMove(_moves.Dequeue(), "scripted"));
        }

        return Task.FromResult(
            _fallbackToFirstLegalMove && request.LegalMoves.Count > 0
                ? new ChessOpponentMove(request.LegalMoves[0], "scripted_fallback_first_legal")
                : null);
    }
}

public sealed class RandomLegalMoveOpponent : IChessOpponent
{
    private readonly Random _random;

    public RandomLegalMoveOpponent(int seed)
    {
        _random = new Random(seed);
    }

    public Task<ChessOpponentMove?> ChooseMoveAsync(
        ChessOpponentRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.LegalMoves.Count == 0)
        {
            return Task.FromResult<ChessOpponentMove?>(null);
        }

        var move = request.LegalMoves[_random.Next(request.LegalMoves.Count)];
        return Task.FromResult<ChessOpponentMove?>(new ChessOpponentMove(move, "random_legal"));
    }
}

public sealed record ChessQuestToolTurn(
    ToolInvocation Invocation,
    ToolResult Result,
    ChessPublicState StateAfter);
