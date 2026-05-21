using Agentica.Artifacts;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed record ChessQuestProjectedLineSummary(
    IReadOnlyList<string> RequestedLine,
    IReadOnlyList<string> AcceptedPrefix,
    string? RejectedMove,
    string? RejectionReason,
    bool Terminal,
    string? TerminalResult,
    ChessQuestColor SideToMoveAfter,
    string FenAfter,
    string? Note);

public sealed record ChessQuestCandidateInspectionSummary(
    string RequestedMove,
    bool CandidateLegal,
    string? RejectionReason,
    IReadOnlyList<ChessPublicCapture> OpponentLegalCapturesAfterCandidate,
    IReadOnlyList<ChessAttackedPiece> AttackedAgentPiecesAfterCandidate,
    string FenAfterCandidate,
    string? Note);

public sealed record ChessQuestCockpitTurnEnvelope(
    int TurnNumber,
    string StepId,
    string ReceiptId,
    ReceiptStatus ReceiptStatus,
    string ReceiptMessage,
    ChessQuestColor AgentColor,
    ChessQuestColor SideToMoveAfter,
    int PlyAfter,
    string SelectedMove,
    string? OpponentMove,
    bool OpponentMoveApplied,
    bool Terminal,
    string? TerminalResult,
    string FenAfter,
    string? PublicIntentAction,
    string? PublicIntentRationale,
    string? PublicIntentExpectedOutcome,
    string? TurnPublicReason,
    int? LegalMoveCountBeforeMove,
    IReadOnlyList<ChessQuestProjectedLineSummary> CandidateLinesExplored)
{
    public bool AgentMoveAccepted { get; init; } = true;

    public bool FenUnchanged { get; init; }

    public int? CommittedAgentTurnNumber { get; init; }

    public string? TurnGoal { get; init; }

    public IReadOnlyList<string> TurnEvidence { get; init; } = [];

    public string? TurnHypothesis { get; init; }

    public string? TurnRiskCheck { get; init; }

    public string? TurnClaimLevel { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<ChessQuestCandidateInspectionSummary> CandidateInspections { get; init; } = [];
}
