namespace Agentica.Events;

public sealed record ExecutionDiagnostics(
    string? Code = null,
    string? Message = null,
    string? ErrorClass = null,
    string? FailureKind = null);
