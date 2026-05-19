using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Runs;

namespace Agentica.CLI.Scenarios.ChessQuest;

public enum ChessQuestStrategyMode
{
    Off,
    Phase,
    Projected
}

public sealed record ChessQuestStrategyProjection(
    string Kind,
    string ProjectionId,
    DateTimeOffset CreatedAt,
    string Source,
    ChessQuestColor AgentColor,
    string Phase,
    string StrategyName,
    string StrategyIntent,
    IReadOnlyList<string> ActiveObjectives,
    IReadOnlyList<string> StopTriggers,
    IReadOnlyList<string> ProgressSignals,
    IReadOnlyList<string> VerificationRules);

public sealed record ChessQuestStrategyFrame(
    string Kind,
    string StrategyFrameId,
    string Phase,
    string StrategyName,
    string StrategyIntent,
    IReadOnlyList<string> ActiveObjectives,
    IReadOnlyList<string> ReplanTriggers);

public sealed record ChessQuestPhaseObjective(
    string Kind,
    string Phase,
    string Goal,
    int MaxAgentTurns,
    IReadOnlyList<string> StopTriggers);

public sealed record ChessQuestPhaseProgress(
    int StartPly,
    int CurrentPly,
    int AgentTurnsPlayed,
    int AgentTurnsRemaining,
    int ProjectedLinesUsed,
    bool Terminal,
    string? TerminalResult);

public sealed record ChessQuestPhaseContext(
    string Kind,
    string PhaseRunId,
    DateTimeOffset StartedAt,
    int StartPly,
    int StartCommittedPlyCount,
    int StartAgentMoveCount,
    string StartFen,
    ChessQuestStrategyProjection? StrategyProjection,
    ChessQuestStrategyFrame StrategyFrame,
    ChessQuestPhaseObjective PhaseObjective,
    ChessQuestPhaseProgress Progress);

public sealed record ChessQuestPhaseReport(
    string Kind,
    string PhaseRunId,
    string Phase,
    string StrategyName,
    string Status,
    string StopReason,
    int AgentTurnsPlayed,
    int AgentTurnsRemaining,
    int StartPly,
    int EndPly,
    string StartFen,
    string EndFen,
    int MaterialBalanceBefore,
    int MaterialBalanceAfter,
    int MaterialBalanceDelta,
    bool Terminal,
    ChessTerminalState? TerminalState,
    IReadOnlyList<string> AgentMoves,
    IReadOnlyList<string> OpponentMoves,
    IReadOnlyList<string> Evidence);

public sealed class ChessQuestPhaseTracker
{
    private ChessQuestPhaseTracker(ChessQuestPhaseContext context)
    {
        Context = context;
    }

    public ChessQuestPhaseContext Context { get; private set; }

    public static ChessQuestPhaseTracker Create(
        ChessQuestSession session,
        string phase,
        int maxAgentTurns,
        ChessQuestStrategyProjection? strategyProjection = null)
    {
        var normalizedPhase = NormalizePhase(phase);
        var frame = strategyProjection is null
            ? DefaultStrategyFrame(normalizedPhase)
            : ToStrategyFrame(strategyProjection);
        var objective = DefaultPhaseObjective(normalizedPhase, maxAgentTurns);
        var state = session.CurrentState;
        var context = new ChessQuestPhaseContext(
            Kind: "ChessQuestPhaseContext",
            PhaseRunId: $"chess_phase_{Guid.NewGuid():N}"[..24],
            StartedAt: DateTimeOffset.UtcNow,
            StartPly: state.Ply,
            StartCommittedPlyCount: session.CommittedPlies.Count,
            StartAgentMoveCount: CountAgentMoves(session, session.CommittedPlies.Count),
            StartFen: state.Fen,
            StrategyProjection: strategyProjection,
            StrategyFrame: frame,
            PhaseObjective: objective,
            Progress: BuildProgress(session, objective, state.Ply, CountAgentMoves(session, session.CommittedPlies.Count)));

        return new ChessQuestPhaseTracker(context);
    }

    public ChessQuestPhaseContext Snapshot(ChessQuestSession session)
    {
        Context = Context with
        {
            Progress = BuildProgress(
                session,
                Context.PhaseObjective,
                Context.StartPly,
                Context.StartAgentMoveCount)
        };
        return Context;
    }

