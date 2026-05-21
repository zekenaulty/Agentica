using Agentica.Clients.Llm;

namespace Agentica.Clients.Images;

public sealed record ImageGenerationResponse(
    string ProviderName,
    string ModelId,
    IReadOnlyList<GeneratedImage> Images,
    string Text = "",
    LlmUsage? Usage = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
