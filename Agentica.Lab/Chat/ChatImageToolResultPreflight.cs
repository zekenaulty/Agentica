using System.Text;
using System.Text.Json;

internal static class ChatImageToolResultPreflight
{
    private const int MaxStructuredDepth = 32;
    private const int MaxStructuredNodes = 16_384;
    private const int MaxTotalSnapshotBytes = 1024 * 1024;
    private const int MaxStringBytes = 256 * 1024;

    // The final journal is appended only after publication and SQLite persistence. Its own
    // contract is 64 entries with 240-character subjects/details and 16 effect names. This
    // reserve covers all three copies (receipt, observation, artifact), runtime-owned ids,
    // evidence, and messages without allowing a post-effect normalizer surprise.
    private const int FinalProofReserveBytes = 192 * 1024;
    private const int FinalProofReserveNodes = 2048;

    public static byte[] ValidateAndSerialize(
        IReadOnlyDictionary<string, object?> data,
        string sourceToolId,
        int imageCount,
        string firstPath)
    {
        ArgumentNullException.ThrowIfNull(data);
        var receiptMessage = sourceToolId.Equals(ChatToolIds.WorkspaceImageCreate, StringComparison.Ordinal)
            ? $"Composed artist prompt and generated {imageCount} image(s). First image: {firstPath}"
            : $"Generated {imageCount} image(s). First image: {firstPath}";
        var observationSummary = sourceToolId.Equals(ChatToolIds.WorkspaceImageCreate, StringComparison.Ordinal)
            ? $"Composed artist prompt and generated {imageCount} workspace image(s)."
            : $"Generated {imageCount} workspace image(s).";

        byte[] serialized;
        try
        {
            serialized = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions.Create());
        }
        catch (Exception exception) when (ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            throw new InvalidOperationException(
                "Image result data could not be serialized before effect publication.",
                exception);
        }

        using var document = JsonDocument.Parse(serialized, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaxStructuredDepth
        });
        var shape = Measure(document.RootElement, depth: 0);
        var estimatedBytes = checked(
            (3L * serialized.Length) +
            Encoding.UTF8.GetByteCount(receiptMessage) +
            Encoding.UTF8.GetByteCount(observationSummary) +
            Encoding.UTF8.GetByteCount(ChatArtifactKinds.WorkspaceImage) +
            FinalProofReserveBytes);
        var estimatedNodes = checked((3L * shape.Nodes) + FinalProofReserveNodes);
        if (estimatedBytes > MaxTotalSnapshotBytes)
        {
            throw new InvalidOperationException(
                "Image result would exceed the runtime's aggregate tool-result byte contract.");
        }

        if (estimatedNodes > MaxStructuredNodes)
        {
            throw new InvalidOperationException(
                "Image result would exceed the runtime's aggregate tool-result node contract.");
        }

        return serialized;
    }

    private static (long Nodes, int MaxDepth) Measure(JsonElement element, int depth)
    {
        if (depth > MaxStructuredDepth)
        {
            throw new InvalidOperationException(
                "Image result would exceed the runtime's structured depth contract.");
        }

        long nodes = 1;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    EnsureStringFits(property.Name, "property name");
                    var child = Measure(property.Value, depth + 1);
                    nodes = checked(nodes + child.Nodes);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var child = Measure(item, depth + 1);
                    nodes = checked(nodes + child.Nodes);
                }

                break;
            case JsonValueKind.String:
                EnsureStringFits(element.GetString() ?? string.Empty, "string value");
                break;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                break;
            default:
                throw new InvalidOperationException("Image result contains an unsupported JSON value.");
        }

        return (nodes, depth);
    }

    private static void EnsureStringFits(string value, string description)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaxStringBytes)
        {
            throw new InvalidOperationException(
                $"Image result {description} exceeds the runtime's per-string byte contract.");
        }
    }
}
