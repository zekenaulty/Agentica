namespace Agentica.Clients.Llm;

/// <summary>
/// Nonsecret host coordinates for one physical provider attempt. These references
/// correlate observations; they do not grant authority or establish durable custody.
/// </summary>
public sealed record LlmAttemptReferences
{
    public LlmAttemptReferences(string attemptId, string? operationId = null,
        string? phaseId = null, string? routeFingerprint = null,
        string? executionBundleReference = null)
    {
        AttemptId = LlmAttemptGuard.Required(attemptId, nameof(attemptId), 256);
        OperationId = LlmAttemptGuard.Optional(operationId, nameof(operationId), 256);
        PhaseId = LlmAttemptGuard.Optional(phaseId, nameof(phaseId), 256);
        RouteFingerprint = LlmAttemptGuard.Optional(routeFingerprint, nameof(routeFingerprint), 512);
        ExecutionBundleReference = LlmAttemptGuard.Optional(executionBundleReference,
            nameof(executionBundleReference), 512);
    }

    public string AttemptId { get; }
    public string? OperationId { get; }
    public string? PhaseId { get; }
    public string? RouteFingerprint { get; }
    public string? ExecutionBundleReference { get; }
}

internal static class LlmAttemptGuard
{
    public static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("Attempt evidence identity exceeds its bounds or contains control characters.",
                parameterName);
        return value;
    }

    public static string? Optional(string? value, string parameterName, int maximumLength) =>
        value is null ? null : Required(value, parameterName, maximumLength);

    public static string? Code(string? value, string parameterName)
    {
        if (value is null) return null;
        if (value.Length is < 1 or > 128 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("Attempt reason must be a bounded stable code.", parameterName);
        return value;
    }
}
