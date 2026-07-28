namespace Agentica.Lab.Benchmarks;

internal sealed record ProductProofReaggregationCommandOptions(
    string? CohortDirectory,
    string? ExpectedRunsSha256,
    bool IsValid,
    string? Error)
{
    public static ProductProofReaggregationCommandOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count != 3 ||
            string.IsNullOrWhiteSpace(args[0]) ||
            !string.Equals(args[1], "--expected-runs-sha256", StringComparison.Ordinal) ||
            !IsVersionedSha256(args[2]))
        {
            return Invalid(
                "Re-aggregation requires a cohort directory and an explicit trusted " +
                "--expected-runs-sha256 sha256-v1:<64 lowercase hex> anchor.");
        }

        if (args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return Invalid($"Unknown re-aggregation option '{args[0]}'.");
        }

        return new ProductProofReaggregationCommandOptions(
            args[0],
            args[2],
            IsValid: true,
            Error: null);
    }

    private static bool IsVersionedSha256(string? value)
    {
        const string prefix = "sha256-v1:";
        if (value is null ||
            !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length != prefix.Length + 64)
        {
            return false;
        }

        foreach (var character in value.AsSpan(prefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static ProductProofReaggregationCommandOptions Invalid(string error) =>
        new(
            CohortDirectory: null,
            ExpectedRunsSha256: null,
            IsValid: false,
            Error: error);
}
