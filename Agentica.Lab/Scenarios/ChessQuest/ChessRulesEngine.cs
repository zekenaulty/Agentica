using Chess;

namespace Agentica.Lab.Scenarios.ChessQuest;

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
        var captures = CaptureFor(0, canonical, legalMove, before);
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
            LegalProjectionOnly: true,
            MoveQualityKnown: false,
            SafetyKnown: false,
            OpponentReplyModeled: accepted.Count >= 2,
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

    public ChessAttackInspection InspectAttacks(ChessQuestColor agentColor)
    {
        var opponentColor = Opposite(agentColor);
        var inspectionFen = FenWithSideToMove(GetFen(), opponentColor);
        var inspector = new GeraChessRulesEngine(inspectionFen);
        var captures = new List<ChessPublicCapture>();

        if (!inspector.GetState().IsTerminal)
        {
            foreach (var legalMove in inspector.ListLegalMoves())
            {
                var move = legalMove.Uci;
                var clone = new GeraChessRulesEngine(inspectionFen);
                var result = clone.TryPlayMove(move);
                if (!result.Accepted)
                {
                    continue;
                }

                foreach (var capture in result.Captures)
                {
                    if (!PieceBelongsTo(capture.Piece, agentColor))
                    {
                        continue;
                    }

                    captures.Add(new ChessPublicCapture(
                        Move: move,
                        From: move[..2],
                        To: move.Substring(2, 2),
                        CapturedPiece: capture.Piece));
                }
            }
        }

        var attackedPieces = captures
            .GroupBy(capture => (capture.To, capture.CapturedPiece))
            .OrderBy(group => group.Key.To, StringComparer.Ordinal)
            .ThenBy(group => group.Key.CapturedPiece, StringComparer.Ordinal)
            .Select(group => new ChessAttackedPiece(
                Square: group.Key.To,
                Piece: group.Key.CapturedPiece,
                Attackers: group
                    .Select(capture => capture.From)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                CaptureMoves: group
                    .Select(capture => capture.Move)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();

        return new ChessAttackInspection(
            Kind: "ChessAttackInspection",
            AgentColor: agentColor,
            OpponentColor: opponentColor,
            SideToMove: GetState().SideToMove,
            AgentKingInCheck: IsKingInCheck(agentColor),
            OpponentLegalCaptures: captures
                .OrderBy(capture => capture.Move, StringComparer.Ordinal)
                .ToArray(),
            AttackedAgentPieces: attackedPieces,
            ReadOnly: true,
            EvaluationIncluded: false,
            GuidanceIncluded: false,
            Note: "Neutral public attack inspection reports legal opponent captures from the current placement only; it does not choose moves or attach quality labels.");
    }

    public ChessCandidateInspection InspectCandidate(string uciMove, ChessQuestColor agentColor)
    {
        var before = GetFen();
        var normalized = uciMove.Trim().ToLowerInvariant();
        var clone = new GeraChessRulesEngine(before);
        var result = clone.TryPlayMove(normalized);

        if (!result.Accepted || result.Move is null)
        {
            return new ChessCandidateInspection(
                Kind: "ChessCandidateInspection",
                RequestedMove: normalized,
                AcceptedMove: null,
                CandidateLegal: false,
                RejectionReason: result.RefusalReason ?? "illegal_move",
                ReadOnly: true,
                SessionFenUnchanged: string.Equals(before, GetFen(), StringComparison.Ordinal),
                AgentAuthoredCandidate: true,
                CandidateScanOnly: true,
                LegalProjectionOnly: true,
                MoveQualityKnown: false,
                SafetyKnown: false,
                EvaluationIncluded: false,
                GuidanceIncluded: false,
                OpponentReplyModeled: false,
                OpponentCaptureFactsIncluded: false,
                FullOpponentReplyModeled: false,
                FenBefore: before,
                FenAfterCandidate: before,
                BoardAfterCandidate: RenderAsciiLines(),
                SideToMoveAfterCandidate: GetState().SideToMove,
                SideToMoveInCheckAfterCandidate: false,
                CandidateMoveGivesCheck: false,
                CandidateMoveGivesCheckmate: false,
                TerminalAfterCandidate: GetState().IsTerminal,
                TerminalStateAfterCandidate: GetState().TerminalState,
                CandidateCaptures: [],
                AttackInspectionAfterCandidate: null,
                Note: "Candidate was not legal from the current public position; no projected opponent capture facts were produced.");
        }

        var projectedState = clone.GetState();
        var sideToMoveInCheckAfter = !projectedState.IsTerminal &&
            clone.IsKingInCheck(projectedState.SideToMove);
        var candidateMoveGivesCheckmate = projectedState.TerminalState is { Winner: not null } terminal &&
            terminal.Winner == agentColor &&
            terminal.Reason.Contains("Checkmate", StringComparison.OrdinalIgnoreCase);
        var candidateMoveGivesCheck = sideToMoveInCheckAfter || candidateMoveGivesCheckmate;
        var attackInspection = projectedState.IsTerminal
            ? null
            : clone.InspectAttacks(agentColor);

        return new ChessCandidateInspection(
            Kind: "ChessCandidateInspection",
            RequestedMove: normalized,
            AcceptedMove: result.Move,
            CandidateLegal: true,
            RejectionReason: null,
            ReadOnly: true,
            SessionFenUnchanged: string.Equals(before, GetFen(), StringComparison.Ordinal),
            AgentAuthoredCandidate: true,
            CandidateScanOnly: true,
            LegalProjectionOnly: true,
            MoveQualityKnown: false,
            SafetyKnown: false,
            EvaluationIncluded: false,
            GuidanceIncluded: false,
            OpponentReplyModeled: false,
            OpponentCaptureFactsIncluded: attackInspection is not null,
            FullOpponentReplyModeled: false,
            FenBefore: before,
            FenAfterCandidate: clone.GetFen(),
            BoardAfterCandidate: clone.RenderAsciiLines(),
            SideToMoveAfterCandidate: projectedState.SideToMove,
            SideToMoveInCheckAfterCandidate: sideToMoveInCheckAfter,
            CandidateMoveGivesCheck: candidateMoveGivesCheck,
            CandidateMoveGivesCheckmate: candidateMoveGivesCheckmate,
            TerminalAfterCandidate: projectedState.IsTerminal,
            TerminalStateAfterCandidate: projectedState.TerminalState,
            CandidateCaptures: result.Captures,
            AttackInspectionAfterCandidate: attackInspection,
            Note: "Neutral candidate scan reports public consequences after the submitted move, including opponent capture facts; it does not rank, score, recommend, or prove safety.");
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

    private static ChessQuestColor Opposite(ChessQuestColor color) =>
        color == ChessQuestColor.White ? ChessQuestColor.Black : ChessQuestColor.White;

    private static bool PieceBelongsTo(string piece, ChessQuestColor color) =>
        piece.StartsWith(
            color.ToString().ToLowerInvariant() + "_",
            StringComparison.Ordinal);

    private static string FenWithSideToMove(string fen, ChessQuestColor sideToMove)
    {
        var parts = fen.Split(' ');
        if (parts.Length < 2)
        {
            return fen;
        }

        parts[1] = sideToMove == ChessQuestColor.White ? "w" : "b";
        return string.Join(' ', parts);
    }

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
        Move move,
        string fen)
    {
        var capturedPiece = PieceNameAt(fen, uciMove.Substring(2, 2)) ??
            (move.CapturedPiece is null ? null : PieceName(move.CapturedPiece));
        return capturedPiece is null
            ? []
            :
            [
                new ChessProjectedCapture(
                    plyOffset,
                    uciMove,
                    capturedPiece)
            ];
    }

    private static string PieceName(Piece piece) =>
        $"{ToQuestColor(piece.Color).ToString().ToLowerInvariant()}_{piece.Type.ToString().ToLowerInvariant()}";

    private static string? PieceNameAt(string fen, string square)
    {
        var board = fen.Split(' ', 2)[0];
        var targetFile = square[0] - 'a';
        var targetRank = square[1] - '1';
        var rank = 7;
        var file = 0;
        foreach (var character in board)
        {
            switch (character)
            {
                case '/':
                    rank--;
                    file = 0;
                    continue;
                case >= '1' and <= '8':
                    file += character - '0';
                    continue;
            }

            if (file == targetFile && rank == targetRank)
            {
                return PieceName(character);
            }

            file++;
        }

        return null;
    }

    private static string PieceName(char piece)
    {
        var color = char.IsUpper(piece) ? ChessQuestColor.White : ChessQuestColor.Black;
        var type = char.ToLowerInvariant(piece) switch
        {
            'p' => "pawn",
            'n' => "knight",
            'b' => "bishop",
            'r' => "rook",
            'q' => "queen",
            'k' => "king",
            _ => "piece"
        };

        return $"{color.ToString().ToLowerInvariant()}_{type}";
    }

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
