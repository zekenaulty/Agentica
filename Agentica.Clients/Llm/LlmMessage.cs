namespace Agentica.Clients.Llm;

public sealed record LlmMessage(
    LlmMessageRole Role,
    string Content);
