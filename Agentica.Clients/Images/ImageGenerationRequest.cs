namespace Agentica.Clients.Images;

public sealed record ImageGenerationRequest(
    string ModelId,
    string Prompt,
    string? AspectRatio = null,
    string? ImageSize = null,
    string? OutputMimeType = null,
    int? OutputCompressionQuality = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
