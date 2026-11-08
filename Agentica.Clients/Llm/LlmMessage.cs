using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

public sealed record LlmMessage(
    LlmMessageRole Role,
    string Content)
{
    /// <summary>Only supply the phase actually associated with this assistant message.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmAssistantPhase? Phase { get; init; }

    /// <summary>Marks the end of this text block, not a promise of a cache hit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmPromptCacheBreakpoint? CacheBreakpoint { get; init; }
}
