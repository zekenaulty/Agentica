namespace Agentica.Clients.Llm;

public enum LlmClientErrorKind
{
    Unknown,
    Transient,
    Network,
    RateLimited,
    ServerError,
    Authentication,
    BadRequest,
    Safety,
    Cancelled
}
