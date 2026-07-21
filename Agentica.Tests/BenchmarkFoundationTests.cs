extern alias AgenticaLab;

using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using LabBenchmarks = AgenticaLab::Agentica.Lab.Benchmarks;

namespace Agentica.Tests;

public sealed class BenchmarkFoundationTests
{
    [Fact]
    public void Product_proof_matrix_is_fixed_versioned_unique_and_complete()
    {
        var matrix = LabBenchmarks.ProductProofBenchmarkMatrix.Current;

        Assert.Equal("agentica-product-proof-v1", matrix.Version);
        Assert.Equal(6, matrix.Cases.Count);
        Assert.Equal(30, matrix.Runs.Count);
        Assert.Equal(matrix.Cases.Count, matrix.Cases.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(matrix.Runs.Count, matrix.Runs.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(matrix.Cases, item => Assert.Contains(matrix.Version, item.CaseId, StringComparison.Ordinal));
        Assert.All(matrix.Runs, item =>
        {
            Assert.Equal(matrix.Version, item.MatrixVersion);
            Assert.Contains(matrix.Version, item.RunId, StringComparison.Ordinal);
        });

        var workbench = matrix.Cases
            .Where(item => item.Suite == LabBenchmarks.BenchmarkSuiteKind.PrimaryWorkbench)
            .ToArray();
        Assert.Equal(5, workbench.Length);
        Assert.Equal(
            ["broken_check", "missing_mapping", "release_gate", "structured_doc_merge", "word_ladder"],
            workbench.Select(item => item.ScenarioId).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(25, matrix.Runs.Count(item =>
            item.Suite == LabBenchmarks.BenchmarkSuiteKind.PrimaryWorkbench));

        var holdout = Assert.Single(matrix.Cases, item =>
            item.Suite == LabBenchmarks.BenchmarkSuiteKind.GeneralizationHoldout);
        Assert.Equal("unlock", holdout.ScenarioId);
        Assert.Equal("173", holdout.Parameters["seed"]);
        Assert.Equal("7", holdout.Parameters["width"]);
        Assert.Equal("7", holdout.Parameters["height"]);
        Assert.Equal("2", holdout.Parameters["visibility"]);
        Assert.Equal(5, matrix.Runs.Count(item => item.CaseId == holdout.CaseId));

        foreach (var benchmarkCase in matrix.Cases)
        {
            Assert.Equal(
                [1, 2, 3, 4, 5],
                matrix.Runs
                    .Where(item => item.CaseId == benchmarkCase.CaseId)
                    .Select(item => item.Repetition)
                    .Order()
                    .ToArray());
        }
    }

    [Fact]
    public async Task Measured_client_captures_logical_success_metadata_usage_retry_and_repair()
    {
        var usage = new LlmUsage(
            PromptTokens: 100,
            OutputTokens: 20,
            ThinkingTokens: 5,
            TotalTokens: 125);
        var inner = new StubLlmClient((_, _) => Task.FromResult(new LlmResponse(
            ProviderName: "google",
            ModelId: LabBenchmarks.ProductProofPricing.Gemini25FlashModelId,
            Text: "{}",
            StructuredJson: "{}",
            Usage: usage,
            FinishReason: LlmFinishReason.Stop,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LabBenchmarks.MeasuredLlmClient.RetryAttemptsMetadataKey] = "3"
            })));
        var measured = new LabBenchmarks.MeasuredLlmClient(inner);
        var request = Request(
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowPlanPromptBuilder.PromptVersionMetadataKey] = "prompt-v1",
                [WorkflowPlanPromptBuilder.SchemaVersionMetadataKey] = "schema-v1",
                [WorkflowPlanPromptBuilder.RequestKindMetadataKey] = "initial_plan",
                [LabBenchmarks.MeasuredLlmClient.RepairAttemptMetadataKey] = "1"
            });

        var response = await measured.GenerateAsync(request);

