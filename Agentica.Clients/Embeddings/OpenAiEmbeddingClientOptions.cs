using Agentica.Memory;
using System.Text.Json.Serialization;

namespace Agentica.Clients.Embeddings;

/// <summary>Host-bound embedding space and transport. The host owns projection and credentials.</summary>
public sealed class OpenAiEmbeddingClientOptions
{
    public const int MaximumSupportedResponseBytes = 33_554_432;

    public OpenAiEmbeddingClientOptions(MemoryEmbeddingProfile profile, string? apiKey = null,
        Uri? endpoint = null, bool sendDimensions = true,
        int maximumResponseBytes = MaximumSupportedResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.Equals(profile.ProviderId, OpenAiEmbeddingClient.ProviderId, StringComparison.Ordinal))
            throw new ArgumentException("The embedding profile must select OpenAI.", nameof(profile));
        if (sendDimensions && string.Equals(profile.ModelId, "text-embedding-ada-002", StringComparison.Ordinal))
            throw new ArgumentException("This model requires dimensions to be omitted explicitly.", nameof(sendDimensions));
        if (apiKey is not null && !ValidKey(apiKey))
            throw new ArgumentException("Embedding credential is invalid.", nameof(apiKey));
        endpoint ??= DefaultEndpoint;
        if (!endpoint.IsAbsoluteUri ||
            !(endpoint.Scheme == Uri.UriSchemeHttps || endpoint.IsLoopback && endpoint.Scheme == Uri.UriSchemeHttp) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Embedding endpoint must be HTTPS or loopback HTTP without credentials, query or fragment.", nameof(endpoint));
        if (maximumResponseBytes is < 128 or > MaximumSupportedResponseBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        Profile = profile;
        ApiKey = apiKey;
        Endpoint = endpoint;
        SendDimensions = sendDimensions;
        MaximumResponseBytes = maximumResponseBytes;
    }

    public static Uri DefaultEndpoint { get; } = new("https://api.openai.com/v1/embeddings");
    public MemoryEmbeddingProfile Profile { get; }
    public Uri Endpoint { get; }
    public bool SendDimensions { get; }
    public int MaximumResponseBytes { get; }

    [JsonIgnore]
    internal string? ApiKey { get; }

    internal static bool ValidKey(string key) => key.Length is > 0 and <= 8192 &&
        key.All(character => character is > ' ' and < '\u007f');

    public override string ToString() => nameof(OpenAiEmbeddingClientOptions) + " (credential redacted)";
}
