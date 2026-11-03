namespace Agentica.Clients.Llm;

/// <summary>Whether the provider's output counter already contains thinking tokens.</summary>
public enum LlmOutputTokenSemantics
{
    Unknown,
    IncludesThinking,
    ExcludesThinking
}

/// <summary>Source counters retain the provider's meanings and are not additive categories.</summary>
public sealed record LlmUsage(
    int? PromptTokens = null,
    int? OutputTokens = null,
    int? ThinkingTokens = null,
    int? TotalTokens = null,
    int? CachedPromptTokens = null,
    int? ToolUsePromptTokens = null)
{
    /// <summary>Unknown is the compatibility default for callers without provider evidence.</summary>
    public LlmOutputTokenSemantics OutputTokenSemantics { get; init; }

    /// <summary>
    /// Output excluding separately accounted thinking, when derivable. This is not
    /// a count of visible answer text: provider formatting and tool output can remain.
    /// </summary>
    public int? NonThinkingOutputTokens => ValidOutputCounts() ? OutputTokenSemantics switch
    {
        LlmOutputTokenSemantics.IncludesThinking when ThinkingTokens is not null => OutputTokens - ThinkingTokens,
        LlmOutputTokenSemantics.ExcludesThinking => OutputTokens,
        _ => null
    } : null;

    /// <summary>
    /// Provider-accounted generated output including thinking, when derivable.
    /// This is neither a cost nor a complete billable-usage or context-occupancy total.
    /// </summary>
    public int? GeneratedOutputTokens => ValidOutputCounts() ? OutputTokenSemantics switch
    {
        LlmOutputTokenSemantics.IncludesThinking => OutputTokens,
        LlmOutputTokenSemantics.ExcludesThinking => SumTokenCounts(OutputTokens, ThinkingTokens),
        _ => null
    } : null;

    private bool ValidOutputCounts()
    {
        if (OutputTokens is null or < 0 || ThinkingTokens is < 0 || TotalTokens is < 0 ||
            PromptTokens is < 0 || CachedPromptTokens is < 0 || ToolUsePromptTokens is < 0) return false;
        if (OutputTokenSemantics == LlmOutputTokenSemantics.IncludesThinking)
            return (ThinkingTokens is null || ThinkingTokens <= OutputTokens) &&
                (TotalTokens is null || OutputTokens <= TotalTokens);
        if (OutputTokenSemantics != LlmOutputTokenSemantics.ExcludesThinking) return false;
        if (ThinkingTokens is null) return TotalTokens is null || OutputTokens <= TotalTokens;
        var generated = SumTokenCounts(OutputTokens, ThinkingTokens);
        return generated is not null && (TotalTokens is null || generated <= TotalTokens);
    }

    internal static int? SumTokenCounts(int? left, int? right) =>
        left is >= 0 && right is >= 0 && (long)left.Value + right.Value <= int.MaxValue
            ? left.Value + right.Value : null;
}
