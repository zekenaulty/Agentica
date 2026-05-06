using Agentica.CLI.Scenarios.HexQuest;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;

internal static class HexQuestCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var board = new HexQuestBoard();

        if (args.Count == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            PrintBoard(board);
            return 0;
        }

        if (string.Equals(args[0], "preview", StringComparison.OrdinalIgnoreCase))
        {
            var scenarioId = args.Count > 1 ? args[1] : "xor_checksum_strength";
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

        if (!string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            services.PrintUsage();
            return 2;
        }

        return await RunScenarioAsync(board, args.Skip(1).ToArray(), services).ConfigureAwait(false);
    }

    private static async Task<int> RunScenarioAsync(
        IHexQuestBoard board,
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var options = HexQuestRunOptions.Parse(args);
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

        HexQuestScenario scenario;
        try
        {
            scenario = board.Load(options.ScenarioId);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var session = new HexQuestSession(scenario);
        var runObjective = BuildObjective(scenario);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "hexquest", args);
        runLog?.WriteJson("hexquest-scenario.json", scenario);
        runLog?.WriteJson("hexquest-initial-public-snapshot.json", session.PublicSnapshot());

        PrintOpening(scenario, session);

        using var runCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            runCancellation.Cancel();
        };

        var planner = options.Planner == PlannerKind.Deterministic
            ? new HexQuestDeterministicPlanner(scenario.Descriptor.ScenarioId)
            : services.CreatePlanner(new CliRunOptions(
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
            toolCatalog: HexQuestTools.CreateCatalog(session),
            eventSink: services.CreateEventSink(new ConsoleEventSink(), runLog),
            outcomeReporter: new HexQuestOutcomeReporter(),
            policy: new ExecutionPolicy(
                MaxSteps: 18,
                MaxRefinements: 12,
                Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
                PlanningMode: options.PlanningMode,
                MaxPlanContinuations: 4,
                PlanningContext: new PlanningContextOptions(MaxRecentObservations: 10, MaxRecentReceipts: 10),
                MaxBlockedRetries: options.MaxBlockedRetries),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("hexquest.objective_completed"));

        OutcomeEnvelope envelope;
        Console.CancelKeyPress += cancelHandler;
        try
        {
            envelope = await runner.RunAsync(
                new RunRequest(runObjective, RequestOrigin.User, session.PublicSnapshot()),
                runCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        services.PrintEnvelope(envelope);
        services.FinishRunLog(runLog, envelope);
        return envelope.Outcome.Status == RunOutcomeStatus.Succeeded ? 0 : 1;
    }

    private static string BuildObjective(HexQuestScenario scenario) =>
        $"""
        HexQuest: {scenario.Descriptor.Title}
        Objective: {scenario.Descriptor.Objective}

        Planner contract:
        - Infer the hidden transform only from tool observations.
        - You may inspect the encoded payload and decoded projection.
        - You may request few-shot examples; each example is a decoded/encoded pair from the same transform.
        - You may use sandbox decoded edits to learn encoded deltas, but sandbox edits never mutate the authoritative payload and cannot win.
        - The final success condition must be achieved only by hexquest.commit_patch against encoded bytes.
        - Before committing, prefer hexquest.validate_patch with the exact same patch.
        - Patch format is comma-separated offset:old>new hex bytes, for example 0:A9>B7,4:E8>E6.
        - The committed patch must set {scenario.Goal.Field} to {scenario.Goal.TargetValue}.
        - The committed patch must preserve protected decoded fields: {string.Join(", ", scenario.Goal.ProtectedFields)}.
        - The committed patch must keep checksum validation passing.
        - Patch budget: {(scenario.Goal.MaxPatchBytes is null ? "no scenario-specific byte limit" : $"at most {scenario.Goal.MaxPatchBytes} byte edits")}.
        - Forbidden encoded offsets: {FormatOffsets(scenario.Goal.ForbiddenOffsets)}.
        - The run succeeds only when hexquest.commit_patch emits the hexquest.objective_completed artifact.
        """;

    private static string FormatOffsets(IReadOnlyList<int>? offsets) =>
        offsets is null || offsets.Count == 0
            ? "none"
            : string.Join(", ", offsets);

    private static void PrintBoard(IHexQuestBoard board)
    {
        Console.WriteLine("Available HexQuest Scenarios:");
        var scenarios = board.ListScenarios();
        for (var index = 0; index < scenarios.Count; index++)
        {
            var scenario = scenarios[index];
            Console.WriteLine($"{index + 1}. {scenario.Title} ({scenario.ScenarioId})");
            Console.WriteLine($"   - {scenario.Description}");
            Console.WriteLine($"   - Difficulty: {scenario.Difficulty}");
            Console.WriteLine($"   - Estimated Steps: {scenario.EstimatedSteps}");
        }
    }

    private static void PrintPreview(HexQuestScenario scenario)
    {
        Console.WriteLine($"HexQuest: \"{scenario.Descriptor.Title}\"");
        Console.WriteLine($"Objective: {scenario.Descriptor.Objective}");
        Console.WriteLine($"Scenario: {scenario.Descriptor.ScenarioId}");
        Console.WriteLine($"Difficulty: {scenario.Descriptor.Difficulty}");
        Console.WriteLine();
        Console.WriteLine("Tool surface:");
        Console.WriteLine("  hexquest.inspect_encoded, hexquest.inspect_decoded, hexquest.request_example, hexquest.sandbox_set_decoded, hexquest.validate_patch, hexquest.commit_patch");
        Console.WriteLine();
        Console.WriteLine("Completion requires an encoded-payload commit that satisfies the decoded goal, preserves protected fields, and keeps checksum valid.");
    }

    private static void PrintOpening(HexQuestScenario scenario, HexQuestSession session)
    {
        Console.WriteLine($"Agent has accepted HexQuest: \"{scenario.Descriptor.Title}\"");
        Console.WriteLine($"Objective: {scenario.Descriptor.Objective}");
        Console.WriteLine($"Scenario: {scenario.Descriptor.ScenarioId}");
        Console.WriteLine($"Initial encoded payload: {HexQuestCodec.ToHex(session.State.Encoded)}");
        Console.WriteLine();
    }
}
