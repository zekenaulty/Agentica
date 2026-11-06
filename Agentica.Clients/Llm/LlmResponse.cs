using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

public sealed record LlmResponse(
    string ProviderName,
    string ModelId,
    string Text,
    string? StructuredJson = null,
    IReadOnlyList<LlmThoughtSummary>? ThoughtSummaries = null,
    LlmUsage? Usage = null,
    LlmFinishReason FinishReason = LlmFinishReason.Unknown,
    IReadOnlyDictionary<string, string>? Metadata = null,
    [property: JsonIgnore] LlmNativeContinuation? NativeContinuation = null)
{
    /// <summary>Observed provider/client facts; a completed client call is not host acceptance.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmAttemptEvidence? AttemptEvidence { get; init; }
}
