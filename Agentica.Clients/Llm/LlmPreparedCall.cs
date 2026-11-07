using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentica.Clients.Llm;

public enum LlmProviderWorkKind { Inference, InputCount }

/// <summary>Nonsecret metadata about exact prepared work. Size is not a token estimate.</summary>
public sealed record LlmPreparedRequestDescriptor(
    LlmAttemptReferences References,
    string HostScopeId,
    LlmProviderWorkKind WorkKind,
    string ProviderName,
    string ApiMode,
    string ModelId,
    string ConfigurationIdentity,
    string RequestIdentity,
    string BodyHash,
    int WireBytes,
    int SerializedCharacters,
    string InputAccountingBasis)
{
    /// <summary>Nonsecret display route; query, fragment and credentials are not included.
    /// RequestIdentity still binds the complete actual endpoint.</summary>
    public string TransportEndpoint { get; init; } = "";
    public string? TransportConfigurationIdentity { get; init; }
}

/// <summary>Required host reservation before a send, independent of optional UI telemetry.
/// The host persists intent, enforces cumulative budgets and rechecks live authorization.
/// An exception denies dispatch. There is no implicit approval or retry.</summary>
public interface ILlmCallAdmission
{
    ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken);
}

/// <summary>
/// A detached, one-use inference request. Explicit body access is sensitive. Normal JSON
/// and ToString expose only bounded metadata. Durable journals and retries belong to hosts.
/// </summary>
public sealed class LlmPreparedCall : IDisposable
{
    private readonly ILlmStreamingClient _client;
    private readonly LlmRequest _request;
    private readonly byte[] _body;
    private readonly Uri _endpoint;
    private readonly string _protocol;
    private readonly LlmPreparedTransportBinding? _transportBinding;
    private int _consumed;
    private int _disposed;

    internal LlmPreparedCall(ILlmStreamingClient client, LlmRequest request,
        LlmPreparedRequestDescriptor descriptor, byte[] body, Uri endpoint, string protocol,
        LlmPreparedTransportBinding? transportBinding = null, LlmConfigurationReport? configurationReport = null)
    {
        ConfigurationReport = configurationReport;
        _client = client;
        _request = request;
        Descriptor = descriptor;
        _body = body;
        _endpoint = endpoint;
        _protocol = protocol;
        _transportBinding = transportBinding;
        Capture = new LlmAttemptCapture(descriptor.References);
    }

    public LlmPreparedRequestDescriptor Descriptor { get; }
    public LlmConfigurationReport? ConfigurationReport { get; }
    [JsonIgnore] public LlmAttemptCapture Capture { get; }
    internal Uri Endpoint => _endpoint;

