using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;
using Agentica.Tools;
using Chess;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed class HeuristicChessOpponent : IChessOpponent
{
    private readonly Random _random;
    private readonly string _difficulty;

    public HeuristicChessOpponent(int seed, string difficulty)
    {
        _random = new Random(seed);
        _difficulty = string.IsNullOrWhiteSpace(difficulty)
            ? "club"
            : difficulty.Trim().ToLowerInvariant();
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

        var settings = HeuristicOpponentSettings.For(_difficulty);
        if (_random.NextDouble() < settings.RandomMoveProbability)
        {
            return Task.FromResult<ChessOpponentMove?>(
                new ChessOpponentMove(PickRandom(request.LegalMoves), $"heuristic_{settings.Name}"));
        }

        var agentColor = Opposite(request.OpponentColor);
        var candidates = request.LegalMoves
            .Select(move => ScoreMove(request.Fen, move, request.OpponentColor, agentColor, settings))
            .Where(candidate => candidate.Legal)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Move, StringComparer.Ordinal)
            .ToArray();

        var selected = candidates.FirstOrDefault()?.Move ?? PickRandom(request.LegalMoves);
        return Task.FromResult<ChessOpponentMove?>(
            new ChessOpponentMove(selected, $"heuristic_{settings.Name}"));
    }

    private ScoredMove ScoreMove(
        string fen,
        string move,
        ChessQuestColor opponentColor,
        ChessQuestColor agentColor,
        HeuristicOpponentSettings settings)
    {
        var rules = new GeraChessRulesEngine(fen);
        var result = rules.TryPlayMove(move);
        if (!result.Accepted || result.Move is null)
        {
            return new ScoredMove(move, Legal: false, Score: int.MinValue);
        }

        var state = rules.GetState();
        var score = MaterialBalance(state.Fen, opponentColor);

        if (state.TerminalState is { } terminal)
        {
            if (terminal.Winner == opponentColor)
            {
                score += 1_000_000;
            }
            else if (terminal.Winner == agentColor)
            {
                score -= 1_000_000;
            }
            else
            {
                score -= 1_000;
            }
        }

        score += result.Captures.Sum(capture => PieceValue(capture.Piece)) / 4;

        if (move.Length == 5)
        {
            score += PromotionValue(move[4]);
        }

        if (!state.IsTerminal && IsKingChecked(state.Fen, agentColor))
        {
            score += 45;
        }

        if (settings.AvoidImmediateMate && AllowsImmediateMate(state.Fen, agentColor, opponentColor))
        {
            score -= 500_000;
        }

        if (settings.ReplyRiskWeight > 0)
        {
            score -= (int)Math.Round(BestImmediateReplyGain(state.Fen, agentColor, opponentColor) * settings.ReplyRiskWeight);
        }

        if (settings.ScoreNoise > 0)
        {
            score += _random.Next(-settings.ScoreNoise, settings.ScoreNoise + 1);
        }

        return new ScoredMove(result.Move, Legal: true, score);
    }

    private string PickRandom(IReadOnlyList<string> moves) =>
        moves[_random.Next(moves.Count)];

    private static bool AllowsImmediateMate(
        string fen,
        ChessQuestColor agentColor,
        ChessQuestColor opponentColor)
    {
        var rules = new GeraChessRulesEngine(fen);
        foreach (var reply in rules.ListLegalMoves())
        {
            var clone = new GeraChessRulesEngine(fen);
            var result = clone.TryPlayMove(reply.Uci);
            if (result.Accepted &&
                clone.GetState().TerminalState?.Winner == agentColor)
            {
                return true;
            }
        }

        return false;
    }

    private static int BestImmediateReplyGain(
        string fen,
        ChessQuestColor agentColor,
        ChessQuestColor opponentColor)
    {
        var rules = new GeraChessRulesEngine(fen);
        var best = 0;
        foreach (var reply in rules.ListLegalMoves())
        {
            var clone = new GeraChessRulesEngine(fen);
            var before = MaterialBalance(fen, opponentColor);
            var result = clone.TryPlayMove(reply.Uci);
            if (!result.Accepted)
            {
                continue;
            }

            if (clone.GetState().TerminalState?.Winner == agentColor)
            {
                return 50_000;
            }

            var after = MaterialBalance(clone.GetFen(), opponentColor);
            best = Math.Max(best, before - after);
        }

        return best;
    }

    private static int MaterialBalance(string fen, ChessQuestColor color)
    {
        var white = 0;
        var black = 0;
        foreach (var piece in fen.Split(' ', 2)[0])
        {
            var value = piece switch
            {
                'P' or 'p' => 100,
                'N' or 'n' => 320,
                'B' or 'b' => 330,
                'R' or 'r' => 500,
                'Q' or 'q' => 900,
                _ => 0
            };

            if (value == 0)
            {
                continue;
            }

            if (char.IsUpper(piece))
            {
                white += value;
            }
            else
            {
                black += value;
            }
        }

        return color == ChessQuestColor.White
            ? white - black
            : black - white;
    }

    private static bool IsKingChecked(string fen, ChessQuestColor color)
    {
        try
        {
            var board = ChessBoard.LoadFromFen(fen, AutoEndgameRules.All);
            return color == ChessQuestColor.White
                ? board.WhiteKingChecked
                : board.BlackKingChecked;
        }
        catch
        {
            return false;
        }
    }

    private static int PieceValue(string projectedPiece)
    {
        var piece = projectedPiece.Split('_').LastOrDefault() ?? string.Empty;
        return piece switch
        {
            "pawn" => 100,
            "knight" => 320,
            "bishop" => 330,
            "rook" => 500,
            "queen" => 900,
            _ => 0
        };
    }

    private static int PromotionValue(char promotion) =>
        char.ToLowerInvariant(promotion) switch
        {
            'q' => 850,
            'r' => 450,
            'b' => 280,
            'n' => 270,
            _ => 0
        };

    private static ChessQuestColor Opposite(ChessQuestColor color) =>
        color == ChessQuestColor.White ? ChessQuestColor.Black : ChessQuestColor.White;

    private sealed record ScoredMove(string Move, bool Legal, int Score);

    private sealed record HeuristicOpponentSettings(
        string Name,
        double RandomMoveProbability,
        int ScoreNoise,
        bool AvoidImmediateMate,
        double ReplyRiskWeight)
    {
        public static HeuristicOpponentSettings For(string difficulty) =>
            difficulty.Trim().ToLowerInvariant() switch
            {
                "random" => new("random", 1.0, 0, AvoidImmediateMate: false, ReplyRiskWeight: 0),
                "beginner" => new("beginner", 0.30, 180, AvoidImmediateMate: true, ReplyRiskWeight: 0.20),
                "club" => new("club", 0.08, 55, AvoidImmediateMate: true, ReplyRiskWeight: 0.60),
                "strong" => new("strong", 0.02, 15, AvoidImmediateMate: true, ReplyRiskWeight: 0.90),
                "max" => new("max", 0.0, 0, AvoidImmediateMate: true, ReplyRiskWeight: 1.0),
                _ => new("club", 0.08, 55, AvoidImmediateMate: true, ReplyRiskWeight: 0.60)
            };
    }
}

