using Agentica.Clients.Gemini;

namespace Agentica.Lab.Benchmarks;

internal sealed record ProductProofBenchmarkManifest(
    string HarnessVersion,
    DateTimeOffset StartedAtUtc,
    BenchmarkMatrix Matrix,
    BenchmarkCohortIdentity Cohort,
    ProductProofBenchmarkConfiguration Configuration,
    LlmPricingSnapshot Pricing);

internal sealed record IncompleteProductProofBenchmarkReport(
    string MatrixVersion,
    BenchmarkCohortIdentity Cohort,
    int ExpectedRunCount,
    int CompletedRunCount,
    bool GatePassed,
    IReadOnlyList<string> GateFailures);

internal static class ProductProofBenchmarkCommand
{
    public const string LiveEnvironmentVariable = "AGENTICA_RUN_LIVE_LLM_BENCHMARKS";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        Func<bool> geminiCredentialsAvailable)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(geminiCredentialsAvailable);

        if (args.Count == 0 ||
            !string.Equals(args[0], "product-proof", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Benchmark subcommand must be 'product-proof'.");
            PrintUsage();
            return 2;
        }

        if (args.Count > 1 &&
            string.Equals(args[1], "aggregate", StringComparison.OrdinalIgnoreCase))
        {
            return ProductProofReaggregationCommand.Run(args.Skip(2).ToArray());
        }

        var options = ProductProofBenchmarkCommandOptions.Parse(args.Skip(1).ToArray());
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            PrintUsage();
            return 2;
        }

        if (!options.Live)
        {
            Console.Error.WriteLine("The product-proof benchmark performs real provider calls. Pass --live to authorize this cohort.");
            return 2;
        }

        if (!LiveEnvironmentEnabled())
        {
            Console.Error.WriteLine($"Live product-proof benchmarks are disabled. Set {LiveEnvironmentVariable}=true in addition to passing --live.");
            return 2;
        }

        if (IsVertexAiEnabled(Environment.GetEnvironmentVariable("GOOGLE_GENAI_USE_VERTEXAI")))
        {
            Console.Error.WriteLine("This fixed cohort requires the Gemini Developer API Standard surface; GOOGLE_GENAI_USE_VERTEXAI=true is not allowed because its pricing and cohort identity differ.");
            return 2;
        }

        if (!geminiCredentialsAvailable())
        {
            Console.Error.WriteLine("Gemini Developer API credentials are unavailable. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        var matrix = ProductProofBenchmarkMatrix.Current;
        var startedAt = DateTimeOffset.UtcNow;
        var cohort = new BenchmarkCohortIdentity(
            CohortId: $"{startedAt:yyyyMMddTHHmmssfffZ}-{matrix.Version}-{Guid.NewGuid():N}",
            MatrixVersion: matrix.Version,
            ProviderName: GeminiLlmClient.ProviderName,
            ModelId: GeminiModelId.Flash25,
            ConfigurationId: ProductProofBenchmarkFixedConfiguration.ConfigurationId);
        var store = ProductProofBenchmarkStore.Create(options.OutputDirectory, cohort.CohortId);
        store.WriteManifest(new ProductProofBenchmarkManifest(
            ProductProofBenchmarkFixedConfiguration.HarnessVersion,
            startedAt,
            matrix,
            cohort,
            ProductProofBenchmarkFixedConfiguration.Current,
            ProductProofPricing.Current));

        Console.WriteLine("LIVE LLM PRODUCT PROOF");
        Console.WriteLine($"Cohort: {cohort.CohortId}");
        Console.WriteLine($"Matrix: {matrix.Version} ({matrix.Runs.Count} runs)");
        Console.WriteLine($"Provider/model: {cohort.ProviderName}/{cohort.ModelId}");
        Console.WriteLine($"Output: {store.DirectoryPath}");
        Console.WriteLine();

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        var runner = new ProductProofBenchmarkRunner();
        var results = new List<BenchmarkRunResult>(matrix.Runs.Count);
        try
        {
            for (var index = 0; index < matrix.Runs.Count; index++)
            {
                var definition = matrix.Runs[index];
                Console.Write($"[{index + 1}/{matrix.Runs.Count}] {definition.RunId} ... ");
                var result = await runner.RunAsync(definition, cohort, cancellation.Token).ConfigureAwait(false);
                results.Add(result);
                store.AppendRun(result);
                Console.WriteLine(
                    $"status={result.RunOutcomeStatus} reported={result.ReportedSuccess} oracle={result.OracleSuccess} calls={result.LlmCalls.Count} elapsed={result.Elapsed.TotalSeconds:0.0}s");

                if (cancellation.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        if (results.Count != matrix.Runs.Count)
        {
            var incomplete = new IncompleteProductProofBenchmarkReport(
                matrix.Version,
                cohort,
                matrix.Runs.Count,
                results.Count,
                GatePassed: false,
                GateFailures: ["cohort_incomplete"]);
            store.WriteAggregate(incomplete);
            Console.Error.WriteLine($"Benchmark cohort interrupted after {results.Count}/{matrix.Runs.Count} runs; the gate failed.");
            return 130;
        }

        BenchmarkReport report;
        try
        {
            report = StrictBenchmarkAggregator.Aggregate(
                matrix,
                results,
                ProductProofPricing.Current);
        }
        catch (BenchmarkCohortValidationException exception)
        {
            var invalid = new IncompleteProductProofBenchmarkReport(
                matrix.Version,
                cohort,
                matrix.Runs.Count,
                results.Count,
                GatePassed: false,
                GateFailures: [$"cohort_validation:{exception.GetType().Name}"]);
            store.WriteAggregate(invalid);
            Console.Error.WriteLine("Benchmark cohort validation failed; the gate failed closed.");
            return 1;
        }

        store.WriteAggregate(report);
        PrintReport(report, store.DirectoryPath);
        return report.GatePassed ? 0 : 1;
    }

    internal static bool LiveEnvironmentEnabled() =>
        string.Equals(
            Environment.GetEnvironmentVariable(LiveEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsVertexAiEnabled(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    internal static void PrintUsage()
    {
        Console.Error.WriteLine(
            "  Agentica.Lab benchmark product-proof --live [--output-dir <path>]  [requires AGENTICA_RUN_LIVE_LLM_BENCHMARKS=true]");
        Console.Error.WriteLine(
            "  Agentica.Lab benchmark product-proof aggregate <cohort-directory>  [offline; no provider calls]");
    }

    private static void PrintReport(BenchmarkReport report, string outputDirectory)
    {
        Console.WriteLine();
        Console.WriteLine("--- Product proof aggregate ---");
        Console.WriteLine($"gatePassed={report.GatePassed}");
        Console.WriteLine($"successRate={report.Overall.SuccessRate:P1}");
        Console.WriteLine($"falseSuccesses={report.Overall.FalseSuccessCount}");
        Console.WriteLine($"invalidPlanRate={report.Overall.InvalidPlanRate:P1}");
        Console.WriteLine($"llmCalls={report.Overall.LlmCallCount}");
        Console.WriteLine($"runtimeRetries={report.Overall.RuntimeRetryCount}");
        Console.WriteLine($"llmRetries={report.Overall.LlmRetryCount}");
        Console.WriteLine($"tokens={report.Overall.Tokens.TotalTokens}");
        Console.WriteLine($"estimatedCostUsd={(report.Overall.EstimatedCostUsd?.ToString("0.000000") ?? "unavailable")}");
        if (report.GateFailures.Count > 0)
        {
            Console.WriteLine($"gateFailures={string.Join(',', report.GateFailures)}");
        }

        Console.WriteLine($"results={outputDirectory}");
    }
}
