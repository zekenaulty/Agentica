namespace Agentica.Clients.Llm;

public sealed record LlmRequest(
    string ModelId,
    IReadOnlyList<LlmMessage> Messages,
    LlmGenerationOptions? GenerationOptions = null,
    LlmStructuredOutputOptions? StructuredOutput = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