public sealed record PlannerChessOpponentOptions(
    string PlannerLabel,
    int TimeoutSeconds,
    int MaxSteps,
    int MaxRefinements,
    int MaxPlanContinuations,
    bool Quiet);

public sealed class PlannerChessOpponent : IChessOpponent
{
    private readonly Func<ChessQuestSession, IWorkflowPlanner> _plannerFactory;
    private readonly Func<ChessQuestSession, IEventSink> _eventSinkFactory;
    private readonly PlannerChessOpponentOptions _options;
    private readonly IChessOpponent _fallback;

    public PlannerChessOpponent(
        Func<ChessQuestSession, IWorkflowPlanner> plannerFactory,
        Func<ChessQuestSession, IEventSink> eventSinkFactory,
        PlannerChessOpponentOptions options,
        IChessOpponent fallback)
    {
        _plannerFactory = plannerFactory;
        _eventSinkFactory = eventSinkFactory;
        _options = options;
        _fallback = fallback;
    }

    public async Task<ChessOpponentMove?> ChooseMoveAsync(
        ChessOpponentRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.LegalMoves.Count == 0)
        {
            return null;
        }

        var session = CreateOneTurnSession(request);
        var planner = _plannerFactory(session);
        var runner = new AgenticaRunner(
            planner,
            ChessQuestTools.CreateCatalog(session),
            _eventSinkFactory(session),
            new ChessQuestOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: _options.MaxSteps,
                MaxRefinements: _options.MaxRefinements,
                Timeout: TimeSpan.FromSeconds(_options.TimeoutSeconds),
                PlanningMode: PlanningMode.QueryAndBlockerDriven,
                MaxPlanContinuations: _options.MaxPlanContinuations,
                PlanningContext: new PlanningContextOptions(MaxRecentObservations: 6, MaxRecentReceipts: 6),
                MaxBlockedRetries: 0),
            completionEvaluator: ChessQuestFirstMoveCompletionEvaluator.Instance,
            planningFrameProjector: new ChessQuestPlanningFrameProjector(session));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            await runner.RunAsync(
                    new RunRequest(
                        BuildObjective(request),
                        RequestOrigin.System,
                        ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session)),
                    timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FallbackAsync(request, "opponent-agent timed out", cancellationToken).ConfigureAwait(false);
        }
        catch (WorkflowPlannerException)
        {
            return await FallbackAsync(request, "opponent-agent planner failed", cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return await FallbackAsync(request, "opponent-agent run failed", cancellationToken).ConfigureAwait(false);
        }

        var selected = session.Turns
            .Where(turn =>
                string.Equals(turn.Invocation.ToolId, ChessQuestToolIds.PlayMove, StringComparison.Ordinal) &&
                turn.Result.Receipt.Status == ReceiptStatus.Succeeded)
            .Select(turn => ReadString(turn.Result.Receipt.Data, "agentMove"))
            .LastOrDefault(move => move is not null && request.LegalMoves.Contains(move, StringComparer.Ordinal));

        if (selected is null)
        {
            return await FallbackAsync(request, "opponent-agent did not commit a legal move", cancellationToken).ConfigureAwait(false);
        }

        return new ChessOpponentMove(selected, $"agent_{_options.PlannerLabel}");
    }

    private async Task<ChessOpponentMove?> FallbackAsync(
        ChessOpponentRequest request,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!_options.Quiet)
        {
            Console.WriteLine($"[{request.OpponentColor} Opponent Agent] fallback: {reason}; using managed heuristic reply.");
        }

        return await _fallback.ChooseMoveAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static ChessQuestSession CreateOneTurnSession(ChessOpponentRequest request)
    {
        var scenario = new ChessQuestScenario(
            ScenarioId: $"opponent_agent_ply_{request.Ply}",
            Title: "ChessQuest Opponent Agent Turn",
            InitialFen: request.Fen,
            AgentColor: request.OpponentColor,
            ObjectiveKind: ChessQuestObjectiveKind.WinGame,
            PublicObjective: $"Choose one legal move as {request.OpponentColor}. The goal is to win the game as {request.OpponentColor}.",
            Difficulty: new ChessQuestDifficulty(
                Scenario: "opponent-agent-turn",
                Surface: "strict_projected",
                Opponent: "none-after-selected-move"),
            DisclosurePolicy: ChessQuestDisclosurePolicy.StrictRefereeProjected);

        return new ChessQuestSession(
            scenario,
            opponent: new ScriptedChessOpponent([], fallbackToFirstLegalMove: false));
    }

    private static string BuildObjective(ChessOpponentRequest request) =>
        $"""
        ChessQuest opponent-agent turn.
        You are playing {request.OpponentColor}.
        Choose exactly one legal UCI move for {request.OpponentColor} from the current public board state.
        Your goal is to win the game as {request.OpponentColor}; draw is not success.
        Use the strict chess tools to inspect public state, list legal moves, optionally project self-authored lines, then commit one selected move with chess.play_move.
        Strict gameplay requires passing the current chess.list_legal_moves legalMoveObservationId into chess.play_move. If the board changes or a move is refused as stale, refresh chess.list_legal_moves.
        Before describing a move as check or checkmate, call chess.project_line for that exact move or line with claims ["check"] or ["checkmate"] and use the returned claimVerification.
        Do not describe a selected move as checkmate, a forced win, or objective completion unless a prior chess.project_line result has already verified that terminal state.
        Do not call chess.complete_objective unless your selected move has already ended the game.
        """;

    private static string? ReadString(IReadOnlyDictionary<string, object?> source, string key) =>
        source.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value)
            : null;
}

