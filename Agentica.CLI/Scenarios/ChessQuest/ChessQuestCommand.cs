using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
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

        if (string.Equals(args[0], "board-probe", StringComparison.OrdinalIgnoreCase))
        {
            return await RunBoardProbeAsync(args.Skip(1).ToArray(), services).ConfigureAwait(false);
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

    private static async Task<int> RunBoardProbeAsync(
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var options = ChessQuestBoardProbeOptions.Parse(args, GeminiModelId.Flash25);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            services.PrintUsage();
            return 2;
        }

        if (!services.GeminiCredentialsAvailable())
        {
            Console.Error.WriteLine("ChessQuest board-probe requires Gemini credentials. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var client = new RetryingLlmClient(
            new GeminiLlmClient(GeminiClientOptions.FromEnvironment(options.ModelId)),
            new LlmRetryOptions(CallTimeout: TimeSpan.FromSeconds(options.TimeoutSeconds)));
        var runner = new ChessQuestBoardProbeRunner(client);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "chessquest-board-probe", args);
        runLog?.WriteJson("chessquest-board-probe-options.json", options);

        if (!options.Json)
        {
            Console.WriteLine("--- ChessQuest Board Probe ---");
            Console.WriteLine($"Model: {options.ModelId}");
            Console.WriteLine($"Trials: {options.Trials}");
            Console.WriteLine($"Seed: {options.Seed}");
            Console.WriteLine($"Scramble Plies: {options.ScramblePlies}");
            Console.WriteLine($"Presentation: {options.Presentation}");
            Console.WriteLine($"Target: {options.TargetMode}");
            Console.WriteLine();
        }

        try
        {
            var summary = await runner.RunAsync(
                    options,
                    (trial, result) =>
                    {
                        runLog?.WriteJsonLine(
                            "chessquest-board-probe-trials.jsonl",
                            new
                            {
                                trial.TrialNumber,
                                trial.Seed,
                                trial.Fen,
                                trial.BoardLines,
                                trial.Square,
                                trial.Expected,
                                Prompt = ChessQuestBoardProbeRunner.BuildPrompt(trial, options.Presentation),
                                Result = result,
                                RawResponse = result.RawResponse
                            });

                        if (!options.Json)
                        {
                            PrintBoardProbeTrial(trial, result);
                        }
                    },
                    timeout.Token)
                .ConfigureAwait(false);

            runLog?.WriteJson("chessquest-board-probe-summary.json", summary);

            if (options.Json)
            {
                Console.WriteLine(ChessQuestBoardProbeRunner.SerializeSummary(summary));
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("--- Board Probe Summary ---");
                Console.WriteLine($"Passed: {summary.Passed}/{summary.Trials}");
                Console.WriteLine($"Failed: {summary.Failed}/{summary.Trials}");
            }

            if (runLog is not null)
            {
                Console.WriteLine();
                Console.WriteLine($"Run log written: {runLog.DirectoryPath}");
            }

            return summary.Failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"ChessQuest board-probe timed out after {options.TimeoutSeconds} second(s).");
            return 1;
        }
        catch (LlmClientException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
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

        var phaseTracker = options.StrategyMode == ChessQuestStrategyMode.Phase
            ? ChessQuestPhaseTracker.Create(session, options.Phase, options.PhaseMaxAgentTurns)
            : null;
        var effectivePlanningMode = phaseTracker is not null && options.PlanningMode == PlanningMode.Stepwise
            ? PlanningMode.QueryAndBlockerDriven
            : options.PlanningMode;
        var runObjective = BuildObjective(scenario, phaseTracker);
        var initialPlannerContext = ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session, phaseTracker);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "chessquest", args);
        if (runLog is not null)
        {
            ChessQuestGameRecordStore.WriteDirectory(runLog.DirectoryPath, session);
        }

        runLog?.WriteJson("chessquest-scenario.json", scenario);
        if (phaseTracker is not null)
        {
            runLog?.WriteJson("chessquest-phase-context.json", phaseTracker.Snapshot(session));
        }

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
                if (phaseTracker is not null)
                {
                    runLog.WriteJson("chessquest-phase-report.json", phaseTracker.BuildReport(session));
                }
            };

        PrintOpening(scenario, session, options, opponentMode, opponentPlanner, phaseTracker, resumeRecord, resumeSource);

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
                effectivePlanningMode,
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
            outcomeReporter: phaseTracker is null
                ? new ChessQuestOutcomeReporter()
                : new ChessQuestPhaseOutcomeReporter(session, phaseTracker),
            policy: new ExecutionPolicy(
                MaxSteps: options.MaxSteps,
                MaxRefinements: options.MaxRefinements,
                Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
                PlanningMode: effectivePlanningMode,
                MaxPlanContinuations: options.MaxPlanContinuations,
                PlanningContext: new PlanningContextOptions(MaxRecentObservations: 12, MaxRecentReceipts: 12),
                MaxBlockedRetries: options.MaxBlockedRetries),
            completionEvaluator: phaseTracker is null
                ? EvidenceCompletionEvaluator.ForArtifactKind("chessquest.objective_completed")
                : new ChessQuestPhaseCompletionEvaluator(session, phaseTracker),
            planningFrameProjector: new ChessQuestPlanningFrameProjector(session, phaseTracker));

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
            if (phaseTracker is not null)
            {
                runLog.WriteJson("chessquest-phase-report.json", phaseTracker.BuildReport(session));
            }
        }

        services.FinishRunLog(runLog, envelope);
        if (phaseTracker is not null)
        {
            PrintPhaseReport(phaseTracker.BuildReport(session));
        }

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

    private static string BuildObjective(
        ChessQuestScenario scenario,
        ChessQuestPhaseTracker? phaseTracker = null)
    {
        var phaseContract = phaseTracker is null
            ? string.Empty
            : $"""

        Phase strategy context:
        - You are executing a bounded ChessQuest phase, not an unbounded whole-game run.
        - Phase: {phaseTracker.Context.PhaseObjective.Phase}
        - Phase goal: {phaseTracker.Context.PhaseObjective.Goal}
        - Strategy: {phaseTracker.Context.StrategyFrame.StrategyName}
        - Strategy intent: {phaseTracker.Context.StrategyFrame.StrategyIntent}
        - Phase budget: {phaseTracker.Context.PhaseObjective.MaxAgentTurns} agent turn(s).
        - Advance the phase when legal, but if phase guidance conflicts with chessFrame board truth, legal moves and receipts win.
        """;

        return
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
        {phaseContract}
        """;
    }

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
        ChessQuestPhaseTracker? phaseTracker = null,
        ChessQuestGameRecord? resumeRecord = null,
        string? resumeSource = null)
    {
        Console.WriteLine($"Agent has accepted ChessQuest: \"{scenario.Title}\"");
        Console.WriteLine($"Objective: {scenario.PublicObjective}");
        Console.WriteLine($"Scenario: {scenario.ScenarioId}");
        Console.WriteLine($"Agent Color: {scenario.AgentColor}");
        Console.WriteLine($"Surface: {scenario.DisclosurePolicy.Mode}");
        Console.WriteLine($"Opponent: {scenario.Difficulty.Opponent}");
        if (phaseTracker is not null)
        {
            var phase = phaseTracker.Snapshot(session);
            Console.WriteLine($"Strategy Mode: {options.StrategyMode}");
            Console.WriteLine($"Phase: {phase.PhaseObjective.Phase}; Budget: {phase.PhaseObjective.MaxAgentTurns} agent turn(s)");
            Console.WriteLine($"Strategy: {phase.StrategyFrame.StrategyName}");
        }

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

    private static void PrintPhaseReport(ChessQuestPhaseReport report)
    {
        Console.WriteLine();
        Console.WriteLine("--- ChessQuest Phase Report ---");
        Console.WriteLine($"Phase: {report.Phase}");
        Console.WriteLine($"Strategy: {report.StrategyName}");
        Console.WriteLine($"Status: {report.Status}");
        Console.WriteLine($"Stop Reason: {report.StopReason}");
        Console.WriteLine($"Agent Turns: {report.AgentTurnsPlayed}; Remaining: {report.AgentTurnsRemaining}");
        Console.WriteLine($"Agent Moves: {(report.AgentMoves.Count == 0 ? "none" : string.Join(", ", report.AgentMoves))}");
        Console.WriteLine($"Opponent Moves: {(report.OpponentMoves.Count == 0 ? "none" : string.Join(", ", report.OpponentMoves))}");
        Console.WriteLine($"Material Delta: {report.MaterialBalanceDelta}");
        Console.WriteLine($"Terminal: {report.Terminal}");
    }

    private static void PrintBoardProbeTrial(
        ChessQuestBoardProbeTrial trial,
        ChessQuestBoardProbeTrialResult result)
    {
        var status = result.Passed ? "PASS" : "FAIL";
        Console.WriteLine($"[{status}] trial={trial.TrialNumber} square={trial.Square} expected={FormatExpected(result.Expected)} answer={FormatAnswer(result.Answer)}");
        if (!result.Passed)
        {
            Console.WriteLine($"  reason: {result.FailureReason}");
            Console.WriteLine($"  fen: {trial.Fen}");
            Console.WriteLine("  board:");
            foreach (var line in trial.BoardLines)
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    private static string FormatExpected(ChessQuestBoardProbeExpected expected) =>
        expected.Occupied
            ? $"{expected.Color}_{expected.Piece}"
            : "empty";

    private static string FormatAnswer(ChessQuestBoardProbeAnswer? answer) =>
        answer is null
            ? "none"
            : answer.Occupied ? $"{answer.Color}_{answer.Piece}" : "empty";

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
