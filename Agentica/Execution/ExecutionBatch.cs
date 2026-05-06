namespace Agentica.Execution;

public sealed record ExecutionBatch(
    string BatchId,
    IReadOnlyList<string> StepIds,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
