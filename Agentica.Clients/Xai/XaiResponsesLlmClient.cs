using Agentica.Clients.OpenAI;

namespace Agentica.Clients.Xai;

/// <summary>xAI Responses uses the same bounded SSE and native-output replay contract,
/// with its own authority, endpoint, credential, and encrypted reasoning request.</summary>
public sealed class XaiResponsesLlmClient : OpenAiResponsesLlmClient
{
    public new const string ProviderName = "xai";

    public XaiResponsesLlmClient(
        XaiResponsesClientOptions? options = null, HttpClient? httpClient = null)
        : base(ToBaseOptions(options),
            httpClient, ProviderName, "XAI_API_KEY", includeEncryptedReasoning: true)
    {
    }

    private static OpenAiResponsesClientOptions ToBaseOptions(XaiResponsesClientOptions? options)
    {
        options ??= XaiResponsesClientOptions.FromEnvironment();
        return new OpenAiResponsesClientOptions(options.ApiKey, options.DefaultModelId,
            options.Endpoint ?? XaiResponsesClientOptions.DefaultEndpoint);
    }
}
