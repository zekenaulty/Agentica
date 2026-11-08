using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.OpenAI;

internal static class OpenAiOrderedMessages
{
    internal static List<object> Input(LlmRequest request, string model)
    {
        var input = new List<object>();
        if (request.NativeContinuation is { } native)
        {
            if (native.ProviderName != OpenAiResponsesLlmClient.ProviderName || native.ModelId != model ||
                native.MessageMode != LlmMessageMode.Ordered)
                throw LlmMessageMapping.Failure(OpenAiResponsesLlmClient.ProviderName, "continuation_binding_mismatch");
            if (request.Messages.Any(message => message.Role == LlmMessageRole.Assistant))
                throw LlmMessageMapping.Failure(OpenAiResponsesLlmClient.ProviderName, "ordered_append_contains_assistant_history");
            using var history = JsonDocument.Parse(native.HistoryStepsJson);
            input.AddRange(history.RootElement.EnumerateArray().Select(item => (object)item.Clone()));
        }
        foreach (var message in request.Messages)
        {
            if (message.Role == LlmMessageRole.Tool)
                throw LlmMessageMapping.Failure(OpenAiResponsesLlmClient.ProviderName, "native_tool_linkage_required");
            var text = new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = message.Content };
            if (message.CacheBreakpoint is { } breakpoint)
            {
                var cache = new Dictionary<string, object?> { ["mode"] = "explicit" };
                if (breakpoint.Lifetime is { } lifetime) cache["ttl"] = LlmMessageMapping.Lifetime(lifetime);
                text["prompt_cache_breakpoint"] = cache;
            }
            var item = new Dictionary<string, object?>
            {
                ["role"] = message.Role.ToString().ToLowerInvariant(),
                ["content"] = new[] { text }
            };
            if (message.Phase is { } phase)
                item["phase"] = phase == LlmAssistantPhase.Commentary ? "commentary" : "final_answer";
            input.Add(item);
        }
        var json = JsonSerializer.Serialize(input);
        if (json.Length > LlmNativeContinuation.MaxPayloadCharacters)
            throw LlmMessageMapping.Failure(OpenAiResponsesLlmClient.ProviderName, "continuation_too_large");
        using var parsed = JsonDocument.Parse(json);
        var breakpoints = parsed.RootElement.EnumerateArray().Sum(item =>
            item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? content.EnumerateArray().Count(block => block.TryGetProperty("prompt_cache_breakpoint", out _)) : 0);
        if (breakpoints > 4)
            throw LlmMessageMapping.Failure(OpenAiResponsesLlmClient.ProviderName, "prompt_cache_breakpoint_limit");
        return input;
    }

    internal static LlmNativeContinuation? Continue(LlmRequest request, string model, JsonElement output)
    {
        if (output.GetArrayLength() == 0 || output.EnumerateArray().Any(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "reasoning" &&
            (!item.TryGetProperty("encrypted_content", out var encrypted) ||
             encrypted.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(encrypted.GetString())))) return null;
        var history = Input(request, model);
        history.AddRange(output.EnumerateArray().Select(item => (object)item.Clone()));
        var json = JsonSerializer.Serialize(history);
        return json.Length <= LlmNativeContinuation.MaxPayloadCharacters
            ? new(OpenAiResponsesLlmClient.ProviderName, model, "", json, messageMode: LlmMessageMode.Ordered) : null;
    }
}
