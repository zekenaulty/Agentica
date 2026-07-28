using System.Collections.ObjectModel;
using System.Text;
using Agentica.Clients.Images;
using Agentica.Clients.Llm;

internal static class ChatProviderResponseCompiler
{
    private const int MaxProviderIdentityBytes = 512;
    private const int MaxProviderTextBytes = 64 * 1024;
    private const int MaxStructuredJsonBytes = 128 * 1024;
    private const int MaxMetadataEntries = 32;
    private const int MaxMetadataKeyBytes = 256;
    private const int MaxMetadataValueBytes = 4096;
    private const int MaxMetadataTotalBytes = 64 * 1024;
    private const int MaxMimeTypeBytes = 64;
    private const int MaxReturnedImages = 4;
    private const int MaxImageBytes = 32 * 1024 * 1024;
    private const long MaxTotalImageBytes = 64L * 1024 * 1024;

    private static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp"
    };

    public static CompiledChatImageResponse Compile(ImageGenerationResponse? response)
    {
        var providerName = SafeIdentity(response?.ProviderName, "unknown");
        var modelId = SafeIdentity(response?.ModelId, "unknown");
        try
        {
            if (response is null)
            {
                throw new InvalidOperationException("Provider returned a null image response.");
            }

            providerName = SnapshotRequiredString(
                response.ProviderName,
                MaxProviderIdentityBytes,
                "provider name");
            modelId = SnapshotRequiredString(
                response.ModelId,
                MaxProviderIdentityBytes,
                "provider model id");
            var metadata = SnapshotMetadata(response.Metadata);
            var requestId = FindRequestId(metadata);
            var text = SnapshotOptionalString(response.Text, MaxProviderTextBytes, "provider image text");
            var usage = SnapshotUsage(response.Usage);
            if (response.Images is null)
            {
                throw new InvalidOperationException("Provider returned a null image collection.");
            }

            var images = new List<GeneratedImage>(MaxReturnedImages);
            long totalBytes = 0;
            foreach (var image in response.Images)
            {
                if (images.Count >= MaxReturnedImages)
                {
                    throw new InvalidOperationException(
                        $"Provider returned more than {MaxReturnedImages} images.");
                }

                if (image?.Bytes is null ||
                    image.Bytes.Length == 0 ||
                    image.Bytes.Length > MaxImageBytes)
                {
                    throw new InvalidOperationException("Provider returned an invalid image part.");
                }

                var mimeType = SnapshotRequiredString(
                        image.MimeType,
                        MaxMimeTypeBytes,
                        "image MIME type")
                    .ToLowerInvariant();
                if (!AllowedImageMimeTypes.Contains(mimeType))
                {
                    throw new InvalidOperationException("Provider returned an invalid image part.");
                }

                totalBytes += image.Bytes.Length;
                if (totalBytes > MaxTotalImageBytes)
                {
                    throw new InvalidOperationException("Provider image output exceeded the allowed size.");
                }

                images.Add(new GeneratedImage(
                    image.Bytes.ToArray(),
                    mimeType));
            }

            if (images.Count == 0)
            {
                throw new InvalidOperationException("Provider returned no images.");
            }

            return new CompiledChatImageResponse(
                providerName,
                modelId,
                Array.AsReadOnly(images.ToArray()),
                text,
                usage,
                metadata,
                requestId);
        }
        catch (ChatProviderResponseValidationException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            throw new ChatProviderResponseValidationException(
                "Image provider response could not be safely compiled.",
                providerName,
                modelId,
                SafeRequestId(response?.Metadata),
                exception);
        }
    }

    public static CompiledChatLlmResponse Compile(LlmResponse? response)
    {
        var providerName = SafeIdentity(response?.ProviderName, "unknown");
        var modelId = SafeIdentity(response?.ModelId, "unknown");
        try
        {
            if (response is null)
            {
                throw new InvalidOperationException("Provider returned a null LLM response.");
            }

            providerName = SnapshotRequiredString(
                response.ProviderName,
                MaxProviderIdentityBytes,
                "provider name");
            modelId = SnapshotRequiredString(
                response.ModelId,
                MaxProviderIdentityBytes,
                "provider model id");
            var metadata = SnapshotMetadata(response.Metadata);
            return new CompiledChatLlmResponse(
                providerName,
                modelId,
                SnapshotOptionalString(response.Text, MaxProviderTextBytes, "provider text"),
                response.StructuredJson is null
                    ? null
                    : SnapshotOptionalString(
                        response.StructuredJson,
                        MaxStructuredJsonBytes,
                        "provider structured JSON"),
                SnapshotUsage(response.Usage),
                metadata,
                FindRequestId(metadata));
        }
        catch (ChatProviderResponseValidationException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            throw new ChatProviderResponseValidationException(
                "LLM provider response could not be safely compiled.",
                providerName,
                modelId,
                SafeRequestId(response?.Metadata),
                exception);
        }
    }

    private static IReadOnlyDictionary<string, string> SnapshotMetadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
        {
            return new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }

        var entries = new List<KeyValuePair<string, string>>(MaxMetadataEntries);
        var totalBytes = 0;
        foreach (var pair in metadata)
        {
            if (entries.Count >= MaxMetadataEntries)
            {
                throw new InvalidOperationException(
                    $"Provider metadata enumeration exceeds the {MaxMetadataEntries}-entry limit.");
            }

            if (pair.Key is null || pair.Value is null)
            {
                throw new InvalidOperationException("Provider metadata contains a null key or value.");
            }

            var key = SnapshotOptionalString(pair.Key, MaxMetadataKeyBytes, "provider metadata key");
            var value = SnapshotOptionalString(pair.Value, MaxMetadataValueBytes, "provider metadata value");
            totalBytes = checked(totalBytes + Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value));
            if (totalBytes > MaxMetadataTotalBytes)
            {
                throw new InvalidOperationException("Provider metadata exceeds the aggregate byte limit.");
            }

            entries.Add(new KeyValuePair<string, string>(key, value));
        }

        var snapshot = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);
        foreach (var pair in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            snapshot.Add(pair.Key, pair.Value);
        }

        return new ReadOnlyDictionary<string, string>(snapshot);
    }

    private static LlmUsage? SnapshotUsage(LlmUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        foreach (var count in new int?[]
                 {
                     usage.PromptTokens,
                     usage.OutputTokens,
                     usage.ThinkingTokens,
                     usage.TotalTokens,
                     usage.CachedPromptTokens,
                     usage.ToolUsePromptTokens
                 })
        {
            if (count < 0)
            {
                throw new InvalidOperationException("Provider usage contains a negative token count.");
            }
        }

        return usage with { };
    }

    private static string SnapshotRequiredString(string? value, int maxBytes, string description)
    {
        if (value is not null && value.Length > maxBytes)
        {
            throw new InvalidOperationException($"Provider {description} exceeds the {maxBytes}-byte limit.");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Provider {description} is required.");
        }

        return SnapshotOptionalString(value.Trim(), maxBytes, description);
    }

    private static string SnapshotOptionalString(string? value, int maxBytes, string description)
    {
        value ??= string.Empty;
        if (value.Length > maxBytes)
        {
            throw new InvalidOperationException($"Provider {description} exceeds the {maxBytes}-byte limit.");
        }

        if (Encoding.UTF8.GetByteCount(value) > maxBytes)
        {
            throw new InvalidOperationException($"Provider {description} exceeds the {maxBytes}-byte limit.");
        }

        return string.Concat(value);
    }

    private static string? FindRequestId(IReadOnlyDictionary<string, string> metadata)
    {
        foreach (var key in new[] { "responseId", "requestId", "providerRequestId" })
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? SafeRequestId(IReadOnlyDictionary<string, string>? metadata)
    {
        try
        {
            if (metadata is null)
            {
                return null;
            }

            var entryCount = 0;
            foreach (var pair in metadata)
            {
                entryCount++;
                if (entryCount > MaxMetadataEntries)
                {
                    return null;
                }

                if (!IsRequestIdKey(pair.Key) ||
                    pair.Value is null ||
                    pair.Value.Length == 0)
                {
                    continue;
                }

                if (pair.Value.Length > MaxMetadataValueBytes ||
                    Encoding.UTF8.GetByteCount(pair.Value) > MaxMetadataValueBytes)
                {
                    return ChatImageEffectJournal.OversizedProviderRequestIdEvidence;
                }

                return SnapshotRequiredString(
                    pair.Value,
                    MaxMetadataValueBytes,
                    "request id");
            }
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            return null;
        }

        return null;
    }

    private static string SafeIdentity(string? value, string fallback)
    {
        const int maxChars = 240;
        if (value is null || value.Length == 0 || value.Length > maxChars)
        {
            return fallback;
        }

        var trimmed = value.AsSpan().Trim();
        return trimmed.IsEmpty ? fallback : trimmed.ToString();
    }

    private static bool IsRequestIdKey(string? key) =>
        string.Equals(key, "responseId", StringComparison.Ordinal) ||
        string.Equals(key, "requestId", StringComparison.Ordinal) ||
        string.Equals(key, "providerRequestId", StringComparison.Ordinal);

    private static bool IsRecoverableFailure(Exception exception) =>
        ChatImageToolSupport.IsRecoverableFailure(exception);
}

internal sealed record CompiledChatImageResponse(
    string ProviderName,
    string ModelId,
    IReadOnlyList<GeneratedImage> Images,
    string Text,
    LlmUsage? Usage,
    IReadOnlyDictionary<string, string> Metadata,
    string? ProviderRequestId);

internal sealed record CompiledChatLlmResponse(
    string ProviderName,
    string ModelId,
    string Text,
    string? StructuredJson,
    LlmUsage? Usage,
    IReadOnlyDictionary<string, string> Metadata,
    string? ProviderRequestId);

internal sealed class ChatProviderResponseValidationException : Exception
{
    public ChatProviderResponseValidationException(
        string message,
        string providerName,
        string modelId,
        string? providerRequestId,
        Exception innerException)
        : base(message, innerException)
    {
        ProviderName = providerName;
        ModelId = modelId;
        ProviderRequestId = providerRequestId;
    }

    public string ProviderName { get; }

    public string ModelId { get; }

    public string? ProviderRequestId { get; }
}
