namespace Agentica.Planning;

public static class PlanRefinementReasons
{
    public const string Observation = "observation";
    public const string Blocked = "blocked";
    public const string AmbiguousAction = "ambiguous_action";
    public const string LowConfidence = "low_confidence";
    public const string ConflictingSignals = "conflicting_signals";
    public const string CompletionCheck = "completion_check";
    public const string Continue = "continue";
    public const string ResourceRisk = "resource_risk";
    public const string RetryUnblock = "retry_unblock";

    private static readonly HashSet<string> KnownReasons =
    [
        Observation,
        Blocked,
        AmbiguousAction,
        LowConfidence,
        ConflictingSignals,
        CompletionCheck,
        Continue,
        ResourceRisk,
        RetryUnblock
    ];

    public static string Normalize(string? reason, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return fallback;
        }

        var normalized = reason.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return KnownReasons.Contains(normalized)
            ? normalized
            : fallback;
    }
}