    public bool IsPhaseComplete(ChessQuestSession session)
    {
        var progress = Snapshot(session).Progress;
        return progress.Terminal || progress.AgentTurnsPlayed >= Context.PhaseObjective.MaxAgentTurns;
    }

    public ChessQuestPhaseReport BuildReport(ChessQuestSession session)
    {
        var snapshot = Snapshot(session);
        var progress = snapshot.Progress;
        var state = session.CurrentState;
        var phasePlies = session.CommittedPlies
            .Skip(snapshot.StartCommittedPlyCount)
            .ToArray();
        var agentMoves = phasePlies
            .Where(ply => string.Equals(ply.Source, "agent", StringComparison.Ordinal))
            .Select(ply => ply.Move)
            .ToArray();
        var opponentMoves = phasePlies
            .Where(ply => string.Equals(ply.Source, "opponent", StringComparison.Ordinal))
            .Select(ply => ply.Move)
            .ToArray();
        var stopReason = state.IsTerminal
            ? "terminal_game_state"
            : progress.AgentTurnsPlayed >= snapshot.PhaseObjective.MaxAgentTurns
                ? "agent_turn_budget_exhausted"
                : "run_stopped_before_phase_boundary";
        var status = stopReason switch
        {
            "terminal_game_state" => "terminal",
            "agent_turn_budget_exhausted" => "budget_complete",
            _ => "partial"
        };
        var materialBefore = MaterialBalance(snapshot.StartFen, session.Scenario.AgentColor);
        var materialAfter = MaterialBalance(state.Fen, session.Scenario.AgentColor);

        return new ChessQuestPhaseReport(
            Kind: "ChessQuestPhaseReport",
            PhaseRunId: snapshot.PhaseRunId,
            Phase: snapshot.PhaseObjective.Phase,
            StrategyName: snapshot.StrategyFrame.StrategyName,
            Status: status,
            StopReason: stopReason,
            AgentTurnsPlayed: progress.AgentTurnsPlayed,
            AgentTurnsRemaining: progress.AgentTurnsRemaining,
            StartPly: snapshot.StartPly,
            EndPly: state.Ply,
            StartFen: snapshot.StartFen,
            EndFen: state.Fen,
            MaterialBalanceBefore: materialBefore,
            MaterialBalanceAfter: materialAfter,
            MaterialBalanceDelta: materialAfter - materialBefore,
            Terminal: state.IsTerminal,
            TerminalState: state.TerminalState,
            AgentMoves: agentMoves,
            OpponentMoves: opponentMoves,
            Evidence: phasePlies
                .Select(ply => $"ply:{ply.Ply}:{ply.Source}:{ply.Move}")
                .ToArray());
    }

    private static ChessQuestPhaseProgress BuildProgress(
        ChessQuestSession session,
        ChessQuestPhaseObjective objective,
        int startPly,
        int startAgentMoveCount)
    {
        var state = session.CurrentState;
        var agentTurnsPlayed = CountAgentMoves(session, session.CommittedPlies.Count) - startAgentMoveCount;
        return new ChessQuestPhaseProgress(
            StartPly: startPly,
            CurrentPly: state.Ply,
            AgentTurnsPlayed: agentTurnsPlayed,
            AgentTurnsRemaining: Math.Max(0, objective.MaxAgentTurns - agentTurnsPlayed),
            ProjectedLinesUsed: session.ProjectedLinesThisTurn,
            Terminal: state.IsTerminal,
            TerminalResult: state.TerminalState?.Result);
    }

    private static int CountAgentMoves(ChessQuestSession session, int committedPlyCount) =>
        session.CommittedPlies
            .Take(committedPlyCount)
            .Count(ply => string.Equals(ply.Source, "agent", StringComparison.Ordinal));

    private static string NormalizePhase(string phase) =>
        string.IsNullOrWhiteSpace(phase)
            ? "opening"
            : phase.Trim().ToLowerInvariant().Replace('_', '-');