    /// <summary>Explicit sensitive copy for host inspection/counting. Never log or receipt it.</summary>
    public byte[] CopySensitiveRequestBody()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return (byte[])_body.Clone();
    }

    public async Task<LlmResponse> GenerateAsync(ILlmCallAdmission admission,
        CancellationToken cancellationToken = default)
    {
        LlmResponse? completed = null;
        await foreach (var item in StreamAsync(admission, cancellationToken).ConfigureAwait(false))
            if (item.Kind == LlmStreamEventKind.Completed) completed = item.Response;
        return completed ?? throw new LlmClientException(Descriptor.ProviderName,
            "Prepared inference did not return a completed response.", errorClass: "stream_incomplete")
        { AttemptEvidence = Capture.Latest };
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(ILlmCallAdmission admission,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
            throw new InvalidOperationException("A prepared call can be consumed only once.");
        var dispatch = new LlmPreparedDispatch(Descriptor, _endpoint, _body, _protocol, admission, _transportBinding);
        var request = _request with { AttemptCapture = Capture, PreparedDispatch = dispatch };
        await foreach (var item in _client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _request.NativeContinuation?.Dispose();
        CryptographicOperations.ZeroMemory(_body);
    }

    public override string ToString() => $"Prepared {Descriptor.ProviderName}/{Descriptor.ApiMode} call (content redacted)";
}

internal sealed class LlmPreparedDispatch(
    LlmPreparedRequestDescriptor descriptor, Uri endpoint, byte[] body,
    string protocol, ILlmCallAdmission admission, LlmPreparedTransportBinding? transportBinding = null)
{
    private int _admitted;

    internal async ValueTask AdmitAsync(HttpRequestMessage message, LlmAttemptRecorder recorder,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _admitted, 1, 0) != 0)
            throw Rejected("prepared_attempt_consumed");
        var actual = message.Content is null ? [] :
            await message.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (message.Method != HttpMethod.Post || message.RequestUri != endpoint ||
            !actual.AsSpan().SequenceEqual(body) ||
            message.Content?.Headers.ContentType?.MediaType != "application/json" ||
            !string.Equals(Protocol(message), protocol, StringComparison.Ordinal) ||
            !ReferenceEquals(recorder.Evidence.References, descriptor.References))
            throw Rejected("prepared_request_mismatch");
        if (transportBinding?.IsCurrent() == false) throw Rejected("prepared_transport_changed");
        try
        {
            await admission.AdmitAsync(descriptor, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (ClientExceptionBoundary.IsRecoverable(exception))
        {
            // The original host exception is retained privately as the cause, not copied
            // into a provider error message or structural receipt.
            throw new LlmClientException(descriptor.ProviderName, "Required host admission failed.",
                exception, LlmClientErrorKind.BadRequest, errorClass: "host_admission_failed");
        }
        if (transportBinding?.IsCurrent() == false) throw Rejected("prepared_transport_changed");
    }

    internal static string Protocol(HttpRequestMessage message) =>
        string.Join(",", message.Headers.Accept.Select(value => value.ToString())) + "|" +
        (message.Headers.TryGetValues("anthropic-version", out var version) ? string.Join(",", version) : "");

    private LlmClientException Rejected(string code) => new(descriptor.ProviderName,
        "Prepared request admission rejected.", errorKind: LlmClientErrorKind.BadRequest, errorClass: code);
}

/// <summary>Borrowed defaults must not change after preparation, including during admission.
/// Handler internals and concurrent mutation of HttpClient remain host-owned transport policy.</summary>
internal sealed class LlmPreparedTransportBinding
{
    private readonly HttpClient? _client;
    internal LlmPreparedTransportBinding(HttpClient? client)
    {
        _client = client;
        Identity = CurrentIdentity();
    }

    internal string? Identity { get; }
    internal bool IsCurrent() => Identity == CurrentIdentity();
    private string? CurrentIdentity() => _client is null ? null :
        LlmPreparedIdentity.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            timeout = _client.Timeout.Ticks,
            headers = _client.DefaultRequestHeaders.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .Select(header => new { name = header.Key.ToLowerInvariant(), values = header.Value.ToArray() })
        }));
}

internal static class LlmPreparedIdentity
{
    internal const int MaximumBodyBytes = 16_777_216;

    internal static LlmPreparedRequestDescriptor Describe(LlmAttemptReferences references,
        string hostScopeId, LlmProviderWorkKind kind, LlmConfigurationReport configuration,
        Uri endpoint, byte[] body, string protocol, string? parentIdentity = null,
        string? transportIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(references);
        hostScopeId = LlmAttemptGuard.Required(hostScopeId, nameof(hostScopeId), 256);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Prepared work requires an absolute HTTP endpoint without userinfo or fragment.", nameof(endpoint));
        if (body.Length is < 1 or > MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(body));
        var bodyHash = Hash(body);
        var identity = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            contract = "agentica.prepared-request.v1",
            provider = configuration.ProviderName,
            mode = configuration.ApiMode,
            model = configuration.EffectiveModelId,
            configuration = configuration.ConfigurationIdentity,
            scope = hostScopeId,
            kind,
            endpoint = endpoint.AbsoluteUri,
            protocol,
            bodyHash,
            references.OperationId,
            references.PhaseId,
            references.RouteFingerprint,
            references.ExecutionBundleReference,
            parentIdentity,
            transportIdentity
        }));
        return new(references, hostScopeId, kind, configuration.ProviderName, configuration.ApiMode,
            configuration.EffectiveModelId, configuration.ConfigurationIdentity, identity, bodyHash,
            body.Length, Encoding.UTF8.GetCharCount(body),
            kind == LlmProviderWorkKind.InputCount ? "separate_native_count_operation" :
                configuration.ProviderName == "openai" ? "native_count_available_separately; unmeasured" :
                "unmeasured; serialized_size_is_not_tokens")
        {
            TransportEndpoint = endpoint.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.UriEscaped),
            TransportConfigurationIdentity = transportIdentity
        };
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
