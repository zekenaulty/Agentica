namespace Agentica.Clients.Llm;

public sealed record LlmThinkingOptions(
    int? ThinkingBudgetTokens = null,
    bool IncludeThoughts = false)
{
    public const int DynamicBudget = -1;
    public const int DisabledBudget = 0;

    public static LlmThinkingOptions Dynamic(bool includeThoughts = false) =>
        new(DynamicBudget, includeThoughts);

    public static LlmThinkingOptions Off(bool includeThoughts = false) =>
        new(DisabledBudget, includeThoughts);

    public static LlmThinkingOptions Budget(int tokens, bool includeThoughts = false)
    {
        if (tokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokens), "Thinking budget must be positive.");
        }

        return new LlmThinkingOptions(tokens, includeThoughts);
    }
}
