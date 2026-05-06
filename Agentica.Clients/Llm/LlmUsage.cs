namespace Agentica.Clients.Llm;

public sealed record LlmUsage(
    int? PromptTokens = null,
    int? OutputTokens = null,
    int? ThinkingTokens = null,
    int? TotalTokens = null);
