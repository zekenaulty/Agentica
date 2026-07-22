namespace Agentica.Lab.Benchmarks;

internal sealed record ProductProofReaggregationCommandOptions(
    string? CohortDirectory,
    bool IsValid,
    string? Error)
{
    public static ProductProofReaggregationCommandOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return Invalid("Re-aggregation requires exactly one cohort directory.");
        }

        if (args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return Invalid($"Unknown re-aggregation option '{args[0]}'.");
        }

        return new ProductProofReaggregationCommandOptions(
            args[0],
            IsValid: true,
            Error: null);
    }

    private static ProductProofReaggregationCommandOptions Invalid(string error) =>
        new(
            CohortDirectory: null,
            IsValid: false,
            Error: error);
}
