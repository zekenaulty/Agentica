using Agentica.Lab.Scenarios.Quest;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Requests;

internal static class QuestCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var board = new InMemoryQuestBoard();

        if (args.Count == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            PrintBoard(board);
            return 0;
        }

        if (!string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            services.PrintUsage();
            return 2;
        }

        var options = QuestRunOptions.Parse(args.Skip(1).ToArray());
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
            : services.CreatePlanner(new CliRunOptions(
                definition.Objective,
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

        PrintOpening(definition);

        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "quest", args);
        runLog?.WriteJson("quest-definition.json", definition);
        var eventSink = services.CreateEventSink(new QuestTraceEventSink(session), runLog);

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
                MaxBlockedRetries: options.MaxBlockedRetries,
                SecurityPolicy: LabSecurityPolicy.ForPlanner(planner)),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("quest.objective_completed"));

        var envelope = await runner.RunAsync(
            new RunRequest($"Quest: {definition.Title}. Objective: {definition.Objective}", RequestOrigin.User),
            CancellationToken.None).ConfigureAwait(false);

        services.PrintEnvelope(envelope);
        services.FinishRunLog(runLog, envelope);
        return envelope.Outcome.Status == RunOutcomeStatus.Succeeded ? 0 : 1;
    }

    private static void PrintBoard(IQuestBoard board)
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

    private static void PrintOpening(QuestDefinition definition)
    {
        Console.WriteLine($"Agent has accepted quest: \"{definition.Title}\"");
        Console.WriteLine($"Objective: {definition.Objective}");
        Console.WriteLine();
    }
}
