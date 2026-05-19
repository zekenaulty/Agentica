using Chess;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed class GeraChessRulesEngine : IChessRulesEngine
{
    private readonly ChessBoard _board;
    private readonly List<string> _movesUci;

    public GeraChessRulesEngine(string fen)
        : this(ChessBoard.LoadFromFen(fen, AutoEndgameRules.All), [])
    {
    }

    private GeraChessRulesEngine(ChessBoard board, IEnumerable<string> movesUci)
    {
        _board = board;
        _movesUci = movesUci.ToList();
    }

    public ChessPublicState GetState() =>
        new(
            Fen: GetFen(),
            SideToMove: ToQuestColor(_board.Turn),
            IsTerminal: _board.IsEndGame,
            TerminalState: TerminalState(_board),
            Ply: _movesUci.Count,
            RecentMovesUci: _movesUci.TakeLast(12).ToArray());

    public string GetFen() => _board.ToFen();

    public string GetPgn() => _board.ToPgn();

    public string RenderAscii() =>
        ChessQuestRenderer.RenderBoardFromFen(GetFen());

    public IReadOnlyList<string> RenderAsciiLines() =>
        ChessQuestRenderer.RenderBoardLinesFromFen(GetFen());

    public IReadOnlyList<ChessLegalMove> ListLegalMoves() =>
        _board.IsEndGame
            ? []
            : _board.Moves(generateSan: false)
                .Select(ToUci)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(move => new ChessLegalMove(move))
                .ToArray();

    public bool IsKingInCheck(ChessQuestColor color) =>
        color == ChessQuestColor.White
            ? _board.WhiteKingChecked
            : _board.BlackKingChecked;

    public ChessMoveResult TryPlayMove(string uciMove)
    {
        var before = GetFen();
        if (_board.IsEndGame)
        {
            return new ChessMoveResult(
                Accepted: false,
                Move: null,
                FenBefore: before,
                FenAfter: before,
                TerminalState: TerminalState(_board),
                Captures: [],
                RefusalReason: "game_is_terminal");
        }

        var legalMove = FindLegalMove(uciMove);
        if (legalMove is null)
        {
            return new ChessMoveResult(
                Accepted: false,
                Move: null,
                FenBefore: before,
                FenAfter: before,
                TerminalState: TerminalState(_board),
                Captures: [],
                RefusalReason: "illegal_move");
        }

        var canonical = ToUci(legalMove);
        var captures = CaptureFor(0, canonical, legalMove);
        _board.Move(legalMove);
        _movesUci.Add(canonical);

        return new ChessMoveResult(
            Accepted: true,
            Move: canonical,
            FenBefore: before,
            FenAfter: GetFen(),
            TerminalState: TerminalState(_board),
            Captures: captures);
    }

    public ChessLineProjection ProjectLine(ChessLineProjectionRequest request)
    {
        var clone = new GeraChessRulesEngine(GetFen());
        var accepted = new List<string>();
        var captures = new List<ChessProjectedCapture>();
        ChessLineRejection? rejected = null;
        var maxPlies = Math.Max(0, request.MaxPlies);
        var line = request.Line.Take(maxPlies).ToArray();

        for (var index = 0; index < line.Length; index++)
        {
            var move = line[index];
            var result = clone.TryPlayMove(move);
            if (!result.Accepted || result.Move is null)
            {
                rejected = new ChessLineRejection(index, move, result.RefusalReason ?? "illegal_move");
                break;
            }

            accepted.Add(result.Move);
            captures.AddRange(result.Captures.Select(capture => capture with
            {
                PlyOffset = index,
                Move = result.Move
            }));

            if (clone.GetState().IsTerminal)
            {
                break;
            }
        }

        var note = request.Line.Count switch
        {
            0 => "No line was supplied.",
            _ when request.Line.Count > maxPlies => $"Line was truncated to maxPlies={maxPlies}.",
            _ when accepted.Count > 0 && rejected is null && !clone.GetState().IsTerminal =>
                "Line ended without generating any opponent reply beyond the submitted moves.",
            _ => null
        };

        var projectedState = clone.GetState();
        var sideToMoveInCheckAfter = accepted.Count > 0 &&
            clone.IsKingInCheck(projectedState.SideToMove);
        var lastAcceptedMoveGivesCheckmate = accepted.Count > 0 &&
            projectedState.TerminalState is { Winner: not null } terminal &&
            terminal.Winner != projectedState.SideToMove &&
            terminal.Reason.Contains("Checkmate", StringComparison.OrdinalIgnoreCase);
        var lastAcceptedMoveGivesCheck = sideToMoveInCheckAfter || lastAcceptedMoveGivesCheckmate;
        return new ChessLineProjection(
            AcceptedPrefix: accepted,
            RejectedAt: rejected,
            ReadOnly: true,
            SessionFenUnchanged: string.Equals(GetFen(), _board.ToFen(), StringComparison.Ordinal),
            FenAfter: clone.GetFen(),
            BoardAfter: clone.RenderAsciiLines(),
            SideToMoveAfter: projectedState.SideToMove,
            SideToMoveInCheckAfter: sideToMoveInCheckAfter,
            LastAcceptedMoveGivesCheck: lastAcceptedMoveGivesCheck,
            LastAcceptedMoveGivesCheckmate: lastAcceptedMoveGivesCheckmate,
            Terminal: projectedState.IsTerminal,
            TerminalState: projectedState.TerminalState,
            Captures: captures,
            ClaimVerification: VerifyClaims(
                request.Claims,
                lastAcceptedMoveGivesCheck,
                lastAcceptedMoveGivesCheckmate),
            Note: note);
    }

    private Move? FindLegalMove(string uciMove)
    {
        if (!IsUci(uciMove))
        {
            return null;
        }

        return _board.Moves(generateSan: false)
            .FirstOrDefault(move => string.Equals(ToUci(move), uciMove.Trim().ToLowerInvariant(), StringComparison.Ordinal));
    }

    private static bool IsUci(string move)
    {
        var value = move.Trim().ToLowerInvariant();
        if (value.Length is not 4 and not 5)
        {
            return false;
        }

        return IsSquare(value[..2]) &&
               IsSquare(value.Substring(2, 2)) &&
               (value.Length == 4 || "qrbn".Contains(value[4], StringComparison.Ordinal));
    }

    private static bool IsSquare(string square) =>
        square.Length == 2 &&
        square[0] is >= 'a' and <= 'h' &&
        square[1] is >= '1' and <= '8';

    private static IReadOnlyDictionary<string, bool> VerifyClaims(
        IReadOnlyList<string>? claims,
        bool givesCheck,
        bool givesCheckmate)
    {
        if (claims is null || claims.Count == 0)
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["check"] = givesCheck,
                ["checkmate"] = givesCheckmate
            };
        }

        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var claim in claims)
        {
            var normalized = claim.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "check":
                    result["check"] = givesCheck;
                    break;
                case "mate":
                case "checkmate":
                    result["checkmate"] = givesCheckmate;
                    break;
            }
        }

        return result;
    }

    private static string ToUci(Move move)
    {
        var value = $"{move.OriginalPosition}{move.NewPosition}".ToLowerInvariant();
        if (move.Promotion is not null)
        {
            value += char.ToLowerInvariant(move.Promotion.Type.AsChar);
        }

        return value;
    }

    private static IReadOnlyList<ChessProjectedCapture> CaptureFor(
        int plyOffset,
        string uciMove,
        Move move) =>
        move.CapturedPiece is null
            ? []
            :
            [
                new ChessProjectedCapture(
                    plyOffset,
                    uciMove,
                    PieceName(move.CapturedPiece))
            ];

    private static string PieceName(Piece piece) =>
        $"{ToQuestColor(piece.Color).ToString().ToLowerInvariant()}_{piece.Type.ToString().ToLowerInvariant()}";

    private static ChessTerminalState? TerminalState(ChessBoard board)
    {
        if (!board.IsEndGame || board.EndGame is null)
        {
            return null;
        }

        var endGame = board.EndGame;
        var winner = endGame.WonSide is null
            ? (ChessQuestColor?)null
            : ToQuestColor(endGame.WonSide);
        var result = winner is null
            ? "draw"
            : $"{winner.Value.ToString().ToLowerInvariant()}_win";

        return new ChessTerminalState(
            Result: result,
            Reason: endGame.EndgameType.ToString(),
            Winner: winner);
    }

    private static ChessQuestColor ToQuestColor(PieceColor color) =>
        color == PieceColor.White ? ChessQuestColor.White : ChessQuestColor.Black;
}
