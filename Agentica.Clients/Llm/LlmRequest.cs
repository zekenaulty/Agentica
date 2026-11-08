using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

public enum OpenAiHistoryMode { NativeContinuation, TranscriptOnly }

public sealed record LlmRequest(
    string ModelId,
    IReadOnlyList<LlmMessage> Messages,
    LlmGenerationOptions? GenerationOptions = null,
    LlmStructuredOutputOptions? StructuredOutput = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    [property: JsonIgnore] LlmNativeContinuation? NativeContinuation = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LlmMessageMode MessageMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LlmInstructionRolePolicy InstructionRolePolicy { get; init; }

    /// <summary>In Ordered mode, Messages are new items appended after the carrier's entire
    /// retained input/output prefix. Never pass the complete frame again with this flag.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AppendToNativeContinuation { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmPromptCacheOptions? PromptCache { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public OpenAiHistoryMode OpenAiHistoryMode { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LlmWebSearchOptions? WebSearch { get; init; }

    [JsonIgnore]
    public LlmInputCompilationReceipt? InputCompilationReceipt { get; init; }

    /// <summary>
    /// Optional single-attempt observation holder. It retains observed evidence even
    /// when ordinary caller cancellation prevents returning a response. It does not
    /// authorize dispatch or replace a host's durable attempt journal.
    /// </summary>
    [JsonIgnore]
    public LlmAttemptCapture? AttemptCapture { get; init; }

    [JsonIgnore]
    internal LlmPreparedDispatch? PreparedDispatch { get; init; }
}

internal static class LlmHistoryModeGuard
{
    internal static void RequireNativeMode(LlmRequest request, string provider, bool supportsOrdered = false)
    {
        LlmMessageMapping.Validate(request, provider, supportsOrdered);
        if (request.OpenAiHistoryMode != OpenAiHistoryMode.NativeContinuation)
            throw new LlmClientException(provider, "OpenAI transcript history mode is unsupported by this route.",
                errorKind: LlmClientErrorKind.BadRequest, errorClass: "unsupported_history_mode");
    }
}
