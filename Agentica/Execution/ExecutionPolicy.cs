using Agentica.Planning;

namespace Agentica.Execution;

public sealed record ExecutionPolicy(
    int MaxSteps = 10,
    int MaxRefinements = 2,
    TimeSpan? Timeout = null,
    PlanningMode PlanningMode = PlanningMode.Stepwise,
    int MaxPlanContinuations = 0,
    ToolEffectPolicy? EffectPolicy = null,
    PlanningContextOptions? PlanningContext = null,
    int MaxBlockedRetries = 2,
    int MaxBatchSize = 8,
    int MaxParallelism = 8,
    bool AllowReadOnlyParallelBatches = true,
    bool EvaluateCompletionAfterEachBatch = false,
    BlockedRetryPolicy? BlockedRetries = null,
    ToolSecurityPolicy? SecurityPolicy = null,
    TimeSpan? EventSinkDeliveryTimeout = null)
{
    private static readonly TimeSpan DefaultEventSinkDeliveryTimeout = TimeSpan.FromSeconds(1);

    public static ExecutionPolicy Default { get; } = new();

    public ToolEffectPolicy EffectiveEffectPolicy => EffectPolicy ?? ToolEffectPolicy.LocalOnly;

    public PlanningContextOptions EffectivePlanningContext => PlanningContext ?? PlanningContextOptions.FullHistory;

    public BlockedRetryPolicy EffectiveBlockedRetries => BlockedRetries ?? BlockedRetryPolicy.Default;

    public ToolSecurityPolicy EffectiveSecurityPolicy => SecurityPolicy ?? ToolSecurityPolicy.Local;

    /// <summary>
    /// Bounds each best-effort observer callback. A timed-out callback is detached and the sink is
    /// circuit-broken for the rest of the attempt; it cannot delay authoritative execution again.
    /// </summary>
    public TimeSpan EffectiveEventSinkDeliveryTimeout =>
        EventSinkDeliveryTimeout ?? DefaultEventSinkDeliveryTimeout;
}
