using Agentica.Clients.Llm;

namespace Agentica.Clients.OpenAI;

/// <summary>Creates an isolated explicit search request without conversation history or custom instructions.</summary>
public static class OpenAiWebSearch
{
    public static LlmRequest CreateRequest(string modelId, string query,
        LlmGenerationOptions? generationOptions = null, int maxToolCalls = 3,
        LlmAttemptCapture? attemptCapture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 32_768) throw new ArgumentOutOfRangeException(nameof(query));
        return new LlmRequest(modelId, [new(LlmMessageRole.User, query)], generationOptions)
        {
            WebSearch = new(LlmWebSearchMode.Required, maxToolCalls),
            AttemptCapture = attemptCapture
        };
    }
}
