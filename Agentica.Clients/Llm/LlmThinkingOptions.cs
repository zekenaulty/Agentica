namespace Agentica.Clients.Llm;

public sealed record LlmThinkingOptions(
    int? ThinkingBudgetTokens = null,
    bool IncludeThoughts = false,
    LlmReasoningEffort? Effort = null)
{
    public const int DynamicBudget = -1;
    public const int DisabledBudget = 0;

    public static LlmThinkingOptions Dynamic(bool includeThoughts = false) =>
        new(DynamicBudget, includeThoughts);

    public static LlmThinkingOptions Off(bool includeThoughts = false) =>
        new(DisabledBudget, includeThoughts);

    /// <summary>Requests the provider's exact named effort or thinking level.
    /// Model support is provider-specific; this does not impose a token ceiling.</summary>
    public static LlmThinkingOptions AtEffort(
        LlmReasoningEffort effort, bool includeThoughts = false) =>
        new(IncludeThoughts: includeThoughts, Effort: effort);

    public static LlmThinkingOptions Budget(int tokens, bool includeThoughts = false)
    {
        if (tokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokens), "Thinking budget must be positive.");
        }

        return new LlmThinkingOptions(tokens, includeThoughts);
    }

    internal string? GetEffortValue(string providerName)
    {
        if (ThinkingBudgetTokens is < DynamicBudget)
            throw new LlmClientException(providerName, "Thinking budget must be -1, zero, or positive.",
                errorKind: LlmClientErrorKind.BadRequest, errorClass: "invalid_thinking_budget");
        return Effort switch
        {
            null => null,
            LlmReasoningEffort.None => "none",
            LlmReasoningEffort.Minimal => "minimal",
            LlmReasoningEffort.Low => "low",
            LlmReasoningEffort.Medium => "medium",
            LlmReasoningEffort.High => "high",
            LlmReasoningEffort.XHigh => "xhigh",
            LlmReasoningEffort.Max => "max",
            _ => throw new LlmClientException(providerName, "Reasoning effort is invalid.",
                errorKind: LlmClientErrorKind.BadRequest, errorClass: "invalid_reasoning_effort")
        };
    }
}
