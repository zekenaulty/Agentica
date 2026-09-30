namespace Agentica.Clients.Llm;

public enum LlmStreamEventKind
{
    Activity,
    TextDelta,
    ThoughtSummaryDelta,
    Completed
}

/// <summary>The completed event is required; deltas alone are never a successful call.</summary>
public sealed record LlmStreamEvent(
    LlmStreamEventKind Kind,
    string? Text = null,
    LlmResponse? Response = null);
