namespace Agentica.Clients.Llm;

public sealed record LlmResponse(
    string ProviderName,
    string ModelId,
    string Text,
    string? StructuredJson = null,
    IReadOnlyList<LlmThoughtSummary>? ThoughtSummaries = null,
    LlmUsage? Usage = null,
    LlmFinishReason FinishReason = LlmFinishReason.Unknown,
    IReadOnlyDictionary<string, string>? Metadata = null);
