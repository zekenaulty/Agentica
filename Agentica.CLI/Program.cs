using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.CLI.Configuration;
using Agentica.CLI.Logging;
using Agentica.CLI.Scenarios.MazeQuest;
using Agentica.CLI.Scenarios.Quest;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;
using IWorkflowPlanner = Agentica.Planning.IWorkflowPlanner;

LocalEnvironmentFile.LoadForCurrentProcess();

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

if (string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
{
    return await RunDefaultAsync(args.Skip(1).ToArray());
}

if (string.Equals(args[0], "quest", StringComparison.OrdinalIgnoreCase))
{
    return await RunQuestAsync(args.Skip(1).ToArray());
}

if (string.Equals(args[0], "mazequest", StringComparison.OrdinalIgnoreCase))
{
    return await RunMazeQuestAsync(args.Skip(1).ToArray());
}

PrintUsage();
return 2;

static async Task<int> RunDefaultAsync(IReadOnlyList<string> args)
{
    var options = CliRunOptions.Parse(args);
    if (!options.IsValid)
    {
        Console.Error.WriteLine(options.Error);
        PrintUsage();
        return 2;
    }

    if (options.Planner == PlannerKind.Gemini && !GeminiCredentialsAvailable())
    {
        Console.Error.WriteLine("Gemini planner requested, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
        return 2;
    }

    var planner = CreatePlanner(options);
    var runLog = CreateRunLog(options.LogRun, options.LogDir, "run", args);
    var eventSink = CreateEventSink(new ConsoleEventSink(), runLog);

    var runner = new AgenticaRunner(
        planner: planner,
        toolCatalog: DemoTools.CreateCatalog(),
        eventSink: eventSink,
        outcomeReporter: new DeterministicOutcomeReporter(),
        policy: new ExecutionPolicy(
            MaxSteps: 10,
            MaxRefinements: 2,
            PlanningMode: options.PlanningMode,
            MaxBlockedRetries: options.MaxBlockedRetries));

    var envelope = await runner.RunAsync(
        new RunRequest(options.Objective, RequestOrigin.User),
        CancellationToken.None);

    PrintEnvelope(envelope);
    FinishRunLog(runLog, envelope);
    return envelope.Outcome.Status == RunOutcomeStatus.Succeeded ? 0 : 1;
}

static async Task<int> RunQuestAsync(IReadOnlyList<string> args)
{
    var board = new InMemoryQuestBoard();

    if (args.Count == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
    {
        PrintQuestBoard(board);
        return 0;
    }

    if (!string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
    {
        PrintUsage();
        return 2;
    }

    var options = QuestRunOptions.Parse(args.Skip(1).ToArray());
    if (!options.IsValid)
    {
        Console.Error.WriteLine(options.Error);
        PrintUsage();
        return 2;
    }

    if (options.Planner == PlannerKind.Gemini && !GeminiCredentialsAvailable())
    {
        Console.Error.WriteLine("Gemini planner requested, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
        return 2;
    }

    QuestDefinition definition;
    try
    {
        definition = board.Load(options.QuestId);
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine(exception.Message);
        return 2;
    }

    var session = new QuestSession(definition);
    var planner = options.Planner == PlannerKind.Deterministic
        ? new QuestDeterministicPlanner(options.Route)
        : CreatePlanner(new CliRunOptions(
            definition.Objective,
            options.Planner,
            options.ModelId,
            options.ThinkingBudget,
            options.IncludeThoughts,
            options.PlanningMode,
            options.MaxBlockedRetries,
            LogRun: false,
            LogDir: null,
            IsValid: true,
            Error: null));

    Console.WriteLine($"Agent has accepted quest: \"{definition.Title}\"");
    Console.WriteLine($"Objective: {definition.Objective}");
    Console.WriteLine();

    var runLog = CreateRunLog(options.LogRun, options.LogDir, "quest", args);
    runLog?.WriteJson("quest-definition.json", definition);
    var eventSink = CreateEventSink(new ConsoleEventSink(), runLog);

    var runner = new AgenticaRunner(
        planner: planner,
        toolCatalog: QuestTools.CreateCatalog(session),
        eventSink: eventSink,
        outcomeReporter: new QuestOutcomeReporter(),
        policy: new ExecutionPolicy(
            MaxSteps: 20,
            MaxRefinements: 12,
            PlanningMode: options.PlanningMode,
            MaxPlanContinuations: 4,
            MaxBlockedRetries: options.MaxBlockedRetries),
        completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("quest.objective_completed"));

    var envelope = await runner.RunAsync(
        new RunRequest($"Quest: {definition.Title}. Objective: {definition.Objective}", RequestOrigin.User),
        CancellationToken.None);

    PrintEnvelope(envelope);
    FinishRunLog(runLog, envelope);
    return envelope.Outcome.Status == RunOutcomeStatus.Succeeded ? 0 : 1;
}

static void PrintQuestBoard(IQuestBoard board)
{
    Console.WriteLine("Available Quests:");
    var quests = board.ListQuests();
    for (var index = 0; index < quests.Count; index++)
    {
        var quest = quests[index];
        Console.WriteLine($"{index + 1}. {quest.Title} ({quest.QuestId})");
        Console.WriteLine($"   - {quest.Description}");
        Console.WriteLine($"   - Difficulty: {quest.Difficulty}");
        Console.WriteLine($"   - Estimated Steps: {quest.EstimatedSteps}");
    }
}

static async Task<int> RunMazeQuestAsync(IReadOnlyList<string> args)
{
    var board = new MazeQuestBoard();

    if (args.Count == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
    {
        PrintMazeQuestBoard(board);
        return 0;
    }

    if (string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
    {
        return await RunMazeQuestScenarioAsync(board, args.Skip(1).ToArray());
    }

    if (!string.Equals(args[0], "preview", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(args[0], "generate", StringComparison.OrdinalIgnoreCase))
    {
        PrintUsage();
        return 2;
    }

    var options = MazeQuestPreviewOptions.Parse(args.Skip(1).ToArray());
    if (!options.IsValid)
    {
        Console.Error.WriteLine(options.Error);
        PrintUsage();
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

    PrintMazeQuestPreview(stage, state, options);
    return 0;
}

static async Task<int> RunMazeQuestScenarioAsync(IMazeQuestBoard board, IReadOnlyList<string> args)
{
    var options = MazeQuestRunOptions.Parse(args);
    if (!options.IsValid)
    {
        Console.Error.WriteLine(options.Error);
        PrintUsage();
        return 2;
    }

    options = ResolveMazeQuestPlannerChoice(options);

    if (options.Planner == PlannerKind.Gemini && !GeminiCredentialsAvailable())
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
    var runObjective = BuildMazeQuestObjective(stage);
    var runLog = CreateRunLog(options.LogRun, options.LogDir, "mazequest", args);
    runLog?.WriteJson("mazequest-stage.json", stage);
    runLog?.WriteJson("mazequest-initial-public-snapshot.json", MazeQuestAnalyzer.BuildPublicSnapshot(stage, initialState));

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
            CreateMazeQuestNarrator(options),
            watchControl,
            runCancellation.Token,
            options.TurnJson,
            options.WatchDelayMilliseconds,
            turnRecorder);
        watchSink.PrintOpening(runObjective);
        eventSink = CreateEventSink(watchSink, runLog);
    }
    else
    {
        Console.WriteLine($"Agent has accepted MazeQuest: \"{stage.Quest.Title}\"");
        Console.WriteLine($"Objective: {stage.Quest.Objective}");
        Console.WriteLine($"Quest Type: {stage.Quest.QuestType}");
        Console.WriteLine($"Coverage: {string.Join(", ", stage.Quest.CoverageTags)}");
        Console.WriteLine($"Seed: {stage.Seed}");
        Console.WriteLine();
        eventSink = CreateEventSink(new ConsoleEventSink(), runLog);
    }

    var planner = options.Planner == PlannerKind.Deterministic
        ? new MazeQuestDeterministicPlanner(session)
        : CreatePlanner(new CliRunOptions(
            runObjective,
            options.Planner,
            options.ModelId,
            options.ThinkingBudget,
            options.IncludeThoughts,
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
            MaxSteps: 120,
            MaxRefinements: 120,
            Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
            PlanningMode: options.PlanningMode,
            MaxPlanContinuations: 16,
            PlanningContext: new PlanningContextOptions(MaxRecentObservations: 8, MaxRecentReceipts: 8),
            MaxBlockedRetries: options.MaxBlockedRetries),
        completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("mazequest.objective_completed"));

    Console.CancelKeyPress += cancelHandler;
    OutcomeEnvelope envelope;
    try
    {
        envelope = await runner.RunAsync(
            new RunRequest(
                runObjective,
                RequestOrigin.User,
                MazeQuestAnalyzer.BuildPublicSnapshot(stage, initialState)),
            runCancellation.Token);
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        watchControl?.Dispose();
    }

    PrintEnvelope(envelope);
    FinishRunLog(runLog, envelope);
    return envelope.Outcome.Status == RunOutcomeStatus.Succeeded ? 0 : 1;
}

static string BuildMazeQuestObjective(MazeQuestStage stage) =>
    $"""
    MazeQuest: {stage.Quest.Title}
    Objective: {stage.Quest.Objective}
    Quest type: {stage.Quest.QuestType}

    Planner contract:
    - Produce only the next safe step or a very small safe slice.
    - Do not produce a full route from partial fog-of-war data.
    - Do not invent hidden map state, object ids, inventory, receipts, artifacts, or success.
    - Before mutation, use public observations from maze.get_state, maze.render_map, maze.sense_objective, and maze.evaluate_moves.
    - For action inputs, copy exact values from legalActions or the tool input schema. Use lowercase cardinal directions only: north, east, south, west.
    - To move, call maze.move with input key direction set to north, east, south, or west.
    - To take an object, call maze.take with input key objectId set to the visible current-cell object id.
    - To use, unlock, activate, or deliver to a target, call maze.use with input key targetId set to the visible current-cell object id and include item only when the legal action or required item says to.
    - If a receipt is Refused, treat its reason/blocker and legalActions as the next planning facts, then recover with a query or legal alternative.
    - Do not call {MazeQuestToolIds.CompleteObjective} until legalActions includes it or all non-complete objectives are receipt-backed as complete.
    - The run succeeds only when {MazeQuestToolIds.CompleteObjective} emits the mazequest.objective_completed artifact.
    """;

static IMazeQuestTurnNarrator CreateMazeQuestNarrator(MazeQuestRunOptions options)
{
    if (options.Narrator == MazeQuestNarratorKind.Off)
    {
        return NullMazeQuestTurnNarrator.Instance;
    }

    if (options.Narrator == MazeQuestNarratorKind.Gemini)
    {
        if (!GeminiCredentialsAvailable())
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

static MazeQuestRunOptions ResolveMazeQuestPlannerChoice(MazeQuestRunOptions options)
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

static void PrintMazeQuestBoard(IMazeQuestBoard board)
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

static void PrintMazeQuestPreview(
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

static void PrintEnvelope(OutcomeEnvelope envelope)
{
    Console.WriteLine();
    Console.WriteLine("--- OutcomeEnvelope ---");
    Console.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions.Create()));
}

static RunLogWriter? CreateRunLog(
    bool enabled,
    string? logDir,
    string scenario,
    IReadOnlyList<string> args)
{
    if (!enabled)
    {
        return null;
    }

    var writer = RunLogWriter.Create(logDir, scenario);
    writer.WriteMetadata(scenario, args);
    Console.WriteLine($"Run log: {writer.DirectoryPath}");
    Console.WriteLine();
    return writer;
}

static IEventSink CreateEventSink(IEventSink inner, RunLogWriter? runLog) =>
    runLog is null
        ? inner
        : new RunLogEventSink(inner, runLog);

static void FinishRunLog(RunLogWriter? runLog, OutcomeEnvelope envelope)
{
    if (runLog is null)
    {
        return;
    }

    runLog.WriteOutcome(envelope);
    Console.WriteLine();
    Console.WriteLine($"Run log written: {runLog.DirectoryPath}");
}

static IWorkflowPlanner CreatePlanner(CliRunOptions options)
{
    if (options.Planner == PlannerKind.Deterministic)
    {
        return new Agentica.Planning.DeterministicWorkflowPlanner();
    }

    var thinkingOptions = options.ThinkingBudget switch
    {
        null when options.IncludeThoughts => new LlmThinkingOptions(IncludeThoughts: true),
        null => null,
        "dynamic" => LlmThinkingOptions.Dynamic(options.IncludeThoughts),
        "off" => LlmThinkingOptions.Off(options.IncludeThoughts),
        "0" => LlmThinkingOptions.Off(options.IncludeThoughts),
        var value when int.TryParse(value, out var tokens) && tokens > 0 =>
            LlmThinkingOptions.Budget(tokens, options.IncludeThoughts),
        _ => throw new InvalidOperationException($"Invalid thinking budget '{options.ThinkingBudget}'.")
    };

    var modelId = options.ModelId ?? GeminiModelId.Flash25;
    var llmClient = new RetryingLlmClient(
        new GeminiLlmClient(GeminiClientOptions.FromEnvironment(modelId)),
        new LlmRetryOptions(CallTimeout: TimeSpan.FromMinutes(10)));
    return new LlmWorkflowPlanner(
        llmClient,
        new LlmPlannerOptions(
            ModelId: modelId,
            GenerationOptions: new LlmGenerationOptions(
                Temperature: 0,
                MaxOutputTokens: 4096,
                Thinking: thinkingOptions)));
}

static bool GeminiCredentialsAvailable()
{
    if (string.Equals(
        Environment.GetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI"),
        "true",
        StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GOOGLE_API_KEY"));
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  Agentica.CLI run \"<objective>\" [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI quest list");
    Console.Error.WriteLine("  Agentica.CLI quest run <quest-id> [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--route observe|blocked] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI mazequest list");
    Console.Error.WriteLine("  Agentica.CLI mazequest preview <quest-id> [--seed <number>] [--type unlock|collect|delivery|explore|activate|puzzle|rescue|resource] [--width <odd 7-11>] [--height <odd 7-11>] [--visibility <1-4>] [--reveal] [--json]");
    Console.Error.WriteLine("  Agentica.CLI mazequest run [quest-id] [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--seed <number>] [--type unlock|collect|delivery|explore|activate|puzzle|rescue|resource] [--watch] [--narrator off|deterministic|gemini] [--turn-json] [--watch-delay-ms <ms>] [--timeout-seconds <seconds>] [--model <model-id>] [--narration-model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--include-thoughts] [--log-run] [--log-dir <path>]");
}

internal static class JsonOptions
{
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

internal static class CliParsing
{
    public static bool TryParsePlanningMode(string value, out PlanningMode planningMode)
    {
        switch (value.ToLowerInvariant())
        {
            case "stepwise":
                planningMode = PlanningMode.Stepwise;
                return true;
            case "query-blocker":
            case "query-and-blocker":
            case "query-and-blocker-driven":
                planningMode = PlanningMode.QueryAndBlockerDriven;
                return true;
            case "blocker":
            case "blocker-driven":
                planningMode = PlanningMode.BlockerDriven;
                return true;
            case "plan-only":
            case "planonly":
                planningMode = PlanningMode.PlanOnly;
                return true;
            default:
                planningMode = PlanningMode.Stepwise;
                return false;
        }
    }
}

internal enum PlannerKind
{
    Deterministic,
    Gemini
}

internal sealed record CliRunOptions(
    string Objective,
    PlannerKind Planner,
    string? ModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    PlanningMode PlanningMode,
    int MaxBlockedRetries,
    bool LogRun,
    string? LogDir,
    bool IsValid,
    string? Error)
{
    public static CliRunOptions Parse(IReadOnlyList<string> args)
    {
        var objectiveParts = new List<string>();
        var planner = PlannerKind.Deterministic;
        string? modelId = null;
        string? thinkingBudget = null;
        var includeThoughts = false;
        var planningMode = PlanningMode.Stepwise;
        var maxBlockedRetries = 2;
        var logRun = false;
        string? logDir = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                objectiveParts.Add(arg);
                continue;
            }

            switch (arg)
            {
                case "--planner":
                    if (!TryReadValue(args, ref index, out var plannerValue))
                    {
                        return Invalid("Missing value for --planner.");
                    }

                    if (!Enum.TryParse<PlannerKind>(plannerValue, ignoreCase: true, out planner))
                    {
                        return Invalid($"Unknown planner '{plannerValue}'.");
                    }

                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, out modelId))
                    {
                        return Invalid("Missing value for --model.");
                    }

                    break;

                case "--thinking-budget":
                    if (!TryReadValue(args, ref index, out thinkingBudget))
                    {
                        return Invalid("Missing value for --thinking-budget.");
                    }

                    thinkingBudget = thinkingBudget.ToLowerInvariant();
                    if (thinkingBudget is not "dynamic" and not "off" &&
                        (!int.TryParse(thinkingBudget, out var tokens) || tokens < 0))
                    {
                        return Invalid($"Invalid thinking budget '{thinkingBudget}'.");
                    }

                    break;

                case "--planning-mode":
                    if (!TryReadValue(args, ref index, out var planningModeValue))
                    {
                        return Invalid("Missing value for --planning-mode.");
                    }

                    if (!CliParsing.TryParsePlanningMode(planningModeValue, out planningMode))
                    {
                        return Invalid($"Unknown planning mode '{planningModeValue}'.");
                    }

                    break;

                case "--max-blocked-retries":
                    if (!TryReadValue(args, ref index, out var maxBlockedRetriesValue) ||
                        !int.TryParse(maxBlockedRetriesValue, out maxBlockedRetries) ||
                        maxBlockedRetries < 0)
                    {
                        return Invalid("Missing or invalid value for --max-blocked-retries.");
                    }

                    break;

                case "--include-thoughts":
                    includeThoughts = true;
                    break;

                case "--log-run":
                    logRun = true;
                    break;

                case "--log-dir":
                    if (!TryReadValue(args, ref index, out logDir))
                    {
                        return Invalid("Missing value for --log-dir.");
                    }

                    break;

                default:
                    return Invalid($"Unknown option '{arg}'.");
            }
        }

        var objective = string.Join(' ', objectiveParts).Trim();
        if (string.IsNullOrWhiteSpace(objective))
        {
            return Invalid("Objective is required.");
        }

        return new CliRunOptions(objective, planner, modelId, thinkingBudget, includeThoughts, planningMode, maxBlockedRetries, logRun, logDir, true, null);
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static CliRunOptions Invalid(string error) =>
        new(string.Empty, PlannerKind.Deterministic, null, null, false, PlanningMode.Stepwise, 2, false, null, false, error);
}

internal sealed record QuestRunOptions(
    string QuestId,
    PlannerKind Planner,
    string? ModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    PlanningMode PlanningMode,
    QuestDeterministicPlannerMode Route,
    int MaxBlockedRetries,
    bool LogRun,
    string? LogDir,
    bool IsValid,
    string? Error)
{
    public static QuestRunOptions Parse(IReadOnlyList<string> args)
    {
        string? questId = null;
        var planner = PlannerKind.Deterministic;
        string? modelId = null;
        string? thinkingBudget = null;
        var includeThoughts = false;
        var planningMode = PlanningMode.Stepwise;
        var route = QuestDeterministicPlannerMode.ObserveThenSolve;
        var maxBlockedRetries = 2;
        var logRun = false;
        string? logDir = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (questId is not null)
                {
                    return Invalid($"Unexpected quest argument '{arg}'.");
                }

                questId = arg;
                continue;
            }

            switch (arg)
            {
                case "--planner":
                    if (!TryReadValue(args, ref index, out var plannerValue))
                    {
                        return Invalid("Missing value for --planner.");
                    }

                    if (!Enum.TryParse<PlannerKind>(plannerValue, ignoreCase: true, out planner))
                    {
                        return Invalid($"Unknown planner '{plannerValue}'.");
                    }

                    break;

                case "--route":
                    if (!TryReadValue(args, ref index, out var routeValue))
                    {
                        return Invalid("Missing value for --route.");
                    }

                    var normalizedRoute = routeValue.ToLowerInvariant();
                    route = normalizedRoute switch
                    {
                        "observe" => QuestDeterministicPlannerMode.ObserveThenSolve,
                        "normal" => QuestDeterministicPlannerMode.ObserveThenSolve,
                        "blocked" => QuestDeterministicPlannerMode.TryLockedGateFirst,
                        "try-locked-gate-first" => QuestDeterministicPlannerMode.TryLockedGateFirst,
                        _ => QuestDeterministicPlannerMode.ObserveThenSolve
                    };

                    if (normalizedRoute is not "observe" and not "normal" and not "blocked" and not "try-locked-gate-first")
                    {
                        return Invalid($"Unknown quest route '{routeValue}'.");
                    }

                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, out modelId))
                    {
                        return Invalid("Missing value for --model.");
                    }

                    break;

                case "--thinking-budget":
                    if (!TryReadValue(args, ref index, out thinkingBudget))
                    {
                        return Invalid("Missing value for --thinking-budget.");
                    }

                    thinkingBudget = thinkingBudget.ToLowerInvariant();
                    if (thinkingBudget is not "dynamic" and not "off" &&
                        (!int.TryParse(thinkingBudget, out var tokens) || tokens < 0))
                    {
                        return Invalid($"Invalid thinking budget '{thinkingBudget}'.");
                    }

                    break;

                case "--planning-mode":
                    if (!TryReadValue(args, ref index, out var planningModeValue))
                    {
                        return Invalid("Missing value for --planning-mode.");
                    }

                    if (!CliParsing.TryParsePlanningMode(planningModeValue, out planningMode))
                    {
                        return Invalid($"Unknown planning mode '{planningModeValue}'.");
                    }

                    break;

                case "--max-blocked-retries":
                    if (!TryReadValue(args, ref index, out var maxBlockedRetriesValue) ||
                        !int.TryParse(maxBlockedRetriesValue, out maxBlockedRetries) ||
                        maxBlockedRetries < 0)
                    {
                        return Invalid("Missing or invalid value for --max-blocked-retries.");
                    }

                    break;

                case "--include-thoughts":
                    includeThoughts = true;
                    break;

                case "--log-run":
                    logRun = true;
                    break;

                case "--log-dir":
                    if (!TryReadValue(args, ref index, out logDir))
                    {
                        return Invalid("Missing value for --log-dir.");
                    }

                    break;

                default:
                    return Invalid($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(questId))
        {
            return Invalid("Quest id is required.");
        }

        return new QuestRunOptions(
            questId,
            planner,
            modelId,
            thinkingBudget,
            includeThoughts,
            planningMode,
            route,
            maxBlockedRetries,
            logRun,
            logDir,
            IsValid: true,
            Error: null);
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static QuestRunOptions Invalid(string error) =>
        new(
            string.Empty,
            PlannerKind.Deterministic,
            null,
            null,
            false,
            PlanningMode.Stepwise,
            QuestDeterministicPlannerMode.ObserveThenSolve,
            MaxBlockedRetries: 2,
            LogRun: false,
            LogDir: null,
            IsValid: false,
            Error: error);
}

internal sealed record MazeQuestPreviewOptions(
    string QuestId,
    int? Seed,
    MazeQuestArchetype? QuestType,
    int Width,
    int Height,
    int VisibilityRadius,
    bool Reveal,
    bool Json,
    bool IsValid,
    string? Error)
{
    public static MazeQuestPreviewOptions Parse(IReadOnlyList<string> args)
    {
        string? questId = null;
        int? seed = null;
        MazeQuestArchetype? questType = null;
        var width = 11;
        var height = 11;
        var visibilityRadius = 2;
        var reveal = false;
        var json = false;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (questId is not null)
                {
                    return Invalid($"Unexpected maze quest argument '{arg}'.");
                }

                questId = arg;
                continue;
            }

            switch (arg)
            {
                case "--seed":
                    if (!TryReadInt(args, ref index, out var seedValue))
                    {
                        return Invalid("Missing or invalid value for --seed.");
                    }

                    seed = seedValue;
                    break;

                case "--type":
                    if (!TryReadValue(args, ref index, out var questTypeValue))
                    {
                        return Invalid("Missing value for --type.");
                    }

                    if (!TryParseQuestType(questTypeValue, out var parsedQuestType))
                    {
                        return Invalid($"Unknown maze quest type '{questTypeValue}'.");
                    }

                    questType = parsedQuestType;
                    break;

                case "--width":
                    if (!TryReadInt(args, ref index, out width))
                    {
                        return Invalid("Missing or invalid value for --width.");
                    }

                    break;

                case "--height":
                    if (!TryReadInt(args, ref index, out height))
                    {
                        return Invalid("Missing or invalid value for --height.");
                    }

                    break;

                case "--visibility":
                    if (!TryReadInt(args, ref index, out visibilityRadius))
                    {
                        return Invalid("Missing or invalid value for --visibility.");
                    }

                    break;

                case "--reveal":
                    reveal = true;
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    return Invalid($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(questId))
        {
            return Invalid("Maze quest id is required.");
        }

        return new MazeQuestPreviewOptions(
            questId,
            seed,
            questType,
            width,
            height,
            visibilityRadius,
            reveal,
            json,
            IsValid: true,
            Error: null);
    }

    private static bool TryReadInt(IReadOnlyList<string> args, ref int index, out int value)
    {
        if (index + 1 >= args.Count ||
            args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
            !int.TryParse(args[index + 1], out value))
        {
            value = 0;
            return false;
        }

        index++;
        return true;
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    public static bool TryParseQuestType(string value, out MazeQuestArchetype questType)
    {
        switch (value.ToLowerInvariant())
        {
            case "unlock":
                questType = MazeQuestArchetype.Unlock;
                return true;
            case "collect":
                questType = MazeQuestArchetype.Collect;
                return true;
            case "delivery":
            case "courier":
                questType = MazeQuestArchetype.Delivery;
                return true;
            case "explore":
            case "discovery":
                questType = MazeQuestArchetype.Explore;
                return true;
            case "activate":
            case "interact":
                questType = MazeQuestArchetype.Activate;
                return true;
            case "puzzle":
            case "sequence":
                questType = MazeQuestArchetype.PuzzleSequence;
                return true;
            case "rescue":
            case "retrieve":
                questType = MazeQuestArchetype.Rescue;
                return true;
            case "resource":
            case "resource-route":
                questType = MazeQuestArchetype.ResourceRoute;
                return true;
            default:
                questType = MazeQuestArchetype.Unlock;
                return false;
        }
    }

    private static MazeQuestPreviewOptions Invalid(string error) =>
        new(
            string.Empty,
            null,
            null,
            Width: 11,
            Height: 11,
            VisibilityRadius: 2,
            Reveal: false,
            Json: false,
            IsValid: false,
            Error: error);
}

internal sealed record MazeQuestRunOptions(
    string QuestId,
    PlannerKind Planner,
    string? ModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    PlanningMode PlanningMode,
    bool PlannerSpecified,
    int MaxBlockedRetries,
    int? Seed,
    MazeQuestArchetype? QuestType,
    int Width,
    int Height,
    int VisibilityRadius,
    bool Watch,
    MazeQuestNarratorKind Narrator,
    bool TurnJson,
    int WatchDelayMilliseconds,
    int TimeoutSeconds,
    string? NarrationModelId,
    bool LogRun,
    string? LogDir,
    bool IsValid,
    string? Error)
{
    public static MazeQuestRunOptions Parse(IReadOnlyList<string> args)
    {
        string? questId = null;
        var planner = PlannerKind.Deterministic;
        var plannerSpecified = false;
        string? modelId = null;
        string? thinkingBudget = null;
        var includeThoughts = false;
        var planningMode = PlanningMode.Stepwise;
        var maxBlockedRetries = 2;
        int? seed = null;
        MazeQuestArchetype? questType = null;
        var width = 11;
        var height = 11;
        var visibilityRadius = 2;
        var watch = false;
        var narrator = MazeQuestNarratorKind.Deterministic;
        var turnJson = false;
        var watchDelayMilliseconds = 0;
        var timeoutSeconds = 120;
        string? narrationModelId = null;
        var logRun = false;
        string? logDir = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (questId is not null)
                {
                    return Invalid($"Unexpected maze quest argument '{arg}'.");
                }

                questId = arg;
                continue;
            }

            switch (arg)
            {
                case "--planner":
                    if (!TryReadValue(args, ref index, out var plannerValue))
                    {
                        return Invalid("Missing value for --planner.");
                    }

                    if (!Enum.TryParse<PlannerKind>(plannerValue, ignoreCase: true, out planner))
                    {
                        return Invalid($"Unknown planner '{plannerValue}'.");
                    }

                    plannerSpecified = true;
                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, out modelId))
                    {
                        return Invalid("Missing value for --model.");
                    }

                    break;

                case "--narration-model":
                    if (!TryReadValue(args, ref index, out narrationModelId))
                    {
                        return Invalid("Missing value for --narration-model.");
                    }

                    break;

                case "--thinking-budget":
                    if (!TryReadValue(args, ref index, out thinkingBudget))
                    {
                        return Invalid("Missing value for --thinking-budget.");
                    }

                    thinkingBudget = thinkingBudget.ToLowerInvariant();
                    if (thinkingBudget is not "dynamic" and not "off" &&
                        (!int.TryParse(thinkingBudget, out var tokens) || tokens < 0))
                    {
                        return Invalid($"Invalid thinking budget '{thinkingBudget}'.");
                    }

                    break;

                case "--include-thoughts":
                    includeThoughts = true;
                    break;

                case "--planning-mode":
                    if (!TryReadValue(args, ref index, out var planningModeValue))
                    {
                        return Invalid("Missing value for --planning-mode.");
                    }

                    if (!CliParsing.TryParsePlanningMode(planningModeValue, out planningMode))
                    {
                        return Invalid($"Unknown planning mode '{planningModeValue}'.");
                    }

                    break;

                case "--max-blocked-retries":
                    if (!TryReadValue(args, ref index, out var maxBlockedRetriesValue) ||
                        !int.TryParse(maxBlockedRetriesValue, out maxBlockedRetries) ||
                        maxBlockedRetries < 0)
                    {
                        return Invalid("Missing or invalid value for --max-blocked-retries.");
                    }

                    break;

                case "--seed":
                    if (!TryReadInt(args, ref index, out var seedValue))
                    {
                        return Invalid("Missing or invalid value for --seed.");
                    }

                    seed = seedValue;
                    break;

                case "--type":
                    if (!TryReadValue(args, ref index, out var questTypeValue))
                    {
                        return Invalid("Missing value for --type.");
                    }

                    if (!MazeQuestPreviewOptions.TryParseQuestType(questTypeValue, out var parsedQuestType))
                    {
                        return Invalid($"Unknown maze quest type '{questTypeValue}'.");
                    }

                    questType = parsedQuestType;
                    break;

                case "--width":
                    if (!TryReadInt(args, ref index, out width))
                    {
                        return Invalid("Missing or invalid value for --width.");
                    }

                    break;

                case "--height":
                    if (!TryReadInt(args, ref index, out height))
                    {
                        return Invalid("Missing or invalid value for --height.");
                    }

                    break;

                case "--visibility":
                    if (!TryReadInt(args, ref index, out visibilityRadius))
                    {
                        return Invalid("Missing or invalid value for --visibility.");
                    }

                    break;

                case "--watch":
                    watch = true;
                    break;

                case "--narrator":
                    if (!TryReadValue(args, ref index, out var narratorValue))
                    {
                        return Invalid("Missing value for --narrator.");
                    }

                    if (!TryParseNarrator(narratorValue, out narrator))
                    {
                        return Invalid($"Unknown narrator '{narratorValue}'.");
                    }

                    break;

                case "--turn-json":
                    turnJson = true;
                    break;

                case "--watch-delay-ms":
                    if (!TryReadInt(args, ref index, out watchDelayMilliseconds) || watchDelayMilliseconds < 0)
                    {
                        return Invalid("Missing or invalid value for --watch-delay-ms.");
                    }

                    break;

                case "--timeout-seconds":
                    if (!TryReadInt(args, ref index, out timeoutSeconds) || timeoutSeconds <= 0)
                    {
                        return Invalid("Missing or invalid value for --timeout-seconds.");
                    }

                    break;

                case "--log-run":
                    logRun = true;
                    break;

                case "--log-dir":
                    if (!TryReadValue(args, ref index, out logDir))
                    {
                        return Invalid("Missing value for --log-dir.");
                    }

                    break;

                default:
                    return Invalid($"Unknown option '{arg}'.");
            }
        }

        return new MazeQuestRunOptions(
            questId ?? string.Empty,
            planner,
            modelId,
            thinkingBudget,
            includeThoughts,
            planningMode,
            plannerSpecified,
            maxBlockedRetries,
            seed,
            questType,
            width,
            height,
            visibilityRadius,
            watch,
            narrator,
            turnJson,
            watchDelayMilliseconds,
            timeoutSeconds,
            narrationModelId,
            logRun,
            logDir,
            IsValid: true,
            Error: null);
    }

    private static bool TryReadInt(IReadOnlyList<string> args, ref int index, out int value)
    {
        if (index + 1 >= args.Count ||
            args[index + 1].StartsWith("--", StringComparison.Ordinal) ||
            !int.TryParse(args[index + 1], out value))
        {
            value = 0;
            return false;
        }

        index++;
        return true;
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static bool TryParseNarrator(string value, out MazeQuestNarratorKind narrator)
    {
        switch (value.ToLowerInvariant())
        {
            case "off":
            case "none":
                narrator = MazeQuestNarratorKind.Off;
                return true;
            case "deterministic":
            case "host":
                narrator = MazeQuestNarratorKind.Deterministic;
                return true;
            case "gemini":
            case "llm":
                narrator = MazeQuestNarratorKind.Gemini;
                return true;
            default:
                narrator = MazeQuestNarratorKind.Deterministic;
                return false;
        }
    }

    private static MazeQuestRunOptions Invalid(string error) =>
        new(
            string.Empty,
            PlannerKind.Deterministic,
            null,
            null,
            false,
            PlanningMode.Stepwise,
            PlannerSpecified: false,
            MaxBlockedRetries: 2,
            Seed: null,
            QuestType: null,
            Width: 11,
            Height: 11,
            VisibilityRadius: 2,
            Watch: false,
            Narrator: MazeQuestNarratorKind.Deterministic,
            TurnJson: false,
            WatchDelayMilliseconds: 0,
            TimeoutSeconds: 120,
            NarrationModelId: null,
            LogRun: false,
            LogDir: null,
            IsValid: false,
            Error: error);
}