    private static ChessQuestStrategyFrame DefaultStrategyFrame(string phase) =>
        phase switch
        {
            "tactical" or "tactical-conversion" => new ChessQuestStrategyFrame(
                Kind: "ChessQuestStrategyFrame",
                StrategyFrameId: $"strategy_{Guid.NewGuid():N}"[..22],
                Phase: phase,
                StrategyName: "verified tactical conversion",
                StrategyIntent: "Use public-rule projection to verify forcing ideas, then commit only legal moves that improve or convert the tactical opportunity.",
                ActiveObjectives:
                [
                    "verify check and checkmate claims with chess.project_line",
                    "prefer forcing legal moves when they are publicly verified",
                    "avoid unsupported tactical claims",
                    "continue playing for a win if the tactic is not terminal"
                ],
                ReplanTriggers:
                [
                    "terminal state reached",
                    "candidate tactic is illegal",
                    "opponent reply invalidates the tactic",
                    "phase turn budget exhausted"
                ]),
            "endgame" or "conversion" => new ChessQuestStrategyFrame(
                Kind: "ChessQuestStrategyFrame",
                StrategyFrameId: $"strategy_{Guid.NewGuid():N}"[..22],
                Phase: phase,
                StrategyName: "endgame conversion",
                StrategyIntent: "Convert durable advantages by improving king activity, preserving material, and advancing promotion chances without immediate collapse.",
                ActiveObjectives:
                [
                    "preserve material unless a verified tactic compensates",
                    "advance passed pawns or support promotion candidates",
                    "improve king activity when legal and safe",
                    "avoid stalemate or draw claims as success"
                ],
                ReplanTriggers:
                [
                    "promotion occurs",
                    "major material swing",
                    "terminal state reached",
                    "phase turn budget exhausted"
                ]),
            _ => new ChessQuestStrategyFrame(
                Kind: "ChessQuestStrategyFrame",
                StrategyFrameId: $"strategy_{Guid.NewGuid():N}"[..22],
                Phase: phase,
                StrategyName: "center-control development",
                StrategyIntent: "Develop pieces, contest central squares, improve king safety, and avoid immediate material loss.",
                ActiveObjectives:
                [
                    "develop a minor piece when legal and useful",
                    "contest or occupy central squares",
                    "improve king safety or prepare castling if available",
                    "avoid obvious one-move material loss",
                    "verify checkmate claims with chess.project_line"
                ],
                ReplanTriggers:
                [
                    "king safety changes sharply",
                    "major material swing",
                    "phase objective no longer matches the board",
                    "terminal state reached",
                    "phase turn budget exhausted"
                ])
        };

    public static ChessQuestStrategyProjection DefaultProjection(
        ChessQuestSession session,
        string phase,
        string source = "host_default")
    {
        var normalizedPhase = NormalizePhase(phase);
        var frame = DefaultStrategyFrame(normalizedPhase);
        return new ChessQuestStrategyProjection(
            Kind: "ChessQuestStrategyProjection",
            ProjectionId: $"strategy_projection_{Guid.NewGuid():N}"[..31],
            CreatedAt: DateTimeOffset.UtcNow,
            Source: source,
            AgentColor: session.Scenario.AgentColor,
            Phase: frame.Phase,
            StrategyName: frame.StrategyName,
            StrategyIntent: frame.StrategyIntent,
            ActiveObjectives: frame.ActiveObjectives,
            StopTriggers: frame.ReplanTriggers,
            ProgressSignals:
            [
                "legal agent move committed",
                "phase objective visibly advanced",
                "no unsupported checkmate claims",
                "phase report remains receipt-backed"
            ],
            VerificationRules:
            [
                "chessFrame is authoritative board truth",
                "legal move receipts override strategy claims",
                "use chess.project_line to verify check and checkmate claims",
                "strategy projection is guidance and never proves completion"
            ]);
    }

    private static ChessQuestStrategyFrame ToStrategyFrame(ChessQuestStrategyProjection projection) =>
        new(
            Kind: "ChessQuestStrategyFrame",
            StrategyFrameId: projection.ProjectionId.Replace("strategy_projection_", "strategy_", StringComparison.Ordinal),
            Phase: projection.Phase,
            StrategyName: projection.StrategyName,
            StrategyIntent: projection.StrategyIntent,
            ActiveObjectives: projection.ActiveObjectives,
            ReplanTriggers: projection.StopTriggers);

