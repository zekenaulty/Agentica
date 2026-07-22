using Agentica.Lab.Scenarios.Quest;
using Agentica.Execution;

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