public sealed class ChessQuestOpponentAgentEventSink : IEventSink
{
    private readonly ChessQuestSession _session;
    private readonly string _label;
    private readonly Dictionary<string, ExecutionIntent> _stepIntents = new(StringComparer.Ordinal);
    private int? _lastLegalMoveCount;
    private int _turnNumber;

    public ChessQuestOpponentAgentEventSink(ChessQuestSession session, string label)
    {
        _session = session;
        _label = label;
    }

    public void Emit(ExecutionEvent executionEvent)
    {
        if (executionEvent.Type == "run.created")
        {
            Console.WriteLine();
            Console.WriteLine($"[{_label}] thinking turn started");
            return;
        }

        if (executionEvent.Type == "step.started" &&
            executionEvent.Context?.StepId is { } stepId &&
            executionEvent.Intent is not null)
        {
            _stepIntents[stepId] = executionEvent.Intent;
            return;
        }

        if (executionEvent.Type == "receipt.emitted")
        {
            PrintReceipt(executionEvent);
        }
    }

    private void PrintReceipt(ExecutionEvent executionEvent)
    {
        var receiptId = executionEvent.Context?.ReceiptId;
        var turn = string.IsNullOrWhiteSpace(receiptId)
            ? _session.Turns.LastOrDefault()
            : _session.Turns.LastOrDefault(item =>
                string.Equals(item.Result.Receipt.ReceiptId, receiptId, StringComparison.Ordinal));
        if (turn is null)
        {
            return;
        }

        if (turn.Invocation.ToolId == ChessQuestToolIds.ListLegalMoves)
        {
            _lastLegalMoveCount = ReadStringList(turn.Result.Receipt.Data, "legalMoves").Count;
            Console.WriteLine($"[{_label}] context: {_lastLegalMoveCount} legal UCI moves available.");
            return;
        }

        if (turn.Invocation.ToolId == ChessQuestToolIds.ProjectLine)
        {
            Console.WriteLine($"[{_label}] projected a self-authored line.");
            return;
        }

        if (turn.Invocation.ToolId != ChessQuestToolIds.PlayMove)
        {
            return;
        }

        _stepIntents.TryGetValue(turn.Invocation.StepId, out var intent);
        var data = turn.Result.Receipt.Data;
        var selectedMove = ReadString(data, "agentMove") ?? ReadString(turn.Invocation.Input, "move") ?? "unknown";
        var turnIntent = ReadDictionary(turn.Invocation.Input, "turnIntent") ?? ReadDictionary(data, "turnIntent");
        var publicReason = ReadString(turnIntent, "publicReason");
        var fenAfter = ReadString(data, "fenAfter") ?? turn.StateAfter.Fen;

        Console.WriteLine();
        Console.WriteLine($"=== ChessQuest Opponent Turn {++_turnNumber:000} | role={_label} ===");
        if (_lastLegalMoveCount is not null)
        {
            Console.WriteLine($"[{_label}] Legal moves considered: {_lastLegalMoveCount}");
        }

        if (!string.IsNullOrWhiteSpace(intent?.Action))
        {
            Console.WriteLine($"[{_label}] Intent: {Compact(intent.Action)}");
        }

        if (!string.IsNullOrWhiteSpace(intent?.Rationale))
        {
            Console.WriteLine($"[{_label}] Rationale: {Compact(intent.Rationale)}");
        }

        Console.WriteLine($"[{_label}] Selected move: {selectedMove}");
        if (!string.IsNullOrWhiteSpace(publicReason))
        {
            Console.WriteLine($"[{_label}] Public reason: {Compact(publicReason)}");
        }

        Console.WriteLine($"[{_label}] State after opponent move: ply={turn.StateAfter.Ply} sideToMove={turn.StateAfter.SideToMove} terminal={turn.StateAfter.IsTerminal}");
        Console.WriteLine($"[{_label}] FEN: {fenAfter}");
        _lastLegalMoveCount = null;
    }

