namespace Agentica.Clients.Gemini;

public sealed record GeminiThinkingConfigSnapshot(
    int? ThinkingBudget,
    bool? IncludeThoughts);
