using System.Text.Json.Serialization;

namespace Agentica.Lab.Web.Providers;

/// <summary>Safe configuration metadata. Readiness does not attest to provider access.</summary>
public sealed record OpenAiProviderSettings(
    string Provider,
    string Model,
    string? ThinkingEffort,
    bool Configured,
    string CredentialSource,
    string CredentialLifetime,
    IReadOnlyList<string> SupportedReasoningEfforts);

/// <summary>A local settings update; credentials never belong in a run request.</summary>
public sealed class OpenAiSettingsUpdate
{
    public required string Model { get; init; }
    public required string? ThinkingEffort { get; init; }
    public required string CredentialAction { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
    public string? ApiKey { get; init; }
}

/// <summary>
/// Operator settings for new OpenAI planners. Overrides exist only for this service process;
/// no key is written to disk or returned as metadata. A planner captures one atomic snapshot.
/// </summary>
public sealed class OpenAiProviderConfiguration(Func<string, string?>? environment = null)
{
    public const string DefaultModel = "gpt-6-luna";
    private static readonly string[] ReasoningEfforts = ["none", "low", "medium", "high", "xhigh", "max"];
    private readonly object _gate = new();
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;
    private string? _model;
    private string? _effort;
    private string? _apiKey;

    public OpenAiProviderSettings GetSettings()
    {
        lock (_gate) return Describe();
    }

    public OpenAiProviderSettings Update(OpenAiSettingsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(update.Model) || update.Model.Length > 200 || update.Model.Any(char.IsControl))
            throw new ArgumentException("A model name of at most 200 characters is required.", nameof(update));
        if (update.ThinkingEffort is not null && !ReasoningEfforts.Contains(update.ThinkingEffort, StringComparer.Ordinal))
            throw new ArgumentException("Select a supported OpenAI reasoning effort.", nameof(update));
        if (update.CredentialAction is not ("keep" or "set" or "environment"))
            throw new ArgumentException("Select a credential action.", nameof(update));
        if (update.CredentialAction == "set")
        {
            if (string.IsNullOrWhiteSpace(update.ApiKey) || update.ApiKey.Length > 8192 || update.ApiKey.Any(char.IsWhiteSpace) || update.ApiKey.Any(char.IsControl))
                throw new ArgumentException("A nonempty API key without whitespace is required.", nameof(update));
        }
        else if (update.ApiKey is not null)
            throw new ArgumentException("Supply a key only when setting a service-memory credential.", nameof(update));

        lock (_gate)
        {
            _model = update.Model.Trim();
            _effort = update.ThinkingEffort;
            if (update.CredentialAction == "set") _apiKey = update.ApiKey;
            else if (update.CredentialAction == "environment") _apiKey = null;
            return Describe();
        }
    }

    internal PlannerConfiguration Capture()
    {
        lock (_gate) return new PlannerConfiguration
        {
            Model = _model ?? Value("AGENTICA_OPENAI_MODEL") ?? DefaultModel,
            ThinkingEffort = EffectiveEffort(),
            ApiKey = _apiKey ?? Value("OPENAI_API_KEY")
        };
    }

    private OpenAiProviderSettings Describe()
    {
        var source = _apiKey is not null ? "serviceMemory" : Value("OPENAI_API_KEY") is not null ? "environment" : "none";
        return new("openai", _model ?? Value("AGENTICA_OPENAI_MODEL") ?? DefaultModel, EffectiveEffort(),
            source != "none", source, "serviceProcess", Array.AsReadOnly(ReasoningEfforts));
    }

    private string? Value(string name) => _environment(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;

    // Preserve omission for existing custom environment models until an operator chooses an effort.
    private string? EffectiveEffort() => _model is not null ? _effort :
        (Value("AGENTICA_OPENAI_MODEL") ?? DefaultModel) == DefaultModel ? "high" : null;

    internal sealed class PlannerConfiguration
    {
        public required string Model { get; init; }
        public required string? ThinkingEffort { get; init; }
        [JsonIgnore]
        public string? ApiKey { get; init; }
    }
}
