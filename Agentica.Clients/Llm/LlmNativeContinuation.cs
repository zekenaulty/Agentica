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
    private readonly object _gate = new();
    private string? _systemInstruction;
    private string? _historyStepsJson;
    private LlmContinuationConfigurationBinding? _configurationBinding;

    internal LlmNativeContinuation(
        string providerName,
        string modelId,
        string systemInstruction,
        string historyStepsJson,
        LlmContinuationConfigurationBinding? configurationBinding = null)
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
        _configurationBinding = configurationBinding;
    }

    public string ProviderName { get; }
    public string ModelId { get; }

    [JsonIgnore]
    internal string SystemInstruction => SnapshotOwned().Instruction;

    [JsonIgnore]
    internal string HistoryStepsJson => SnapshotOwned().History;

    internal (string Instruction, string History, LlmContinuationConfigurationBinding? Binding) SnapshotOwned()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_historyStepsJson is null || _systemInstruction is null, this);
            return (_systemInstruction, _historyStepsJson, _configurationBinding);
        }
    }

    /// <summary>Creates separately disposable ownership while retaining an imported/attached binding.</summary>
    internal LlmNativeContinuation CloneOwned()
    {
        var snapshot = SnapshotOwned();
        return new(ProviderName, ModelId, snapshot.Instruction, snapshot.History, snapshot.Binding);
    }

    internal bool TryBindConfiguration(LlmContinuationConfigurationBinding binding)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_historyStepsJson is null, this);
            if (_configurationBinding is not null) return _configurationBinding == binding;
            _configurationBinding = binding;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _historyStepsJson = null;
            _systemInstruction = null;
            _configurationBinding = null;
        }
    }

    public override string ToString() =>
        $"Native continuation for {ProviderName}/{ModelId} (payload redacted)";
}

internal sealed record LlmContinuationConfigurationBinding(string ApiMode, string ConfigurationIdentity);
