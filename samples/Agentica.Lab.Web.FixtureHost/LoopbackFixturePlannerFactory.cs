using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Planning;
using Agentica.Lab.Web.Providers;
using Agentica.Planning;

namespace Agentica.Lab.Web.FixtureHost;

/// <summary>A provider-free transport qualification host. Its fixture server owns deterministic plans.</summary>
public sealed class LoopbackFixturePlannerFactory : ILabPlannerFactory, IDisposable
{
    public const string FixtureModel = "fixture-model";
    public const string FixtureCredential = "agentica-loopback-fixture";
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private bool _disposed;

    public LoopbackFixturePlannerFactory(Uri endpoint, HttpClient? httpClient = null)
    {
        _endpoint = ValidateEndpoint(endpoint);
        _ownsClient = httpClient is null;
        // A local fixture must not forward a request to another authority via redirects or a proxy.
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
    }

    public static Uri ValidateEndpoint(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp || !endpoint.IsLoopback ||
            endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.Query.Length != 0)
            throw new ArgumentException("Fixture endpoint must be an explicit loopback HTTP URL without credentials, query, or fragment.", nameof(endpoint));
        return endpoint;
    }

    public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(onStreamEvent);
        if (!string.Equals(settings.Provider, "fixture", StringComparison.OrdinalIgnoreCase) ||
            (settings.Model is not null && settings.Model != FixtureModel))
            throw new ArgumentException("This host accepts provider=fixture and model=fixture-model only; it never selects a paid provider.", nameof(settings));
        if (settings.GeminiApi is not null || settings.ThinkingEffort is not null)
            throw new ArgumentException("Provider-specific routing and named thinking controls do not apply to this deterministic fixture.", nameof(settings));
        if (settings.MaxOutputTokens is < 256 or > LabPlannerFactory.MaxOutputTokenLimit ||
            settings.ContextWindowTokens is < 8192 or > LabPlannerFactory.MaxContextWindowTokens ||
            settings.ContextWindowTokens <= settings.MaxOutputTokens + 4096)
            throw new ArgumentOutOfRangeException(nameof(settings), "Fixture planning budgets exceed the supported bounds.");
        var client = new OpenAiResponsesLlmClient(
            new OpenAiResponsesClientOptions(FixtureCredential, FixtureModel, _endpoint), _http);
        return new LlmWorkflowPlanner(client, new LlmPlannerOptions(FixtureModel,
            new LlmGenerationOptions(MaxOutputTokens: settings.MaxOutputTokens,
                Thinking: settings.IncludeThoughtSummaries ? new LlmThinkingOptions(IncludeThoughts: true) : null),
            InvalidJsonRepairAttempts: 0)
        {
            ContextWindowBudget = new LlmContextWindowBudget(settings.ContextWindowTokens, settings.MaxOutputTokens)
        }, onStreamEvent);
    }

    public IReadOnlyList<ProviderMetadata> GetProviders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return [new ProviderMetadata("fixture", FixtureModel, true, null, "loopback-responses-fixture")];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _http.Dispose();
    }
}
