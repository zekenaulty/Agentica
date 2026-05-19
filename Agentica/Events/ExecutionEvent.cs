using Agentica.Observations;

namespace Agentica.Events;

public sealed record ExecutionEvent(
    string EventId,
    string Type,
    DateTimeOffset At,
    IReadOnlyDictionary<string, string> Data)
{
    public long? Sequence { get; init; }

    public string? Source { get; init; }

    public ExecutionEventContext? Context { get; init; }

    public ExecutionIntent? Intent { get; init; }

    public UserFacingReason? UserFacingReason { get; init; }

    public IReadOnlyList<EvidenceRef> EvidenceRefs { get; init; } = [];

    public IReadOnlyDictionary<string, object?> Payload { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public ExecutionDiagnostics? Diagnostics { get; init; }
}
