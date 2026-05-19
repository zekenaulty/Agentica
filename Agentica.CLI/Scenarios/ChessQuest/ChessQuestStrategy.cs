using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Runs;
using System.Text.RegularExpressions;

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
    IReadOnlyList<string> VerificationRules,
    IReadOnlyList<string> ClaimDiscipline);

public sealed record ChessQuestPlayingDoctrine(
    string Kind,
    string Version,
    string Summary,
    IReadOnlyList<string> GoodPlayCriteria,
    IReadOnlyList<string> WinCriteria,
    IReadOnlyList<string> EvidenceDiscipline,
    IReadOnlyList<string> CommonFailurePatterns);

public sealed record ChessQuestDecisionProtocol(
    string Kind,
    string Version,
    ChessQuestPlayingDoctrine PlayingDoctrine,
    string Phase,
    string TaskDirection,
    string PublicRationale,
    string PhaseGoal,
    IReadOnlyList<string> ActiveObjectives,
    IReadOnlyList<string> SuccessSignals,
    IReadOnlyList<string> ReplanTriggers,
    IReadOnlyList<string> ClaimDiscipline,
    IReadOnlyDictionary<string, string> ToolSemantics,
    IReadOnlyList<string> TurnIntentFields,
    IReadOnlyList<string> ForbiddenClaimLanguageWithoutEvidence,
    IReadOnlyList<string> HostGuardrails);

public static partial class ChessQuestGoalShapingPolicy
{
    public static ChessQuestPlayingDoctrine StaticDoctrine { get; } = new(
        Kind: "ChessQuestPlayingDoctrine",
        Version: "1.0",
        Summary: "Play a good game of chess by preserving king safety, material, piece activity, and evidence discipline while pursuing a win.",
        GoodPlayCriteria:
        [
            "preserve king safety",
            "avoid losing material without compensation",
            "improve piece activity and coordination",
            "contest the center and important squares",
            "create threats only when they are supported",
            "respond to opponent threats before pursuing plans",
            "convert advantages without allowing avoidable counterplay"
        ],
        WinCriteria:
        [
            "win the game for the assigned color",
            "draw is not success",
            "terminal win must be verified by the host objective gate"
        ],
        EvidenceDiscipline:
        [
            "legal move listing proves legality only, not quality",
            "one-ply projection proves rule consequences only, not safety",
            "safety, material, forcing, and decisive claims require supporting projected lines or committed receipts",
            "phase guidance is public intent and must yield to board truth, legal moves, and receipts"
        ],
        CommonFailurePatterns:
        [
            "treating legal as safe",
            "treating nonterminal as harmless",
            "treating a one-ply projection as a tactical evaluation",
            "continuing a phase after material or king-safety evidence invalidates it",
            "using fluent chess language without verifier-backed evidence"
        ]);

