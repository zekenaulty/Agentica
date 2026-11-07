using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

public sealed record LlmRequest(
    string ModelId,
    IReadOnlyList<LlmMessage> Messages,
    LlmGenerationOptions? GenerationOptions = null,
    LlmStructuredOutputOptions? StructuredOutput = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    [property: JsonIgnore] LlmNativeContinuation? NativeContinuation = null)
{
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
