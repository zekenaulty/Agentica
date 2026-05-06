using Agentica.Clients.Llm;
using Google.GenAI.Types;

namespace Agentica.Clients.Gemini;

public static class GeminiThinkingOptionsMapper
{
    public static GeminiThinkingConfigSnapshot Map(LlmThinkingOptions? options) =>
        new(options?.ThinkingBudgetTokens, options?.IncludeThoughts);

    internal static ThinkingConfig? ToSdk(LlmThinkingOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        return new ThinkingConfig
        {
            ThinkingBudget = options.ThinkingBudgetTokens,
            IncludeThoughts = options.IncludeThoughts
        };
    }
}
