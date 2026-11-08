using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.OpenAI;

internal static class OpenAiReadableStream
{
    internal static LlmStreamSource Source(JsonElement item, string provider, string type) => new(provider, type,
        item.TryGetProperty("sequence_number", out var sequence) && sequence.ValueKind == JsonValueKind.Number && sequence.TryGetInt64(out var ordinal) ? ordinal : null,
        Text(item, "item_id") ?? (item.TryGetProperty("item", out var output) ? Text(output, "id") : null),
        Index(item, "output_index"), Index(item, "content_index"), Index(item, "summary_index"));

    internal static LlmStreamEvent? Boundary(JsonElement item, string provider, string? type)
    {
        var summary = type is "response.reasoning_summary_part.added" or "response.reasoning_summary_part.done" or "response.reasoning_summary_text.done";
        var content = type is "response.content_part.added" or "response.content_part.done";
        var textDone = type is "response.output_text.done" or "response.reasoning_summary_text.done";
        if (!summary && !content && !textDone) return null;
        var part = item.TryGetProperty("part", out var value) ? value : default;
        if (!textDone && Text(part, "type") != (summary ? "summary_text" : "output_text")) return null;
        var lifecycle = textDone ? LlmStreamBlockLifecycle.TextCompleted
            : type!.EndsWith(".added", StringComparison.Ordinal) ? LlmStreamBlockLifecycle.Started : LlmStreamBlockLifecycle.Completed;
        return new(LlmStreamEventKind.ContentBlock)
        {
            Source = Source(item, provider, type!),
            Block = new(summary ? LlmStreamBlockKind.ReasoningSummary : LlmStreamBlockKind.OutputText,
                lifecycle, Text(textDone ? item : part, "text"),
                Text(item, "status") switch { "incomplete" => true, "completed" => false, _ => null })
        };
    }

    private static string? Text(JsonElement item, string field) => item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? Index(JsonElement item, string field) => item.TryGetProperty(field, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var index) && index >= 0 ? index : null;
}
