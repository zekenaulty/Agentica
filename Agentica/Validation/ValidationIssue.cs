namespace Agentica.Validation;

public sealed record ValidationIssue(
    string Code,
    string Message,
    string? StepId = null);
