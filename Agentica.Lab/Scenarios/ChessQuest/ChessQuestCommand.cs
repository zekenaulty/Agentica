using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Orchestration;
using Agentica.Clients.Planning;
using Agentica.Lab.Logging;
using Agentica.Lab.Scenarios.ChessQuest;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Orchestration;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Planning;
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

        if (string.Equals(args[0], "legal-action-probe", StringComparison.OrdinalIgnoreCase))
        {
            return await RunLegalActionProbeAsync(args.Skip(1).ToArray(), services).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "puzzle-probe", StringComparison.OrdinalIgnoreCase))
        {
            return await RunPuzzleProbeAsync(args.Skip(1).ToArray(), services).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "state-probe", StringComparison.OrdinalIgnoreCase))
        {
            return await RunStateProbeAsync(args.Skip(1).ToArray(), services).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "resume", StringComparison.OrdinalIgnoreCase))
        {
            return await ResumeGameAsync(board, args.Skip(1).ToArray(), services).ConfigureAwait(false);
        }

        if (string.Equals(args[0], "orchestrate", StringComparison.OrdinalIgnoreCase))
        {
            return await RunStrategicOrchestrationAsync(board, args.Skip(1).ToArray(), services).ConfigureAwait(false);
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

            if (runLog?.IsEnabled == true)
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

    private static async Task<int> RunLegalActionProbeAsync(
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
            Console.Error.WriteLine("ChessQuest legal-action-probe requires Gemini credentials. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var client = new RetryingLlmClient(
            new GeminiLlmClient(GeminiClientOptions.FromEnvironment(options.ModelId)),
            new LlmRetryOptions(CallTimeout: TimeSpan.FromSeconds(options.TimeoutSeconds)));
        var runner = new ChessQuestLegalActionProbeRunner(client);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "chessquest-legal-action-probe", args);
        runLog?.WriteJson("chessquest-legal-action-probe-options.json", options);

        if (!options.Json)
        {
            Console.WriteLine("--- ChessQuest Legal Action Probe ---");
            Console.WriteLine($"Model: {options.ModelId}");
            Console.WriteLine($"Trials: {options.Trials}");
            Console.WriteLine($"Seed: {options.Seed}");
            Console.WriteLine($"Scramble Plies: {options.ScramblePlies}");
            Console.WriteLine($"Presentation: {options.Presentation}");
            Console.WriteLine();
        }

        try
        {
            var summary = await runner.RunAsync(
                    options,
                    (trial, result) =>
                    {
                        runLog?.WriteJsonLine(
                            "chessquest-legal-action-probe-trials.jsonl",
                            new
                            {
                                trial.TrialNumber,
                                trial.Seed,
                                trial.Fen,
                                trial.BoardLines,
                                trial.SideToMove,
                                trial.LegalMoves,
                                Prompt = ChessQuestLegalActionProbeRunner.BuildPrompt(trial, options.Presentation),
                                Result = result,
                                RawResponse = result.RawResponse
                            });

                        if (!options.Json)
                        {
                            PrintLegalActionProbeTrial(trial, result);
                        }
                    },
                    timeout.Token)
                .ConfigureAwait(false);

            runLog?.WriteJson("chessquest-legal-action-probe-summary.json", summary);
            if (options.Json)
            {
                Console.WriteLine(ChessQuestLegalActionProbeRunner.SerializeSummary(summary));
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("--- Legal Action Probe Summary ---");
                Console.WriteLine($"Passed: {summary.Passed}/{summary.Trials}");
                Console.WriteLine($"Failed: {summary.Failed}/{summary.Trials}");
            }

            if (runLog?.IsEnabled == true)
            {
                Console.WriteLine();
                Console.WriteLine($"Run log written: {runLog.DirectoryPath}");
            }

            return summary.Failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("ChessQuest legal-action-probe timed out.");
            return 124;
        }
    }

    private static async Task<int> RunPuzzleProbeAsync(
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
            Console.Error.WriteLine("ChessQuest puzzle-probe requires Gemini credentials. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var client = new RetryingLlmClient(
            new GeminiLlmClient(GeminiClientOptions.FromEnvironment(options.ModelId)),
            new LlmRetryOptions(CallTimeout: TimeSpan.FromSeconds(options.TimeoutSeconds)));
        var runner = new ChessQuestPuzzleProbeRunner(client);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "chessquest-puzzle-probe", args);
        runLog?.WriteJson("chessquest-puzzle-probe-options.json", options);

        if (!options.Json)
        {
            Console.WriteLine("--- ChessQuest Puzzle Probe ---");
            Console.WriteLine($"Model: {options.ModelId}");
            Console.WriteLine($"Trials: {options.Trials}");
            Console.WriteLine($"Presentation: {options.Presentation}");
            Console.WriteLine($"Puzzle Source: {options.PuzzleSource}");
            Console.WriteLine();
        }

        try
        {
            var summary = await runner.RunAsync(
                    options,
                    (trial, result) =>
                    {
                        runLog?.WriteJsonLine(
                            "chessquest-puzzle-probe-trials.jsonl",
                            new
                            {
                                trial.TrialNumber,
                                trial.PuzzleId,
                                trial.Source,
                                trial.Objective,
                                trial.GenerationNote,
                                trial.Fen,
                                trial.BoardLines,
                                trial.AgentColor,
                                Prompt = ChessQuestPuzzleProbeRunner.BuildPrompt(trial, options.Presentation),
                                Result = result,
                                RawResponse = result.RawResponse
                            });

                        if (!options.Json)
                        {
                            PrintPuzzleProbeTrial(trial, result);
                        }
                    },
                    timeout.Token)
                .ConfigureAwait(false);

            runLog?.WriteJson("chessquest-puzzle-probe-summary.json", summary);
            if (options.Json)
            {
                Console.WriteLine(ChessQuestPuzzleProbeRunner.SerializeSummary(summary));
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("--- Puzzle Probe Summary ---");
                Console.WriteLine($"Passed: {summary.Passed}/{summary.Trials}");
                Console.WriteLine($"Failed: {summary.Failed}/{summary.Trials}");
            }

            if (runLog?.IsEnabled == true)
            {
                Console.WriteLine();
                Console.WriteLine($"Run log written: {runLog.DirectoryPath}");
            }

            return summary.Failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("ChessQuest puzzle-probe timed out.");
            return 124;
        }
    }

    private static async Task<int> RunStateProbeAsync(
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
            Console.Error.WriteLine("ChessQuest state-probe requires Gemini credentials. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var client = new RetryingLlmClient(
            new GeminiLlmClient(GeminiClientOptions.FromEnvironment(options.ModelId)),
            new LlmRetryOptions(CallTimeout: TimeSpan.FromSeconds(options.TimeoutSeconds)));
        var runner = new ChessQuestStateProbeRunner(client);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "chessquest-state-probe", args);
        runLog?.WriteJson("chessquest-state-probe-options.json", options);

        if (!options.Json)
        {
            Console.WriteLine("--- ChessQuest State Probe ---");
            Console.WriteLine($"Model: {options.ModelId}");
            Console.WriteLine($"Trials: {options.Trials}");
            Console.WriteLine($"Seed: {options.Seed}");
            Console.WriteLine($"Scramble Plies: {options.ScramblePlies}");
            Console.WriteLine($"Presentation: {options.Presentation}");
            Console.WriteLine($"Probe Kind: {options.StateProbeKind}");
            Console.WriteLine();
        }

        try
        {
            var summary = await runner.RunAsync(
                    options,
                    (trial, result) =>
                    {
                        runLog?.WriteJsonLine(
                            "chessquest-state-probe-trials.jsonl",
                            new
                            {
                                trial.TrialNumber,
                                trial.Seed,
                                trial.Kind,
                                trial.Fen,
                                trial.BoardLines,
                                trial.SideToMove,
                                trial.Ply,
                                trial.LegalityMove,
                                trial.LegalityExpected,
                                trial.CaptureMove,
                                trial.CaptureExpected,
                                trial.CapturedPieceExpected,
                                trial.SideToMoveInCheckExpected,
                                trial.WhiteMaterialExpected,
                                trial.BlackMaterialExpected,
                                trial.MaterialDeltaForSideToMoveExpected,
                                trial.PhaseExpected,
                                trial.LegalMoves,
                                Prompt = ChessQuestStateProbeRunner.BuildPrompt(trial, options.Presentation),
                                Result = result,
                                RawResponse = result.RawResponse
                            });

                        if (!options.Json)
                        {
                            PrintStateProbeTrial(trial, result);
                        }
                    },
                    timeout.Token)
                .ConfigureAwait(false);

            runLog?.WriteJson("chessquest-state-probe-summary.json", summary);
            if (options.Json)
            {
                Console.WriteLine(ChessQuestStateProbeRunner.SerializeSummary(summary));
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("--- State Probe Summary ---");
                Console.WriteLine($"Passed: {summary.Passed}/{summary.Trials}");
                Console.WriteLine($"Failed: {summary.Failed}/{summary.Trials}");
            }

            if (runLog?.IsEnabled == true)
            {
                Console.WriteLine();
                Console.WriteLine($"Run log written: {runLog.DirectoryPath}");
            }

            return summary.Failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("ChessQuest state-probe timed out.");
            return 124;
        }
    }

    private static async Task<int> RunStrategicOrchestrationAsync(
        IChessQuestBoard board,
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var options = ChessQuestOrchestrationOptions.Parse(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            services.PrintUsage();
            return 2;
        }

        if ((options.TaskPlanner == PlannerKind.Gemini || options.RunPlanner == PlannerKind.Gemini) &&
            !services.GeminiCredentialsAvailable())
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

        var runOptions = options.ToRunOptions();
        var opponentMode = ResolveOpponentMode(scenario, runOptions);
        var opponentPlanner = runOptions.OpponentPlanner ?? runOptions.Planner;
        if (opponentMode == ChessQuestOpponentMode.Agent &&
            opponentPlanner == PlannerKind.Gemini &&
            !services.GeminiCredentialsAvailable())
        {
            Console.Error.WriteLine("Agent opponent requested with Gemini planner, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        scenario = ApplyOpponentDifficulty(scenario, opponentMode, runOptions);
        var opponent = CreateOpponent(scenario, runOptions, opponentMode, opponentPlanner, services);
        var session = new ChessQuestSession(scenario, opponent: opponent);
        var state = new ChessQuestStrategicOrchestrationState(session);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "chessquest-orchestrate", args);
        var gameRecordCreatedAt = DateTimeOffset.UtcNow;
        runLog?.WriteJson("chessquest-orchestration-options.json", options);
        runLog?.WriteJson("chessquest-scenario.json", scenario);
        runLog?.WriteJson("chessquest-initial-planning-frame.json", ChessQuestCapabilitySurfaceCompiler.BuildPlanningFrame(session));
        WriteGameRecord(runLog, session, gameRecordCreatedAt);
        WriteContinuityCapsule(runLog, session);

        Action<ChessQuestCockpitTurnEnvelope>? turnRecorder = runLog is null
            ? null
            : turn =>
            {
                runLog.WriteJsonLine("chessquest-turns.jsonl", turn);
                WriteGameRecord(runLog, session, gameRecordCreatedAt);
            };

        Action<ChessQuestStrategyProjection, ChessQuestPhaseReport>? phaseCompleted = runLog is null
            ? null
            : (projection, report) =>
            {
                runLog.WriteJsonLine("chessquest-strategy-projections.jsonl", projection);
                runLog.WriteJsonLine("chessquest-phase-reports.jsonl", report);
                runLog.WriteJson("chessquest-latest-phase-report.json", report);
                WriteGameRecord(runLog, session, gameRecordCreatedAt);
                WriteContinuityCapsule(runLog, session, report, projection);
            };

        PrintStrategicOrchestrationOpening(scenario, session, options, opponentMode, opponentPlanner);

        var orchestrator = new TaskOrchestrator(
            CreateChessQuestTaskPlanner(options),
            new ChessQuestPhaseRunExecutor(
                state,
                phaseRequest => CreateChessQuestRunPlanner(session, phaseRequest, options, services),
                phaseTracker => services.CreateEventSink(
                    CreateEventSink(session, runOptions, turnRecorder),
                    runLog),
                options.PhaseMaxAgentTurns,
                new ExecutionPolicy(
                    MaxSteps: options.MaxSteps,
                    MaxRefinements: options.MaxRefinements,
                    Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
                    PlanningMode: PlanningMode.QueryAndBlockerDriven,
                    MaxPlanContinuations: options.MaxPlanContinuations,
                    PlanningContext: new PlanningContextOptions(MaxRecentObservations: 12, MaxRecentReceipts: 12),
                    MaxBlockedRetries: options.MaxBlockedRetries,
                    EvaluateCompletionAfterEachBatch: true),
                phaseCompleted),
            new ChessQuestTaskAcceptanceEvaluator(state, options.MaxOrchestrationRuns),
            new DeterministicWorkContextCompiler(),
            state.BuildHostState,
            new OrchestrationPolicy(
                MaxRuns: options.MaxOrchestrationRuns + 1,
                MaxRefinements: options.MaxOrchestrationRefinements,
                MaxGraphMutationsPerRefinement: options.MaxGraphMutations));

        using var runCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            runCancellation.Cancel();
        };

        OrchestrationOutcomeEnvelope outcome;
        Console.CancelKeyPress += cancelHandler;
        try
        {
            outcome = await orchestrator.RunAsync(
                new LargeTaskRequest(
                    BuildStrategicOrchestrationObjective(scenario, options),
                    RequestOrigin.User,
                    BuildStrategicOrchestrationContext(session, options)),
                runCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine($"ChessQuest strategic orchestration timed out after {options.TimeoutSeconds} second(s).");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        var diagnostics = BuildStrategicOrchestrationDiagnostics(outcome, state);
        PrintStrategicOrchestrationSummary(outcome, state, diagnostics);
        runLog?.WriteJson("chessquest-orchestration-outcome.json", outcome);
        runLog?.WriteJson("chessquest-orchestration-diagnostics.json", diagnostics);
        WriteGameRecord(runLog, session, gameRecordCreatedAt);
        WriteContinuityCapsule(
            runLog,
            session,
            state.LatestPhaseReport,
            state.LatestStrategyProjection);
        if (runLog?.IsEnabled == true)
        {
            Console.WriteLine();
            Console.WriteLine($"Run log written: {runLog.DirectoryPath}");
        }

        return outcome.Status == OrchestrationStatus.Succeeded ? 0 : 1;
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
                if (!string.IsNullOrWhiteSpace(resumeSource) &&
                    ChessQuestGameRecordStore.TryLoadContinuityCapsule(resumeSource) is { } capsule)
                {
                    session.ImportContinuityCapsule(capsule);
                }
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }
        }

        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "chessquest", args);
        var gameRecordCreatedAt = DateTimeOffset.UtcNow;
        WriteGameRecord(runLog, session, gameRecordCreatedAt);
        WriteContinuityCapsule(runLog, session);

        runLog?.WriteJson("chessquest-scenario.json", scenario);
        if (resumeRecord is not null)
        {
            runLog?.WriteJson("chessquest-resume-source.json", new
            {
                resumed = true,
                sourceKind = Directory.Exists(resumeSource) ? "directory" : "file",
                record = resumeRecord
            });
        }

        ChessQuestStrategyProjectionResult? strategyProjectionResult;
        try
        {
            strategyProjectionResult = options.StrategyMode == ChessQuestStrategyMode.Projected
                ? await CreateStrategyProjectionAsync(session, options, runLog).ConfigureAwait(false)
                : null;
        }
        catch (Exception exception) when (exception is LlmClientException or JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"ChessQuest strategy projection failed: {exception.Message}");
            return 1;
        }
        var phaseTracker = options.StrategyMode is ChessQuestStrategyMode.Phase or ChessQuestStrategyMode.Projected
            ? ChessQuestPhaseTracker.Create(
                session,
                options.Phase,
                options.PhaseMaxAgentTurns,
                strategyProjectionResult?.Projection)
            : null;
        var effectivePlanningMode = phaseTracker is not null && options.PlanningMode == PlanningMode.Stepwise
            ? PlanningMode.QueryAndBlockerDriven
            : options.PlanningMode;
        var runObjective = BuildObjective(scenario, phaseTracker);
        var initialPlannerContext = ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session, phaseTracker);

        if (phaseTracker is not null)
        {
            runLog?.WriteJson("chessquest-phase-context.json", phaseTracker.Snapshot(session));
        }

        runLog?.WriteJson("chessquest-initial-planning-context.json", initialPlannerContext);
        runLog?.WriteJson("chessquest-initial-planning-frame.json", ChessQuestCapabilitySurfaceCompiler.BuildPlanningFrame(session));
        Action<ChessQuestCockpitTurnEnvelope>? turnRecorder = runLog is null
            ? null
            : turn =>
            {
                runLog.WriteJsonLine("chessquest-turns.jsonl", turn);
                WriteGameRecord(runLog, session, gameRecordCreatedAt);
                if (phaseTracker is not null)
                {
                    runLog.WriteJson("chessquest-phase-report.json", phaseTracker.BuildReport(session));
                }
                WriteContinuityCapsule(
                    runLog,
                    session,
                    phaseTracker?.BuildReport(session),
                    phaseTracker?.Snapshot(session).StrategyProjection);
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
                MaxBlockedRetries: options.MaxBlockedRetries,
                EvaluateCompletionAfterEachBatch: true,
                SecurityPolicy: LabSecurityPolicy.ForPlanner(planner)),
            completionEvaluator: phaseTracker is null
                ? new ChessQuestCompletionEvaluator(session)
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
            WriteGameRecord(runLog, session, gameRecordCreatedAt);
            if (phaseTracker is not null)
            {
                runLog.WriteJson("chessquest-phase-report.json", phaseTracker.BuildReport(session));
            }
            WriteContinuityCapsule(
                runLog,
                session,
                phaseTracker?.BuildReport(session),
                phaseTracker?.Snapshot(session).StrategyProjection);
        }

        services.FinishRunLog(runLog, envelope);
        if (phaseTracker is not null)
        {
            PrintPhaseReport(phaseTracker.BuildReport(session));
        }

        return envelope.Outcome.Status == RunOutcomeStatus.Succeeded ? 0 : 1;
    }

    private static ITaskPlanner CreateChessQuestTaskPlanner(ChessQuestOrchestrationOptions options)
    {
        ITaskPlanner planner;
        ITaskPlanner? fallback = null;
        if (options.TaskPlanner == PlannerKind.Deterministic)
        {
            planner = new ChessQuestDeterministicTaskPlanner(options.PhaseMaxAgentTurns);
        }
        else
        {
            var modelId = options.TaskModelId ?? GeminiModelId.Flash25;
            var llmClient = new RetryingLlmClient(
                new GeminiLlmClient(GeminiClientOptions.FromEnvironment(modelId)),
                new LlmRetryOptions(CallTimeout: TimeSpan.FromMinutes(10)));
            planner = new LlmTaskPlanner(
                llmClient,
                new LlmTaskPlannerOptions(
                    modelId,
                    new LlmGenerationOptions(
                        Temperature: 0,
                        MaxOutputTokens: options.MaxOutputTokens ?? LlmPlannerOptions.DefaultMaxOutputTokens,
                        Thinking: ToThinkingOptions(options.ThinkingBudget ?? "off", options.IncludeThoughts))));
            fallback = new ChessQuestDeterministicTaskPlanner(options.PhaseMaxAgentTurns);
        }

        return new ChessQuestConsoleTaskPlanner(planner, fallback);
    }

    private static IWorkflowPlanner CreateChessQuestRunPlanner(
        ChessQuestSession session,
        RunRequest phaseRequest,
        ChessQuestOrchestrationOptions options,
        CliCommandServices services)
    {
        if (options.RunPlanner == PlannerKind.Deterministic)
        {
            return new ChessQuestDeterministicPlanner(session);
        }

        return services.CreatePlanner(new CliRunOptions(
            phaseRequest.Objective,
            options.RunPlanner,
            options.RunModelId ?? options.TaskModelId,
            options.ThinkingBudget,
            options.IncludeThoughts,
            options.MaxOutputTokens,
            PlanningMode.QueryAndBlockerDriven,
            options.MaxBlockedRetries,
            LogRun: false,
            LogDir: null,
            IsValid: true,
            Error: null));
    }

    private static LlmThinkingOptions? ToThinkingOptions(
        string? thinkingBudget,
        bool includeThoughts) =>
        thinkingBudget switch
        {
            null when includeThoughts => new LlmThinkingOptions(IncludeThoughts: true),
            null => null,
            "dynamic" => LlmThinkingOptions.Dynamic(includeThoughts),
            "off" => LlmThinkingOptions.Off(includeThoughts),
            "0" => LlmThinkingOptions.Off(includeThoughts),
            var value when int.TryParse(value, out var tokens) && tokens > 0 =>
                LlmThinkingOptions.Budget(tokens, includeThoughts),
            _ => throw new InvalidOperationException($"Invalid thinking budget '{thinkingBudget}'.")
        };

    private static string BuildStrategicOrchestrationObjective(
        ChessQuestScenario scenario,
        ChessQuestOrchestrationOptions options) =>
        $"""
        ChessQuest strategic orchestration for scenario "{scenario.Title}".

        Campaign objective:
        {scenario.PublicObjective}

        Orchestration role:
        - Choose the next bounded strategic phase task for the active ChessQuest runner.
        - Do not choose a chess move directly; the active runner chooses moves through ChessQuest strict referee tools.
        - Initial plan should contain exactly one non-optional phase_run task.
        - Refinement may add exactly one next phase_run task when a receipt-backed phase report shows the game is nonterminal and more phase budget remains.
        - Strategy frames shape public intent only. chessFrame, legal move receipts, and objective verifier outputs are authoritative.
        - Goal content should come from orchestration. Host guardrails only define the grammar: role invariants, legal evidence types, forbidden claim patterns, and terminal verification.
        - Do not include move-level direction such as concrete UCI moves, square-specific attacks, opening lines, or hidden solution lines in the phase envelope.

        Required phase task contextProjection keys:
        - chessquest.taskKind = "phase_run"
        - chessquest.phase = one of opening, tactical, conversion, defense, endgame
        - chessquest.taskDirection = short public strategic direction
        - chessquest.publicRationale = concise evidence-based rationale from public context
        - chessquest.phaseGoal = bounded goal for the active runner
        - chessquest.activeObjectives = 2-5 public objectives for this phase
        - chessquest.successSignals = 2-5 observable signals the active runner should try to produce
        - chessquest.claimDiscipline = 2-5 evidence/claim rules for this phase
        - chessquest.maxAgentTurns = integer from 1 to {Math.Max(1, options.PhaseMaxAgentTurns)}
        - chessquest.replanTriggers = public stop/replan triggers

        Acceptance:
        - Phase tasks must use an acceptance requirement with kind "Artifact" and artifactKind "chessquest.phase_report".
        - Do not use ObjectiveVerifier, Verifier, PhaseReport, Completion, or any other acceptance kind. Valid kinds are only OutcomeStatus, Artifact, Receipt, HostState.
        - The host verifies all mutation and completion evidence.
        """;

    private static IReadOnlyDictionary<string, object?> BuildStrategicOrchestrationContext(
        ChessQuestSession session,
        ChessQuestOrchestrationOptions options)
    {
        var context = new Dictionary<string, object?>(
            ChessQuestCapabilitySurfaceCompiler.BuildPlannerContext(session),
            StringComparer.Ordinal)
        {
            ["chessquest.orchestration.kind"] = "strategic_phase_task_planner",
            ["chessquest.orchestration.maxPhaseTasks"] = options.MaxOrchestrationRuns,
            ["chessquest.orchestration.defaultMaxAgentTurnsPerPhase"] = options.PhaseMaxAgentTurns,
            ["chessquest.orchestration.allowedPhases"] = new[]
            {
                "opening",
                "tactical",
                "conversion",
                "defense",
                "endgame"
            },
            ["chessquest.orchestration.allowedTaskKind"] = "phase_run",
            ["chessquest.orchestration.acceptanceArtifactKind"] = "chessquest.phase_report",
            ["chessquest.playingDoctrine"] = ChessQuestGoalShapingPolicy.StaticDoctrine,
            ["chessquest.orchestration.goalOwnership"] =
                "The orchestration planner owns phase choice, phase goal, active objectives, success signals, rationale, and claim discipline. The host owns guardrails and sanitization.",
            ["chessquest.orchestration.forbiddenMoveLevelGuidance"] = new[]
            {
                "concrete UCI moves",
                "square-specific tactics",
                "opening lines",
                "best move hints",
                "hidden solution lines"
            },
            ["chessquest.orchestration.requiredContextProjectionKeys"] = new[]
            {
                "chessquest.taskKind",
                "chessquest.phase",
                "chessquest.taskDirection",
                "chessquest.publicRationale",
                "chessquest.phaseGoal",
                "chessquest.activeObjectives",
                "chessquest.successSignals",
                "chessquest.claimDiscipline",
                "chessquest.maxAgentTurns",
                "chessquest.replanTriggers"
            },
            ["chessquest.orchestration.boundary"] =
                "Orchestration selects phase envelopes; active ChessQuest runs select and commit legal moves."
        };

        return context;
    }

    private static async Task<ChessQuestStrategyProjectionResult> CreateStrategyProjectionAsync(
        ChessQuestSession session,
        ChessQuestRunOptions options,
        RunLogWriter? runLog)
    {
        ChessQuestStrategyProjectionResult result;
        if (options.Planner == PlannerKind.Deterministic)
        {
            result = ChessQuestStrategyProjectionRunner.Deterministic(
                session,
                options.Phase,
                options.PhaseMaxAgentTurns);
        }
        else
        {
            using var projectionTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(Math.Min(options.TimeoutSeconds, 120)));
            var modelId = options.ModelId ?? GeminiModelId.Flash25;
            var client = new RetryingLlmClient(
                new GeminiLlmClient(GeminiClientOptions.FromEnvironment(modelId)),
                new LlmRetryOptions(CallTimeout: TimeSpan.FromSeconds(Math.Min(options.TimeoutSeconds, 120))));
            var runner = new ChessQuestStrategyProjectionRunner(client);
            result = await runner.ProjectAsync(
                    session,
                    options.Phase,
                    options.PhaseMaxAgentTurns,
                    modelId,
                    options.ThinkingBudget ?? "off",
                    options.IncludeThoughts,
                    options.MaxOutputTokens ?? 1024,
                    projectionTimeout.Token)
                .ConfigureAwait(false);
        }

        runLog?.WriteJson("chessquest-strategy-projection.json", result);
        PrintStrategyProjection(result);
        return result;
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
        var doctrine = ChessQuestGoalShapingPolicy.StaticDoctrine;
        var attackInspectionContract = scenario.DisclosurePolicy.AllowAttackInspection
            ? "- You may use chess.inspect_attacks for neutral public opponent-capture facts. It does not score, choose, or prove a response is safe."
            : string.Empty;
        var candidateInspectionContract = scenario.DisclosurePolicy.EffectiveAllowCandidateInspection
            ? "- If exposed, chess.inspect_candidate may inspect your own candidate move and report neutral opponent capture facts from the resulting placement. A bad scan should make you revise or abandon the candidate; a quiet scan still does not prove full safety, score the move, rank it, recommend it, or choose a reply."
            : string.Empty;
        var evidenceSourceContract = scenario.DisclosurePolicy.EffectiveAllowCandidateInspection
            ? "- Evidence sources have distinct limits, not a proof ranking: current state gives placement, legal moves give affordances, project_line gives submitted rule projection, chess.inspect_candidate gives after-candidate capture facts for your submitted move, and self-authored opponent-reply lines model only the replies you supplied."
            : "- Evidence sources have distinct limits, not a proof ranking: current state gives placement, legal moves give affordances, project_line gives submitted rule projection, and self-authored opponent-reply lines model only the replies you supplied.";
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
        - Active objectives:
        {FormatObjectiveBullets(phaseTracker.Context.StrategyFrame.ActiveObjectives)}
        - Stop/replan triggers:
        {FormatObjectiveBullets(phaseTracker.Context.PhaseObjective.StopTriggers)}
        - Claim discipline:
        {FormatObjectiveBullets(phaseTracker.Context.StrategyProjection?.ClaimDiscipline ?? [])}
        - Advance the phase when legal, but if phase guidance conflicts with chessFrame board truth, legal moves and receipts win.
        """;

        return
        $"""
        ChessQuest: {scenario.Title}
        Objective: {scenario.PublicObjective}

        Planner contract:
        - You are playing {scenario.AgentColor}. The opponent is the other color.
        - Win is required. Draw and loss do not satisfy the objective.
        - Playing doctrine: {doctrine.Summary}
        - Good play means:
        {FormatObjectiveBullets(doctrine.GoodPlayCriteria)}
        - Evidence discipline:
        {FormatObjectiveBullets(doctrine.EvidenceDiscipline)}
        - Use coordinate UCI notation only: origin square followed by destination square, plus a promotion letter only when promoting.
        - Do not use SAN, piece letters, capture markers, check symbols, or checkmate symbols in move fields.
        - Use chess.get_state, chess.render_board, and chess.list_legal_moves to inspect current public state.
        - You may use chess.project_line only for hypothetical UCI lines you authored yourself. It is read-only, can verify submitted check/checkmate claims, and does not generate opponent replies.
        - Commit exactly one selected agent move with chess.play_move. Include a concise public turnIntent matching the selected move.
        - turnIntent should separate goal, evidence, hypothesis, riskCheck, claimLevel, and publicReason. Public intent is audit text, not proof.
        - Strict gameplay requires passing the current chess.list_legal_moves legalMoveObservationId into chess.play_move. If the board changes or a move is refused as stale, refresh chess.list_legal_moves.
        - Before describing a move as check or checkmate, call chess.project_line for that exact move or line with claims ["check"] or ["checkmate"] and use the returned claimVerification.
        {attackInspectionContract}
        {candidateInspectionContract}
        - Do not describe a selected move as checkmate, a forced win, or objective completion unless a prior chess.project_line result or committed receipt has already verified that terminal state.
        - Do not describe a move as safe, winning, material-gaining, forced, or decisive unless your evidence/riskCheck explains what was verified and what remains unmodeled.
        - Legal does not mean safe. One-ply project_line does not prove safety or move quality.
        {evidenceSourceContract}
        - chess.play_move applies the host-controlled opponent reply after your accepted move unless the game is terminal.
        - Do not claim completion unless chess.complete_objective emits chessquest.objective_completed.
        - The strict surface does not provide move rankings, scores, tactical labels, or opponent policy details.
        {phaseContract}
        """;
    }

    private static string FormatObjectiveBullets(IReadOnlyList<string> values) =>
        values.Count == 0
            ? "        - none"
            : string.Join(Environment.NewLine, values.Select(value => $"        - {value}"));

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
        var previewSession = new ChessQuestSession(scenario, opponent: new ScriptedChessOpponent([], fallbackToFirstLegalMove: false));
        Console.WriteLine($"  {string.Join(", ", ChessQuestTools.CreateCatalog(previewSession).Descriptors.Select(descriptor => descriptor.ToolId))}");
        Console.WriteLine();
        Console.WriteLine("Completion requires terminal board verification through chess.complete_objective.");
    }

    private static void PrintStrategicOrchestrationOpening(
        ChessQuestScenario scenario,
        ChessQuestSession session,
        ChessQuestOrchestrationOptions options,
        ChessQuestOpponentMode opponentMode,
        PlannerKind opponentPlanner)
    {
        Console.WriteLine("=== ChessQuest Orchestration Tier | Strategic Session ===");
        Console.WriteLine($"Scenario: {scenario.ScenarioId} ({scenario.Title})");
        Console.WriteLine($"Campaign Objective: {scenario.PublicObjective}");
        Console.WriteLine($"Agent Color: {scenario.AgentColor}");
        Console.WriteLine($"Task Planner: {options.TaskPlanner}; Active Run Planner: {options.RunPlanner}");
        Console.WriteLine($"Phase Budget: {options.PhaseMaxAgentTurns} agent turn(s) per phase; Max Phase Tasks: {options.MaxOrchestrationRuns}");
        Console.WriteLine($"Opponent: {scenario.Difficulty.Opponent}");
        if (opponentMode == ChessQuestOpponentMode.Agent)
        {
            Console.WriteLine($"Opponent Agent Planner: {opponentPlanner}");
        }

        Console.WriteLine();
        Console.WriteLine("Initial board:");
        Console.WriteLine(session.CurrentState.Fen);
        Console.WriteLine(ChessQuestRenderer.RenderBoardFromFen(session.CurrentState.Fen));
        Console.WriteLine();
    }

    private static void PrintStrategicOrchestrationSummary(
        OrchestrationOutcomeEnvelope outcome,
        ChessQuestStrategicOrchestrationState state,
        IReadOnlyDictionary<string, object?> diagnostics)
    {
        Console.WriteLine();
        Console.WriteLine("--- ChessQuest Strategic Orchestration Summary ---");
        Console.WriteLine($"Status: {outcome.Status}");
        Console.WriteLine($"Stop Reason: {outcome.StopReason}");
        Console.WriteLine($"Completed Phase Tasks: {outcome.State.CompletedTaskIds.Count}");
        Console.WriteLine($"Run Outcomes: {outcome.RunOutcomes.Count}");
        Console.WriteLine($"Terminal: {state.Session.CurrentState.IsTerminal}");
        if (state.Session.CurrentState.TerminalState is not null)
        {
            Console.WriteLine($"Result: {state.Session.CurrentState.TerminalState.Result} ({state.Session.CurrentState.TerminalState.Reason})");
        }

        Console.WriteLine($"Final FEN: {state.Session.CurrentState.Fen}");
        if (diagnostics.TryGetValue("failureModeCounts", out var failureModes) &&
            failureModes is IReadOnlyDictionary<string, int> counts &&
            counts.Count > 0)
        {
            Console.WriteLine("Failure Mode Counts:");
            foreach (var pair in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"  {pair.Key}: {pair.Value}");
            }
        }

        if (outcome.RunOutcomes.Count > 0)
        {
            Console.WriteLine("Child Run Outcomes:");
            foreach (var item in outcome.RunOutcomes.Select((run, index) => new { run, index }))
            {
                var blockers = item.run.Outcome.Blockers.Count == 0
                    ? string.Empty
                    : $" blockers={string.Join(" | ", item.run.Outcome.Blockers.Take(2))}";
                Console.WriteLine(
                    $"  {item.index + 1}. run={item.run.Outcome.RunId} status={item.run.Outcome.Status} stop={item.run.Outcome.StopReason}{blockers}");
            }
        }

        foreach (var report in state.PhaseReports)
        {
            Console.WriteLine();
            Console.WriteLine($"Phase Report: {report.PhaseRunId}");
            Console.WriteLine($"  Phase: {report.Phase}; Strategy: {report.StrategyName}");
            Console.WriteLine($"  Status: {report.Status}; Stop: {report.StopReason}");
            Console.WriteLine($"  Agent Moves: {(report.AgentMoves.Count == 0 ? "none" : string.Join(", ", report.AgentMoves))}");
            Console.WriteLine($"  Opponent Moves: {(report.OpponentMoves.Count == 0 ? "none" : string.Join(", ", report.OpponentMoves))}");
            Console.WriteLine($"  Material Delta: {report.MaterialBalanceDelta}; Terminal: {report.Terminal}");
        }
    }

    private static IReadOnlyDictionary<string, object?> BuildStrategicOrchestrationDiagnostics(
        OrchestrationOutcomeEnvelope outcome,
        ChessQuestStrategicOrchestrationState state)
    {
        var terminalState = state.Session.CurrentState.TerminalState;
        var agentWon = terminalState?.Winner == state.Session.Scenario.AgentColor;
        var failureModes = new Dictionary<string, int>(StringComparer.Ordinal);
        IncrementIf(failureModes, "terminal_loss", state.Session.CurrentState.IsTerminal && !agentWon && terminalState?.Winner is not null);
        IncrementIf(failureModes, "terminal_draw", state.Session.CurrentState.IsTerminal && terminalState?.Winner is null);
        IncrementIf(failureModes, "orchestration_plan_invalid", outcome.Status == OrchestrationStatus.PlanInvalid);
        IncrementIf(failureModes, "orchestration_max_refinements", outcome.StopReason == OrchestrationStopReason.MaxRefinementsReached);
        IncrementIf(failureModes, "orchestration_max_runs", outcome.StopReason == OrchestrationStopReason.MaxRunsReached);

        foreach (var run in outcome.RunOutcomes)
        {
            IncrementIf(failureModes, "child_plan_invalid", run.Outcome.Status == RunOutcomeStatus.PlanInvalid);
            IncrementIf(failureModes, "child_failed", run.Outcome.Status == RunOutcomeStatus.Failed);
            IncrementIf(failureModes, "child_blocked", run.Outcome.Status == RunOutcomeStatus.Blocked);
            IncrementIf(failureModes, "child_planner_unavailable", run.Outcome.StopReason == StopReason.PlannerUnavailable);
            IncrementIf(failureModes, "child_terminal_loss", run.Outcome.StopReason == StopReason.TerminalLoss);
            IncrementIf(failureModes, "child_terminal_draw", run.Outcome.StopReason == StopReason.TerminalDraw);
            IncrementIf(failureModes, "child_timeout", run.Outcome.StopReason == StopReason.Timeout);
        }

        foreach (var report in state.PhaseReports)
        {
            IncrementIf(failureModes, "phase_material_loss", report.MaterialBalanceDelta < 0);
            IncrementIf(failureModes, "phase_terminal", report.Terminal);
            IncrementIf(failureModes, "phase_budget_exhausted", string.Equals(report.StopReason, "agent_turn_budget_exhausted", StringComparison.Ordinal));
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = outcome.Status.ToString(),
            ["stopReason"] = outcome.StopReason.ToString(),
            ["terminal"] = state.Session.CurrentState.IsTerminal,
            ["terminalResult"] = terminalState?.Result,
            ["agentWon"] = agentWon,
            ["failureModeCounts"] = failureModes,
            ["runOutcomeCounts"] = outcome.RunOutcomes
                .GroupBy(run => $"{run.Outcome.Status}:{run.Outcome.StopReason}", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            ["phaseStatusCounts"] = state.PhaseReports
                .GroupBy(report => $"{report.Status}:{report.StopReason}", StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            ["runLineage"] = outcome.RunOutcomes
                .Select((run, index) => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["index"] = index + 1,
                    ["runId"] = run.Outcome.RunId,
                    ["status"] = run.Outcome.Status.ToString(),
                    ["stopReason"] = run.Outcome.StopReason.ToString(),
                    ["blockers"] = run.Outcome.Blockers
                })
                .ToArray(),
            ["phaseLineage"] = state.PhaseReports
                .Select((report, index) => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["index"] = index + 1,
                    ["phaseRunId"] = report.PhaseRunId,
                    ["phase"] = report.Phase,
                    ["status"] = report.Status,
                    ["stopReason"] = report.StopReason,
                    ["materialDelta"] = report.MaterialBalanceDelta,
                    ["terminal"] = report.Terminal,
                    ["terminalResult"] = report.TerminalState?.Result,
                    ["agentMoves"] = report.AgentMoves,
                    ["opponentMoves"] = report.OpponentMoves
                })
                .ToArray()
        };
    }

    private static void IncrementIf(
        Dictionary<string, int> counts,
        string key,
        bool condition)
    {
        if (!condition)
        {
            return;
        }

        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static void PrintStrategyProjection(ChessQuestStrategyProjectionResult result)
    {
        var projection = result.Projection;
        Console.WriteLine();
        Console.WriteLine("=== ChessQuest Orchestration Tier | Strategy Projection ===");
        Console.WriteLine($"Source: {projection.Source}");
        Console.WriteLine($"Provider: {result.ProviderName ?? "unknown"}; Model: {result.ResponseModelId ?? "unknown"}; Finish: {result.FinishReason}");
        Console.WriteLine($"Phase: {projection.Phase}");
        Console.WriteLine($"Strategy: {projection.StrategyName}");
        Console.WriteLine($"Intent: {projection.StrategyIntent}");
        Console.WriteLine("Objectives:");
        foreach (var objective in projection.ActiveObjectives)
        {
            Console.WriteLine($"  - {objective}");
        }

        Console.WriteLine("Stop Triggers:");
        foreach (var trigger in projection.StopTriggers)
        {
            Console.WriteLine($"  - {trigger}");
        }

        Console.WriteLine("Verification Rules:");
        foreach (var rule in projection.VerificationRules)
        {
            Console.WriteLine($"  - {rule}");
        }

        Console.WriteLine("Claim Discipline:");
        foreach (var rule in projection.ClaimDiscipline)
        {
            Console.WriteLine($"  - {rule}");
        }

        Console.WriteLine();
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
        Console.WriteLine("=== ChessQuest Active Run Tier | ChessQuest Execution ===");
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
            Console.WriteLine($"  provider: {result.ProviderName ?? "unknown"} finish={result.FinishReason} usage={FormatUsage(result.Usage)}");
            if (!string.IsNullOrWhiteSpace(result.RawResponse))
            {
                Console.WriteLine($"  raw: {Preview(result.RawResponse, 240)}");
            }

            Console.WriteLine($"  fen: {trial.Fen}");
            Console.WriteLine("  board:");
            foreach (var line in trial.BoardLines)
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    private static void PrintLegalActionProbeTrial(
        ChessQuestLegalActionProbeTrial trial,
        ChessQuestLegalActionProbeTrialResult result)
    {
        var status = result.Passed ? "PASS" : "FAIL";
        Console.WriteLine($"[{status}] trial={trial.TrialNumber} side={trial.SideToMove} move={result.Move ?? "none"} legalMoves={trial.LegalMoves.Count}");
        if (!result.Passed)
        {
            Console.WriteLine($"  reason: {result.FailureReason}");
            Console.WriteLine($"  provider: {result.ProviderName ?? "unknown"} finish={result.FinishReason} usage={FormatUsage(result.Usage)}");
            if (!string.IsNullOrWhiteSpace(result.RawResponse))
            {
                Console.WriteLine($"  raw: {Preview(result.RawResponse, 240)}");
            }

            Console.WriteLine($"  fen: {trial.Fen}");
            Console.WriteLine("  board:");
            foreach (var line in trial.BoardLines)
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    private static void PrintPuzzleProbeTrial(
        ChessQuestPuzzleProbeTrial trial,
        ChessQuestPuzzleProbeTrialResult result)
    {
        var status = result.Passed ? "PASS" : "FAIL";
        Console.WriteLine($"[{status}] trial={trial.TrialNumber} source={trial.Source} puzzle={trial.PuzzleId} role={trial.AgentColor} move={result.Move ?? "none"}");
        if (!result.Passed)
        {
            Console.WriteLine($"  reason: {result.FailureReason}");
            Console.WriteLine($"  provider: {result.ProviderName ?? "unknown"} finish={result.FinishReason} usage={FormatUsage(result.Usage)}");
            if (!string.IsNullOrWhiteSpace(result.RawResponse))
            {
                Console.WriteLine($"  raw: {Preview(result.RawResponse, 240)}");
            }

            Console.WriteLine($"  fen: {trial.Fen}");
            Console.WriteLine("  board:");
            foreach (var line in trial.BoardLines)
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    private static void PrintStateProbeTrial(
        ChessQuestStateProbeTrial trial,
        ChessQuestStateProbeTrialResult result)
    {
        var status = result.Passed ? "PASS" : "FAIL";
        Console.WriteLine($"[{status}] trial={trial.TrialNumber} kind={trial.Kind} side={trial.SideToMove} ply={trial.Ply}");
        Console.WriteLine($"  legality={trial.LegalityMove}:{trial.LegalityExpected} capture={trial.CaptureMove}:{trial.CaptureExpected}/{trial.CapturedPieceExpected} check={trial.SideToMoveInCheckExpected} material={trial.WhiteMaterialExpected}-{trial.BlackMaterialExpected} delta={trial.MaterialDeltaForSideToMoveExpected} phase={trial.PhaseExpected}");
        if (!result.Passed)
        {
            Console.WriteLine($"  reason: {string.Join("; ", result.FailureReasons)}");
            Console.WriteLine($"  provider: {result.ProviderName ?? "unknown"} finish={result.FinishReason} usage={FormatUsage(result.Usage)}");
            if (!string.IsNullOrWhiteSpace(result.RawResponse))
            {
                Console.WriteLine($"  raw: {Preview(result.RawResponse, 320)}");
            }

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

    private static string FormatUsage(Agentica.Clients.Llm.LlmUsage? usage) =>
        usage is null
            ? "none"
            : $"prompt={usage.PromptTokens?.ToString() ?? "?"} output={usage.OutputTokens?.ToString() ?? "?"} thinking={usage.ThinkingTokens?.ToString() ?? "?"} total={usage.TotalTokens?.ToString() ?? "?"}";

    private static string Preview(string value, int maxCharacters)
    {
        var compact = value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= maxCharacters
            ? compact
            : compact[..maxCharacters] + "...";
    }

    private static void WriteGameRecord(
        RunLogWriter? runLog,
        ChessQuestSession session,
        DateTimeOffset createdAt)
    {
        if (runLog?.IsEnabled != true)
        {
            return;
        }

        runLog.WriteJson(
            ChessQuestGameRecordStore.GameRecordFileName,
            ChessQuestGameRecordStore.FromSession(session, createdAt));
    }

    private static void WriteContinuityCapsule(
        RunLogWriter? runLog,
        ChessQuestSession session,
        ChessQuestPhaseReport? latestPhaseReport = null,
        ChessQuestStrategyProjection? latestStrategyProjection = null)
    {
        if (runLog?.IsEnabled != true)
        {
            return;
        }

        runLog.WriteJson(
            ChessQuestGameRecordStore.ContinuityCapsuleFileName,
            ChessQuestContinuityCapsuleCompiler.Compile(
                session,
                latestPhaseReport,
                latestStrategyProjection));
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
