using System.Text.Json;

namespace Agentica.Clients.Llm;

/// <summary>Legacy retains existing adapter behavior. Ordered preserves explicit message boundaries.</summary>
public enum LlmMessageMode { Legacy, Ordered }
public enum LlmInstructionRolePolicy { AllowInterpretedFallback, RequireNativeSeparation }
public enum LlmAssistantPhase { Commentary, FinalAnswer }
public enum LlmPromptCacheMode { Implicit, Explicit }
public enum LlmPromptCacheLifetime { ThirtyMinutes, TwelveHours }

public sealed record LlmPromptCacheBreakpoint(LlmPromptCacheLifetime? Lifetime = null);
public sealed record LlmPromptCacheOptions(
    LlmPromptCacheMode Mode = LlmPromptCacheMode.Implicit,
    LlmPromptCacheLifetime Lifetime = LlmPromptCacheLifetime.ThirtyMinutes);

internal static class LlmMessageMapping
{
    internal static void Validate(LlmRequest request, string provider, bool supportsOrdered = false)
    {
        ArgumentNullException.ThrowIfNull(request.Messages);
        if (request.Messages.Any(message => message is null || message.Content is null || !Enum.IsDefined(message.Role)))
            throw Failure(provider, "invalid_message");
        if (!Enum.IsDefined(request.MessageMode)) throw Failure(provider, "unsupported_message_mode");
        if (!Enum.IsDefined(request.InstructionRolePolicy)) throw Failure(provider, "invalid_instruction_role_policy");
        if (request.MessageMode == LlmMessageMode.Ordered)
        {
            if (!supportsOrdered) throw Failure(provider, "unsupported_message_mode");
            if (provider is not ("openai" or "xai") && request.Messages.Any(message => message.FunctionCall is not null || message.ToolCallId is not null))
                throw Failure(provider, "unsupported_native_tool_mapping");
            if (provider != "openai" && (request.PromptCache is not null || request.Messages.Any(message =>
                    message.Phase is not null || message.CacheBreakpoint is not null)))
                throw Failure(provider, "unsupported_message_metadata");
            if (provider == "openai" && request.Messages.Any(message =>
                    message.Role == LlmMessageRole.Assistant && message.CacheBreakpoint is not null))
                throw Failure(provider, "unsupported_assistant_cache_breakpoint");
            if (provider != "openai" && request.InstructionRolePolicy == LlmInstructionRolePolicy.RequireNativeSeparation &&
                request.Messages.Any(message => message.Role == LlmMessageRole.Developer))
                throw Failure(provider, "native_developer_role_required");
            if (request.OpenAiHistoryMode != OpenAiHistoryMode.NativeContinuation)
                throw Failure(provider, "ordered_history_mode_conflict");
            if (request.AppendToNativeContinuation != (request.NativeContinuation is not null))
                throw Failure(provider, "ordered_continuation_requires_explicit_append");
        }
        else if (request.InstructionRolePolicy != LlmInstructionRolePolicy.AllowInterpretedFallback ||
                 request.AppendToNativeContinuation || request.PromptCache is not null ||
                 request.Messages.Any(message => message.Phase is not null || message.CacheBreakpoint is not null ||
                     message.FunctionCall is not null || message.ToolCallId is not null))
            throw Failure(provider, "ordered_message_mode_required");

        if (request.MessageMode == LlmMessageMode.Ordered && request.Messages.Count is < 1 or > 8192)
            throw Failure(provider, "invalid_message");
        foreach (var message in request.Messages)
        {
            if (message.FunctionCall is { } call &&
                (message.Role != LlmMessageRole.Assistant || message.Content.Length != 0 || message.Phase is not null ||
                 message.CacheBreakpoint is not null || message.ToolCallId is not null || !ValidCall(call)))
                throw Failure(provider, "invalid_native_function_call");
            if (message.ToolCallId is { } callId && (message.Role != LlmMessageRole.Tool || !ValidIdentity(callId) ||
                message.CacheBreakpoint is not null)) throw Failure(provider, "invalid_native_tool_result");
            if (message.Phase is { } phase &&
                (message.Role != LlmMessageRole.Assistant || !Enum.IsDefined(phase)))
                throw Failure(provider, "invalid_assistant_phase");
            if (message.CacheBreakpoint?.Lifetime is { } lifetime && !Enum.IsDefined(lifetime))
                throw Failure(provider, "invalid_prompt_cache_options");
        }
        if (request.PromptCache is { } cache &&
            (!Enum.IsDefined(cache.Mode) || !Enum.IsDefined(cache.Lifetime)))
            throw Failure(provider, "invalid_prompt_cache_options");
    }

    internal static bool ValidIdentity(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 512 &&
        text.Trim() == text && !text.Any(char.IsControl);

    internal static bool ValidCall(LlmFunctionCall call)
    {
        if (!ValidIdentity(call.CallId) || !ValidIdentity(call.Name) ||
            call.ArgumentsJson is null || call.ArgumentsJson.Length > 1_048_576) return false;
        try
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 64 });
            return arguments.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    internal static string Lifetime(LlmPromptCacheLifetime lifetime) => lifetime switch
    {
        LlmPromptCacheLifetime.ThirtyMinutes => "30m",
        LlmPromptCacheLifetime.TwelveHours => "12h",
        _ => throw new ArgumentOutOfRangeException(nameof(lifetime))
    };

    internal static LlmClientException Failure(string provider, string code) =>
        new(provider, "The requested message mapping is unsupported or invalid.",
            errorKind: LlmClientErrorKind.BadRequest, errorClass: code);
}
