using System.Text.Json;
using Agentica.Clients.Llm;

namespace Agentica.Clients.OpenAI;

internal static class OpenAiOrderedMessages
{
    internal static List<object> Input(LlmRequest request, string model, string provider)
    {
        var input = LlmOrderedProjection.History(request, provider, model);
        var calls = new HashSet<string>(StringComparer.Ordinal);
        var allCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var retained in input.Cast<JsonElement>())
        {
            if (!retained.TryGetProperty("type", out var type)) continue;
            if (type.GetString() == "function_call")
            {
                var id = retained.GetProperty("call_id").GetString()!;
                calls.Add(id); allCalls.Add(id);
            }
            else if (type.GetString() == "function_call_output") calls.Remove(retained.GetProperty("call_id").GetString()!);
        }
        foreach (var message in request.Messages)
        {
            if (message.FunctionCall is { } call)
            {
                if (!allCalls.Add(call.CallId)) throw LlmMessageMapping.Failure(provider, "duplicate_native_function_call");
                calls.Add(call.CallId);
                input.Add(new { type = "function_call", call_id = call.CallId, name = call.Name, arguments = call.ArgumentsJson });
                continue;
            }
            if (message.Role == LlmMessageRole.Tool)
            {
                if (message.ToolCallId is not { } id || !calls.Remove(id))
                    throw LlmMessageMapping.Failure(provider, "native_tool_linkage_required");
                input.Add(new { type = "function_call_output", call_id = id, output = message.Content });
                continue;
            }
            var text = new Dictionary<string, object?> { ["type"] = "input_text", ["text"] = message.Content };
            if (message.CacheBreakpoint is { } breakpoint)
            {
                var cache = new Dictionary<string, object?> { ["mode"] = "explicit" };
                if (breakpoint.Lifetime is { } lifetime) cache["ttl"] = LlmMessageMapping.Lifetime(lifetime);
                text["prompt_cache_breakpoint"] = cache;
            }
            var item = new Dictionary<string, object?>
            {
                ["role"] = provider == "xai" && message.Role == LlmMessageRole.Developer ? "system" : message.Role.ToString().ToLowerInvariant(),
                ["content"] = provider == "xai" ? message.Role == LlmMessageRole.Developer
                    ? LlmOrderedProjection.InstructionPrefix([message]) : message.Content : new[] { text }
            };
            if (message.Phase is { } phase)
                item["phase"] = phase == LlmAssistantPhase.Commentary ? "commentary" : "final_answer";
            input.Add(item);
        }
        var json = JsonSerializer.Serialize(input);
        if (json.Length > LlmNativeContinuation.MaxPayloadCharacters)
            throw LlmMessageMapping.Failure(provider, "continuation_too_large");
        using var parsed = JsonDocument.Parse(json);
        var breakpoints = parsed.RootElement.EnumerateArray().Sum(item =>
            item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? content.EnumerateArray().Count(block => block.TryGetProperty("prompt_cache_breakpoint", out _)) : 0);
        if (breakpoints > 4)
            throw LlmMessageMapping.Failure(provider, "prompt_cache_breakpoint_limit");
        return input;
    }

    internal static LlmNativeContinuation? Continue(LlmRequest request, string model, JsonElement output, string provider)
    {
        if (output.GetArrayLength() == 0 || output.EnumerateArray().Any(item =>
            item.TryGetProperty("type", out var type) && type.GetString() == "reasoning" &&
            (!item.TryGetProperty("encrypted_content", out var encrypted) ||
             encrypted.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(encrypted.GetString())))) return null;
        var history = Input(request, model, provider);
        history.AddRange(output.EnumerateArray().Select(item => (object)item.Clone()));
        var json = JsonSerializer.Serialize(history);
        return json.Length <= LlmNativeContinuation.MaxPayloadCharacters
            ? new(provider, model, "", json, messageMode: LlmMessageMode.Ordered) : null;
    }
}
