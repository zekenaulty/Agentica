namespace Agentica.Clients.Llm;

public enum LlmFinishReason
{
    Unknown,
    Stop,
    MaxTokens,
    Safety,
    Blocked,
    Error,
    Other
}
