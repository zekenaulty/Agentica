namespace Agentica.Clients.Llm;

/// <summary>Legacy retains existing adapter behavior. Ordered preserves explicit message boundaries.</summary>
public enum LlmMessageMode { Legacy, Ordered }
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
        if (request.MessageMode == LlmMessageMode.Ordered)
        {
            if (!supportsOrdered) throw Failure(provider, "unsupported_message_mode");
            if (request.OpenAiHistoryMode != OpenAiHistoryMode.NativeContinuation)
                throw Failure(provider, "ordered_history_mode_conflict");
            if (request.AppendToNativeContinuation != (request.NativeContinuation is not null))
                throw Failure(provider, "ordered_continuation_requires_explicit_append");
        }
        else if (request.AppendToNativeContinuation || request.PromptCache is not null ||
                 request.Messages.Any(message => message.Phase is not null || message.CacheBreakpoint is not null))
            throw Failure(provider, "ordered_message_mode_required");

        if (request.MessageMode == LlmMessageMode.Ordered && request.Messages.Count is < 1 or > 8192)
            throw Failure(provider, "invalid_message");
        foreach (var message in request.Messages)
        {
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
