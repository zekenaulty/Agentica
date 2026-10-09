namespace Agentica.Lab.Web.Providers;

/// <summary>Per-run controls. Credentials and endpoints belong to the service environment.</summary>
public sealed record ProviderSettings(
    string Provider = "gemini",
    string? Model = null,
    string? ThinkingEffort = null,
    int MaxOutputTokens = 4096,
    int ContextWindowTokens = 32768,
    bool IncludeThoughtSummaries = false,
    string? GeminiApi = null);

/// <summary>Local configuration readiness only; no provider request is made.</summary>
public sealed record ProviderMetadata(
    string Provider,
    string? DefaultModel,
    bool Configured,
    string? ConfigurationIssue,
    string Api,
    bool Streams = true);
