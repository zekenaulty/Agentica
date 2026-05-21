using System.Text.Json;
using Agentica.Outcomes;

internal static class ChatRenderer
{
    public static string AssistantMessage(OutcomeEnvelope envelope)
    {
        var response = envelope.Details.Artifacts
            .LastOrDefault(artifact => artifact.Kind == ChatArtifactKinds.Response);
        if (response is not null && response.Payload.TryGetValue("content", out var content))
        {
            return ContentToString(content) ?? envelope.Report.Summary;
        }

        if (envelope.Outcome.Blockers.Count > 0)
        {
            return "Blocked: " + string.Join("; ", envelope.Outcome.Blockers);
        }

        return envelope.Report.Summary;
    }

    public static void PrintTurn(OutcomeEnvelope envelope, string assistantMessage)
    {
        Console.WriteLine();
        Console.WriteLine("assistant> " + assistantMessage.ReplaceLineEndings("\n           "));
        Console.WriteLine();
        Console.WriteLine($"run {envelope.Outcome.RunId} status={envelope.Outcome.Status} receipts={envelope.Receipts.Items.Count} artifacts={envelope.Details.Artifacts.Count}");

        foreach (var receipt in envelope.Receipts.Items)
        {
            Console.WriteLine($"  receipt {receipt.Status,-9} {receipt.ToolId}: {Compact(receipt.Message)}");
        }

        foreach (var imagePath in ImagePaths(envelope))
        {
            Console.WriteLine($"  image {imagePath}");
        }

        if (envelope.Outcome.Blockers.Count > 0)
        {
            foreach (var blocker in envelope.Outcome.Blockers)
            {
                Console.WriteLine($"  blocker {Compact(blocker)}");
            }
        }
    }

    public static string SerializeOutcome(OutcomeEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, JsonOptions.Create());

    private static string? ContentToString(object? content)
    {
        if (content is null)
        {
            return null;
        }

        if (content is string text)
        {
            return text;
        }

        if (content is JsonElement element)
        {
            return element.ValueKind == JsonValueKind.String
                ? element.GetString()
                : element.ToString();
        }

        return content.ToString();
    }

    private static IEnumerable<string> ImagePaths(OutcomeEnvelope envelope)
    {
        foreach (var artifact in envelope.Details.Artifacts.Where(artifact => artifact.Kind == ChatArtifactKinds.WorkspaceImage))
        {
            if (!artifact.Payload.TryGetValue("images", out var images) || images is null)
            {
                continue;
            }

            foreach (var path in ImagePathsFromValue(images))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> ImagePathsFromValue(object value)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.TryGetProperty("path", out var pathElement) &&
                    pathElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(pathElement.GetString()))
                {
                    yield return pathElement.GetString()!;
                }
            }

            yield break;
        }

        if (value is not System.Collections.IEnumerable enumerable || value is string)
        {
            yield break;
        }

        foreach (var item in enumerable)
        {
            if (item is IReadOnlyDictionary<string, object?> dictionary &&
                dictionary.TryGetValue("path", out var path) &&
                path is not null &&
                !string.IsNullOrWhiteSpace(path.ToString()))
            {
                yield return path.ToString()!;
            }
        }
    }

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 180 ? compact : compact[..177] + "...";
    }
}
