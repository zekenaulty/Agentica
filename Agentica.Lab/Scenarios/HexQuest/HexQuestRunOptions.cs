using Agentica.Execution;

internal sealed record HexQuestRunOptions(
    string ScenarioId,
    PlannerKind Planner,
    string? ModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    PlanningMode PlanningMode,
    int MaxBlockedRetries,
    int TimeoutSeconds,
    bool LogRun,
    string? LogDir,
    bool IsValid,
    string? Error)
{
    public static HexQuestRunOptions Parse(IReadOnlyList<string> args)
    {
        string? scenarioId = null;
        var planner = PlannerKind.Deterministic;
        string? modelId = null;
        string? thinkingBudget = null;
        var includeThoughts = false;
        var planningMode = PlanningMode.Stepwise;
        var maxBlockedRetries = 1;
        var timeoutSeconds = 120;
        var logRun = false;
        string? logDir = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (scenarioId is not null)
                {
                    return Invalid($"Unexpected hexquest argument '{arg}'.");
                }

                scenarioId = arg;
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

                case "--timeout-seconds":
                    if (!TryReadValue(args, ref index, out var timeoutSecondsValue) ||
                        !int.TryParse(timeoutSecondsValue, out timeoutSeconds) ||
                        timeoutSeconds <= 0)
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

        return new HexQuestRunOptions(
            scenarioId ?? "xor_checksum_strength",
            planner,
            modelId,
            thinkingBudget,
            includeThoughts,
            planningMode,
            maxBlockedRetries,
            timeoutSeconds,
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

    private static HexQuestRunOptions Invalid(string error) =>
        new(
            "xor_checksum_strength",
            PlannerKind.Deterministic,
            null,
            null,
            false,
            PlanningMode.Stepwise,
            MaxBlockedRetries: 1,
            TimeoutSeconds: 120,
            LogRun: false,
            LogDir: null,
            IsValid: false,
            Error: error);
}
