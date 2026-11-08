using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

public enum LlmStreamEventKind
{
    Started,
    Activity,
    TextDelta,
    ThoughtSummaryDelta,
    Completed,
    Failed,
    Cancelled,
    ContentBlock
}

/// <summary>The completed event is required; deltas alone are never a successful call.</summary>
public sealed record LlmStreamEvent(
    LlmStreamEventKind Kind,
    string? Text = null,
    LlmResponse? Response = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmStreamSource? Source { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmStreamBlock? Block { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmWebSearchEvidence? WebSearch { get; init; }

    /// <summary>Optional observed attempt facts; UI delivery remains best effort.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmAttemptEvidence? AttemptEvidence { get; init; }
}
