using System.Text.Json;
using System.Security.Cryptography;
using Agentica.Clients.Gemini;
using Agentica.Clients.Images;

internal static class ChatImageToolSupport
{
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
        IChatImageStagingWriter stagingWriter,
        ChatImageEffectJournal effectJournal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceBoundary);
        ArgumentNullException.ThrowIfNull(stagingWriter);
        ArgumentNullException.ThrowIfNull(effectJournal);
        if (!workspaceBoundary.TryResolveContainedPath("images", out var imageDirectory, out var boundaryError))
        {
            throw new InvalidOperationException(boundaryError);
        }

        var workspaceRoot = workspaceBoundary.WorkspaceRoot;
        ImageGenerationResponse rawResponse;
        effectJournal.ProviderDispatchAttempted("image_generator", options.ModelId);
        try
        {
            rawResponse = await imageClient.GenerateAsync(
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
        }
        catch (OperationCanceledException exception) when (IsRecoverableFailure(exception))
        {
            effectJournal.ProviderDispatchFailed("image_generator", exception, cancelled: true);
            throw new ChatImageEffectException(
                "Image provider dispatch was cancelled; its remote outcome is indeterminate.",
                effectJournal,
                cancelled: true,
                exception);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            effectJournal.ProviderDispatchFailed("image_generator", exception, cancelled: false);
            throw new ChatImageEffectException(
                $"Image provider dispatch failed after it was attempted: {LimitMessage(exception.Message)}",
                effectJournal,
                cancelled: false,
                exception);
        }

        CompiledChatImageResponse response;
        try
        {
            response = ChatProviderResponseCompiler.Compile(rawResponse);
        }
        catch (ChatProviderResponseValidationException exception)
        {
            effectJournal.ProviderResponseReceived(
                "image_generator",
                exception.ProviderName,
                exception.ModelId,
                exception.ProviderRequestId);
            throw new ChatImageEffectException(
                "Image provider returned an invalid response after dispatch.",
                effectJournal,
                cancelled: false,
                exception);
        }

        effectJournal.ProviderResponseReceived(
            "image_generator",
            response.ProviderName,
            response.ModelId,
            response.ProviderRequestId);

        var fileEffects = new List<(string EffectName, string Path, string RelativePath)>();
        var directoryEffects = new List<(string EffectName, string Path, string RelativePath)>();
        var publishFiles = new List<ChatImagePublishFile>();
        string? contextItemId = null;
        const string contextEffectName = "image_context_item";
        try
        {
            if (!TryPrepareOwnedDirectory(
                    workspaceBoundary,
                    "images",
                    "workspace_image_directory",
                    directoryEffects,
                    effectJournal,
                    out imageDirectory,
                    out boundaryError))
            {
                throw new InvalidOperationException(boundaryError);
            }

            var generatedAt = DateTimeOffset.UtcNow;
            var baseName = $"{generatedAt:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24];
            var stagingRelativePath = Path.Combine("images", $".agentica-staging-{Guid.NewGuid():N}");
            if (!TryPrepareOwnedDirectory(
                    workspaceBoundary,
                    stagingRelativePath,
                    "image_staging_directory",
                    directoryEffects,
                    effectJournal,
                    out var stagingDirectory,
                    out boundaryError))
            {
                throw new InvalidOperationException(boundaryError);
            }

            var savedImages = new List<Dictionary<string, object?>>();
            for (var index = 0; index < response.Images.Count; index++)
            {
                var image = response.Images[index];
                var extension = ExtensionForMimeType(image.MimeType);
                var fileName = response.Images.Count == 1
                    ? $"{baseName}{extension}"
                    : $"{baseName}_{index + 1}{extension}";
                var publishedRelativePath = Path.Combine("images", fileName);
                if (!workspaceBoundary.TryResolveNewFile(
                        publishedRelativePath,
                        out var publishedPath,
                        out boundaryError))
                {
                    throw new InvalidOperationException(boundaryError);
                }

                var stagedRelativePath = Path.Combine(stagingRelativePath, fileName);
                if (!workspaceBoundary.TryResolveNewFile(
                        stagedRelativePath,
                        out var stagedPath,
                        out boundaryError))
                {
                    throw new InvalidOperationException(boundaryError);
                }

                var stagedEffectName = $"staged_image_file_{index + 1}";
                var publishedEffectName = $"image_file_{index + 1}";
                fileEffects.Add((stagedEffectName, stagedPath, stagedRelativePath));
                await WriteStagedFileAsync(
                        workspaceBoundary,
                        stagingWriter,
                        stagedEffectName,
                        stagedPath,
                        stagedRelativePath,
                        image.Bytes,
                        effectJournal,
                        cancellationToken)
                    .ConfigureAwait(false);
                publishFiles.Add(new ChatImagePublishFile(
                    stagedEffectName,
                    stagedPath,
                    stagedRelativePath,
                    publishedEffectName,
                    publishedPath,
                    publishedRelativePath));
                savedImages.Add(new Dictionary<string, object?>
                {
                    ["path"] = publishedPath,
                    ["relativePath"] = Path.GetRelativePath(workspaceRoot, publishedPath),
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
            if (!workspaceBoundary.TryResolveNewFile(
                    metadataRelativePath,
                    out var metadataPath,
                    out boundaryError))
            {
                throw new InvalidOperationException(boundaryError);
            }

            data["metadataPath"] = metadataPath;
            var firstPath = savedImages[0]["path"]?.ToString() ?? imageDirectory;
            var metadataBytes = ChatImageToolResultPreflight.ValidateAndSerialize(
                data,
                sourceToolId,
                savedImages.Count,
                firstPath);
            var stagedMetadataRelativePath = Path.Combine(
                stagingRelativePath,
                $"{baseName}.metadata.json");
            if (!workspaceBoundary.TryResolveNewFile(
                    stagedMetadataRelativePath,
                    out var stagedMetadataPath,
                    out boundaryError))
            {
                throw new InvalidOperationException(boundaryError);
            }

            const string stagedMetadataEffectName = "staged_image_metadata_file";
            const string metadataEffectName = "image_metadata_file";
            fileEffects.Add((stagedMetadataEffectName, stagedMetadataPath, stagedMetadataRelativePath));
            await WriteStagedFileAsync(
                    workspaceBoundary,
                    stagingWriter,
                    stagedMetadataEffectName,
                    stagedMetadataPath,
                    stagedMetadataRelativePath,
                    metadataBytes,
                    effectJournal,
                    cancellationToken)
                .ConfigureAwait(false);
            publishFiles.Add(new ChatImagePublishFile(
                stagedMetadataEffectName,
                stagedMetadataPath,
                stagedMetadataRelativePath,
                metadataEffectName,
                metadataPath,
                metadataRelativePath));

            foreach (var publish in publishFiles)
            {
                PublishStagedFile(
                    workspaceBoundary,
                    publish,
                    fileEffects,
                    effectJournal);
            }

            RemoveStagingDirectory(
                workspaceBoundary,
                stagingDirectory,
                stagingRelativePath,
                "image_staging_directory",
                effectJournal);

            contextItemId = store.NewContextItemId();
            effectJournal.MutationAttempted(contextEffectName, "persist image context item");
            try
            {
                store.AddImageContextItem(
                    contextItemId,
                    conversation.ConversationId,
                    "image",
                    firstPath,
                    sourceToolId,
                    JsonSerializer.Serialize(data, JsonOptions.Create()));
            }
            catch (Exception exception) when (IsRecoverableFailure(exception))
            {
                effectJournal.MutationFailed(
                    contextEffectName,
                    exception.GetType().Name,
                    outcomeIndeterminate: true);
                throw;
            }

            effectJournal.MutationCompleted(contextEffectName, "image context item persisted");

            data["effectJournal"] = effectJournal.Snapshot("succeeded");

            return new ChatSavedWorkspaceImages(
                data,
                firstPath,
                savedImages.Count);
        }
        catch (OperationCanceledException exception) when (IsRecoverableFailure(exception))
        {
            CleanupLocalEffects(
                store,
                contextItemId,
                contextEffectName,
                fileEffects,
                directoryEffects,
                workspaceBoundary,
                effectJournal);
            throw new ChatImageEffectException(
                "Image generation was cancelled after provider dispatch; recorded effects were compensated where possible.",
                effectJournal,
                cancelled: true,
                exception);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            CleanupLocalEffects(
                store,
                contextItemId,
                contextEffectName,
                fileEffects,
                directoryEffects,
                workspaceBoundary,
                effectJournal);
            throw new ChatImageEffectException(
                $"Image generation failed after provider dispatch: {LimitMessage(exception.Message)}",
                effectJournal,
                cancelled: false,
                exception);
        }
    }

    internal static bool TryPrepareOwnedDirectory(
        WorkspacePathBoundary workspaceBoundary,
        string relativePath,
        string effectName,
        ICollection<(string EffectName, string Path, string RelativePath)> directoryEffects,
        ChatImageEffectJournal effectJournal,
        out string directoryPath,
        out string error)
    {
        effectJournal.MutationAttempted(effectName, $"prepare {relativePath}");
        var prepared = workspaceBoundary.TryPrepareDirectory(
            relativePath,
            out directoryPath,
            out var createdDirectories,
            out error);
        for (var index = 0; index < createdDirectories.Count; index++)
        {
            var createdPath = createdDirectories[index];
            var createdRelativePath = Path.GetRelativePath(workspaceBoundary.WorkspaceRoot, createdPath);
            var createdEffectName = index == createdDirectories.Count - 1
                ? effectName
                : $"{effectName}_parent_{index + 1}";
            effectJournal.MutationCompleted(createdEffectName, createdRelativePath);
            directoryEffects.Add((createdEffectName, createdPath, createdRelativePath));
        }

        if (!prepared)
        {
            effectJournal.MutationFailed(
                effectName,
                "workspace directory preparation failed after an ownership-aware recheck",
                outcomeIndeterminate: createdDirectories.Count > 0);
            return false;
        }

        if (createdDirectories.Count == 0)
        {
            effectJournal.MutationConfirmedAbsent(effectName, "directory already existed");
        }

        return true;
    }

    internal static async Task WriteStagedFileAsync(
        WorkspacePathBoundary workspaceBoundary,
        IChatImageStagingWriter stagingWriter,
        string effectName,
        string path,
        string relativePath,
        ReadOnlyMemory<byte> content,
        ChatImageEffectJournal effectJournal,
        CancellationToken cancellationToken)
    {
        effectJournal.MutationAttempted(effectName, relativePath);
        var expectedSha256 = SHA256.HashData(content.Span);
        try
        {
            await stagingWriter.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
            ValidateStagedFile(
                workspaceBoundary,
                path,
                content.Length,
                expectedSha256);
            effectJournal.MutationCompleted(effectName, relativePath);
        }
        catch (OperationCanceledException exception) when (IsRecoverableFailure(exception))
        {
            effectJournal.MutationFailed(effectName, "cancelled during staged write", outcomeIndeterminate: true);
            throw;
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            effectJournal.MutationFailed(effectName, exception.GetType().Name, outcomeIndeterminate: true);
            throw;
        }
    }

    private static void ValidateStagedFile(
        WorkspacePathBoundary workspaceBoundary,
        string path,
        int expectedBytes,
        ReadOnlySpan<byte> expectedSha256)
    {
        if (!workspaceBoundary.TryResolveExistingFile(path, out var resolvedPath, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var actualBytes = new FileInfo(resolvedPath).Length;
        if (actualBytes != expectedBytes)
        {
            throw new IOException(
                $"Staged image output length mismatch. Expected {expectedBytes} bytes; found {actualBytes}.");
        }

        using var stream = new FileStream(
            resolvedPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        var actualSha256 = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actualSha256, expectedSha256))
        {
            throw new IOException("Staged image output digest did not match the frozen provider bytes.");
        }
    }

    internal static void PublishStagedFile(
        WorkspacePathBoundary workspaceBoundary,
        ChatImagePublishFile publish,
        ICollection<(string EffectName, string Path, string RelativePath)> fileEffects,
        ChatImageEffectJournal effectJournal)
    {
        fileEffects.Add((publish.PublishedEffectName, publish.PublishedPath, publish.PublishedRelativePath));
        effectJournal.PublishAttempted(
            publish.PublishedEffectName,
            $"{publish.StagedRelativePath} -> {publish.PublishedRelativePath}");
        try
        {
            if (!workspaceBoundary.TryResolveExistingFile(
                    publish.StagedPath,
                    out var stagedPath,
                    out var boundaryError) ||
                !workspaceBoundary.TryResolveNewFile(
                    publish.PublishedRelativePath,
                    out var publishedPath,
                    out boundaryError))
            {
                throw new InvalidOperationException(boundaryError);
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(
                    Path.GetPathRoot(stagedPath),
                    Path.GetPathRoot(publishedPath),
                    comparison))
            {
                throw new InvalidOperationException("Staged and published image paths are not on the same volume.");
            }

            File.Move(stagedPath, publishedPath);
            effectJournal.PublishCompleted(
                publish.StagedEffectName,
                publish.PublishedEffectName,
                publish.PublishedRelativePath);
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            effectJournal.PublishFailed(publish.PublishedEffectName, exception.GetType().Name);
            throw;
        }
    }

    internal static void RemoveStagingDirectory(
        WorkspacePathBoundary workspaceBoundary,
        string stagingDirectory,
        string stagingRelativePath,
        string stagingEffectName,
        ChatImageEffectJournal effectJournal)
    {
        try
        {
            if (!Directory.Exists(stagingDirectory))
            {
                effectJournal.CleanupNotNeeded(stagingEffectName, "staging directory absent");
                return;
            }

            if (!workspaceBoundary.TryResolveExistingPath(stagingDirectory, out var resolved, out var error))
            {
                throw new InvalidOperationException(error);
            }

            if (Directory.EnumerateFileSystemEntries(resolved).Any())
            {
                throw new IOException("Image staging directory was not empty after publication.");
            }

            Directory.Delete(resolved, recursive: false);
            effectJournal.CleanupCompleted(stagingEffectName, $"{stagingRelativePath} removed");
        }
        catch (Exception exception) when (IsRecoverableFailure(exception))
        {
            effectJournal.CleanupFailed(stagingEffectName, exception.GetType().Name);
            throw;
        }
    }

    internal static void CleanupLocalEffects(
        ChatStore store,
        string? contextItemId,
        string contextEffectName,
        IReadOnlyList<(string EffectName, string Path, string RelativePath)> fileEffects,
        IReadOnlyList<(string EffectName, string Path, string RelativePath)> directoryEffects,
        WorkspacePathBoundary workspaceBoundary,
        ChatImageEffectJournal effectJournal)
    {
        if (!string.IsNullOrWhiteSpace(contextItemId))
        {
            try
            {
                if (store.RemoveContextItem(contextItemId))
                {
                    effectJournal.CleanupCompleted(contextEffectName, "context item removed");
                }
                else
                {
                    effectJournal.CleanupNotNeeded(contextEffectName, "context item confirmed absent");
                }
            }
            catch (Exception exception) when (IsRecoverableFailure(exception))
            {
                effectJournal.CleanupFailed(contextEffectName, exception.GetType().Name);
            }
        }

        foreach (var file in fileEffects.Reverse())
        {
            try
            {
                if (!File.Exists(file.Path))
                {
                    effectJournal.CleanupNotNeeded(file.EffectName, $"{file.RelativePath} absent");
                    continue;
                }

                if (!workspaceBoundary.TryResolveExistingFile(file.Path, out var resolved, out _))
                {
                    effectJournal.CleanupFailed(file.EffectName, $"{file.RelativePath} could not be revalidated");
                    continue;
                }

                File.Delete(resolved);
                if (File.Exists(resolved))
                {
                    effectJournal.CleanupFailed(file.EffectName, $"{file.RelativePath} still exists");
                }
                else
                {
                    effectJournal.CleanupCompleted(file.EffectName, $"{file.RelativePath} removed");
                }
            }
            catch (Exception exception) when (IsRecoverableFailure(exception))
            {
                effectJournal.CleanupFailed(file.EffectName, exception.GetType().Name);
            }
        }

        foreach (var directory in directoryEffects.Reverse())
        {
            try
            {
                if (!Directory.Exists(directory.Path))
                {
                    effectJournal.CleanupNotNeeded(directory.EffectName, $"{directory.RelativePath} absent");
                    continue;
                }

                if (!workspaceBoundary.TryResolveExistingPath(directory.Path, out var resolved, out _))
                {
                    effectJournal.CleanupFailed(
                        directory.EffectName,
                        $"{directory.RelativePath} could not be revalidated");
                    continue;
                }

                if (Directory.EnumerateFileSystemEntries(resolved).Any())
                {
                    effectJournal.CleanupFailed(directory.EffectName, $"{directory.RelativePath} is not empty");
                    continue;
                }

                Directory.Delete(resolved, recursive: false);
                effectJournal.CleanupCompleted(directory.EffectName, $"{directory.RelativePath} removed");
            }
            catch (Exception exception) when (IsRecoverableFailure(exception))
            {
                effectJournal.CleanupFailed(directory.EffectName, exception.GetType().Name);
            }
        }
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

    private static string LimitMessage(string message)
    {
        const int maxLength = 240;
        if (string.IsNullOrEmpty(message))
        {
            return "unspecified provider error";
        }

        var bounded = message.AsSpan(0, Math.Min(message.Length, maxLength)).Trim();
        return bounded.IsEmpty ? "unspecified provider error" : bounded.ToString();
    }

    internal static bool IsRecoverableFailure(Exception exception)
    {
        const int maxExceptionGraphNodes = 256;
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);

        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (visited.Count > maxExceptionGraphNodes)
            {
                return false;
            }

            if (current is OutOfMemoryException or StackOverflowException or AccessViolationException)
            {
                return false;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var innerException in aggregate.InnerExceptions)
                {
                    if (pending.Count >= maxExceptionGraphNodes)
                    {
                        return false;
                    }

                    pending.Push(innerException);
                }
            }
            else if (current.InnerException is { } innerException)
            {
                pending.Push(innerException);
            }
        }

        return true;
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

internal sealed record ChatImagePublishFile(
    string StagedEffectName,
    string StagedPath,
    string StagedRelativePath,
    string PublishedEffectName,
    string PublishedPath,
    string PublishedRelativePath);
