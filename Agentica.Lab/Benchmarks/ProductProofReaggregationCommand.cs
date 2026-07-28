namespace Agentica.Lab.Benchmarks;

internal sealed record ProductProofReaggregationReceipt(
    string ReceiptVersion,
    DateTimeOffset ReaggregatedAtUtc,
    BenchmarkOriginalManifestIdentity OriginalManifest,
    string PricingSnapshotId,
    DateOnly PricingReviewedOn,
    string PricingSourceUrl,
    string RunsSha256,
    string ExpectedRunsSha256,
    string TrustAnchorKind,
    int RunCount,
    bool GatePassed,
    IReadOnlyList<string> GateFailures);

internal static class ProductProofReaggregationCommand
{
    public const string ReceiptVersion = "agentica-product-proof-reaggregation-v1";

    public static int Run(IReadOnlyList<string> args)
    {
        var options = ProductProofReaggregationCommandOptions.Parse(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            ProductProofBenchmarkCommand.PrintUsage();
            return 2;
        }

        ProductProofBenchmarkCohortSnapshot snapshot;
        BenchmarkReport report;
        try
        {
            snapshot = ProductProofBenchmarkCohortReader.Read(options.CohortDirectory!);
            if (!string.Equals(
                    snapshot.RunsSha256,
                    options.ExpectedRunsSha256,
                    StringComparison.Ordinal))
            {
                throw new ProductProofBenchmarkCohortException(
                    "runs.jsonl does not match the explicitly trusted SHA-256 anchor.");
            }

            report = StrictBenchmarkAggregator.Aggregate(
                ProductProofBenchmarkMatrix.Current,
                snapshot.Results,
                ProductProofPricing.Current);
        }
        catch (Exception exception) when (
            exception is ProductProofBenchmarkCohortException or
                BenchmarkCohortValidationException or
                IOException or
                UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"Offline benchmark re-aggregation refused the cohort ({exception.GetType().Name}). No aggregate was changed.");
            return 2;
        }

        var reaggregatedAtUtc = DateTimeOffset.UtcNow;
        var originalManifest = new BenchmarkOriginalManifestIdentity(
            snapshot.Manifest.HarnessVersion,
            snapshot.Manifest.Matrix.Version,
            snapshot.Manifest.Cohort.CohortId,
            snapshot.Manifest.Cohort.ProviderName,
            snapshot.Manifest.Cohort.ModelId,
            snapshot.Manifest.Cohort.ConfigurationId,
            snapshot.Manifest.Configuration.PricingSnapshotId);
        var trust = new BenchmarkReaggregationTrust(
            ReceiptVersion,
            reaggregatedAtUtc,
            originalManifest,
            snapshot.RunsSha256,
            options.ExpectedRunsSha256!,
            "explicit-command-line-sha256");
        report = report with { ReaggregationTrust = trust };
        var receipt = new ProductProofReaggregationReceipt(
            ReceiptVersion,
            reaggregatedAtUtc,
            originalManifest,
            ProductProofPricing.Current.SnapshotId,
            ProductProofPricing.Current.ReviewedOn,
            ProductProofPricing.Current.SourceUrl,
            snapshot.RunsSha256,
            options.ExpectedRunsSha256!,
            "explicit-command-line-sha256",
            snapshot.Results.Count,
            report.GatePassed,
            report.GateFailures);

        try
        {
            var store = ProductProofBenchmarkStore.OpenExisting(snapshot.DirectoryPath);
            store.PublishReaggregation(report, receipt);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(
                $"Offline benchmark re-aggregation could not persist its result ({exception.GetType().Name}).");
            return 1;
        }

        Console.WriteLine("OFFLINE LLM PRODUCT-PROOF RE-AGGREGATION");
        Console.WriteLine($"cohort={snapshot.Manifest.Cohort.CohortId}");
        Console.WriteLine($"matrix={ProductProofBenchmarkMatrix.Current.Version}");
        Console.WriteLine($"runsSha256={snapshot.RunsSha256}");
        Console.WriteLine($"pricingSnapshot={report.PricingSnapshotId}");
        Console.WriteLine($"gatePassed={report.GatePassed}");
        Console.WriteLine($"results={snapshot.DirectoryPath}");
        return report.GatePassed ? 0 : 1;
    }
}
