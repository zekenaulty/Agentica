using Agentica.Events;
using Agentica.Outcomes;
using Agentica.Planning;

namespace Agentica.Lab.Scenarios.Campaign;

internal static class CampaignCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var board = new DungeonCampaignBoard();
        if (args.Count == 0 || string.Equals(args[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            PrintCampaigns(board);
            return 0;
        }

        if (!string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            services.PrintUsage();
            return 2;
        }

        var options = CampaignRunOptions.Parse(args.Skip(1).ToArray());
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

        CampaignDefinition definition;
        try
        {
            definition = board.Load(options.CampaignId);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var state = new CampaignState(definition);
        var session = new DungeonCampaignSession(definition);
        var runLog = services.CreateRunLog(options.LogRun, options.LogDir, "campaign", args);
        runLog?.WriteJson("campaign-definition.json", definition);
        runLog?.WriteJson("campaign-initial-snapshot.json", session.PublicSnapshot());

        PrintOpening(definition);
        var campaignRunner = new CampaignRunner(
            definition,
            state,
            milestone => options.Planner == PlannerKind.Deterministic
                ? new DungeonCampaignDeterministicPlanner(milestone)
                : services.CreatePlanner(new CliRunOptions(
                    milestone.Objective,
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
                    Error: null)),
            () => DungeonCampaignTools.CreateCatalog(session),
            new DeterministicOutcomeReporter(),
            services.CreateEventSink(new CampaignTraceEventSink(definition, state, session), runLog),
            session.PublicSnapshot,
            new CampaignRunnerOptions(
                MaxRuns: 16,
                MaxStepsPerRun: 8,
                MaxRefinementsPerRun: 4,
                PlanningMode: options.PlanningMode,
                MaxBlockedRetries: options.MaxBlockedRetries));

        var result = await campaignRunner.RunAsync(CancellationToken.None).ConfigureAwait(false);
        PrintProgress(result);
        WriteCampaignRunLog(runLog, result);

        return result.State.Status == CampaignRunStatus.Succeeded ? 0 : 1;
    }

    private static void PrintCampaigns(DungeonCampaignBoard board)
    {
        Console.WriteLine("Available Campaigns:");
        foreach (var campaign in board.ListCampaigns())
        {
            Console.WriteLine($"- {campaign.Title} ({campaign.CampaignId})");
            Console.WriteLine($"  {campaign.Goal}");
            Console.WriteLine($"  Milestones: {campaign.Milestones.Count}");
        }
    }

    private static void PrintOpening(CampaignDefinition definition)
    {
        Console.WriteLine($"Agent has accepted campaign: \"{definition.Title}\"");
        Console.WriteLine($"Goal: {definition.Goal}");
        Console.WriteLine();
    }

    private static void PrintProgress(CampaignRunResult result)
    {
        Console.WriteLine();
        Console.WriteLine("--- Campaign Progress ---");
        Console.WriteLine($"Campaign: {result.Definition.CampaignId}");
        Console.WriteLine($"Status: {result.State.Status}");
        Console.WriteLine($"Runs: {result.Envelopes.Count}");
        Console.WriteLine("Completed milestones:");
        foreach (var milestoneId in result.State.CompletedMilestones)
        {
            Console.WriteLine($"  - {milestoneId}");
        }

        if (result.State.BlockedMilestones.Count > 0)
        {
            Console.WriteLine("Blocked milestones:");
            foreach (var milestoneId in result.State.BlockedMilestones)
            {
                Console.WriteLine($"  - {milestoneId}");
            }
        }
    }

    private static void WriteCampaignRunLog(
        Agentica.Lab.Logging.RunLogWriter? runLog,
        CampaignRunResult result)
    {
        if (runLog is null)
        {
            return;
        }

        for (var index = 0; index < result.Envelopes.Count; index++)
        {
            runLog.WriteJson($"campaign-run-{index + 1:000}-outcome.json", result.Envelopes[index]);
        }

        runLog.WriteJson("campaign-result.json", new
        {
            campaignId = result.Definition.CampaignId,
            title = result.Definition.Title,
            status = result.State.Status,
            completedMilestones = result.State.CompletedMilestones,
            blockedMilestones = result.State.BlockedMilestones,
            availableMilestones = result.State.AvailableMilestones,
            priorRunRefs = result.State.PriorRunRefs,
            runIds = result.Envelopes.Select(envelope => envelope.Outcome.RunId).ToArray(),
            finalSnapshot = result.State.ProgressSnapshot,
            finalWorkingContext = result.State.WorkingContext
        });

        if (!runLog.IsEnabled)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Campaign run log written: {runLog.DirectoryPath}");
    }
}
