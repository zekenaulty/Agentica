using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Planning;
using Agentica.Clients.Xai;
using Agentica.Planning;

namespace Agentica.Lab.Web.Providers;

/// <summary>
/// Creates a fresh planner for each run, retaining native reasoning only in that planner's
/// existing session lifetime. Every accepted provider route uses its actual stream API.
/// </summary>
public sealed class LabPlannerFactory : ILabPlannerFactory, IDisposable
{
    public const int MaxOutputTokenLimit = 65536;
    public const int MaxContextWindowTokens = 1048576;
    private readonly HttpClient _httpClient;
    private readonly Func<string, string?> _environment;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public LabPlannerFactory(HttpClient? httpClient = null, Func<string, string?>? environment = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onStreamEvent);
        var provider = NormalizeProvider(settings.Provider);
        var model = settings.Model ?? DefaultModel(provider);
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || model.Any(char.IsControl))
            throw new ArgumentException("A model name of at most 200 characters is required.", nameof(settings));
        if (settings.MaxOutputTokens is < 256 or > MaxOutputTokenLimit)
            throw new ArgumentOutOfRangeException(nameof(settings), "Output budget must be between 256 and 65536 tokens.");
        if (settings.ContextWindowTokens is < 8192 or > MaxContextWindowTokens ||
            settings.ContextWindowTokens <= settings.MaxOutputTokens + 4096)
            throw new ArgumentOutOfRangeException(nameof(settings), "Context budget must be between 8192 and 1048576 tokens, with more than 4096 tokens remaining after output reservation.");
        var thinking = ParseThinking(settings);
        if (provider == "gemini") ValidateGeminiApi(settings.GeminiApi);
        else if (settings.GeminiApi is not null)
            throw new ArgumentException("GeminiApi applies only to the Gemini provider.", nameof(settings));

        ILlmStreamingClient client = provider switch
        {
            "openai" => new OpenAiResponsesLlmClient(new(RequireKey("OPENAI_API_KEY"), model), _httpClient),
            "gemini" => new GeminiInteractionsLlmClient(new(RequireKey("GEMINI_API_KEY", "GOOGLE_API_KEY"), model), _httpClient),
            "anthropic" => new AnthropicMessagesLlmClient(new(RequireKey("ANTHROPIC_API_KEY"), model), _httpClient),
            "grok" => new XaiResponsesLlmClient(new(RequireKey("XAI_API_KEY"), model), _httpClient),
            "ollama" => new OllamaLlmClient(new(OllamaEndpoint(), model), _httpClient),
            _ => throw new ArgumentException("Unsupported provider.", nameof(settings))
        };
        return new LlmWorkflowPlanner(client, new LlmPlannerOptions(
            ModelId: model,
            GenerationOptions: new LlmGenerationOptions(MaxOutputTokens: settings.MaxOutputTokens, Thinking: thinking),
            InvalidJsonRepairAttempts: 1)
        {
            ContextWindowBudget = new LlmContextWindowBudget(settings.ContextWindowTokens, settings.MaxOutputTokens)
        }, onStreamEvent);
    }

    public IReadOnlyList<ProviderMetadata> GetProviders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return [
            Describe("gemini", "interactions", "GEMINI_API_KEY", "GOOGLE_API_KEY"),
            Describe("openai", "responses", "OPENAI_API_KEY"),
            Describe("anthropic", "messages", "ANTHROPIC_API_KEY"),
            Describe("grok", "responses", "XAI_API_KEY"),
            Describe("ollama", "chat")
        ];
    }

    private ProviderMetadata Describe(string provider, string api, params string[] keyNames)
    {
        var model = DefaultModel(provider);
        var configured = keyNames.Length == 0 || keyNames.Any(name => Value(name) is not null);
        var issue = configured ? null : "Provider credential is not configured on the service.";
        if (provider == "ollama")
        {
            // A caller may supply a model even when OLLAMA_MODEL is absent.
            try { _ = OllamaEndpoint(); }
            catch (ArgumentException)
            {
                configured = false;
                issue = "Ollama endpoint configuration is invalid.";
            }
            if (configured && model is null) issue = "Select a model when starting a run.";
        }
        return new(provider, model, configured, issue, api);
    }

    private string? DefaultModel(string provider) => provider switch
    {
        "gemini" => Value("AGENTICA_GEMINI_MODEL") ?? GeminiModelId.Flash25,
        "openai" => Value("AGENTICA_OPENAI_MODEL") ?? new OpenAiResponsesClientOptions().DefaultModelId,
        "anthropic" => Value("AGENTICA_ANTHROPIC_MODEL") ?? new AnthropicMessagesClientOptions().DefaultModelId,
        "grok" => Value("AGENTICA_GROK_MODEL") ?? new XaiResponsesClientOptions().DefaultModelId,
        "ollama" => Value("OLLAMA_MODEL"),
        _ => null
    };

    private static string NormalizeProvider(string? provider) => provider?.Trim().ToLowerInvariant() switch
    {
        "openai" => "openai",
        "gemini" or "google" => "gemini",
        "anthropic" or "claude" => "anthropic",
        "grok" or "xai" => "grok",
        "ollama" => "ollama",
        _ => throw new ArgumentException("Provider must be gemini, openai, anthropic, grok, or ollama.", nameof(provider))
    };

    private static LlmThinkingOptions? ParseThinking(ProviderSettings settings)
    {
        if (settings.ThinkingEffort is null)
            return settings.IncludeThoughtSummaries ? new LlmThinkingOptions(IncludeThoughts: true) : null;
        // Enum.TryParse also accepts numeric strings; the wire contract accepts named controls only.
        var effort = settings.ThinkingEffort.Trim().ToLowerInvariant() switch
        {
            "none" => LlmReasoningEffort.None,
            "minimal" => LlmReasoningEffort.Minimal,
            "low" => LlmReasoningEffort.Low,
            "medium" => LlmReasoningEffort.Medium,
            "high" => LlmReasoningEffort.High,
            "xhigh" => LlmReasoningEffort.XHigh,
            "max" => LlmReasoningEffort.Max,
            _ => throw new ArgumentException("Thinking effort must be none, minimal, low, medium, high, xhigh, or max.", nameof(settings))
        };
        return LlmThinkingOptions.AtEffort(effort, settings.IncludeThoughtSummaries);
    }

    private static void ValidateGeminiApi(string? api)
    {
        if (api is null || string.Equals(api, "interactions", StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(api, "legacy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(api, "generatecontent", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Gemini GenerateContent does not yet implement the streaming client contract. Select interactions for this service.");
        throw new ArgumentException("GeminiApi must be interactions. GenerateContent streaming is not supported by this service.", nameof(api));
    }

    private string RequireKey(params string[] names)
    {
        foreach (var name in names)
            if (Value(name) is { } key) return key;
        throw new InvalidOperationException("Provider credential is not configured on the service.");
    }

    private string? Value(string name)
    {
        var value = _environment(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private Uri OllamaEndpoint()
    {
        var configured = Value("AGENTICA_OLLAMA_ENDPOINT");
        Uri endpoint;
        if (configured is not null)
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var parsed))
                throw new ArgumentException("Ollama endpoint configuration is invalid.");
            endpoint = parsed;
        }
        else if (Value("OLLAMA_HOST") is { } host)
        {
            if (!host.Contains("://", StringComparison.Ordinal)) host = "http://" + host;
            if (!Uri.TryCreate(host.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
                throw new ArgumentException("Ollama endpoint configuration is invalid.");
            endpoint = new Uri(baseUri, "api/chat");
        }
        else endpoint = OllamaClientOptions.DefaultEndpoint;
        if (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp))
            throw new ArgumentException("Ollama endpoint must use HTTPS or loopback HTTP.");
        return endpoint;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
