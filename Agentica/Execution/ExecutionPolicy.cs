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
    bool EvaluateCompletionAfterEachBatch = false)
{
    public static ExecutionPolicy Default { get; } = new();

    public ToolEffectPolicy EffectiveEffectPolicy => EffectPolicy ?? ToolEffectPolicy.LocalOnly;

    public PlanningContextOptions EffectivePlanningContext => PlanningContext ?? PlanningContextOptions.FullHistory;
}
