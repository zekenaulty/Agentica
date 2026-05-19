using Agentica.Execution;

internal sealed record CliRunOptions(
    string Objective,
    PlannerKind Planner,
    string? ModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    int? MaxOutputTokens,
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
        int? maxOutputTokens = null;
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

                case "--max-output-tokens":
                    if (!TryReadValue(args, ref index, out var maxOutputTokensValue) ||
                        !int.TryParse(maxOutputTokensValue, out var parsedMaxOutputTokens) ||
                        parsedMaxOutputTokens <= 0)
                    {
                        return Invalid("Missing or invalid value for --max-output-tokens.");
                    }

                    maxOutputTokens = parsedMaxOutputTokens;
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

        return new CliRunOptions(
            objective,
            planner,
            modelId,
            thinkingBudget,
            includeThoughts,
            maxOutputTokens,
            planningMode,
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

    private static CliRunOptions Invalid(string error) =>
        new(string.Empty, PlannerKind.Deterministic, null, null, false, null, PlanningMode.Stepwise, 2, false, null, false, error);
}
