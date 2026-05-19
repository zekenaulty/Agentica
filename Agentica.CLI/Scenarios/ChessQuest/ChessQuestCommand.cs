using System.Text.Json;
using Agentica.CLI.Scenarios.ChessQuest;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;

internal static class ChessQuestCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var board = new ChessQuestBoard();

        if (args.Count == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            PrintBoard(board);
            return 0;
        }

        if (string.Equals(args[0], "preview", StringComparison.OrdinalIgnoreCase))
        {
            var scenarioId = args.Count > 1 ? args[1] : ChessQuestBoard.DefaultScenarioId;
            try
            {
                PrintPreview(board.Load(scenarioId));
                return 0;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
        }

        if (string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase))
        {
            return ReplayGame(args.Skip(1).ToArray());
        }

        if (string.Equals(args[0], "resume", StringComparison.OrdinalIgnoreCase))
        {
            return await ResumeGameAsync(board, args.Skip(1).ToArray(), services).ConfigureAwait(false);
        }

        if (!string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            services.PrintUsage();
            return 2;
        }

        return await RunScenarioAsync(board, args.Skip(1).ToArray(), services).ConfigureAwait(false);
    }

    private static async Task<int> RunScenarioAsync(
        IChessQuestBoard board,
        IReadOnlyList<string> args,
        CliCommandServices services,
        ChessQuestGameRecord? resumeRecord = null,
        string? resumeSource = null)
    {
        var options = ChessQuestRunOptions.Parse(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            services.PrintUsage();
            return 2;
        }

        if (options.Planner == PlannerKind.Gemini && !services.GeminiCredentialsAvailable())
        {
            Console.Error.WriteLine("Gemini planner requested, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        ChessQuestScenario scenario;
        try
        {
            scenario = board.Load(options.ScenarioId);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        if (resumeRecord is not null)
        {
            scenario = scenario with
            {
                InitialFen = resumeRecord.InitialFen,
                AgentColor = resumeRecord.AgentColor,
                Difficulty = resumeRecord.Difficulty
            };
        }

        var opponentMode = ResolveOpponentMode(scenario, options);
        var opponentPlanner = options.OpponentPlanner ?? options.Planner;
        if (opponentMode == ChessQuestOpponentMode.Agent &&
            opponentPlanner == PlannerKind.Gemini &&
            !services.GeminiCredentialsAvailable())
        {
            Console.Error.WriteLine("Agent opponent requested with Gemini planner, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        scenario = ApplyOpponentDifficulty(scenario, opponentMode, options);
        var opponent = CreateOpponent(scenario, options, opponentMode, opponentPlanner, services);
        var session = new ChessQuestSession(
            scenario,
            opponent: opponent);

        if (resumeRecord is not null)
        {
            try
            {
                ChessQuestGameRecordStore.ReplayIntoSession(session, resumeRecord);
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
        }

        var runObjective = BuildObjective(scenario);
        var initialPlannerContext = ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "chessquest", args);
        if (runLog is not null)
        {
            ChessQuestGameRecordStore.WriteDirectory(runLog.DirectoryPath, session);
        }

        runLog?.WriteJson("chessquest-scenario.json", scenario);
        if (resumeRecord is not null)
        {
            runLog?.WriteJson("chessquest-resume-source.json", new
            {
                source = resumeSource,
                record = resumeRecord
            });
        }

        runLog?.WriteJson("chessquest-initial-planning-context.json", initialPlannerContext);
        runLog?.WriteJson("chessquest-initial-planning-frame.json", ChessQuestCapabilitySurfaceCompiler.BuildPlanningFrame(session));
        Action<ChessQuestCockpitTurnEnvelope>? turnRecorder = runLog is null
            ? null
            : turn =>
            {
                runLog.WriteJsonLine("chessquest-turns.jsonl", turn);
                ChessQuestGameRecordStore.WriteDirectory(runLog.DirectoryPath, session);
            };

        PrintOpening(scenario, session, options, opponentMode, opponentPlanner, resumeRecord, resumeSource);

        using var runCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            runCancellation.Cancel();
        };

        var planner = options.Planner == PlannerKind.Deterministic
            ? new ChessQuestDeterministicPlanner(session)
            : services.CreatePlanner(new CliRunOptions(
                runObjective,
                options.Planner,
                options.ModelId,
                options.ThinkingBudget,
                options.IncludeThoughts,
                options.MaxOutputTokens,
                options.PlanningMode,
                options.MaxBlockedRetries,
                LogRun: false,
                LogDir: null,
                IsValid: true,
                Error: null));

        var runner = new AgenticaRunner(
            planner: planner,
            toolCatalog: ChessQuestTools.CreateCatalog(session),
            eventSink: services.CreateEventSink(
                CreateEventSink(session, options, turnRecorder),
                runLog),
            outcomeReporter: new ChessQuestOutcomeReporter(),
            policy: new ExecutionPolicy(
                MaxSteps: options.MaxSteps,
                MaxRefinements: options.MaxRefinements,
                Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
                PlanningMode: options.PlanningMode,
                MaxPlanContinuations: options.MaxPlanContinuations,
                PlanningContext: new PlanningContextOptions(MaxRecentObservations: 12, MaxRecentReceipts: 12),
                MaxBlockedRetries: options.MaxBlockedRetries),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("chessquest.objective_completed"),
            planningFrameProjector: new ChessQuestPlanningFrameProjector(session));

        OutcomeEnvelope envelope;
        Console.CancelKeyPress += cancelHandler;
        try
        {
            envelope = await runner.RunAsync(
                new RunRequest(runObjective, RequestOrigin.User, initialPlannerContext),
                runCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        if (options.VerboseEnvelope)
        {
            services.PrintEnvelope(envelope);
        }
        else
        {
            PrintSummary(envelope);
        }

        if (runLog is not null)
        {
            ChessQuestGameRecordStore.WriteDirectory(runLog.DirectoryPath, session);
        }

        services.FinishRunLog(runLog, envelope);
        return envelope.Outcome.Status == RunOutcomeStatus.Succeeded ? 0 : 1;
    }

    private static int ReplayGame(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            Console.Error.WriteLine("ChessQuest replay requires a game record file or run log directory.");
            return 2;
        }

        try
        {
            var record = ChessQuestGameRecordStore.Load(args[0]);
            PrintReplay(record, args[0]);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<int> ResumeGameAsync(
        IChessQuestBoard board,
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        if (args.Count == 0)
        {
            Console.Error.WriteLine("ChessQuest resume requires a game record file or run log directory.");
            return 2;
        }

        ChessQuestGameRecord record;
        try
        {
            record = ChessQuestGameRecordStore.Load(args[0]);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var runArgs = new[] { record.ScenarioId }
            .Concat(args.Skip(1))
            .ToArray();
        return await RunScenarioAsync(board, runArgs, services, record, args[0]).ConfigureAwait(false);
    }

    private static string BuildObjective(ChessQuestScenario scenario) =>
        $"""
        ChessQuest: {scenario.Title}
        Objective: {scenario.PublicObjective}

        Planner contract:
        - You are playing {scenario.AgentColor}. The opponent is the other color.
        - Win is required. Draw and loss do not satisfy the objective.
        - Use UCI notation only, for example e2e4, g1f3, or a7a8q.
        - Use chess.get_state, chess.render_board, and chess.list_legal_moves to inspect current public state.
        - You may use chess.project_line only for hypothetical UCI lines you authored yourself. It is read-only, can verify submitted check/checkmate claims, and does not generate opponent replies.
        - Commit exactly one selected agent move with chess.play_move. Include a concise public turnIntent matching the selected move.
        - Before describing a move as check or checkmate, call chess.project_line for that exact move or line with claims ["check"] or ["checkmate"] and use the returned claimVerification.
        - Do not describe a selected move as checkmate, a forced win, or objective completion unless a prior chess.project_line result or committed receipt has already verified that terminal state.
        - chess.play_move applies the host-controlled opponent reply after your accepted move unless the game is terminal.
        - Do not claim completion unless chess.complete_objective emits chessquest.objective_completed.
        - The strict surface does not provide move rankings, scores, tactical labels, or opponent policy details.
        """;

    private static IChessOpponent CreateOpponent(
        ChessQuestScenario scenario,
        ChessQuestRunOptions options,
        ChessQuestOpponentMode opponentMode,
        PlannerKind opponentPlanner,
        CliCommandServices services)
    {
        if (string.Equals(scenario.ScenarioId, ChessQuestBoard.DefaultScenarioId, StringComparison.OrdinalIgnoreCase))
        {
            return new ScriptedChessOpponent([], fallbackToFirstLegalMove: false);
        }

        return opponentMode switch
        {
            ChessQuestOpponentMode.Heuristic => new HeuristicChessOpponent(
                options.OpponentSeed,
                options.OpponentDifficulty),
            ChessQuestOpponentMode.Agent => new PlannerChessOpponent(
                plannerFactory: opponentSession => CreateOpponentPlanner(opponentSession, options, opponentPlanner, services),
                eventSinkFactory: opponentSession => options.Quiet
                    ? SilentEventSink.Instance
                    : new ChessQuestOpponentAgentEventSink(
                        opponentSession,
                        $"{opponentSession.Scenario.AgentColor} Opponent Agent"),
                options: new PlannerChessOpponentOptions(
                    PlannerLabel: opponentPlanner.ToString().ToLowerInvariant(),
                    TimeoutSeconds: options.OpponentTimeoutSeconds,
                    MaxSteps: options.OpponentMaxSteps,
                    MaxRefinements: options.OpponentMaxRefinements,
                    MaxPlanContinuations: options.OpponentMaxPlanContinuations,
                    Quiet: options.Quiet),
                fallback: new HeuristicChessOpponent(options.OpponentSeed, options.OpponentDifficulty)),
            _ => new RandomLegalMoveOpponent(options.OpponentSeed)
        };
    }

    private static IWorkflowPlanner CreateOpponentPlanner(
        ChessQuestSession opponentSession,
        ChessQuestRunOptions options,
        PlannerKind opponentPlanner,
        CliCommandServices services)
    {
        if (opponentPlanner == PlannerKind.Deterministic)
        {
            return new ChessQuestDeterministicPlanner(opponentSession);
        }

        return services.CreatePlanner(new CliRunOptions(
            "ChessQuest opponent-agent one-move selection.",
            opponentPlanner,
            options.OpponentModelId ?? options.ModelId,
            options.OpponentThinkingBudget ?? options.ThinkingBudget,
            options.OpponentIncludeThoughts || options.IncludeThoughts,
            options.OpponentMaxOutputTokens ?? options.MaxOutputTokens,
            PlanningMode.QueryAndBlockerDriven,
            options.MaxBlockedRetries,
            LogRun: false,
            LogDir: null,
            IsValid: true,
            Error: null));
    }

    private static ChessQuestOpponentMode ResolveOpponentMode(
        ChessQuestScenario scenario,
        ChessQuestRunOptions options)
    {
        if (options.OpponentMode is { } mode)
        {
            return mode;
        }

        if (scenario.Difficulty.Opponent.Contains("agent", StringComparison.OrdinalIgnoreCase))
        {
            return ChessQuestOpponentMode.Agent;
        }

        if (scenario.Difficulty.Opponent.Contains("heuristic", StringComparison.OrdinalIgnoreCase))
        {
            return ChessQuestOpponentMode.Heuristic;
        }

        return ChessQuestOpponentMode.Random;
    }

    private static ChessQuestScenario ApplyOpponentDifficulty(
        ChessQuestScenario scenario,
        ChessQuestOpponentMode opponentMode,
        ChessQuestRunOptions options)
    {
        var opponent = opponentMode switch
        {
            ChessQuestOpponentMode.Heuristic => $"heuristic-{options.OpponentDifficulty}",
            ChessQuestOpponentMode.Agent => $"agent-{(options.OpponentPlanner ?? options.Planner).ToString().ToLowerInvariant()}",
            _ => "random-legal"
        };

        if (string.Equals(scenario.ScenarioId, ChessQuestBoard.DefaultScenarioId, StringComparison.OrdinalIgnoreCase))
        {
            opponent = "none-after-terminal";
        }

        return scenario with
        {
            Difficulty = scenario.Difficulty with
            {
                Opponent = opponent
            }
        };
    }

    private static IEventSink CreateEventSink(
        ChessQuestSession session,
        ChessQuestRunOptions options,
        Action<ChessQuestCockpitTurnEnvelope>? turnRecorder)
    {
        if (options.Quiet)
        {
            return SilentEventSink.Instance;
        }

        if (options.VerboseEvents)
        {
            return new ConsoleEventSink();
        }

        return new ChessQuestCockpitEventSink(
            session,
            turnJson: options.TurnJson,
            turnRecorder: turnRecorder);
    }

    private static void PrintBoard(IChessQuestBoard board)
    {
        Console.WriteLine("Available ChessQuest Scenarios:");
        var scenarios = board.ListScenarios();
        for (var index = 0; index < scenarios.Count; index++)
        {
            var scenario = scenarios[index];
            Console.WriteLine($"{index + 1}. {scenario.Title} ({scenario.ScenarioId})");
            Console.WriteLine($"   - {scenario.Description}");
            Console.WriteLine($"   - Difficulty: {scenario.Difficulty}; Surface: {scenario.Surface}; Opponent: {scenario.Opponent}");
            Console.WriteLine($"   - Estimated Steps: {scenario.EstimatedSteps}");
        }
    }

    private static void PrintPreview(ChessQuestScenario scenario)
    {
        Console.WriteLine($"ChessQuest: \"{scenario.Title}\"");
        Console.WriteLine($"Objective: {scenario.PublicObjective}");
        Console.WriteLine($"Scenario: {scenario.ScenarioId}");
        Console.WriteLine($"Agent Color: {scenario.AgentColor}");
        Console.WriteLine($"Difficulty: {scenario.Difficulty.Scenario}; Surface: {scenario.Difficulty.Surface}; Opponent: {scenario.Difficulty.Opponent}");
        Console.WriteLine();
        Console.WriteLine("Initial board:");
        Console.WriteLine(ChessQuestRenderer.RenderBoardFromFen(scenario.InitialFen));
        Console.WriteLine();
        Console.WriteLine("Tool surface:");
        Console.WriteLine("  chess.get_state, chess.render_board, chess.list_legal_moves, chess.project_line, chess.play_move, chess.complete_objective");
        Console.WriteLine();
        Console.WriteLine("Completion requires terminal board verification through chess.complete_objective.");
    }

    private static void PrintOpening(
        ChessQuestScenario scenario,
        ChessQuestSession session,
        ChessQuestRunOptions options,
        ChessQuestOpponentMode opponentMode,
        PlannerKind opponentPlanner,
        ChessQuestGameRecord? resumeRecord = null,
        string? resumeSource = null)
    {
        Console.WriteLine($"Agent has accepted ChessQuest: \"{scenario.Title}\"");
        Console.WriteLine($"Objective: {scenario.PublicObjective}");
        Console.WriteLine($"Scenario: {scenario.ScenarioId}");
        Console.WriteLine($"Agent Color: {scenario.AgentColor}");
        Console.WriteLine($"Surface: {scenario.DisclosurePolicy.Mode}");
        Console.WriteLine($"Opponent: {scenario.Difficulty.Opponent}");
        if (resumeRecord is not null)
        {
            Console.WriteLine($"Resume Source: {resumeSource}");
            Console.WriteLine($"Resumed Plies: {resumeRecord.Plies.Count}; FEN: {session.CurrentState.Fen}");
        }

        if (opponentMode == ChessQuestOpponentMode.Agent)
        {
            Console.WriteLine($"Opponent Agent: planner={opponentPlanner} maxSteps={options.OpponentMaxSteps} maxRefinements={options.OpponentMaxRefinements} timeoutSeconds={options.OpponentTimeoutSeconds}");
        }

        Console.WriteLine();
        Console.WriteLine(session.CurrentState.Fen);
        Console.WriteLine(ChessQuestRenderer.RenderBoardFromFen(session.CurrentState.Fen));
        Console.WriteLine();
    }

    private static void PrintReplay(ChessQuestGameRecord record, string source)
    {
        Console.WriteLine($"ChessQuest Replay: \"{record.Title}\"");
        Console.WriteLine($"Source: {source}");
        Console.WriteLine($"Scenario: {record.ScenarioId}");
        Console.WriteLine($"Agent Color: {record.AgentColor}");
        Console.WriteLine($"Plies: {record.Plies.Count}");
        Console.WriteLine($"Terminal: {record.Terminal}");
        if (record.TerminalState is not null)
        {
            Console.WriteLine($"Result: {record.TerminalState.Result} ({record.TerminalState.Reason})");
        }

        Console.WriteLine();
        Console.WriteLine("Initial FEN:");
        Console.WriteLine(record.InitialFen);
        Console.WriteLine(ChessQuestRenderer.RenderBoardFromFen(record.InitialFen));
        Console.WriteLine();
        Console.WriteLine("Moves:");
        if (record.Plies.Count == 0)
        {
            Console.WriteLine("  none");
        }
        else
        {
            foreach (var ply in record.Plies.OrderBy(item => item.Ply))
            {
                Console.WriteLine($"  {ply.Ply:000}. {ply.Color} {ply.Source}: {ply.Move}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Final FEN:");
        Console.WriteLine(record.CurrentFen);
        Console.WriteLine(ChessQuestRenderer.RenderBoardFromFen(record.CurrentFen));
    }

    private static void PrintSummary(OutcomeEnvelope envelope)
    {
        Console.WriteLine();
        Console.WriteLine("--- ChessQuest Summary ---");
        Console.WriteLine($"Status: {envelope.Outcome.Status}");
        Console.WriteLine($"Stop Reason: {envelope.Outcome.StopReason}");
        Console.WriteLine($"Completed Steps: {envelope.Outcome.CompletedSteps.Count}");
        Console.WriteLine($"Report: {envelope.Report.Summary}");
        if (envelope.Outcome.Blockers.Count > 0)
        {
            Console.WriteLine($"Blockers: {string.Join(", ", envelope.Outcome.Blockers)}");
        }

        var completionArtifact = envelope.Details.Artifacts.FirstOrDefault(item =>
            string.Equals(item.Kind, "chessquest.objective_completed", StringComparison.Ordinal));
        Console.WriteLine($"Completion Artifact: {(completionArtifact is null ? "none" : completionArtifact.ArtifactId)}");
    }

    private sealed class SilentEventSink : IEventSink
    {
        public static SilentEventSink Instance { get; } = new();

        public void Emit(ExecutionEvent executionEvent)
        {
        }
    }
}
