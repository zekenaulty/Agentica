using Agentica.Clients.Llm;

namespace Agentica.Lab.Benchmarks;

public enum BenchmarkSuiteKind
{
    PrimaryWorkbench,
    GeneralizationHoldout
}

public sealed record BenchmarkCaseDefinition(
    string CaseId,
    BenchmarkSuiteKind Suite,
    string ScenarioId,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record BenchmarkRunDefinition(
    string RunId,
    string MatrixVersion,
    string CaseId,
    BenchmarkSuiteKind Suite,
    string ScenarioId,
    int Repetition,
    IReadOnlyDictionary<string, string> Parameters);

public sealed record BenchmarkMatrix(
    string Version,
    IReadOnlyList<BenchmarkCaseDefinition> Cases,
    IReadOnlyList<BenchmarkRunDefinition> Runs);

public sealed record BenchmarkCohortIdentity(
    string CohortId,
    string MatrixVersion,
    string ProviderName,
    string ModelId,
    string ConfigurationId);

public sealed record BenchmarkRunResult(
    BenchmarkCohortIdentity Cohort,
    string RunId,
    string RunOutcomeStatus,
    bool ReportedSuccess,
    bool OracleSuccess,
    bool InvalidPlan,
    TimeSpan Elapsed,
    int RuntimeRetryCount,
    string? OracleEvidence,
    string? OracleFailure,
    IReadOnlyList<BenchmarkLlmCallTelemetry> LlmCalls);

public sealed record BenchmarkLlmCallTelemetry(
    string CallId,
    DateTimeOffset StartedAtUtc,
    TimeSpan Latency,
    bool Succeeded,
    string RequestedModelId,
    string? ProviderName,
    string? ResponseModelId,
    LlmFinishReason FinishReason,
    LlmUsage? Usage,
    string? PromptVersion,
    string? SchemaVersion,
    string? RequestKind,
    bool IsRepair,
    int RetryAttempts,
    string? ErrorClass);

public sealed record BenchmarkDurationSummary(
    TimeSpan Total,
    TimeSpan Mean,
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan Maximum);

public sealed record BenchmarkTokenTotals(
    long PromptTokens,
    long OutputTokens,
    long ThinkingTokens,
    long TotalTokens);

public sealed record BenchmarkMetrics(
    int RunCount,
    int VerifiedSuccessCount,
    decimal SuccessRate,
    int FalseSuccessCount,
    decimal FalseSuccessRate,
    int InvalidPlanCount,
    decimal InvalidPlanRate,
    int RuntimeRetryCount,
    BenchmarkDurationSummary RunElapsed,
    int LlmCallCount,
    int FailedLlmCallCount,
    int LlmRetryCount,
    int RepairCallCount,
    BenchmarkDurationSummary LlmLatency,
    BenchmarkTokenTotals Tokens,
    bool CostAvailable,
    decimal? EstimatedCostUsd);

public sealed record BenchmarkCaseReport(
    BenchmarkCaseDefinition Case,
    BenchmarkMetrics Metrics);

public sealed record BenchmarkReport(
    string MatrixVersion,
    BenchmarkCohortIdentity Cohort,
    string PricingSnapshotId,
    DateOnly PricingReviewedOn,
    string PricingSourceUrl,
    BenchmarkMetrics Overall,
    BenchmarkMetrics Primary,
    BenchmarkMetrics Holdout,
    IReadOnlyList<BenchmarkCaseReport> Cases,
    bool GatePassed,
    IReadOnlyList<string> GateFailures);

public sealed class BenchmarkCohortValidationException : InvalidOperationException
{
    public BenchmarkCohortValidationException(string message)
        : base(message)
    {
    }
}
