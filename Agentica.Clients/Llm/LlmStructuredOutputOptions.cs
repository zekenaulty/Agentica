namespace Agentica.Clients.Llm;

public sealed record LlmStructuredOutputOptions(
    string ResponseMimeType = "application/json",
    string? JsonSchema = null);
