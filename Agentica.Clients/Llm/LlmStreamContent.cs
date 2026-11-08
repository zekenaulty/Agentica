namespace Agentica.Clients.Llm;

/// <summary>Observed provider coordinates. Null values were not supplied or not recognized.
/// Identity is scoped to one host call/attempt; source ordinals are not a capture-gap detector.</summary>
public sealed record LlmStreamSource(string Provider, string EventType, long? SequenceNumber = null,
    string? ItemId = null, int? OutputIndex = null, int? ContentIndex = null, int? SummaryIndex = null)
{
    public int Version { get; init; } = 1;
}

public enum LlmStreamBlockKind { OutputText, ReasoningSummary }
public enum LlmStreamBlockLifecycle { Started, Delta, TextCompleted, Completed }

/// <summary>A native readable content boundary or delta, never host acceptance.
/// TextSnapshot is a separate provider snapshot, not another delta to append.</summary>
public sealed record LlmStreamBlock(LlmStreamBlockKind Kind, LlmStreamBlockLifecycle Lifecycle,
    string? TextSnapshot = null, bool? Incomplete = null);
