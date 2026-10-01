using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

/// <summary>
/// Provider-owned replay state for a stateless follow-up. It is deliberately absent from
/// ordinary JSON serialization, logs, planner text, and receipts. Only the matching adapter
/// may interpret its bounded native payload. It is a continuation carrier, not
/// canonical task state, an authority grant, or a verified account of reasoning.
/// Disposal releases references to private strings; it cannot erase immutable
/// string copies already made by provider parsing or the runtime.
/// </summary>
public sealed class LlmNativeContinuation : IDisposable
{
    internal const int MaxPayloadCharacters = 4_194_304;
    private string? _systemInstruction;
    private string? _historyStepsJson;

    internal LlmNativeContinuation(
        string providerName,
        string modelId,
        string systemInstruction,
        string historyStepsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(systemInstruction);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyStepsJson);
        if (historyStepsJson.Length > MaxPayloadCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(historyStepsJson));
        }
        using var parsed = JsonDocument.Parse(historyStepsJson);
        if (parsed.RootElement.ValueKind != JsonValueKind.Array ||
            parsed.RootElement.GetArrayLength() == 0 ||
            parsed.RootElement.EnumerateArray().Any(step =>
                step.ValueKind != JsonValueKind.Object))
        {
            throw new ArgumentException(
                "Native continuation must contain nonempty provider steps.",
                nameof(historyStepsJson));
        }

        ProviderName = providerName;
        ModelId = modelId;
        _systemInstruction = systemInstruction;
        _historyStepsJson = historyStepsJson;
    }

    public string ProviderName { get; }
    public string ModelId { get; }

    [JsonIgnore]
    internal string SystemInstruction => Volatile.Read(ref _systemInstruction) ??
        throw new ObjectDisposedException(nameof(LlmNativeContinuation));

    [JsonIgnore]
    internal string HistoryStepsJson => Volatile.Read(ref _historyStepsJson) ??
        throw new ObjectDisposedException(nameof(LlmNativeContinuation));

    public void Dispose()
    {
        Interlocked.Exchange(ref _historyStepsJson, null);
        Interlocked.Exchange(ref _systemInstruction, null);
    }

    public override string ToString() =>
        $"Native continuation for {ProviderName}/{ModelId} (payload redacted)";
}