    public static ChessQuestDecisionProtocol BuildDecisionProtocol(
        ChessQuestSession session,
        ChessQuestPhaseTracker? phaseTracker)
    {
        var phaseContext = phaseTracker?.Snapshot(session);
        var projection = phaseContext?.StrategyProjection;
        var objective = phaseContext?.PhaseObjective;
        var frame = phaseContext?.StrategyFrame;
        var phase = SanitizePhase(objective?.Phase ?? projection?.Phase ?? frame?.Phase ?? "game");
        var phaseGoal = SanitizeText(
            objective?.Goal ?? projection?.StrategyIntent ?? frame?.StrategyIntent ?? session.Scenario.PublicObjective,
            session.Scenario.PublicObjective);
        var taskDirection = SanitizeText(
            projection?.StrategyName ?? frame?.StrategyName ?? "play for a verified win",
            "play for a verified win");
        var publicRationale = SanitizeText(
            projection?.StrategyIntent ?? frame?.StrategyIntent ?? "Use public board truth and receipts to guide play.",
            "Use public board truth and receipts to guide play.");

        return new ChessQuestDecisionProtocol(
            Kind: "ChessQuestDecisionProtocol",
            Version: "1.0",
            PlayingDoctrine: StaticDoctrine,
            Phase: phase,
            TaskDirection: taskDirection,
            PublicRationale: publicRationale,
            PhaseGoal: phaseGoal,
            ActiveObjectives: SanitizeList(
                projection?.ActiveObjectives ?? frame?.ActiveObjectives,
                DefaultActiveObjectives(phase, phaseGoal),
                maxItems: 6),
            SuccessSignals: SanitizeList(
                projection?.ProgressSignals,
                DefaultSuccessSignals(phase),
                maxItems: 6),
            ReplanTriggers: SanitizeList(
                projection?.StopTriggers ?? frame?.ReplanTriggers,
                DefaultReplanTriggers(),
                maxItems: 8),
            ClaimDiscipline: EnsureClaimDiscipline(SanitizeList(
                projection?.ClaimDiscipline,
                DefaultClaimDiscipline(phase),
                maxItems: 8)),
            ToolSemantics: ToolSemanticsFor(session),
            TurnIntentFields:
            [
                "goal",
                "evidence",
                "hypothesis",
                "riskCheck",
                "claimLevel",
                "publicReason"
            ],
            ForbiddenClaimLanguageWithoutEvidence:
            [
                "safe",
                "winning",
                "wins material",
                "decisive",
                "forced",
                "forces",
                "checkmate",
                "mate"
            ],
            HostGuardrails:
            [
                "host may shape role, phase, protocol, and evidence grammar",
                "host must not choose board-specific tactical salience in strict mode",
                "orchestration goals are sanitized to remove move-level directions",
                "legal move receipts and board state override strategy prose"
            ]);
    }

    public static IReadOnlyList<string> SanitizeList(
        IReadOnlyList<string>? values,
        IReadOnlyList<string> fallback,
        int maxItems)
    {
        var sanitized = (values ?? [])
            .Select(value => SanitizeText(value, string.Empty))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxItems)
            .ToArray();

