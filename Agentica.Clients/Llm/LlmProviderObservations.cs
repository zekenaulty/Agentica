using System.Text.Json;

namespace Agentica.Clients.Llm;

/// <summary>Reads bounded structural facts before content acceptance or native replay construction.</summary>
internal static class LlmProviderObservations
{
    internal static void Responses(LlmAttemptRecorder recorder, JsonElement root)
    {
        var response = Object(root, "response");
        if (response.ValueKind != JsonValueKind.Object) response = root;
        var usage = Object(response, "usage");
        var reason = String(Object(response, "incomplete_details"), "reason");
        var status = String(response, "status");
        recorder.Observe(providerReportedModelId: String(response, "model", 512),
            providerResponseId: String(response, "id", 512), providerStatus: status,
            finishReason: status == "incomplete" && reason == "max_output_tokens" ? LlmFinishReason.MaxTokens :
                status == "failed" ? LlmFinishReason.Error : status == "completed" ? LlmFinishReason.Stop : null,
            usage: usage.ValueKind == JsonValueKind.Object ? new LlmUsage(Number(usage, "input_tokens"),
                Number(usage, "output_tokens"), Number(Object(usage, "output_tokens_details"), "reasoning_tokens"),
                Number(usage, "total_tokens"), Number(Object(usage, "input_tokens_details"), "cached_tokens"))
                { OutputTokenSemantics = LlmOutputTokenSemantics.IncludesThinking } : null);
    }

    internal static void Interactions(LlmAttemptRecorder recorder, JsonElement root)
    {
        var interaction = Object(root, "interaction");
        var usage = Object(interaction, "usage");
        var status = String(interaction, "status");
        recorder.Observe(providerReportedModelId: String(interaction, "model", 512),
            providerResponseId: String(interaction, "id", 512), providerStatus: status,
            finishReason: status == "incomplete" ? LlmFinishReason.MaxTokens :
                status is "failed" or "cancelled" ? LlmFinishReason.Error : status == "completed" ? LlmFinishReason.Stop : null,
            usage: usage.ValueKind == JsonValueKind.Object ? new LlmUsage(Number(usage, "total_input_tokens"),
                Number(usage, "total_output_tokens"), Number(usage, "total_thought_tokens"),
                Number(usage, "total_tokens"), Number(usage, "total_cached_tokens"), Number(usage, "total_tool_use_tokens"))
                { OutputTokenSemantics = LlmOutputTokenSemantics.ExcludesThinking } : null);
    }

    internal static void Anthropic(LlmAttemptRecorder recorder, JsonElement root)
    {
        var initial = Object(root, "message");
        var usage = Object(root, "usage");
        if (usage.ValueKind != JsonValueKind.Object) usage = Object(initial, "usage");
        var reason = String(Object(root, "delta"), "stop_reason") ?? String(initial, "stop_reason");
        var input = Number(usage, "input_tokens") ?? recorder.Evidence.Usage?.PromptTokens;
        var output = Number(usage, "output_tokens") ?? recorder.Evidence.Usage?.OutputTokens;
        recorder.Observe(providerReportedModelId: String(initial, "model", 512),
            providerResponseId: String(initial, "id", 512), providerStatus: reason,
            finishReason: reason == "max_tokens" ? LlmFinishReason.MaxTokens :
                reason == "refusal" ? LlmFinishReason.Blocked : reason == "end_turn" ? LlmFinishReason.Stop : null,
            usage: usage.ValueKind == JsonValueKind.Object ? new LlmUsage(input, output,
                TotalTokens: LlmUsage.SumTokenCounts(input, output), CachedPromptTokens: Number(usage, "cache_read_input_tokens"))
                { OutputTokenSemantics = LlmOutputTokenSemantics.IncludesThinking } : null);
    }

    internal static void Ollama(LlmAttemptRecorder recorder, JsonElement root)
    {
        var input = Number(root, "prompt_eval_count");
        var output = Number(root, "eval_count");
        recorder.Observe(providerReportedModelId: String(root, "model", 512),
            providerStatus: String(root, "done_reason"),
            usage: input is not null || output is not null ? new LlmUsage(input, output,
                TotalTokens: LlmUsage.SumTokenCounts(input, output), CachedPromptTokens: Number(root, "prompt_eval_cached_count")) : null,
            finishReason: String(root, "done_reason") == "length" ? LlmFinishReason.MaxTokens :
                String(root, "done_reason") == "stop" ? LlmFinishReason.Stop : null);
    }

    internal static void GenerateContent(LlmAttemptRecorder recorder, JsonElement root)
    {
        var usage = Object(root, "usageMetadata");
        string? reason = null;
        var candidates = Object(root, "candidates");
        if (candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
            reason = String(candidates[0], "finishReason");
        reason ??= String(Object(root, "promptFeedback"), "blockReason");
        recorder.Observe(providerReportedModelId: String(root, "modelVersion", 512),
            providerResponseId: String(root, "responseId", 512), providerStatus: reason,
            finishReason: reason switch
            {
                "STOP" => LlmFinishReason.Stop,
                "MAX_TOKENS" => LlmFinishReason.MaxTokens,
                "SAFETY" => LlmFinishReason.Safety,
                "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "RECITATION" => LlmFinishReason.Blocked,
                "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL" => LlmFinishReason.Error,
                null or "FINISH_REASON_UNSPECIFIED" or "BLOCK_REASON_UNSPECIFIED" => null,
                _ => LlmFinishReason.Other
            },
            usage: usage.ValueKind == JsonValueKind.Object ? new LlmUsage(Number(usage, "promptTokenCount"),
                Number(usage, "candidatesTokenCount"), Number(usage, "thoughtsTokenCount"),
                Number(usage, "totalTokenCount"), Number(usage, "cachedContentTokenCount"), Number(usage, "toolUsePromptTokenCount"))
                { OutputTokenSemantics = LlmOutputTokenSemantics.ExcludesThinking } : null);
    }

    private static JsonElement Object(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
    private static int? Number(JsonElement value, string key) =>
        Object(value, key) is var found && found.ValueKind == JsonValueKind.Number && found.TryGetInt32(out var result) ? result : null;
    private static string? String(JsonElement value, string key, int max = 128)
    {
        var item = Object(value, key);
        var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
        return text is { Length: > 0 } && text.Length <= max && !text.Any(char.IsControl) ? text : null;
    }
}
