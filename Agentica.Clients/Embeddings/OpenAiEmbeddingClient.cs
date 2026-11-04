using Agentica.Clients.Llm;
using Agentica.Memory;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Agentica.Clients.Embeddings;

/// <summary>One bounded HTTP request per call. No batching retries, input rewriting or vector-space fallback.</summary>
public sealed class OpenAiEmbeddingClient : IMemoryEmbeddingProvider
{
    public const string ProviderId = "openai";
    public const int MaximumInputs = 512;
    public const int MaximumInputCharacters = 32_768;
    public const int MaximumInputUtf8Bytes = 1_048_576;
    public const int MaximumRequestBytes = 8_388_608;
    private static readonly HttpClient SharedHttpClient = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly OpenAiEmbeddingClientOptions _options;
    private readonly HttpClient _httpClient;

    public OpenAiEmbeddingClient(OpenAiEmbeddingClientOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<MemoryEmbeddingBatch> EmbedAsync(MemoryEmbeddingProfile profile,
        MemoryEmbeddingPurpose purpose, IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inputs);
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        if (!string.Equals(profile.ProfileId, _options.Profile.ProfileId, StringComparison.Ordinal))
            throw new ArgumentException("Embedding profile differs from the adapter configuration.", nameof(profile));
        var snapshot = Snapshot(inputs);
        var key = _options.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (key is null || !OpenAiEmbeddingClientOptions.ValidKey(key))
            throw new EmbeddingClientException("missing_or_invalid_credential");
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = profile.ModelId,
            ["input"] = snapshot,
            ["encoding_format"] = "float"
        };
        if (_options.SendDimensions) body["dimensions"] = profile.Dimensions;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > MaximumRequestBytes)
            throw new ArgumentException("Embedding request exceeds its serialized byte bound.", nameof(inputs));
        using var message = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new ByteArrayContent(bytes)
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
                throw new EmbeddingClientException("http_status", (int)response.StatusCode);
            var content = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Parse(content, profile, snapshot.Length);
        }
        catch (HttpRequestException error) when (ClientExceptionBoundary.IsRecoverable(error))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new EmbeddingClientException("transport_failure");
        }
        catch (IOException error) when (ClientExceptionBoundary.IsRecoverable(error))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new EmbeddingClientException("response_read_failure");
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested &&
            ClientExceptionBoundary.IsRecoverable(error))
        {
            throw new EmbeddingClientException("timeout");
        }
    }

    private static string[] Snapshot(IReadOnlyList<string> inputs)
    {
        var declaredCount = inputs.Count;
        if (declaredCount is < 1 or > MaximumInputs)
            throw new ArgumentException("Embedding input count exceeds the batch bound.", nameof(inputs));
        var copy = new List<string>(declaredCount);
        var inputBytes = 0;
        foreach (var input in inputs)
        {
            if (copy.Count >= MaximumInputs || input is null || input.Length is < 1 or > MaximumInputCharacters)
                throw new ArgumentException("Embedding input exceeds the text/batch bounds.", nameof(inputs));
            try { inputBytes += StrictUtf8.GetByteCount(input); }
            catch (EncoderFallbackException)
            {
                throw new ArgumentException("Embedding input must contain valid Unicode.", nameof(inputs));
            }
            if (inputBytes > MaximumInputUtf8Bytes)
                throw new ArgumentException("Embedding inputs exceed the UTF-8 byte bound.", nameof(inputs));
            copy.Add(input);
        }
        if (copy.Count != declaredCount)
            throw new ArgumentException("Embedding input enumeration differs from its declared count.", nameof(inputs));
        return copy.ToArray();
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaximumResponseBytes)
            throw new EmbeddingClientException("response_too_large");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var remaining = _options.MaximumResponseBytes - (int)bytes.Length;
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) return bytes.ToArray();
            if (read > remaining) throw new EmbeddingClientException("response_too_large");
            bytes.Write(buffer, 0, read);
        }
    }

    private static MemoryEmbeddingBatch Parse(byte[] content, MemoryEmbeddingProfile profile, int count)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { throw new EmbeddingClientException("invalid_response"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null ||
                root.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null)
                throw new EmbeddingClientException("provider_refusal");
            if (!StringEquals(root, "object", "list") || !StringEquals(root, "model", profile.ModelId) ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != count)
                throw Invalid();
            var vectors = new IReadOnlyList<float>[count];
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !StringEquals(item, "object", "embedding") ||
                    !item.TryGetProperty("index", out var position) || position.ValueKind != JsonValueKind.Number ||
                    !position.TryGetInt32(out var index) ||
                    index < 0 || index >= count || vectors[index] is not null ||
                    !item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array ||
                    embedding.GetArrayLength() != profile.Dimensions)
                    throw Invalid();
                var vector = new float[profile.Dimensions];
                var dimension = 0;
                double normSquared = 0;
                foreach (var component in embedding.EnumerateArray())
                {
                    if (component.ValueKind != JsonValueKind.Number || !component.TryGetSingle(out var value) || !float.IsFinite(value))
                        throw Invalid();
                    vector[dimension++] = value;
                    normSquared += (double)value * value;
                }
                if (normSquared == 0) throw Invalid();
                vectors[index] = Array.AsReadOnly(vector);
            }
            long? inputTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.ValueKind == JsonValueKind.Number &&
                prompt.TryGetInt64(out var measured) && measured >= 0)
                inputTokens = measured;
            return new MemoryEmbeddingBatch(profile.ProfileId, Array.AsReadOnly(vectors), inputTokens);
        }

        static EmbeddingClientException Invalid() => new("invalid_response");
    }

    private static bool StringEquals(JsonElement parent, string name, string expected) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        string.Equals(value.GetString(), expected, StringComparison.Ordinal);
}