        return sanitized.Length == 0
            ? fallback.Take(maxItems).ToArray()
            : sanitized;
    }

    public static string SanitizeText(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var text = value.Trim().ReplaceLineEndings(" ");
        return ContainsMoveLevelGuidance(text)
            ? fallback
            : text;
    }

    public static string SanitizePhase(string value)
    {
        var phase = string.IsNullOrWhiteSpace(value)
            ? "opening"
            : value.Trim().ToLowerInvariant().Replace('_', '-');

        return phase switch
        {
            "opening" or "tactical" or "conversion" or "defense" or "endgame" or "game" or "recovery" => phase,
            _ => "opening"
        };
    }

    public static bool ContainsMoveLevelGuidance(string text) =>
        MoveLikePattern().IsMatch(text);

    public static bool HasRiskyClaimLanguage(string text) =>
        RiskyClaimPattern().IsMatch(text);

    public static bool HasAffirmativeMateClaim(string text) =>
        MateClaimPattern().IsMatch(text);

    private static IReadOnlyDictionary<string, string> ToolSemanticsFor(ChessQuestSession session)
    {
        var semantics = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["chess.list_legal_moves"] = "Returns legal affordances only. The order is not a ranking, and legal does not mean good or safe.",
            ["chess.project_line"] = "Projects an agent-authored line under public rules. It proves legality and resulting public board state only; it does not evaluate quality, safety, or best play.",
            ["chess.play_move"] = "Commits one selected move with public intent. Receipts prove mutation; public intent is a claim to audit, not proof.",
            ["chess.complete_objective"] = "Only the host verifier can prove the win objective."
        };

        if (session.Scenario.DisclosurePolicy.AllowAttackInspection)
        {
            semantics["chess.inspect_attacks"] = "Returns opponent legal captures and capturable agent pieces from the current placement. It does not score, choose, or prove a response is safe.";
        }

        return semantics;
    }

    private static IReadOnlyList<string> DefaultActiveObjectives(
        string phase,
        string phaseGoal) =>
        phase switch
        {
            "tactical" =>
            [
                phaseGoal,
                "treat tactical ideas as hypotheses until opponent replies are modeled",
                "do not equate legal projection with safety",
                "prefer verifier-backed claims over chess-sounding prose"
            ],
            "defense" or "recovery" =>
            [
                phaseGoal,
                "preserve king safety",
                "avoid further material loss",
                "seek counterplay only after checking public consequences"
            ],
            "conversion" or "endgame" =>
            [
                phaseGoal,
                "simplify only when the advantage is real or the position is stable",
                "avoid draw outcomes",
                "verify terminal claims through the host objective gate"
            ],
            _ =>
            [
                phaseGoal,
                "develop pieces",
                "contest central squares",
                "improve king safety",
                "avoid unsupported tactical claims"
            ]
        };

    private static IReadOnlyList<string> DefaultSuccessSignals(string phase) =>
        phase switch
        {
            "tactical" =>
            [
                "agent-authored line includes plausible opponent replies before safety claims",
                "material or terminal claims are backed by projection or receipts",
                "phase ends without material collapse"
            ],
            "defense" or "recovery" =>
            [
                "king is not in check after committed opponent reply",
                "material balance does not worsen",
                "immediate threats are reduced or acknowledged as unresolved"
            ],
            "conversion" or "endgame" =>
            [
                "material advantage or stable conversion condition is present",
                "simplification does not worsen the objective",
                "terminal claims are verifier-backed"
            ],
            _ =>
            [
                "legal agent move committed",
                "king safety improves or remains stable",
                "piece activity and center control improve without unsupported claims"
            ]
        };

    private static IReadOnlyList<string> DefaultReplanTriggers() =>
    [
        "terminal game state",
        "agent king in check",
        "major material swing",
        "phase budget exhausted",
        "phase goal no longer fits legal board state"
    ];

    private static IReadOnlyList<string> DefaultClaimDiscipline(string phase) =>
        phase switch
        {
            "tactical" =>
            [
                "treat unverified tactical ideas as hypotheses",
                "do not describe a move as safe based only on one-ply projection",
                "do not claim material gain unless the submitted line or receipt evidence supports it",
                "do not claim checkmate unless terminal state is verified"
            ],
            "conversion" or "endgame" =>
            [
                "do not select conversion/endgame framing merely because ply is high",
                "conversion claims require real material, positional, promotion, or terminal evidence",
                "do not claim simplification helps unless receipts or projected lines support it"
            ],
            _ =>
            [
                "state what is verified, what is hypothesized, and what remains unmodeled",
                "do not call legal moves good or safe without additional evidence",
                "do not claim checkmate unless terminal state is verified"
            ]
        };

    private static IReadOnlyList<string> EnsureClaimDiscipline(IReadOnlyList<string> rules)
    {
        var result = rules.ToList();
        if (!result.Any(rule => rule.Contains("legal", StringComparison.OrdinalIgnoreCase) &&
                                rule.Contains("safe", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add("legal does not mean safe");
        }

        if (!result.Any(rule => rule.Contains("projection", StringComparison.OrdinalIgnoreCase) &&
                                rule.Contains("safety", StringComparison.OrdinalIgnoreCase)))
        {
            result.Add("one-ply projection does not prove tactical safety");
        }

        return result.Take(8).ToArray();
    }

    [GeneratedRegex(@"\b(?:[a-h][1-8][a-h][1-8][qrbn]?|[KQRBN]?[a-h]?[1-8]?x?[a-h][1-8](?:=[QRBN])?[+#]?|O-O(?:-O)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MoveLikePattern();

    [GeneratedRegex(@"\b(?:safe|safety|winning|wins material|win material|decisive|forced|forces|forcing|best|blunder|tactic|tactical)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RiskyClaimPattern();

    [GeneratedRegex(@"\b(?<!not\s)(?<!no\s)(?<!without\s)(?:checkmate|checkmating|mate|mating)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MateClaimPattern();
}

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
        ChessQuestStrategyProjection? strategyProjection = null,
        string? phaseGoal = null,
        IReadOnlyList<string>? stopTriggers = null)
    {
        var normalizedPhase = NormalizePhase(phase);
        var frame = strategyProjection is null
            ? DefaultStrategyFrame(normalizedPhase)
            : ToStrategyFrame(strategyProjection);
        var objective = DefaultPhaseObjective(normalizedPhase, maxAgentTurns, phaseGoal, stopTriggers);
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
            ],
            ClaimDiscipline: ChessQuestGoalShapingPolicy.SanitizeList(
                null,
                [
                    "state what is verified, what is hypothesized, and what remains unmodeled",
                    "legal does not mean safe",
                    "one-ply projection does not prove tactical safety",
                    "do not claim checkmate unless terminal state is verified"
                ],
                maxItems: 8));
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

    private static ChessQuestPhaseObjective DefaultPhaseObjective(
        string phase,
        int maxAgentTurns,
        string? phaseGoal = null,
        IReadOnlyList<string>? stopTriggers = null) =>
        new(
            Kind: "ChessQuestPhaseObjective",
            Phase: phase,
            Goal: ChessQuestGoalShapingPolicy.SanitizeText(
                phaseGoal,
                phase switch
            {
                "tactical" or "tactical-conversion" =>
                    "Exploit a tactical opportunity only when it can be verified through public line projection.",
                "endgame" or "conversion" =>
                    "Convert the position toward a win while preserving enough material and avoiding draw outcomes.",
                _ =>
                    "Develop pieces, improve king safety, contest the center, and avoid immediate material loss."
            }),
            MaxAgentTurns: Math.Max(1, maxAgentTurns),
            StopTriggers: ChessQuestGoalShapingPolicy.SanitizeList(
                stopTriggers,
            [
                "terminal_game_state",
                "agent_turn_budget_exhausted",
                "planner_blocked",
                "tool_failure"
            ],
                maxItems: 8));

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

        var state = _session.CurrentState;
        if (state.IsTerminal)
        {
            if (state.TerminalState?.Winner == _session.Scenario.AgentColor)
            {
                return CompletionEvaluation.Complete();
            }

            return state.TerminalState?.Winner is null
                ? CompletionEvaluation.Failed(
                    StopReason.TerminalDraw,
                    "ChessQuest reached a terminal draw; draw is not success.")
                : CompletionEvaluation.Failed(
                    StopReason.TerminalLoss,
                    "ChessQuest reached a terminal loss for the agent.");
        }

        return _phaseTracker.IsPhaseComplete(_session)
            ? CompletionEvaluation.Complete()
            : CompletionEvaluation.Continue("ChessQuest phase boundary has not been reached.");
    }
}

public sealed class ChessQuestCompletionEvaluator : ICompletionEvaluator
{
    private readonly ChessQuestSession _session;

    public ChessQuestCompletionEvaluator(ChessQuestSession session)
    {
        _session = session;
    }

    public CompletionEvaluation Evaluate(AgenticaRun run)
    {
        if (run.Artifacts.Any(artifact =>
                string.Equals(artifact.Kind, "chessquest.objective_completed", StringComparison.Ordinal)))
        {
            return CompletionEvaluation.Complete();
        }

        var state = _session.CurrentState;
        if (!state.IsTerminal)
        {
            return CompletionEvaluation.Continue("ChessQuest objective has not been verified.");
        }

        if (state.TerminalState?.Winner == _session.Scenario.AgentColor)
        {
            return CompletionEvaluation.Continue("ChessQuest terminal win reached; complete_objective must emit the verifier artifact.");
        }

        return state.TerminalState?.Winner is null
            ? CompletionEvaluation.Failed(
                StopReason.TerminalDraw,
                "ChessQuest reached a terminal draw; draw is not success.")
            : CompletionEvaluation.Failed(
                StopReason.TerminalLoss,
                "ChessQuest reached a terminal loss for the agent.");
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
        var terminalFailure = _session.CurrentState.IsTerminal &&
            _session.CurrentState.TerminalState?.Winner != _session.Scenario.AgentColor;
        return new OutcomeReport(
            ReportId: Agentica.AgenticaIds.New("report"),
            Summary: terminalFailure
                ? $"The ChessQuest phase '{report.Phase}' ended in terminal non-win state '{_session.CurrentState.TerminalState?.Result ?? "draw"}'."
                : status == RunOutcomeStatus.Succeeded
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