    private static IReadOnlyDictionary<string, object?>? ReadDictionary(
        IReadOnlyDictionary<string, object?> source,
        string key) =>
        source.TryGetValue(key, out var value)
            ? value as IReadOnlyDictionary<string, object?> ??
              (value as IDictionary<string, object?>)?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : null;

    private static string? ReadString(
        IReadOnlyDictionary<string, object?>? source,
        string key)
    {
        if (source is null || !source.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return Convert.ToString(value);
    }

    private static IReadOnlyList<string> ReadStringList(
        IReadOnlyDictionary<string, object?> source,
        string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            IEnumerable<string> strings => strings.ToArray(),
            IEnumerable<object> objects => objects
                .Select(Convert.ToString)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray(),
            _ => []
        };
    }

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 220 ? compact : compact[..217] + "...";
    }
}

internal sealed class ChessQuestFirstMoveCompletionEvaluator : ICompletionEvaluator
{
    public static ChessQuestFirstMoveCompletionEvaluator Instance { get; } = new();

    private ChessQuestFirstMoveCompletionEvaluator()
    {
    }

    public CompletionEvaluation Evaluate(AgenticaRun run) =>
        run.Receipts.Any(receipt =>
            string.Equals(receipt.ToolId, ChessQuestToolIds.PlayMove, StringComparison.Ordinal) &&
            receipt.Status == ReceiptStatus.Succeeded)
            ? CompletionEvaluation.Complete()
            : CompletionEvaluation.Continue("Opponent agent has not committed a chess move yet.");
}
