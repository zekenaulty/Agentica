namespace Agentica.Clients.Llm;

/// <summary>Host-side decision record for the bounded provider-facing planner input.
/// Identities and dispositions stay outside ordinary JSON provider requests.</summary>
public sealed record LlmInputDecision(string Kind, string RefId, bool Included,
    string? Representation = null);

public sealed record LlmInputCompilationReceipt(
    int MaxInputCharacters,
    int InputCharacters,
    string InputSha256,
    IReadOnlyList<LlmInputDecision> Decisions,
    int? InputAllowanceTokens = null,
    long? EstimatedInputTokens = null,
    string? TokenEstimator = null);
