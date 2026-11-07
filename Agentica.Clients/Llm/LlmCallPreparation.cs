using System.Collections.ObjectModel;
using System.Text.Json;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Clients.Llm;

/// <summary>Network-free exact preparation using the same serializers as supported HTTP adapters.
/// Options, messages, native history and credentials are captured once; hosts retain authority.</summary>
public static class LlmCallPreparation
{
    public static LlmPreparedCall Prepare(LlmRequest request, OpenAiResponsesClientOptions options,
        LlmAttemptReferences references, string hostScopeId, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options with { ApiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "" };
        return Prepare(request, references, hostScopeId, options.DefaultModelId,
            options.Endpoint ?? OpenAiResponsesClientOptions.DefaultEndpoint, "text/event-stream|",
            snapshot => LlmConfigurationReports.Describe(snapshot, options),
            snapshot => new OpenAiResponsesLlmClient(options, httpClient),
            snapshot => JsonSerializer.SerializeToUtf8Bytes(new OpenAiResponsesLlmClient(options).BuildRequestBody(snapshot, snapshot.ModelId)), httpClient);
    }

    public static LlmPreparedCall Prepare(LlmRequest request, XaiResponsesClientOptions options,
        LlmAttemptReferences references, string hostScopeId, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options with { ApiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("XAI_API_KEY") ?? "" };
        return Prepare(request, references, hostScopeId, options.DefaultModelId,
            options.Endpoint ?? XaiResponsesClientOptions.DefaultEndpoint, "text/event-stream|",
            snapshot => LlmConfigurationReports.Describe(snapshot, options),
            snapshot => new XaiResponsesLlmClient(options, httpClient),
            snapshot => JsonSerializer.SerializeToUtf8Bytes(new XaiResponsesLlmClient(options).BuildRequestBody(snapshot, snapshot.ModelId)), httpClient);
    }

    public static LlmPreparedCall Prepare(LlmRequest request, AnthropicMessagesClientOptions options,
        LlmAttemptReferences references, string hostScopeId, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options with { ApiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "" };
        return Prepare(request, references, hostScopeId, options.DefaultModelId,
            options.Endpoint ?? AnthropicMessagesClientOptions.DefaultEndpoint, "text/event-stream|2023-06-01",
            snapshot => LlmConfigurationReports.Describe(snapshot, options),
            snapshot => new AnthropicMessagesLlmClient(options, httpClient),
            snapshot => JsonSerializer.SerializeToUtf8Bytes(AnthropicMessagesLlmClient.BuildRequestBody(snapshot, snapshot.ModelId)), httpClient);
    }

    public static LlmPreparedCall Prepare(LlmRequest request, OllamaClientOptions options,
        LlmAttemptReferences references, string hostScopeId, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Prepare(request, references, hostScopeId, options.DefaultModelId,
            options.Endpoint ?? OllamaClientOptions.DefaultEndpoint, "|",
            snapshot => LlmConfigurationReports.Describe(snapshot, options),
            snapshot => new OllamaLlmClient(options, httpClient),
            snapshot => JsonSerializer.SerializeToUtf8Bytes(OllamaLlmClient.BuildRequestBody(snapshot, snapshot.ModelId)), httpClient);
    }

    public static LlmPreparedCall Prepare(LlmRequest request, GeminiInteractionsClientOptions options,
        LlmAttemptReferences references, string hostScopeId, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options with
        {
            ApiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ??
            Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? ""
        };
        return Prepare(request, references, hostScopeId, options.DefaultModelId,
            options.Endpoint ?? GeminiInteractionsClientOptions.DefaultEndpoint, "text/event-stream|",
            snapshot => LlmConfigurationReports.Describe(snapshot, options),
            snapshot => new GeminiInteractionsLlmClient(options, httpClient),
            snapshot => JsonSerializer.SerializeToUtf8Bytes(GeminiInteractionsLlmClient.BuildRequestBody(snapshot, snapshot.ModelId)), httpClient);
    }

