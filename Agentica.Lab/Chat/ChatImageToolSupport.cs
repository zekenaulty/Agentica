using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Images;

internal static class ChatImageToolSupport
{
    private const int MaxReturnedImages = 4;
    private const int MaxImageBytes = 32 * 1024 * 1024;
    private const long MaxTotalImageBytes = 64L * 1024 * 1024;

    public static bool TryReadOptions(
        IReadOnlyDictionary<string, object?> input,
        out ChatImageGenerationOptions options,
        out string error)
    {
        var aspectRatio = EmptyToNull(ChatToolInput.String(input, "aspectRatio"));
        if (aspectRatio is not null && !AllowedAspectRatios.Contains(aspectRatio))
        {
            options = default;
            error = $"Unsupported image aspect ratio: {aspectRatio}";
            return false;
        }

        var imageSize = EmptyToNull(ChatToolInput.String(input, "imageSize"));
        if (imageSize is not null && !AllowedImageSizes.Contains(imageSize))
        {
            options = default;
            error = $"Unsupported image size: {imageSize}";
            return false;
        }

        var outputMimeType = EmptyToNull(ChatToolInput.String(input, "outputMimeType"));
        if (outputMimeType is not null && !AllowedImageMimeTypes.Contains(outputMimeType))
        {
            options = default;
            error = $"Unsupported output MIME type: {outputMimeType}";
            return false;
        }

        var outputCompressionQuality = ChatToolInput.Int(
            input,
            "outputCompressionQuality",
            fallback: 0,
            min: 0,
            max: 100);
        var modelId = EmptyToNull(ChatToolInput.String(input, "model")) ?? GeminiModelId.FlashImage31Preview;

        options = new ChatImageGenerationOptions(
            aspectRatio,
            imageSize,
            outputMimeType,
            outputCompressionQuality == 0 ? null : outputCompressionQuality,
            modelId);
        error = string.Empty;
        return true;
    }

    public static async Task<ChatSavedWorkspaceImages> GenerateAndSaveAsync(
        ChatStore store,
        ChatConversation conversation,
        WorkspacePathBoundary workspaceBoundary,
        IImageGenerationClient imageClient,
        string prompt,
        ChatImageGenerationOptions options,
        string sourceToolId,
        IReadOnlyDictionary<string, object?>? additionalData,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceBoundary);
        if (!workspaceBoundary.TryPrepareDirectory("images", out var imageDirectory, out var boundaryError))
        {
            throw new InvalidOperationException(boundaryError);
        }

        var workspaceRoot = workspaceBoundary.WorkspaceRoot;
        var response = await imageClient.GenerateAsync(
                new ImageGenerationRequest(
                    options.ModelId,
                    prompt,
                    AspectRatio: options.AspectRatio,
                    ImageSize: options.ImageSize,
                    OutputMimeType: options.OutputMimeType,
                    OutputCompressionQuality: options.OutputCompressionQuality,
                    Metadata: null),
                cancellationToken)
            .ConfigureAwait(false);

        ValidateProviderResponse(response);

