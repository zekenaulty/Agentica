using Agentica.Clients.Llm;
using Agentica.Clients.Planning;

namespace Agentica.Lab.Benchmarks;

public static class StrictBenchmarkAggregator
{
    public const decimal MinimumPrimarySuccessRate = 0.80m;
    public const decimal MinimumWorkbenchCaseSuccessRate = 0.60m;
    public const decimal MinimumHoldoutSuccessRate = 0.60m;

    public static BenchmarkReport Aggregate(
        BenchmarkMatrix matrix,
        IReadOnlyCollection<BenchmarkRunResult> results,
        LlmPricingSnapshot pricing)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(pricing);

        var runById = ValidateMatrix(matrix);
        var materialized = results.ToArray();
        var cohort = ValidateCohort(matrix, runById, materialized);

        var overall = BuildMetrics(materialized, pricing);
        var primaryResults = SelectSuite(
            materialized,
            runById,
            BenchmarkSuiteKind.PrimaryWorkbench);
        var holdoutResults = SelectSuite(
            materialized,
            runById,
            BenchmarkSuiteKind.GeneralizationHoldout);
        var primary = BuildMetrics(primaryResults, pricing);
        var holdout = BuildMetrics(holdoutResults, pricing);

        var cases = matrix.Cases
            .Select(benchmarkCase => new BenchmarkCaseReport(
                benchmarkCase,
                BuildMetrics(
                    materialized
                        .Where(result => string.Equals(
                            runById[result.RunId].CaseId,
                            benchmarkCase.CaseId,
                            StringComparison.Ordinal))
                        .ToArray(),
                    pricing)))
            .ToArray();

