internal sealed record OrchestrationRunOptions(
    string Objective,
    PlannerKind TaskPlanner,
    string? ModelId,
    bool IsValid,
    string? Error)
{
    public static OrchestrationRunOptions Parse(IReadOnlyList<string> args)
    {
        var objectiveParts = new List<string>();
        var taskPlanner = PlannerKind.Deterministic;
        string? modelId = null;

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
                case "--task-planner":
                    if (!TryReadValue(args, ref index, out var plannerValue))
                    {
                        return Invalid("Missing value for --task-planner.");
                    }

                    if (!Enum.TryParse<PlannerKind>(plannerValue, ignoreCase: true, out taskPlanner))
                    {
                        return Invalid($"Unknown task planner '{plannerValue}'.");
                    }

                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, out modelId))
                    {
                        return Invalid("Missing value for --model.");
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

        return new OrchestrationRunOptions(objective, taskPlanner, modelId, IsValid: true, Error: null);
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

    private static OrchestrationRunOptions Invalid(string error) =>
        new(string.Empty, PlannerKind.Deterministic, null, IsValid: false, Error: error);
}
