using Agentica.Clients.Llm;
using Google.GenAI.Types;

namespace Agentica.Clients.Gemini;

public static class GeminiThinkingOptionsMapper
{
    public static GeminiThinkingConfigSnapshot Map(LlmThinkingOptions? options)
    {
        var effort = options?.GetEffortValue(GeminiLlmClient.ProviderName);
        if (options?.Effort is LlmReasoningEffort.None or LlmReasoningEffort.XHigh or LlmReasoningEffort.Max)
            throw new LlmClientException(GeminiLlmClient.ProviderName,
                "Gemini does not support this reasoning effort.",
                errorKind: LlmClientErrorKind.BadRequest, errorClass: "unsupported_reasoning_effort");
        if (effort is not null && options?.ThinkingBudgetTokens is >= 0)
            throw new LlmClientException(GeminiLlmClient.ProviderName,
                "Choose a Gemini thinking level or a thinking budget.",
                errorKind: LlmClientErrorKind.BadRequest, errorClass: "conflicting_thinking_controls");
        return new GeminiThinkingConfigSnapshot(effort is null ? options?.ThinkingBudgetTokens : null,
            options?.IncludeThoughts, effort);
    }

    internal static ThinkingConfig? ToSdk(LlmThinkingOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var snapshot = Map(options);
        return new ThinkingConfig
        {
            ThinkingBudget = snapshot.ThinkingBudget,
            IncludeThoughts = snapshot.IncludeThoughts,
            ThinkingLevel = options.Effort switch
            {
                LlmReasoningEffort.Minimal => ThinkingLevel.Minimal,
                LlmReasoningEffort.Low => ThinkingLevel.Low,
                LlmReasoningEffort.Medium => ThinkingLevel.Medium,
                LlmReasoningEffort.High => ThinkingLevel.High,
                _ => (ThinkingLevel?)null
            }
        };
    }
}
