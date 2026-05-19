namespace Agentica.Events;

public sealed record ExecutionIntent(
    string Action,
    string Rationale,
    string? ExpectedOutcome = null);
