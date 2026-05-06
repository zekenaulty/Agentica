namespace Agentica.Clients.Llm;

public sealed record LlmThoughtSummary(
    string Text,
    string Provider = "",
    string? Signature = null);
