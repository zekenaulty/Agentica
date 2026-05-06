using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.CLI.Configuration;
using Agentica.CLI.Logging;
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
    Console.Error.WriteLine("  Agentica.CLI mazequest preview <quest-id> [--seed <number>] [--type unlock|collect|delivery|explore|activate|puzzle|rescue|resource] [--width <odd 7-15>] [--height <odd 7-15>] [--visibility <1-4>] [--reveal] [--json]");
    Console.Error.WriteLine("  Agentica.CLI mazequest run [quest-id] [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--seed <number>] [--type unlock|collect|delivery|explore|activate|puzzle|rescue|resource] [--watch] [--narrator off|deterministic|gemini] [--turn-json] [--watch-delay-ms <ms>] [--timeout-seconds <seconds>] [--model <model-id>] [--narration-model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--include-thoughts] [--log-run] [--log-dir <path>]");
    Console.Error.WriteLine("  Agentica.CLI workbench list");
    Console.Error.WriteLine("  Agentica.CLI workbench preview [scenario-id]");
    Console.Error.WriteLine("  Agentica.CLI workbench run [scenario-id] [--planner deterministic|gemini] [--planning-mode stepwise|query-blocker|blocker|plan-only] [--max-blocked-retries <count>] [--timeout-seconds <seconds>] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--include-thoughts] [--log-run] [--log-dir <path>]");
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