        if (!workspaceBoundary.TryPrepareDirectory("images", out imageDirectory, out boundaryError))
        {
            throw new InvalidOperationException(boundaryError);
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var baseName = $"{generatedAt:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24];
        var savedImages = new List<Dictionary<string, object?>>();
        for (var index = 0; index < response.Images.Count; index++)
        {
            var image = response.Images[index];
            var extension = ExtensionForMimeType(image.MimeType);
            var fileName = response.Images.Count == 1
                ? $"{baseName}{extension}"
                : $"{baseName}_{index + 1}{extension}";
            var relativePath = Path.Combine("images", fileName);
            if (!workspaceBoundary.TryResolveNewFile(relativePath, out var path, out boundaryError))
            {
                throw new InvalidOperationException(boundaryError);
            }

            await using (var stream = new FileStream(
                             path,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await stream.WriteAsync(image.Bytes, cancellationToken).ConfigureAwait(false);
            }

            savedImages.Add(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["relativePath"] = Path.GetRelativePath(workspaceRoot, path),
                ["mimeType"] = image.MimeType,
                ["bytes"] = image.Bytes.Length
            });
        }

        var data = new Dictionary<string, object?>
        {
            ["prompt"] = prompt,
            ["provider"] = response.ProviderName,
            ["model"] = response.ModelId,
            ["aspectRatio"] = options.AspectRatio,
            ["imageSize"] = options.ImageSize,
            ["outputMimeType"] = options.OutputMimeType,
            ["outputCompressionQuality"] = options.OutputCompressionQuality,
            ["generatedAt"] = generatedAt,
            ["workspaceRoot"] = workspaceRoot,
            ["images"] = savedImages,
            ["text"] = string.IsNullOrWhiteSpace(response.Text) ? null : response.Text,
            ["usage"] = response.Usage,
            ["metadata"] = response.Metadata
        };

        if (additionalData is not null)
        {
            foreach (var pair in additionalData)
            {
                data[pair.Key] = pair.Value;
            }
        }

        var metadataRelativePath = Path.Combine("images", $"{baseName}.metadata.json");
        if (!workspaceBoundary.TryResolveNewFile(metadataRelativePath, out var metadataPath, out boundaryError))
        {
            throw new InvalidOperationException(boundaryError);
        }

        await using (var stream = new FileStream(
                         metadataPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 4096,
                         useAsync: true))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(
                    JsonSerializer.Serialize(data, JsonOptions.Create()).AsMemory(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        data["metadataPath"] = metadataPath;

        var firstPath = savedImages[0]["path"]?.ToString() ?? imageDirectory;
        store.AddContextItem(
            conversation.ConversationId,
            "image",
            firstPath,
            sourceToolId,
            JsonSerializer.Serialize(data, JsonOptions.Create()));

        return new ChatSavedWorkspaceImages(
            data,
            firstPath,
            savedImages.Count);
    }

    private static readonly HashSet<string> AllowedAspectRatios = new(StringComparer.Ordinal)
    {
        "1:1",
        "2:3",
        "3:2",
        "3:4",
        "4:3",
        "4:5",
        "5:4",
        "9:16",
        "16:9",
        "21:9"
    };

    private static readonly HashSet<string> AllowedImageSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        "1K",
        "2K",
        "4K"
    };

    private static readonly HashSet<string> AllowedImageMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/webp"
    };

    public static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateProviderResponse(ImageGenerationResponse response)
    {
        if (response.Images is null || response.Images.Count == 0)
        {
            throw new InvalidOperationException("Provider returned no valid image parts.");
        }

        if (response.Images.Count > MaxReturnedImages)
        {
            throw new InvalidOperationException("Provider returned too many image parts.");
        }

        long totalBytes = 0;
        foreach (var image in response.Images)
        {
            if (image is null ||
                image.Bytes is null ||
                image.Bytes.Length == 0 ||
                image.Bytes.Length > MaxImageBytes ||
                string.IsNullOrWhiteSpace(image.MimeType) ||
                !AllowedImageMimeTypes.Contains(image.MimeType))
            {
                throw new InvalidOperationException("Provider returned an invalid image part.");
            }

            totalBytes += image.Bytes.Length;
            if (totalBytes > MaxTotalImageBytes)
            {
                throw new InvalidOperationException("Provider image output exceeded the allowed size.");
            }
        }
    }

    private static string ExtensionForMimeType(string mimeType) =>
        mimeType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => ".png"
        };
}

internal readonly record struct ChatImageGenerationOptions(
    string? AspectRatio,
    string? ImageSize,
    string? OutputMimeType,
    int? OutputCompressionQuality,
    string ModelId);

internal sealed record ChatSavedWorkspaceImages(
    IReadOnlyDictionary<string, object?> Data,
    string FirstPath,
    int ImageCount);
