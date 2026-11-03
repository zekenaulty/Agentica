namespace Agentica.Clients.Llm;

public sealed record LlmStructuredOutputOptions(
    string ResponseMimeType = "application/json",
    string? JsonSchema = null)
{
    /// <summary>Controls strictness where the provider supports it. Providers have different
    /// schema subsets and enforcement guarantees; this is not a portable validation guarantee.</summary>
    public LlmJsonSchemaEnforcement SchemaEnforcement { get; init; } = LlmJsonSchemaEnforcement.Strict;
}

public enum LlmJsonSchemaEnforcement
{
    Strict,
    BestEffort
}
