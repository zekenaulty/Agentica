using Agentica.Lab.Scenarios.WorkbenchQuest;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;

internal static class WorkbenchQuestCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var board = new WorkbenchQuestBoard();

        if (args.Count == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            PrintBoard(board);
            return 0;
        }

        if (string.Equals(args[0], "preview", StringComparison.OrdinalIgnoreCase))
        {
            var scenarioId = args.Count > 1 ? args[1] : "broken_check";
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
        IWorkbenchQuestBoard board,
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var options = WorkbenchQuestRunOptions.Parse(args);
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

        WorkbenchScenario scenario;
        try
        {
            scenario = board.Load(options.ScenarioId);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var session = new WorkbenchQuestSession(scenario);
        var runObjective = BuildObjective(scenario);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "workbench", args);
        runLog?.WriteJson("workbench-scenario.json", scenario);
        runLog?.WriteJson("workbench-initial-public-snapshot.json", session.PublicSnapshot());

        PrintOpening(scenario);

        using var runCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            runCancellation.Cancel();
        };

        var planner = options.Planner == PlannerKind.Deterministic
            ? new WorkbenchQuestDeterministicPlanner(scenario.Descriptor.ScenarioId)
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
            toolCatalog: WorkbenchQuestTools.CreateCatalog(session),
            eventSink: services.CreateEventSink(new WorkbenchQuestTraceEventSink(session), runLog),
            outcomeReporter: new WorkbenchQuestOutcomeReporter(),
            policy: new ExecutionPolicy(
                MaxSteps: 24,
                MaxRefinements: 18,
                Timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
                PlanningMode: options.PlanningMode,
                MaxPlanContinuations: 6,
                PlanningContext: new PlanningContextOptions(MaxRecentObservations: 8, MaxRecentReceipts: 8),
                MaxBlockedRetries: options.MaxBlockedRetries,
                SecurityPolicy: LabSecurityPolicy.ForPlanner(planner)),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("workbench.objective_completed"));

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

    internal static string BuildObjective(WorkbenchScenario scenario) =>
        $"""
        WorkbenchQuest: {scenario.Descriptor.Title}
        Objective: {scenario.Descriptor.Objective}

        Planner contract:
        - Produce only the next safe step or a very small safe slice.
        - Use raw tool outputs to decide what evidence is needed.
        - Do not invent file contents, check results, patch receipts, artifacts, or success.
        - Before the first mutation, you must call workbench.run_check and observe a failed baseline check.
        - Before the first mutation, you must read at least one relevant evidence file.
        - Do not call workbench.apply_patch until failed baseline check evidence and relevant read evidence exist.
        - Patch only after the evidence supports a concrete change.
        - To patch, call workbench.apply_patch with exact scenario-relative path, exact find text, and exact replacement text.
        - Do not patch read-only files or paths outside the scenario.
        - After any patch, run workbench.run_check before completion.
        - Do not call workbench.complete until there is evidence for a failed check before mutation, a relevant file read, an applied patch receipt, and a passing check after mutation.
        - The run succeeds only when workbench.complete emits the workbench.objective_completed artifact.
        """;

    private static void PrintBoard(IWorkbenchQuestBoard board)
    {
        Console.WriteLine("Available WorkbenchQuest Scenarios:");
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

    private static void PrintPreview(WorkbenchScenario scenario)
    {
        Console.WriteLine($"WorkbenchQuest: \"{scenario.Descriptor.Title}\"");
        Console.WriteLine($"Objective: {scenario.Descriptor.Objective}");
        Console.WriteLine($"Scenario: {scenario.Descriptor.ScenarioId}");
        Console.WriteLine($"Difficulty: {scenario.Descriptor.Difficulty}");
        Console.WriteLine();
        Console.WriteLine("Files:");
        foreach (var file in scenario.Files.Values.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            Console.WriteLine($"  - {file.Path} bytes={file.Content.Length} readOnly={file.ReadOnly}");
        }

        Console.WriteLine();
        Console.WriteLine("Tool surface:");
        Console.WriteLine("  workbench.list_files, workbench.read_file, workbench.search, workbench.run_check, workbench.diff, workbench.apply_patch, workbench.write_note, workbench.complete");
        Console.WriteLine();
        Console.WriteLine("Completion requires a failed check, relevant file evidence, an applied patch, a later passing check, and the completion artifact.");
    }

    private static void PrintOpening(WorkbenchScenario scenario)
    {
        Console.WriteLine($"Agent has accepted WorkbenchQuest: \"{scenario.Descriptor.Title}\"");
        Console.WriteLine($"Objective: {scenario.Descriptor.Objective}");
        Console.WriteLine($"Scenario: {scenario.Descriptor.ScenarioId}");
        Console.WriteLine();
    }
}
