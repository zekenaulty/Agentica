namespace Agentica.CLI.Scenarios.ChessQuest;

public interface IChessRulesEngine
{
    ChessPublicState GetState();

    string GetFen();

    string GetPgn();

    string RenderAscii();

    IReadOnlyList<string> RenderAsciiLines();

    IReadOnlyList<ChessLegalMove> ListLegalMoves();

    bool IsKingInCheck(ChessQuestColor color);

    ChessMoveResult TryPlayMove(string uciMove);

    ChessLineProjection ProjectLine(ChessLineProjectionRequest request);

    ChessAttackInspection InspectAttacks(ChessQuestColor agentColor);

    ChessCandidateInspection InspectCandidate(string uciMove, ChessQuestColor agentColor);
}