        var gateFailures = EvaluateGate(materialized, overall, primary, holdout, cases);
        return new BenchmarkReport(
            matrix.Version,
            cohort,
            pricing.SnapshotId,
            pricing.ReviewedOn,
            pricing.SourceUrl,
            overall,
            primary,
            holdout,
            Array.AsReadOnly(cases),
            GatePassed: gateFailures.Count == 0,
            GateFailures: Array.AsReadOnly(gateFailures.ToArray()));
    }

    private static IReadOnlyDictionary<string, BenchmarkRunDefinition> ValidateMatrix(BenchmarkMatrix matrix)
    {
        if (string.IsNullOrWhiteSpace(matrix.Version) ||
            matrix.Cases is null ||
            matrix.Cases.Count == 0 ||
            matrix.Runs is null ||
            matrix.Runs.Count == 0)
        {
            throw Invalid("The benchmark matrix is incomplete.");
        }

        var cases = new Dictionary<string, BenchmarkCaseDefinition>(StringComparer.Ordinal);
        foreach (var benchmarkCase in matrix.Cases)
        {
            if (string.IsNullOrWhiteSpace(benchmarkCase.CaseId) ||
                !benchmarkCase.CaseId.Contains(matrix.Version, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(benchmarkCase.ScenarioId) ||
                benchmarkCase.Parameters is null ||
                !cases.TryAdd(benchmarkCase.CaseId, benchmarkCase))
            {
                throw Invalid("Benchmark case ids must be unique, versioned, and complete.");
            }
        }

        var runs = new Dictionary<string, BenchmarkRunDefinition>(StringComparer.Ordinal);
        foreach (var run in matrix.Runs)
        {
            if (string.IsNullOrWhiteSpace(run.RunId) ||
                !run.RunId.Contains(matrix.Version, StringComparison.Ordinal) ||
                !string.Equals(run.MatrixVersion, matrix.Version, StringComparison.Ordinal) ||
                run.Repetition <= 0 ||
                run.Parameters is null ||
                !cases.TryGetValue(run.CaseId, out var benchmarkCase) ||
                run.Suite != benchmarkCase.Suite ||
                !string.Equals(run.ScenarioId, benchmarkCase.ScenarioId, StringComparison.Ordinal) ||
                !runs.TryAdd(run.RunId, run))
            {
                throw Invalid("Benchmark run ids must be unique, versioned, and consistent with their cases.");
            }
        }

        if (cases.Keys.Any(caseId => runs.Values.All(run =>
                !string.Equals(run.CaseId, caseId, StringComparison.Ordinal))))
        {
            throw Invalid("Every benchmark case must contain at least one run.");
        }

        return runs;
    }

    private static BenchmarkCohortIdentity ValidateCohort(
        BenchmarkMatrix matrix,
        IReadOnlyDictionary<string, BenchmarkRunDefinition> runById,
        IReadOnlyList<BenchmarkRunResult> results)
    {
        if (results.Count != runById.Count)
        {
            throw Invalid($"Incomplete cohort: expected {runById.Count} runs and received {results.Count}.");
        }

        var resultsByRun = new Dictionary<string, BenchmarkRunResult>(StringComparer.Ordinal);
        var callIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.RunId) || !resultsByRun.TryAdd(result.RunId, result))
            {
                throw Invalid("Duplicate or empty benchmark run id.");
            }

            if (!runById.ContainsKey(result.RunId))
            {
                throw Invalid($"Run '{result.RunId}' is not part of matrix '{matrix.Version}'.");
            }

            ValidateRunResult(result, callIds);
        }

        var missing = runById.Keys.Where(runId => !resultsByRun.ContainsKey(runId)).ToArray();
        if (missing.Length > 0)
        {
            throw Invalid($"Incomplete cohort; missing run '{missing[0]}'.");
        }

        var cohorts = results.Select(result => result.Cohort).Distinct().ToArray();
        if (cohorts.Length != 1)
        {
            throw Invalid("Mixed benchmark cohorts cannot be aggregated.");
        }

        var cohort = cohorts[0];
        if (string.IsNullOrWhiteSpace(cohort.CohortId) ||
            string.IsNullOrWhiteSpace(cohort.ProviderName) ||
            string.IsNullOrWhiteSpace(cohort.ModelId) ||
            string.IsNullOrWhiteSpace(cohort.ConfigurationId) ||
            !string.Equals(cohort.MatrixVersion, matrix.Version, StringComparison.Ordinal))
        {
            throw Invalid("Benchmark cohort identity is incomplete or uses a different matrix version.");
        }

        foreach (var call in results.SelectMany(result => result.LlmCalls))
        {
            if (!string.Equals(call.RequestedModelId, cohort.ModelId, StringComparison.Ordinal) ||
                call.Succeeded &&
                (!string.Equals(call.ProviderName, cohort.ProviderName, StringComparison.Ordinal) ||
                 !string.Equals(call.ResponseModelId, cohort.ModelId, StringComparison.Ordinal)))
            {
                throw Invalid("LLM telemetry contains mixed provider or model cohorts.");
            }
        }

        return cohort;
    }

    private static void ValidateRunResult(
        BenchmarkRunResult result,
        ISet<string> callIds)
    {
        if (result.Cohort is null ||
            string.IsNullOrWhiteSpace(result.RunOutcomeStatus) ||
            result.ReportedSuccess != string.Equals(
                result.RunOutcomeStatus,
                "Succeeded",
                StringComparison.Ordinal) ||
            result.Elapsed < TimeSpan.Zero ||
            result.RuntimeRetryCount < 0 ||
            result.LlmCalls is null ||
            result.LlmCalls.Count == 0)
        {
            throw Invalid($"Run '{result.RunId}' has incomplete outcome or telemetry data.");
        }

        if (result.OracleSuccess && string.IsNullOrWhiteSpace(result.OracleEvidence))
        {
            throw Invalid($"Run '{result.RunId}' lacks authoritative success evidence.");
        }

        if (result.OracleSuccess && !string.IsNullOrWhiteSpace(result.OracleFailure))
        {
            throw Invalid($"Run '{result.RunId}' contains contradictory oracle verdict data.");
        }

        if (!result.OracleSuccess && string.IsNullOrWhiteSpace(result.OracleFailure))
        {
            throw Invalid($"Run '{result.RunId}' lacks an authoritative failure explanation.");
        }

        foreach (var call in result.LlmCalls)
        {
            if (call is null ||
                string.IsNullOrWhiteSpace(call.CallId) ||
                !callIds.Add(call.CallId) ||
                call.StartedAtUtc == default ||
                call.Latency < TimeSpan.Zero ||
                string.IsNullOrWhiteSpace(call.RequestedModelId) ||
                string.IsNullOrWhiteSpace(call.PromptVersion) ||
                string.IsNullOrWhiteSpace(call.SchemaVersion) ||
                string.IsNullOrWhiteSpace(call.RequestKind) ||
                !UsesApprovedPlannerContract(call) ||
                call.RetryAttempts <= 0 ||
                HasNegativeUsage(call.Usage) ||
                call.Succeeded &&
                (string.IsNullOrWhiteSpace(call.ProviderName) ||
                 string.IsNullOrWhiteSpace(call.ResponseModelId)))
            {
                throw Invalid($"Run '{result.RunId}' contains incomplete or duplicate LLM telemetry.");
            }
        }
    }

    private static bool HasNegativeUsage(LlmUsage? usage) =>
        usage is not null &&
        (usage.PromptTokens is < 0 ||
         usage.OutputTokens is < 0 ||
         usage.ThinkingTokens is < 0 ||
         usage.TotalTokens is < 0 ||
         usage.CachedPromptTokens is < 0 ||
         usage.ToolUsePromptTokens is < 0);

    private static bool UsesApprovedPlannerContract(BenchmarkLlmCallTelemetry call) =>
        call.RequestKind switch
        {
            WorkflowPlanPromptBuilder.InitialRequestKind =>
                string.Equals(
                    call.PromptVersion,
                    WorkflowPlanPromptBuilder.InitialPromptVersion,
                    StringComparison.Ordinal) &&
                string.Equals(
                    call.SchemaVersion,
                    WorkflowPlanPromptBuilder.InitialSchemaVersion,
                    StringComparison.Ordinal),
            WorkflowPlanPromptBuilder.RefinementRequestKind =>
                string.Equals(
                    call.PromptVersion,
                    WorkflowPlanPromptBuilder.RefinementPromptVersion,
                    StringComparison.Ordinal) &&
                string.Equals(
                    call.SchemaVersion,
                    WorkflowPlanPromptBuilder.RefinementSchemaVersion,
                    StringComparison.Ordinal),
            _ => false
        };

    private static BenchmarkRunResult[] SelectSuite(
        IEnumerable<BenchmarkRunResult> results,
        IReadOnlyDictionary<string, BenchmarkRunDefinition> runById,
        BenchmarkSuiteKind suite) =>
        results.Where(result => runById[result.RunId].Suite == suite).ToArray();

    private static BenchmarkMetrics BuildMetrics(
        IReadOnlyCollection<BenchmarkRunResult> results,
        LlmPricingSnapshot pricing)
    {
        var calls = results.SelectMany(result => result.LlmCalls).ToArray();
        var verifiedSuccesses = results.Count(result => result.ReportedSuccess && result.OracleSuccess);
        var falseSuccesses = results.Count(result => result.ReportedSuccess && !result.OracleSuccess);
        var invalidPlans = results.Count(result => result.InvalidPlan);
        var tokens = TokenTotals(calls);
        var costAvailable = calls.Length > 0;
        decimal cost = 0;

        foreach (var call in calls)
        {
            if (!call.Succeeded ||
                call.FinishReason == LlmFinishReason.Unknown ||
                !LlmCostCalculator.TryCalculate(
                    call.ResponseModelId ?? call.RequestedModelId,
                    call.Usage,
                    pricing,
                    out var callCost))
            {
                costAvailable = false;
                continue;
            }

            cost += callCost.TotalCostUsd;
        }

        return new BenchmarkMetrics(
            RunCount: results.Count,
            VerifiedSuccessCount: verifiedSuccesses,
            SuccessRate: Rate(verifiedSuccesses, results.Count),
            FalseSuccessCount: falseSuccesses,
            FalseSuccessRate: Rate(falseSuccesses, results.Count),
            InvalidPlanCount: invalidPlans,
            InvalidPlanRate: Rate(invalidPlans, results.Count),
            RuntimeRetryCount: results.Sum(result => result.RuntimeRetryCount),
            RunElapsed: Durations(results.Select(result => result.Elapsed)),
            LlmCallCount: calls.Length,
            FailedLlmCallCount: calls.Count(call => !call.Succeeded),
            LlmRetryCount: calls.Sum(call => Math.Max(0, call.RetryAttempts - 1)),
            RepairCallCount: calls.Count(call => call.IsRepair),
            LlmLatency: Durations(calls.Select(call => call.Latency)),
            Tokens: tokens,
            CostAvailable: costAvailable,
            EstimatedCostUsd: costAvailable ? cost : null);
    }

    private static List<string> EvaluateGate(
        IReadOnlyCollection<BenchmarkRunResult> results,
        BenchmarkMetrics overall,
        BenchmarkMetrics primary,
        BenchmarkMetrics holdout,
        IReadOnlyList<BenchmarkCaseReport> cases)
    {
        var failures = new List<string>();
        if (overall.FalseSuccessCount != 0)
        {
            failures.Add($"false_successes:{overall.FalseSuccessCount}; required:0");
        }

        if (primary.SuccessRate < MinimumPrimarySuccessRate)
        {
            failures.Add($"primary_success_rate:{primary.SuccessRate:P0}; required:{MinimumPrimarySuccessRate:P0}");
        }

        foreach (var benchmarkCase in cases.Where(item =>
                     item.Case.Suite == BenchmarkSuiteKind.PrimaryWorkbench &&
                     item.Metrics.SuccessRate < MinimumWorkbenchCaseSuccessRate))
        {
            failures.Add(
                $"workbench_case_success_rate:{benchmarkCase.Case.CaseId}:{benchmarkCase.Metrics.SuccessRate:P0}; required:{MinimumWorkbenchCaseSuccessRate:P0}");
        }

        if (holdout.SuccessRate < MinimumHoldoutSuccessRate)
        {
            failures.Add($"holdout_success_rate:{holdout.SuccessRate:P0}; required:{MinimumHoldoutSuccessRate:P0}");
        }

        if (!overall.CostAvailable)
        {
            failures.Add("cost_unavailable: exact reviewed pricing and complete valid usage are required");
        }

        if (results.SelectMany(result => result.LlmCalls)
            .Any(call => call.Succeeded && call.FinishReason == LlmFinishReason.Unknown))
        {
            failures.Add("finish_reason_unavailable: every successful logical LLM call must report a finish reason");
        }

        return failures;
    }

    private static BenchmarkTokenTotals TokenTotals(IEnumerable<BenchmarkLlmCallTelemetry> calls)
    {
        long prompt = 0;
        long output = 0;
        long thinking = 0;
        long total = 0;
        foreach (var usage in calls.Where(call => call.Succeeded).Select(call => call.Usage))
        {
            if (usage is null)
            {
                continue;
            }

            prompt = checked(prompt + (usage.PromptTokens ?? 0));
            output = checked(output + (usage.OutputTokens ?? 0));
            thinking = checked(thinking + (usage.ThinkingTokens ?? 0));
            total = checked(total + (usage.TotalTokens ??
                ((long)(usage.PromptTokens ?? 0) +
                 (usage.OutputTokens ?? 0) +
                 (usage.ThinkingTokens ?? 0))));
        }

        return new BenchmarkTokenTotals(prompt, output, thinking, total);
    }

    private static BenchmarkDurationSummary Durations(IEnumerable<TimeSpan> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return new BenchmarkDurationSummary(
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero);
        }

        var totalTicks = ordered.Aggregate(0L, (current, value) => checked(current + value.Ticks));
        return new BenchmarkDurationSummary(
            TimeSpan.FromTicks(totalTicks),
            TimeSpan.FromTicks(totalTicks / ordered.Length),
            Percentile(ordered, 0.50m),
            Percentile(ordered, 0.95m),
            ordered[^1]);
    }

    private static TimeSpan Percentile(IReadOnlyList<TimeSpan> ordered, decimal percentile)
    {
        var rank = (int)Math.Ceiling(percentile * ordered.Count);
        return ordered[Math.Clamp(rank - 1, 0, ordered.Count - 1)];
    }

    private static decimal Rate(int count, int total) =>
        total == 0 ? 0 : (decimal)count / total;

    private static BenchmarkCohortValidationException Invalid(string message) => new(message);
}