        Assert.Equal("{}", response.StructuredJson);
        var call = Assert.Single(measured.Snapshot());
        Assert.StartsWith(LabBenchmarks.MeasuredLlmClient.CallIdVersion + "/", call.CallId, StringComparison.Ordinal);
        Assert.True(call.StartedAtUtc > DateTimeOffset.MinValue);
        Assert.True(call.Latency >= TimeSpan.Zero);
        Assert.True(call.Succeeded);
        Assert.Equal("google", call.ProviderName);
        Assert.Equal(request.ModelId, call.RequestedModelId);
        Assert.Equal(request.ModelId, call.ResponseModelId);
        Assert.Equal(LlmFinishReason.Stop, call.FinishReason);
        Assert.Same(usage, call.Usage);
        Assert.Equal("prompt-v1", call.PromptVersion);
        Assert.Equal("schema-v1", call.SchemaVersion);
        Assert.Equal("initial_plan", call.RequestKind);
        Assert.True(call.IsRepair);
        Assert.Equal(3, call.RetryAttempts);
        Assert.Null(call.ErrorClass);
    }

    [Fact]
    public async Task Measured_client_records_logical_failure_and_rethrows_original_exception()
    {
        var failure = new LlmClientException(
            "google",
            "provider exhausted",
            errorKind: LlmClientErrorKind.ServerError,
            attempts: 4,
            errorClass: "server_error");
        var measured = new LabBenchmarks.MeasuredLlmClient(
            new StubLlmClient((_, _) => Task.FromException<LlmResponse>(failure)));

        var thrown = await Assert.ThrowsAsync<LlmClientException>(() => measured.GenerateAsync(Request()));

        Assert.Same(failure, thrown);
        var call = Assert.Single(measured.Snapshot());
        Assert.False(call.Succeeded);
        Assert.Equal("google", call.ProviderName);
        Assert.Null(call.ResponseModelId);
        Assert.Equal(LlmFinishReason.Error, call.FinishReason);
        Assert.Equal(4, call.RetryAttempts);
        Assert.Equal("server_error", call.ErrorClass);
    }

    [Fact]
    public void Pricing_is_exact_reviewed_and_separates_cached_and_uncached_input()
    {
        var usage = new LlmUsage(
            PromptTokens: 1_000_000,
            OutputTokens: 100_000,
            ThinkingTokens: 20_000,
            TotalTokens: 1_120_000);

        Assert.True(LabBenchmarks.LlmCostCalculator.TryCalculate(
            LabBenchmarks.ProductProofPricing.Gemini25FlashModelId,
            usage,
            LabBenchmarks.ProductProofPricing.Current,
            out var cost));
        Assert.Equal(1_000_000, cost.UncachedInputTokens);
        Assert.Equal(0, cost.CachedInputTokens);
        Assert.Equal(0.30m, cost.UncachedInputCostUsd);
        Assert.Equal(0m, cost.CachedInputCostUsd);
        Assert.Equal(0.30m, cost.OutputCostUsd);
        Assert.Equal(0.60m, cost.TotalCostUsd);
        Assert.Equal("gemini-api-standard-pricing-2026-07-21-v2", cost.PricingSnapshotId);
        Assert.Equal(new DateOnly(2026, 7, 21), LabBenchmarks.ProductProofPricing.Current.ReviewedOn);
        Assert.Equal(
            "https://ai.google.dev/gemini-api/docs/pricing",
            LabBenchmarks.ProductProofPricing.Current.SourceUrl);

        Assert.False(LabBenchmarks.LlmCostCalculator.TryCalculate(
            "gemini-2.5-flash-latest",
            usage,
            LabBenchmarks.ProductProofPricing.Current,
            out _));
        Assert.True(LabBenchmarks.LlmCostCalculator.TryCalculate(
            LabBenchmarks.ProductProofPricing.Gemini25FlashModelId,
            usage with { CachedPromptTokens = 400_000 },
            LabBenchmarks.ProductProofPricing.Current,
            out var cachedCost));
        Assert.Equal(600_000, cachedCost.UncachedInputTokens);
        Assert.Equal(400_000, cachedCost.CachedInputTokens);
        Assert.Equal(0.18m, cachedCost.UncachedInputCostUsd);
        Assert.Equal(0.012m, cachedCost.CachedInputCostUsd);
        Assert.Equal(0.30m, cachedCost.OutputCostUsd);
        Assert.Equal(0.492m, cachedCost.TotalCostUsd);

        Assert.False(LabBenchmarks.LlmCostCalculator.TryCalculate(
            LabBenchmarks.ProductProofPricing.Gemini25FlashModelId,
            usage with { CachedPromptTokens = 1_000_001 },
            LabBenchmarks.ProductProofPricing.Current,
            out _));
        Assert.False(LabBenchmarks.LlmCostCalculator.TryCalculate(
            LabBenchmarks.ProductProofPricing.Gemini25FlashModelId,
            usage with { CachedPromptTokens = -1 },
            LabBenchmarks.ProductProofPricing.Current,
            out _));

        var explicitPricing = new LabBenchmarks.LlmPricingSnapshot(
            "reviewed-other-model-v1",
            new DateOnly(2026, 7, 21),
            "https://pricing.example.test/review",
            [new LabBenchmarks.LlmModelPricing("other-model", 1m, 0.1m, 2m)]);
        Assert.True(LabBenchmarks.LlmCostCalculator.TryCalculate(
            "other-model",
            usage,
            explicitPricing,
            out _));
    }

    [Fact]
    public void Strict_aggregate_accepts_complete_cohort_and_reports_all_required_metrics()
    {
        var matrix = LabBenchmarks.ProductProofBenchmarkMatrix.Current;
        var results = PassingResults(matrix);
        results[0] = results[0] with
        {
            InvalidPlan = true,
            RuntimeRetryCount = 2,
            LlmCalls =
            [
                results[0].LlmCalls[0] with
                {
                    IsRepair = true,
                    RetryAttempts = 3
                }
            ]
        };

        var report = LabBenchmarks.StrictBenchmarkAggregator.Aggregate(
            matrix,
            results,
            LabBenchmarks.ProductProofPricing.Current);

        Assert.True(report.GatePassed);
        Assert.Empty(report.GateFailures);
        Assert.Equal(LabBenchmarks.ProductProofPricing.SnapshotId, report.PricingSnapshotId);
        Assert.Equal(LabBenchmarks.ProductProofPricing.Current.ReviewedOn, report.PricingReviewedOn);
        Assert.Equal(LabBenchmarks.ProductProofPricing.Current.SourceUrl, report.PricingSourceUrl);
        Assert.Equal(30, report.Overall.RunCount);
        Assert.Equal(30, report.Overall.VerifiedSuccessCount);
        Assert.Equal(1m, report.Overall.SuccessRate);
        Assert.Equal(0, report.Overall.FalseSuccessCount);
        Assert.Equal(1, report.Overall.InvalidPlanCount);
        Assert.Equal(2, report.Overall.RuntimeRetryCount);
        Assert.Equal(TimeSpan.FromSeconds(465), report.Overall.RunElapsed.Total);
        Assert.Equal(TimeSpan.FromMilliseconds(15_500), report.Overall.RunElapsed.Mean);
        Assert.Equal(TimeSpan.FromSeconds(15), report.Overall.RunElapsed.P50);
        Assert.Equal(TimeSpan.FromSeconds(29), report.Overall.RunElapsed.P95);
        Assert.Equal(30, report.Overall.LlmCallCount);
        Assert.Equal(2, report.Overall.LlmRetryCount);
        Assert.Equal(1, report.Overall.RepairCallCount);
        Assert.Equal(new LabBenchmarks.BenchmarkTokenTotals(3_000, 600, 150, 3_750), report.Overall.Tokens);
        Assert.True(report.Overall.CostAvailable);
        Assert.Equal(0.002775m, report.Overall.EstimatedCostUsd);
        Assert.Equal(25, report.Primary.RunCount);
        Assert.Equal(5, report.Holdout.RunCount);
        Assert.Equal(6, report.Cases.Count);
        Assert.All(report.Cases, item => Assert.Equal(1m, item.Metrics.SuccessRate));
    }

    [Fact]
    public void Strict_gate_rejects_false_success_and_each_success_threshold()
    {
        var matrix = LabBenchmarks.ProductProofBenchmarkMatrix.Current;

        var falseSuccess = PassingResults(matrix);
        falseSuccess[0] = Failed(falseSuccess[0], reportedSuccess: true);
        AssertGateFailure(matrix, falseSuccess, "false_successes:");

        var primaryBelowEighty = PassingResults(matrix);
        foreach (var index in Enumerable.Range(0, 6))
        {
            primaryBelowEighty[index] = Failed(primaryBelowEighty[index]);
        }
        AssertGateFailure(matrix, primaryBelowEighty, "primary_success_rate:");

        var caseBelowSixty = PassingResults(matrix);
        foreach (var index in Enumerable.Range(0, 3))
        {
            caseBelowSixty[index] = Failed(caseBelowSixty[index]);
        }
        var caseReport = LabBenchmarks.StrictBenchmarkAggregator.Aggregate(
            matrix,
            caseBelowSixty,
            LabBenchmarks.ProductProofPricing.Current);
        Assert.False(caseReport.GatePassed);
        Assert.Contains(caseReport.GateFailures, item => item.StartsWith("workbench_case_success_rate:", StringComparison.Ordinal));
        Assert.DoesNotContain(caseReport.GateFailures, item => item.StartsWith("primary_success_rate:", StringComparison.Ordinal));

        var holdoutBelowSixty = PassingResults(matrix);
        foreach (var index in Enumerable.Range(25, 3))
        {
            holdoutBelowSixty[index] = Failed(holdoutBelowSixty[index]);
        }
        AssertGateFailure(matrix, holdoutBelowSixty, "holdout_success_rate:");
    }

    [Fact]
    public void Strict_aggregate_rejects_incomplete_duplicate_and_mixed_cohorts()
    {
        var matrix = LabBenchmarks.ProductProofBenchmarkMatrix.Current;
        var passing = PassingResults(matrix);

        Assert.Throws<LabBenchmarks.BenchmarkCohortValidationException>(() =>
            LabBenchmarks.StrictBenchmarkAggregator.Aggregate(
                matrix,
                passing[..^1],
                LabBenchmarks.ProductProofPricing.Current));

        var duplicate = passing.ToArray();
        duplicate[^1] = duplicate[0];
        Assert.Throws<LabBenchmarks.BenchmarkCohortValidationException>(() =>
            LabBenchmarks.StrictBenchmarkAggregator.Aggregate(
                matrix,
                duplicate,
                LabBenchmarks.ProductProofPricing.Current));

        var mixed = passing.ToArray();
        mixed[^1] = mixed[^1] with
        {
            Cohort = mixed[^1].Cohort with { ConfigurationId = "different-config-v1" }
        };
        Assert.Throws<LabBenchmarks.BenchmarkCohortValidationException>(() =>
            LabBenchmarks.StrictBenchmarkAggregator.Aggregate(
                matrix,
                mixed,
                LabBenchmarks.ProductProofPricing.Current));
    }

    [Fact]
    public void Strict_aggregate_rejects_mixed_or_unapproved_prompt_schema_versions()
    {
        var matrix = LabBenchmarks.ProductProofBenchmarkMatrix.Current;
        var results = PassingResults(matrix);
        results[^1] = results[^1] with
        {
            LlmCalls =
            [
                results[^1].LlmCalls[0] with
                {
                    PromptVersion = WorkflowPlanPromptBuilder.RefinementPromptVersion,
                    RequestKind = WorkflowPlanPromptBuilder.InitialRequestKind
                }
            ]
        };

        Assert.Throws<LabBenchmarks.BenchmarkCohortValidationException>(() =>
            LabBenchmarks.StrictBenchmarkAggregator.Aggregate(
                matrix,
                results,
                LabBenchmarks.ProductProofPricing.Current));

        var contradictoryStatus = PassingResults(matrix);
        contradictoryStatus[0] = contradictoryStatus[0] with { RunOutcomeStatus = "Failed" };
        Assert.Throws<LabBenchmarks.BenchmarkCohortValidationException>(() =>
            LabBenchmarks.StrictBenchmarkAggregator.Aggregate(
                matrix,
                contradictoryStatus,
                LabBenchmarks.ProductProofPricing.Current));
    }

    [Fact]
    public void Unknown_exact_model_pricing_makes_the_gate_invalid()
    {
        var matrix = LabBenchmarks.ProductProofBenchmarkMatrix.Current;
        var cohort = Cohort(modelId: "unpriced-model-v1");
        var results = PassingResults(matrix, cohort);

        var report = LabBenchmarks.StrictBenchmarkAggregator.Aggregate(
            matrix,
            results,
            LabBenchmarks.ProductProofPricing.Current);

        Assert.False(report.GatePassed);
        Assert.False(report.Overall.CostAvailable);
        Assert.Null(report.Overall.EstimatedCostUsd);
        Assert.Contains(report.GateFailures, item => item.StartsWith("cost_unavailable:", StringComparison.Ordinal));
    }

    private static void AssertGateFailure(
        LabBenchmarks.BenchmarkMatrix matrix,
        IReadOnlyCollection<LabBenchmarks.BenchmarkRunResult> results,
        string prefix)
    {
        var report = LabBenchmarks.StrictBenchmarkAggregator.Aggregate(
            matrix,
            results,
            LabBenchmarks.ProductProofPricing.Current);
        Assert.False(report.GatePassed);
        Assert.Contains(report.GateFailures, item => item.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static LabBenchmarks.BenchmarkRunResult Failed(
        LabBenchmarks.BenchmarkRunResult result,
        bool reportedSuccess = false) =>
        result with
        {
            RunOutcomeStatus = reportedSuccess ? "Succeeded" : "Failed",
            ReportedSuccess = reportedSuccess,
            OracleSuccess = false,
            OracleEvidence = null,
            OracleFailure = "Independent oracle rejected the claimed outcome."
        };

    private static LabBenchmarks.BenchmarkRunResult[] PassingResults(
        LabBenchmarks.BenchmarkMatrix matrix,
        LabBenchmarks.BenchmarkCohortIdentity? cohort = null)
    {
        cohort ??= Cohort();
        return matrix.Runs.Select((run, index) =>
        {
            var usage = new LlmUsage(
                PromptTokens: 100,
                OutputTokens: 20,
                ThinkingTokens: 5,
                TotalTokens: 125);
            var call = new LabBenchmarks.BenchmarkLlmCallTelemetry(
                CallId: $"benchmark-llm-call-v1/{index:000}",
                StartedAtUtc: new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero).AddMinutes(index),
                Latency: TimeSpan.FromMilliseconds(index + 1),
                Succeeded: true,
                RequestedModelId: cohort.ModelId,
                ProviderName: cohort.ProviderName,
                ResponseModelId: cohort.ModelId,
                FinishReason: LlmFinishReason.Stop,
                Usage: usage,
                PromptVersion: WorkflowPlanPromptBuilder.InitialPromptVersion,
                SchemaVersion: WorkflowPlanPromptBuilder.InitialSchemaVersion,
                RequestKind: WorkflowPlanPromptBuilder.InitialRequestKind,
                IsRepair: false,
                RetryAttempts: 1,
                ErrorClass: null);
            return new LabBenchmarks.BenchmarkRunResult(
                Cohort: cohort,
                RunId: run.RunId,
                RunOutcomeStatus: "Succeeded",
                ReportedSuccess: true,
                OracleSuccess: true,
                InvalidPlan: false,
                Elapsed: TimeSpan.FromSeconds(index + 1),
                RuntimeRetryCount: 0,
                OracleEvidence: "Independent terminal artifact and authoritative state agree.",
                OracleFailure: null,
                LlmCalls: [call]);
        }).ToArray();
    }

    private static LabBenchmarks.BenchmarkCohortIdentity Cohort(string? modelId = null) =>
        new(
            CohortId: "product-proof-cohort-v1/test-001",
            MatrixVersion: LabBenchmarks.ProductProofBenchmarkMatrix.Version,
            ProviderName: "google",
            ModelId: modelId ?? LabBenchmarks.ProductProofPricing.Gemini25FlashModelId,
            ConfigurationId: "gemini-2.5-flash-default-v1");

    private static LlmRequest Request(IReadOnlyDictionary<string, string>? metadata = null) =>
        new(
            LabBenchmarks.ProductProofPricing.Gemini25FlashModelId,
            [new LlmMessage(LlmMessageRole.User, "plan")],
            StructuredOutput: new LlmStructuredOutputOptions(JsonSchema: "{}"),
            Metadata: metadata ?? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [WorkflowPlanPromptBuilder.PromptVersionMetadataKey] = "prompt-v1",
                [WorkflowPlanPromptBuilder.SchemaVersionMetadataKey] = "schema-v1",
                [WorkflowPlanPromptBuilder.RequestKindMetadataKey] = "initial_plan"
            });

    private sealed class StubLlmClient : ILlmClient
    {
        private readonly Func<LlmRequest, CancellationToken, Task<LlmResponse>> _generate;

        public StubLlmClient(Func<LlmRequest, CancellationToken, Task<LlmResponse>> generate)
        {
            _generate = generate;
        }

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default) =>
            _generate(request, cancellationToken);
    }
}
