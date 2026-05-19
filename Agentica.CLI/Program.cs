using System.Text.Json;
using Agentica.CLI.Configuration;
using Agentica.CLI.Logging;
using Agentica.CLI.Scenarios.Campaign;
using Agentica.CLI.Scenarios.ChessQuest;
using Agentica.CLI.Scenarios.HexQuest;
using Agentica.CLI.Scenarios.MazeQuest;
using Agentica.CLI.Scenarios.Quest;
using Agentica.CLI.Scenarios.WorkbenchQuest;
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
    return await QuestCommand.RunAsync(args.Skip(1).ToArray(), CreateCommandServices());
}

if (string.Equals(args[0], "mazequest", StringComparison.OrdinalIgnoreCase))
{
    return await MazeQuestCommand.RunAsync(args.Skip(1).ToArray(), CreateCommandServices());
}

if (string.Equals(args[0], "workbench", StringComparison.OrdinalIgnoreCase))
{
    return await WorkbenchQuestCommand.RunAsync(args.Skip(1).ToArray(), CreateCommandServices());
}

if (string.Equals(args[0], "hexquest", StringComparison.OrdinalIgnoreCase))
{
    return await HexQuestCommand.RunAsync(args.Skip(1).ToArray(), CreateCommandServices());
}

if (string.Equals(args[0], "chessquest", StringComparison.OrdinalIgnoreCase))
{
    return await ChessQuestCommand.RunAsync(args.Skip(1).ToArray(), CreateCommandServices());
}

if (string.Equals(args[0], "campaign", StringComparison.OrdinalIgnoreCase))
{
    return await CampaignCommand.RunAsync(args.Skip(1).ToArray(), CreateCommandServices());
}

if (string.Equals(args[0], "orchestrate", StringComparison.OrdinalIgnoreCase))
{
    return await OrchestrationCommand.RunAsync(
        args.Skip(1).ToArray(),
        GeminiCredentialsAvailable,
        PrintUsage);
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

static CliCommandServices CreateCommandServices() =>
    new(
        CreatePlanner,
        CreateRunLog,
        CreateEventSink,
        PrintEnvelope,
        FinishRunLog,
        GeminiCredentialsAvailable,
        PrintUsage);

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
                MaxOutputTokens: options.MaxOutputTokens ?? LlmPlannerOptions.DefaultMaxOutputTokens,
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
    Console.Error.WriteLine("  Agentica.CLI run \"<objective>\" [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--max-output-tokens <count>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI quest list");
    Console.Error.WriteLine("  Agentica.CLI quest run <quest-id> [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--route observe|blocked] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI mazequest list");
    Console.Error.WriteLine("  Agentica.CLI mazequest preview <quest-id> [--seed <number>] [--type unlock|collect|delivery|explore|activate|puzzle|rescue|resource] [--width <odd 7-15>] [--height <odd 7-15>] [--visibility <1-4>] [--reveal] [--json]");
    Console.Error.WriteLine("  Agentica.CLI mazequest run [quest-id] [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--seed <number>] [--type unlock|collect|delivery|explore|activate|puzzle|rescue|resource] [--watch] [--narrator off|deterministic|gemini] [--turn-json] [--watch-delay-ms <ms>] [--timeout-seconds <seconds>] [--model <model-id>] [--narration-model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI workbench list");
    Console.Error.WriteLine("  Agentica.CLI workbench preview [scenario-id]");
    Console.Error.WriteLine("  Agentica.CLI workbench run [scenario-id] [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--timeout-seconds <seconds>] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI hexquest list");
    Console.Error.WriteLine("  Agentica.CLI hexquest preview [scenario-id]");
    Console.Error.WriteLine("  Agentica.CLI hexquest run [scenario-id] [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--timeout-seconds <seconds>] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--max-output-tokens <count>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI chessquest list");
    Console.Error.WriteLine("  Agentica.CLI chessquest preview [scenario-id]");
    Console.Error.WriteLine("  Agentica.CLI chessquest board-probe [--trials <count>] [--seed <number>] [--scramble-plies <count>] [--presentation ascii|fen|both] [--target occupied|empty|mixed] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--max-output-tokens <count>] [--timeout-seconds <seconds>] [--json] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI chessquest replay <game-record-file|run-log-directory>");
    Console.Error.WriteLine("  Agentica.CLI chessquest resume <game-record-file|run-log-directory> [run options]");
    Console.Error.WriteLine("  Agentica.CLI chessquest run [scenario-id] [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--strategy-mode off|phase|projected] [--phase opening|tactical|endgame] [--phase-max-agent-turns <count>] [--max-steps <count>] [--max-refinements <count>] [--max-plan-continuations <count>] [--max-blocked-retries <count>] [--timeout-seconds <seconds>] [--opponent random|heuristic|agent] [--opponent-difficulty beginner|club|strong|max] [--opponent-planner deterministic|gemini] [--opponent-model <model-id>] [--opponent-thinking-budget dynamic|off|<tokens>] [--opponent-max-output-tokens <count>] [--opponent-max-steps <count>] [--opponent-max-refinements <count>] [--opponent-timeout-seconds <seconds>] [--opponent-seed <number>] [--quiet] [--verbose-events] [--turn-json] [--verbose-envelope] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--max-output-tokens <count>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI chessquest orchestrate [scenario-id] [--task-planner deterministic|gemini] [--run-planner deterministic|gemini] [--task-model <model-id>] [--run-model <model-id>] [--phase-max-agent-turns <count>] [--max-orchestration-runs <count>] [--max-orchestration-refinements <count>] [--max-graph-mutations <count>] [--max-steps <count>] [--max-refinements <count>] [--max-plan-continuations <count>] [--timeout-seconds <seconds>] [--opponent random|heuristic|agent] [--opponent-difficulty beginner|club|strong|max] [--thinking-budget dynamic|off|<tokens>] [--max-output-tokens <count>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI campaign list");
    Console.Error.WriteLine("  Agentica.CLI campaign run [campaign-id] [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--max-output-tokens <count>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI orchestrate \"<objective>\" [--task-planner deterministic|gemini] [--model <model-id>]");
}
