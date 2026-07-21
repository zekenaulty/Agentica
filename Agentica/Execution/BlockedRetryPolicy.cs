using System.Collections.Frozen;
using Agentica.Outcomes;

namespace Agentica.Execution;

/// <summary>
/// Frozen retry authority. Mutation retry requires an exact authorized tool id here and an
/// idempotent declaration on the current tool registration.
/// </summary>
public sealed class BlockedRetryPolicy
{
    public BlockedRetryPolicy(
        IEnumerable<StopReason>? retryableStopReasons = null,
        IEnumerable<string>? authorizedMutationToolIds = null)
    {
        RetryableStopReasons = (retryableStopReasons ?? [StopReason.ToolUnavailable])
            .ToFrozenSet();
        AuthorizedMutationToolIds = (authorizedMutationToolIds ?? [])
            .ToFrozenSet(StringComparer.Ordinal);
    }

    public static BlockedRetryPolicy Default { get; } = new();

    public IReadOnlySet<StopReason> RetryableStopReasons { get; }

    public IReadOnlySet<string> AuthorizedMutationToolIds { get; }
}
