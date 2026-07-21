using System.Text.Json;
using Agentica.Lab.Scenarios.MazeQuest;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;

internal static class MazeQuestCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var board = new MazeQuestBoard();

        if (args.Count == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            PrintBoard(board);
            return 0;
        }

        if (string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            return await RunScenarioAsync(board, args.Skip(1).ToArray(), services).ConfigureAwait(false);
        }

        if (!string.Equals(args[0], "preview", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(args[0], "generate", StringComparison.OrdinalIgnoreCase))
        {
            services.PrintUsage();
            return 2;
        }

        var options = MazeQuestPreviewOptions.Parse(args.Skip(1).ToArray());
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            services.PrintUsage();
            return 2;
        }

        MazeQuestDescriptor descriptor;
        try
        {
            var questId = string.IsNullOrWhiteSpace(options.QuestId)
                ? board.ListQuests().First().QuestId
                : options.QuestId;
            descriptor = board.GetQuest(questId);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var seed = options.Seed ?? descriptor.DefaultSeed;
        var stage = new MazeQuestGenerator().Generate(
            descriptor,
            new MazeQuestGenerationOptions(
                QuestId: descriptor.QuestId,
                Seed: seed,
                Width: options.Width,
                Height: options.Height,
                VisibilityRadius: options.VisibilityRadius,
                QuestType: options.QuestType));
        var state = MazeQuestRunState.Create(stage);

        PrintPreview(stage, state, options);
        return 0;
    }

    private static async Task<int> RunScenarioAsync(
        IMazeQuestBoard board,
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var options = MazeQuestRunOptions.Parse(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            services.PrintUsage();
            return 2;
        }

        options = ResolvePlannerChoice(options);

        if (options.Planner == PlannerKind.Gemini && !services.GeminiCredentialsAvailable())
        {
            Console.Error.WriteLine("Gemini planner requested, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        MazeQuestDescriptor descriptor;
        try
        {
            var questId = string.IsNullOrWhiteSpace(options.QuestId)
                ? board.ListQuests().First().QuestId
                : options.QuestId;
            descriptor = board.GetQuest(questId);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var seed = options.Seed ?? descriptor.DefaultSeed;
        var stage = new MazeQuestGenerator().Generate(
            descriptor,
            new MazeQuestGenerationOptions(
                QuestId: descriptor.QuestId,
                Seed: seed,
                Width: options.Width,
                Height: options.Height,
                VisibilityRadius: options.VisibilityRadius,
                QuestType: options.QuestType));
        var session = new MazeQuestSession(stage);
        var initialState = session.CurrentRunState;
        var runObjective = BuildObjective(stage);
        var plannerContext = MazeQuestCapabilitySurfaceCompiler.BuildPlannerContext(
            stage,
            initialState,
            runObjective);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "mazequest", args);
        runLog?.WriteJson("mazequest-stage.json", stage);
        runLog?.WriteJson("mazequest-initial-public-snapshot.json", MazeQuestAnalyzer.BuildPublicSnapshot(stage, initialState));
        runLog?.WriteJson("mazequest-initial-cockpit-frame.json", MazeQuestCockpitFrameCompiler.BuildFrame(stage, initialState, session.Turns));
        if (runLog is not null &&
            plannerContext.TryGetValue(MazeQuestCapabilitySurfaceCompiler.ContextKey, out var harnessContext) &&
            harnessContext is not null)
        {
            runLog.WriteJson("mazequest-initial-agentic-harness-context.json", harnessContext);
        }

        using var runCancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            runCancellation.Cancel();
        };

        MazeQuestWatchControl? watchControl = null;
        IEventSink eventSink;
        if (options.Watch)
        {
            watchControl = new MazeQuestWatchControl(runCancellation);
            watchControl.Start();
            Action<MazeQuestTurnEnvelope>? turnRecorder = runLog is null ? null : turn => runLog.WriteTurn(turn);
            var watchSink = new MazeQuestWatchEventSink(
                session,
                CreateNarrator(options, services),
                watchControl,
                runCancellation.Token,
                options.TurnJson,
                options.WatchDelayMilliseconds,
                turnRecorder);
            watchSink.PrintOpening(runObjective);
            eventSink = services.CreateEventSink(watchSink, runLog);
        }
        else
        {
            PrintOpening(stage);
            eventSink = services.CreateEventSink(new MazeQuestTraceEventSink(session, runObjective), runLog);
        }

        var planner = options.Planner == PlannerKind.Deterministic
            ? new MazeQuestDeterministicPlanner(session)
            : services.CreatePlanner(new CliRunOptions(
                runObjective,
                options.Planner,
                options.ModelId,
                options.ThinkingBudget,
                options.IncludeThoughts,
                null,
                options.PlanningMode,
                options.MaxBlockedRetries,
                LogRun: false,
                LogDir: null,
                IsValid: true,
                Error: null));

        var runner = new AgenticaRunner(
            planner: planner,
            toolCatalog: MazeQuestTools.CreateCatalog(session),
            eventSink: eventSink,
            outcomeReporter: new MazeQuestOutcomeReporter(),
            policy: new ExecutionPolicy(
                MaxSteps: 240,
                MaxRefinements: 240,
                Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
                PlanningMode: options.PlanningMode,
                MaxPlanContinuations: 16,
                PlanningContext: new PlanningContextOptions(MaxRecentObservations: 8, MaxRecentReceipts: 8),
                MaxBlockedRetries: options.MaxBlockedRetries,
                SecurityPolicy: LabSecurityPolicy.ForPlanner(planner)),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("mazequest.objective_completed"),
            planningFrameProjector: new MazeQuestCockpitFrameProjector(session),
            userFacingReasonProjector: MazeQuestUserFacingReasonProjector.Instance);

        Console.CancelKeyPress += cancelHandler;
        OutcomeEnvelope envelope;
        try
        {
            envelope = await runner.RunAsync(
                new RunRequest(
                    runObjective,
                    RequestOrigin.User,
                    plannerContext),
                runCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            watchControl?.Dispose();
        }

        services.PrintEnvelope(envelope);
        services.FinishRunLog(runLog, envelope);
        return envelope.Outcome.Status == RunOutcomeStatus.Succeeded ? 0 : 1;
    }

    internal static string BuildObjective(MazeQuestStage stage) =>
        $"""
        MazeQuest: {stage.Quest.Title}
        Objective: {stage.Quest.Objective}
        Quest type: {stage.Quest.QuestType}

        Planner contract:
        - Produce only the next safe step or a very small safe slice.
        - Do not produce a full route from partial fog-of-war data.
        - Do not invent hidden map state, object ids, inventory, receipts, artifacts, or success.
        - Before mutation, use public observations from maze.get_state, maze.render_map, maze.sense_objective, and maze.evaluate_moves.
        - Use objectiveBoard to weigh required objectives, optional objectives, priority, resource rewards, and current resource risk.
        - moveEvaluations provide local legality, cost, risk, and frontier facts; they do not identify the best route.
        - Treat repeated A/B movement, repeated frontierGain = 0, unchanged objective signal, and revisiting the same public cells as stagnation evidence.
        - Do not prefer a safe move if it returns to a known cul-de-sac or repeated cell pair that has already failed to improve objective progress.
        - If all safe moves are no-progress and the host-projected resourceRisk allows it, choose a bounded-risk move when it is the only non-repeating legal branch.
        - When trapped between a safe dead-end and a risky branch, name the tradeoff in ExecutionIntent.Rationale.
        - If recent steps alternate directions, first call maze.analyze_progress or maze.evaluate_escape_moves, then choose a move that changes the loop state.
        - Rest only when health, energy, cost, or visible risk makes the next action unsafe and rest enables a specific next move; do not rest as filler or as a substitute for route change.
        - Treat cockpitFrame and agenticHarness.activeCapabilitySurface as host-compiled public planner context for the current turn.
        - Request context keys such as agentica.* are not tools unless their exact id appears in the tool catalog.
        - For action inputs, copy exact values from legalActions or the tool input schema. Use lowercase cardinal directions only: north, east, south, west.
        - To move, call maze.move with input key direction set to north, east, south, or west.
        - To take an object, call maze.take with input key objectId set to the visible current-cell object id.
        - To use, unlock, activate, or deliver to a target, call maze.use with input key targetId set to the visible current-cell object id and include item only when the legal action or required item says to.
        - If a receipt is Refused, treat its reason/blocker and legalActions as the next planning facts, then recover with a query or legal alternative.
        - Do not call {MazeQuestToolIds.CompleteObjective} until legalActions includes it or all required non-complete objectives are receipt-backed as complete.
        - The run succeeds only when {MazeQuestToolIds.CompleteObjective} emits the mazequest.objective_completed artifact.
        """;

    private static IMazeQuestTurnNarrator CreateNarrator(
        MazeQuestRunOptions options,
        CliCommandServices services)
    {
        if (options.Narrator == MazeQuestNarratorKind.Off)
        {
            return NullMazeQuestTurnNarrator.Instance;
        }

        if (options.Narrator == MazeQuestNarratorKind.Gemini)
        {
            if (!services.GeminiCredentialsAvailable())
            {
                Console.Error.WriteLine("Gemini narrator requested, but no Gemini API key was configured. Falling back to deterministic narration.");
                return DeterministicMazeQuestTurnNarrator.Instance;
            }

            var modelId = options.NarrationModelId ?? options.ModelId ?? GeminiModelId.Flash25;
            return new LlmMazeQuestTurnNarrator(
                new GeminiLlmClient(GeminiClientOptions.FromEnvironment(modelId)),
                modelId);
        }

        return DeterministicMazeQuestTurnNarrator.Instance;
    }

    private static MazeQuestRunOptions ResolvePlannerChoice(MazeQuestRunOptions options)
    {
        if (options.PlannerSpecified || Console.IsInputRedirected)
        {
            return options;
        }

        while (true)
        {
            Console.Write("Run Gemini-backed MazeQuest planner test? [y/N]: ");
            var answer = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(answer) ||
                string.Equals(answer, "n", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer, "no", StringComparison.OrdinalIgnoreCase))
            {
                return options with { Planner = PlannerKind.Deterministic };
            }

            if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                return options with { Planner = PlannerKind.Gemini };
            }

            Console.WriteLine("Please answer y or n.");
        }
    }

    private static void PrintBoard(IMazeQuestBoard board)
    {
        Console.WriteLine("Available Maze Quests:");
        var quests = board.ListQuests();
        for (var index = 0; index < quests.Count; index++)
        {
            var quest = quests[index];
            Console.WriteLine($"{index + 1}. {quest.Title} ({quest.QuestId})");
            Console.WriteLine($"   - {quest.Description}");
            Console.WriteLine($"   - Difficulty: {quest.Difficulty}");
            Console.WriteLine($"   - Estimated Steps: {quest.EstimatedSteps}");
            Console.WriteLine($"   - Default Seed: {quest.DefaultSeed}");
        }
    }

    private static void PrintOpening(MazeQuestStage stage)
    {
        Console.WriteLine($"Agent has accepted MazeQuest: \"{stage.Quest.Title}\"");
        Console.WriteLine($"Objective: {stage.Quest.Objective}");
        Console.WriteLine($"Quest Type: {stage.Quest.QuestType}");
        Console.WriteLine($"Coverage: {string.Join(", ", stage.Quest.CoverageTags)}");
        Console.WriteLine($"Seed: {stage.Seed}");
        Console.WriteLine();
    }

    private static void PrintPreview(
        MazeQuestStage stage,
        MazeQuestRunState state,
        MazeQuestPreviewOptions options)
    {
        Console.WriteLine($"Generated MazeQuest: \"{stage.Quest.Title}\"");
        Console.WriteLine($"Objective: {stage.Quest.Objective}");
        Console.WriteLine($"Quest Type: {stage.Quest.QuestType}");
        Console.WriteLine($"Coverage: {string.Join(", ", stage.Quest.CoverageTags)}");
        Console.WriteLine($"Seed: {stage.Seed}");
        Console.WriteLine($"Size: {stage.Grid.Width}x{stage.Grid.Height}");
        Console.WriteLine();
        Console.WriteLine("Objective Chain:");
        foreach (var objective in stage.Quest.Objectives)
        {
            Console.WriteLine($"  - {objective.ObjectiveId}: {objective.Description}");
        }

        Console.WriteLine();
        Console.WriteLine(MazeQuestRenderer.RenderFog(stage, state));

        var signal = MazeQuestAnalyzer.SenseObjective(stage, state);
        Console.WriteLine("Objective Signal:");
        Console.WriteLine($"  objective={signal.ObjectiveId} bearing={signal.Bearing} distance={signal.DistanceBand} warmth={signal.Warmth:0.00} confidence={signal.Confidence:0.00}");
        Console.WriteLine();

        Console.WriteLine("Local Move Evaluation:");
        foreach (var move in MazeQuestAnalyzer.EvaluateMoves(stage, state))
        {
            var legal = move.Legal ? "legal" : "blocked";
            Console.WriteLine($"  {move.Direction,-5} {legal,-7} to=({move.To.X},{move.To.Y}) reason={move.Reason} delta={move.ObjectiveDelta} cost={move.TerrainCost} risk={move.VisibleRisk:0.00} frontier={move.FrontierGain}");
        }

        if (options.Reveal)
        {
            Console.WriteLine();
            Console.WriteLine("Developer Reveal:");
            Console.WriteLine($"  start=({stage.Placements.Start.X},{stage.Placements.Start.Y}) primary=({stage.Placements.Key.X},{stage.Placements.Key.Y}) secondary=({stage.Placements.Gate.X},{stage.Placements.Gate.Y}) exit=({stage.Placements.Exit.X},{stage.Placements.Exit.Y})");
            Console.WriteLine("  objects:");
            foreach (var questObject in stage.Objects.Values.OrderBy(item => item.Point.Y).ThenBy(item => item.Point.X))
            {
                Console.WriteLine($"    {questObject.ObjectId,-16} {questObject.Kind,-16} at=({questObject.Point.X},{questObject.Point.Y}) name=\"{questObject.DisplayName}\" required={questObject.RequiredItem ?? "none"}");
            }

            Console.WriteLine(MazeQuestRenderer.RenderRevealed(stage, state));
        }

        if (options.Json)
        {
            Console.WriteLine();
            Console.WriteLine("--- MazeQuest Public Snapshot ---");
            Console.WriteLine(JsonSerializer.Serialize(MazeQuestAnalyzer.BuildPublicSnapshot(stage, state), JsonOptions.Create()));
        }
    }
}