    private static ChessQuestPhaseObjective DefaultPhaseObjective(string phase, int maxAgentTurns) =>
        new(
            Kind: "ChessQuestPhaseObjective",
            Phase: phase,
            Goal: phase switch
            {
                "tactical" or "tactical-conversion" =>
                    "Exploit a tactical opportunity only when it can be verified through public line projection.",
                "endgame" or "conversion" =>
                    "Convert the position toward a win while preserving enough material and avoiding draw outcomes.",
                _ =>
                    "Develop pieces, improve king safety, contest the center, and avoid immediate material loss."
            },
            MaxAgentTurns: Math.Max(1, maxAgentTurns),
            StopTriggers:
            [
                "terminal_game_state",
                "agent_turn_budget_exhausted",
                "planner_blocked",
                "tool_failure"
            ]);

    private static int MaterialBalance(string fen, ChessQuestColor agentColor)
    {
        var board = fen.Split(' ', 2)[0];
        var score = 0;
        foreach (var character in board)
        {
            var value = char.ToLowerInvariant(character) switch
            {
                'p' => 1,
                'n' => 3,
                'b' => 3,
                'r' => 5,
                'q' => 9,
                _ => 0
            };
            if (value == 0)
            {
                continue;
            }

            var isWhite = char.IsUpper(character);
            var isAgentPiece = agentColor == ChessQuestColor.White ? isWhite : !isWhite;
            score += isAgentPiece ? value : -value;
        }

        return score;
    }
}

public sealed class ChessQuestPhaseCompletionEvaluator : ICompletionEvaluator
{
    private readonly ChessQuestSession _session;
    private readonly ChessQuestPhaseTracker _phaseTracker;

    public ChessQuestPhaseCompletionEvaluator(
        ChessQuestSession session,
        ChessQuestPhaseTracker phaseTracker)
    {
        _session = session;
        _phaseTracker = phaseTracker;
    }

    public CompletionEvaluation Evaluate(AgenticaRun run)
    {
        if (run.Artifacts.Any(artifact =>
                string.Equals(artifact.Kind, "chessquest.objective_completed", StringComparison.Ordinal)))
        {
            return CompletionEvaluation.Complete();
        }

        return _phaseTracker.IsPhaseComplete(_session)
            ? CompletionEvaluation.Complete()
            : CompletionEvaluation.Continue("ChessQuest phase boundary has not been reached.");
    }
}

public sealed class ChessQuestPhaseOutcomeReporter : IOutcomeReporter
{
    private readonly ChessQuestOutcomeReporter _inner = new();
    private readonly ChessQuestSession _session;
    private readonly ChessQuestPhaseTracker _phaseTracker;

    public ChessQuestPhaseOutcomeReporter(
        ChessQuestSession session,
        ChessQuestPhaseTracker phaseTracker)
    {
        _session = session;
        _phaseTracker = phaseTracker;
    }

    public OutcomeReport BuildReport(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<Agentica.Validation.ValidationIssue> validationIssues,
        IReadOnlyList<string> blockers)
    {
        var objectiveArtifact = run.Artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Kind, "chessquest.objective_completed", StringComparison.Ordinal));
        if (objectiveArtifact is not null)
        {
            return _inner.BuildReport(run, status, stopReason, validationIssues, blockers);
        }

        var report = _phaseTracker.BuildReport(_session);
        return new OutcomeReport(
            ReportId: Agentica.AgenticaIds.New("report"),
            Summary: status == RunOutcomeStatus.Succeeded
                ? $"The ChessQuest phase '{report.Phase}' stopped at boundary '{report.StopReason}' after {report.AgentTurnsPlayed} agent turn(s)."
                : $"The ChessQuest phase '{report.Phase}' stopped with status {status}. Stop reason: {stopReason}.",
            Claims:
            [
                new ReportClaim(
                    "The phase report was compiled from committed ChessQuest plies and current board state.",
                    report.Evidence.Select(evidence => new Agentica.Observations.EvidenceRef("phaseEvidence", evidence)).ToArray())
            ]);
    }
}