    public static LlmPreparedCall Prepare(LlmRequest request, GeminiGenerateContentClientOptions options,
        LlmAttemptReferences references, string hostScopeId, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        options = options with
        {
            ApiKey = options.ApiKey ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ??
            Environment.GetEnvironmentVariable("GOOGLE_API_KEY") ?? ""
        };
        var model = string.IsNullOrWhiteSpace(request.ModelId) ? options.DefaultModelId : request.ModelId;
        return Prepare(request, references, hostScopeId, model,
            options.Endpoint ?? GeminiGenerateContentLlmClient.Endpoint(model), "text/event-stream|",
            snapshot => LlmConfigurationReports.Describe(snapshot, options),
            snapshot => new GeminiGenerateContentLlmClient(options, httpClient),
            snapshot => System.Text.Encoding.UTF8.GetBytes(GeminiGenerateContentLlmClient.BuildRequest(snapshot, snapshot.ModelId, out _).ToJsonString()), httpClient);
    }

    private static LlmPreparedCall Prepare(LlmRequest request, LlmAttemptReferences references,
        string hostScopeId, string? defaultModel, Uri endpoint, string protocol,
        Func<LlmRequest, LlmConfigurationReport> report, Func<LlmRequest, ILlmStreamingClient> client,
        Func<LlmRequest, byte[]> serialize, HttpClient? httpClient)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PreparedDispatch is not null || request.AttemptCapture is not null)
            throw new ArgumentException("Preparation owns its new attempt capture; supply references separately.", nameof(request));
        var snapshot = Snapshot(request, defaultModel);
        try
        {
            var configuration = report(snapshot) with { RequestedModelId = request.ModelId };
            if (!configuration.AdapterAcceptsRequest)
                throw new LlmClientException(configuration.ProviderName, "The exact request is unsupported by this adapter.",
                    errorKind: LlmClientErrorKind.BadRequest, errorClass: configuration.Issues[0].Code);
            if (snapshot.NativeContinuation is { } native)
            {
                using var binding = LlmNativeContinuationBinding.FromRequest(configuration, snapshot);
                var compatibility = LlmNativeContinuationCodec.ValidateBinding(native, binding);
                if (!compatibility.IsCompatible)
                    throw new LlmClientException(configuration.ProviderName,
                        "Native continuation does not match the prepared configuration.",
                        errorKind: LlmClientErrorKind.BadRequest,
                        errorClass: "continuation_" + compatibility.Rejection.ToString().ToLowerInvariant());
            }
            var body = serialize(snapshot);
            var transportBinding = new LlmPreparedTransportBinding(httpClient);
            var descriptor = LlmPreparedIdentity.Describe(references, hostScopeId,
                LlmProviderWorkKind.Inference, configuration, endpoint, body, protocol,
                transportIdentity: transportBinding.Identity);
            // Keep the caller's requested model distinct from the frozen effective
            // default. The captured options resolve the same wire model at dispatch.
            return new(client(snapshot), snapshot with { ModelId = request.ModelId },
                descriptor, body, endpoint, protocol, transportBinding, configuration);
        }
        catch { snapshot.NativeContinuation?.Dispose(); throw; }
    }

    private static LlmRequest Snapshot(LlmRequest request, string? defaultModel)
    {
        ArgumentNullException.ThrowIfNull(request.Messages);
        var count = request.Messages.Count;
        if (count is < 1 or > 8192) throw new ArgumentException("Prepared message count is out of bounds.", nameof(request));
        var messages = request.Messages.Take(8193).ToArray();
        if (messages.Length != count || messages.Any(message => message is null || message.Content is null))
            throw new ArgumentException("Prepared messages must be stable and nonnull.", nameof(request));
        var model = string.IsNullOrWhiteSpace(request.ModelId) ? defaultModel : request.ModelId;
        model = LlmAttemptGuard.Required(model ?? "", nameof(request.ModelId), 512);
        LlmNativeContinuation? native = null;
        if (request.NativeContinuation is { } previous)
            native = previous.CloneOwned();
        return request with
        {
            ModelId = model,
            Messages = Array.AsReadOnly(messages),
            Metadata = request.Metadata is null ? null : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal)),
            NativeContinuation = native
        };
    }
}
