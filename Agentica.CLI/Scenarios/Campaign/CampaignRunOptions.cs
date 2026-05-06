using Agentica.Execution;

namespace Agentica.CLI.Scenarios.Campaign;

internal sealed record CampaignRunOptions(
    string CampaignId,
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
    public static CampaignRunOptions Parse(IReadOnlyList<string> args)
    {
        var campaignId = "dungeon_campaign";
        var planner = PlannerKind.Deterministic;
        string? modelId = null;
        string? thinkingBudget = null;
        var includeThoughts = false;
        var planningMode = PlanningMode.PlanOnly;
        var maxBlockedRetries = 0;
        var logRun = false;
        string? logDir = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                campaignId = arg;
                continue;
            }

            switch (arg)
            {
                case "--planner":
                    if (!CliOptionReader.TryReadValue(args, ref index, out var plannerValue))
                    {
                        return Invalid("Missing value for --planner.");
                    }

                    if (!Enum.TryParse<PlannerKind>(plannerValue, ignoreCase: true, out planner))
                    {
                        return Invalid($"Unknown planner '{plannerValue}'.");
                    }

                    break;

                case "--model":
                    if (!CliOptionReader.TryReadValue(args, ref index, out modelId))
                    {
                        return Invalid("Missing value for --model.");
                    }

                    break;

                case "--thinking-budget":
                    if (!CliOptionReader.TryReadValue(args, ref index, out thinkingBudget))
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
                    if (!CliOptionReader.TryReadValue(args, ref index, out var planningModeValue))
                    {
                        return Invalid("Missing value for --planning-mode.");
                    }

                    if (!CliParsing.TryParsePlanningMode(planningModeValue, out planningMode))
                    {
                        return Invalid($"Unknown planning mode '{planningModeValue}'.");
                    }

                    break;

                case "--max-blocked-retries":
                    if (!CliOptionReader.TryReadInt(args, ref index, out maxBlockedRetries) ||
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
                    if (!CliOptionReader.TryReadValue(args, ref index, out logDir))
                    {
                        return Invalid("Missing value for --log-dir.");
                    }

                    break;

                default:
                    return Invalid($"Unknown option '{arg}'.");
            }
        }

        return new CampaignRunOptions(
            campaignId,
            planner,
            modelId,
            thinkingBudget,
            includeThoughts,
            planningMode,
            maxBlockedRetries,
            logRun,
            logDir,
            IsValid: true,
            Error: null);
    }

    private static CampaignRunOptions Invalid(string error) =>
        new(
            "dungeon_campaign",
            PlannerKind.Deterministic,
            null,
            null,
            false,
            PlanningMode.PlanOnly,
            0,
            false,
            null,
            IsValid: false,
            Error: error);
}
