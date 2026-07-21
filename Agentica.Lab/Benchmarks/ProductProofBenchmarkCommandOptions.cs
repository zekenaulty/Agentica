namespace Agentica.Lab.Benchmarks;

internal sealed record ProductProofBenchmarkCommandOptions(
    bool Live,
    string? OutputDirectory,
    bool IsValid,
    string? Error)
{
    public static ProductProofBenchmarkCommandOptions Parse(IReadOnlyList<string> args)
    {
        var live = false;
        string? outputDirectory = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--live":
                    live = true;
                    break;

                case "--output-dir":
                    if (index + 1 >= args.Count ||
                        args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Invalid("Missing value for --output-dir.");
                    }

                    outputDirectory = args[++index];
                    break;

                default:
                    return Invalid($"Unknown benchmark option '{args[index]}'.");
            }
        }

        return new ProductProofBenchmarkCommandOptions(
            live,
            outputDirectory,
            IsValid: true,
            Error: null);
    }

    private static ProductProofBenchmarkCommandOptions Invalid(string error) =>
        new(
            Live: false,
            OutputDirectory: null,
            IsValid: false,
            Error: error);
}
