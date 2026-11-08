using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

public enum LlmNativeContinuationRejection
{
    None,
    Unavailable,
    Disposed,
    UnsupportedVersion,
    UnsupportedProvider,
    UnsupportedApiMode,
    MalformedPayload,
    PayloadTooLarge,
    DepthLimitExceeded,
    CollectionLimitExceeded,
    InvalidNativeShape,
    ProviderMismatch,
    ApiModeMismatch,
    ModelMismatch,
    InstructionMismatch,
    ConfigurationMismatch,
    IntegrityMismatch,
    MessageModeMismatch
}

/// <summary>Exact target/source bindings. Instructions are sensitive and excluded from ordinary
/// serialization and display. Configuration identity is a host assertion for an unbound carrier.</summary>
public sealed class LlmNativeContinuationBinding : IDisposable
{
    private string? _instructions;

    public LlmNativeContinuationBinding(string providerName, string apiMode, string modelId,
        string configurationIdentity, string systemInstruction,
        LlmMessageMode messageMode = LlmMessageMode.Legacy)
    {
        ProviderName = Identity(providerName, nameof(providerName), 128);
        ApiMode = Identity(apiMode, nameof(apiMode), 128);
        ModelId = Identity(modelId, nameof(modelId), 512);
        ConfigurationIdentity = Identity(configurationIdentity, nameof(configurationIdentity), 512);
        ArgumentNullException.ThrowIfNull(systemInstruction);
        if (systemInstruction.Length > LlmNativeContinuationCodec.MaximumInstructionCharacters)
            throw new ArgumentOutOfRangeException(nameof(systemInstruction));
        _instructions = systemInstruction;
        if (!Enum.IsDefined(messageMode)) throw new ArgumentOutOfRangeException(nameof(messageMode));
        MessageMode = messageMode;
    }

    public string ProviderName { get; }
    public string ApiMode { get; }
    public string ModelId { get; }
    public string ConfigurationIdentity { get; }
    public LlmMessageMode MessageMode { get; }
    [JsonIgnore]
    internal string SystemInstruction => Volatile.Read(ref _instructions) ?? throw new ObjectDisposedException(nameof(LlmNativeContinuationBinding));

    /// <summary>Uses the actual report's resolved configuration and the adapter's instruction
    /// projection. It does not certify which configuration produced an existing unbound carrier.</summary>
    public static LlmNativeContinuationBinding FromRequest(LlmConfigurationReport report, LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(request);
        if (!report.AdapterAcceptsRequest || report.RouteResolution.State != LlmCapabilitySupport.Supported ||
            report.RequestedModelId != request.ModelId)
            throw new ArgumentException("A matching accepted request and resolved configuration report are required.", nameof(report));
        var instructions = request.MessageMode == LlmMessageMode.Ordered ? ""
            : report.ProviderName == "ollama"
            ? string.Join("\n\n", request.Messages.Where(message => message.Role == LlmMessageRole.System).Select(message => message.Content))
            : string.Join("\n\n", request.Messages.Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.Developer)
                .Select(message => message.Content).Where(content => !string.IsNullOrWhiteSpace(content)));
        return new(report.ProviderName, report.ApiMode, report.EffectiveModelId, report.ConfigurationIdentity, instructions, request.MessageMode);
    }

    public void Dispose() => Interlocked.Exchange(ref _instructions, null);
    public override string ToString() => "Native continuation binding (instructions redacted)";

    private static string Identity(string value, string name, int max)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > max || value.Any(char.IsControl) || value.StartsWith("unresolved:", StringComparison.Ordinal))
            throw new ArgumentException("Binding identity must be bounded, resolved and free of control characters.", name);
        return value;
    }
}

/// <summary>Sensitive bytes require an explicit copy. Dispose zeroes this instance's byte array,
/// not copies made by callers, JSON parsing or the runtime. Hosts own encryption and retention.</summary>
public sealed class LlmSensitiveContinuationPayload : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _bytes;
    internal LlmSensitiveContinuationPayload(byte[] bytes) => _bytes = bytes;

    public byte[] CopySensitiveBytes()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_bytes is null, this);
            return _bytes.ToArray();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_bytes is null) return;
            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }
    }

    public override string ToString() => "Sensitive native continuation payload (redacted)";
}

public sealed record LlmNativeContinuationCheck(
    bool IsCompatible, LlmNativeContinuationRejection Rejection, bool ConfigurationBound);

public sealed record LlmNativeContinuationExportResult(
    LlmNativeContinuationRejection Rejection,
    [property: JsonIgnore] LlmSensitiveContinuationPayload? SensitivePayload = null)
{
    public bool Succeeded => Rejection == LlmNativeContinuationRejection.None && SensitivePayload is not null;
    public override string ToString() => $"Native continuation export: {Rejection} (payload redacted)";
}

public sealed record LlmNativeContinuationImportResult(
    LlmNativeContinuationRejection Rejection,
    [property: JsonIgnore] LlmNativeContinuation? Continuation = null)
{
    public bool Succeeded => Rejection == LlmNativeContinuationRejection.None && Continuation is not null;
    public override string ToString() => $"Native continuation import: {Rejection} (payload redacted)";
}
