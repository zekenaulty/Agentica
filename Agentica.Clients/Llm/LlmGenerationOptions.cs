namespace Agentica.Clients.Llm;

public sealed record LlmGenerationOptions(
    double? Temperature = null,
    int? MaxOutputTokens = null,
    LlmThinkingOptions? Thinking = null);
