namespace Agentica.Clients.Llm;

/// <summary>
/// A named provider control, not a token budget. Providers and models support different
/// subsets and assign their own meaning to each level.
/// </summary>
public enum LlmReasoningEffort
{
    None,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max
}
