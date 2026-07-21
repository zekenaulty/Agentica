namespace Agentica.Lab.Benchmarks;

internal sealed record OriginalProductProofManifestIdentity(
    string HarnessVersion,
    string MatrixVersion,
    string CohortId,
    string ProviderName,
    string ModelId,
    string ConfigurationId,
    string OriginalPricingSnapshotId);

internal sealed record ProductProofReaggregationReceipt(
    string ReceiptVersion,
    DateTimeOffset ReaggregatedAtUtc,
    OriginalProductProofManifestIdentity OriginalManifest,
    string PricingSnapshotId,
    DateOnly PricingReviewedOn,
    string PricingSourceUrl,
    string RunsSha256,
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

        var receipt = new ProductProofReaggregationReceipt(
            ReceiptVersion,
            DateTimeOffset.UtcNow,
            new OriginalProductProofManifestIdentity(
                snapshot.Manifest.HarnessVersion,
                snapshot.Manifest.Matrix.Version,
                snapshot.Manifest.Cohort.CohortId,
                snapshot.Manifest.Cohort.ProviderName,
                snapshot.Manifest.Cohort.ModelId,
                snapshot.Manifest.Cohort.ConfigurationId,
                snapshot.Manifest.Configuration.PricingSnapshotId),
            ProductProofPricing.Current.SnapshotId,
            ProductProofPricing.Current.ReviewedOn,
            ProductProofPricing.Current.SourceUrl,
            snapshot.RunsSha256,
            snapshot.Results.Count,
            report.GatePassed,
            report.GateFailures);

        try
        {
            var store = ProductProofBenchmarkStore.OpenExisting(snapshot.DirectoryPath);
            store.WriteAggregate(report);
            store.WriteReaggregationReceipt(receipt);
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
