using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Agentica.Clients.Llm;

namespace Agentica.Lab.Web.Chat;

/// <summary>Turn-owned HTTP admission and inspection, including the planner's repair calls.</summary>
internal sealed class ExperimentProviderTransport : HttpMessageHandler
{
    private readonly HttpClient _inner;
    private readonly ExperimentBorrowedTransportBinding _binding;
    private readonly ExperimentExecutionContext _context;
    private readonly Dictionary<string, (string CallId, string Purpose)> _admitted = new(StringComparer.Ordinal);
    private readonly List<LlmAttemptEvidence> _attempts = [];
    private readonly List<JsonElement> _searchOutcomes = [];
    private readonly object _gate = new();
    private int _prepared;
    private int _reservedCalls;
    private int _calls;
    private string? _credential;
    private IExperimentInputTokenCounter? _counter;
    private bool _ownsCounter;
    public int Calls => Volatile.Read(ref _calls);
    public string? LatestCallId { get; private set; }
    public string? AdmissionFailure { get; private set; }
    public string? ProviderErrorCode { get; private set; }
    public string? ProviderErrorMessage { get; private set; }
    public bool TelemetryDeliveryFailed { get; private set; }
    public string? RedactText(string? text) => text is null ? null : Redact(text, _credential);

    public ExperimentProviderTransport(HttpClient inner, ExperimentExecutionContext context)
    {
        _inner = inner;
        _binding = new(inner);
        _context = context;
    }

    public IReadOnlyList<JsonElement> Attempts { get { lock (_gate) return _attempts.Select(item => Sanitize(JsonSerializer.SerializeToElement(item), _credential)).ToArray(); } }

    public IReadOnlyList<JsonElement> SearchOutcomes { get { lock (_gate) return _searchOutcomes.ToArray(); } }

    internal LlmAttemptReferences NextReferences(string purpose)
    {
        AdmissionFailure = null; ProviderErrorCode = null; ProviderErrorMessage = null;
        return new(_context.Turn.Id + "-attempt-" + Interlocked.Increment(ref _prepared),
            "lab.chat", purpose, routeFingerprint: _binding.Identity, executionBundleReference: _context.Turn.Id + ":settings:" + _context.Turn.SettingsRevision);
    }

    internal ILlmCallAdmission Admission(LlmPreparedCall prepared, string purpose) => new TurnAdmission(this, prepared, purpose);

    internal async Task RecordOutcomeAsync(LlmAttemptEvidence? evidence, LlmWebSearchEvidence? search)
    {
        if (evidence is null) return;
        JsonElement? searchSnapshot = null;
        lock (_gate)
        {
            _attempts.Add(evidence);
            if (search is not null)
            {
                searchSnapshot = RetainSearchOutcome(evidence.References.AttemptId, search);
                _searchOutcomes.Add(searchSnapshot.Value);
            }
            if (evidence.DispatchCertainty == LlmDispatchCertainty.NotDispatched)
            {
                var unused = _admitted.FirstOrDefault(item => item.Value.CallId == evidence.References.AttemptId);
                if (unused.Key is not null && _admitted.Remove(unused.Key)) _reservedCalls--;
            }
        }
        if (search is not null)
        {
            try
            {
                await _context.Trace(new("provider.web-search.outcome", new
                {
                    snapshot = searchSnapshot,
                    hostAcceptance = "separate; partial search evidence does not establish a completed turn"
                }, evidence.References.AttemptId)).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
            { TelemetryDeliveryFailed = true; }
        }
        // This bounded structural record contains no private native capsule or output.
        try
        {
            await _context.Trace(new("provider.attempt", new
            {
                evidence = Sanitize(JsonSerializer.SerializeToElement(evidence), _credential),
                recoveryState = evidence.DispatchCertainty == LlmDispatchCertainty.NotDispatched ? "not-dispatched"
                : evidence.ProviderStatus is "completed" or "failed" or "incomplete" ? "provider-terminal-observed"
                : "dispatched-unresolved; do not automatically retry",
                hostAcceptance = "separate; inspect turn or harness outcome"
            }, evidence.References.AttemptId)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        { TelemetryDeliveryFailed = true; }
    }

    private JsonElement RetainSearchOutcome(string callId, LlmWebSearchEvidence search)
    {
        // Saved turn rows are returned with conversation pages. Share a 1 MiB
        // ceiling across the maximum admitted calls instead of duplicating MBs
        // of external references into each visible turn indefinitely.
        var maximum = 1_048_576 / _context.Turn.Settings.MaxProviderCalls;
        var calls = search.Calls.ToArray(); var sources = search.Sources.ToArray(); var citations = search.Citations.ToArray();
        var truncated = false; var removedQueries = false;
        while (true)
        {
            var snapshot = Sanitize(JsonSerializer.SerializeToElement(new
            {
                callId,
                evidence = new LlmWebSearchEvidence(calls, sources, citations, search.ReferencesTruncated || truncated),
                hostRetentionTruncated = truncated,
                observed = new { calls = search.Calls.Count, completedCalls = search.CompletedCalls, sources = search.Sources.Count, citations = search.Citations.Count }
            }), _credential);
            if (snapshot.GetRawText().Length <= maximum) return snapshot;
            truncated = true;
            if (citations.Length != 0) citations = citations.Take(citations.Length / 2).ToArray();
            else if (sources.Length != 0) sources = sources.Take(sources.Length / 2).ToArray();
            else if (!removedQueries)
            {
                calls = calls.Select(item => new LlmWebSearchCall(item.CallId, item.OutputIndex, item.Status, item.ActionType)).ToArray();
                removedQueries = true;
            }
            else calls = calls.Take(calls.Length / 2).ToArray();
        }
    }

    private void ValidateBinding()
    {
        try { _binding.Validate(); }
        catch (ExperimentException error) { AdmissionFailure = error.Code; throw; }
    }

    private async ValueTask AdmitAsync(LlmPreparedCall prepared, string purpose,
        LlmPreparedRequestDescriptor descriptor, CancellationToken cancellationToken)
    {
        ValidateBinding();
        if (descriptor != prepared.Descriptor) throw new InvalidOperationException("Prepared admission identity changed.");
        var settings = _context.Turn.Settings;
        var callId = descriptor.References.AttemptId;
        LatestCallId = callId;
        bool reserved;
        lock (_gate)
        {
            reserved = _calls + _reservedCalls < settings.MaxProviderCalls;
            if (reserved) _reservedCalls++;
        }
        if (!reserved)
        {
            AdmissionFailure = "provider.call_limit";
            await _context.Trace(new("provider.admission.denied", new { code = AdmissionFailure, providerCalls = Calls, limit = settings.MaxProviderCalls }, callId)).ConfigureAwait(false);
            throw new ExperimentException(AdmissionFailure, "The configured provider-call limit was reached. No further request was dispatched.");
        }
        byte[] bytes = [];
        var armed = false;
        try
        {
            bytes = prepared.CopySensitiveRequestBody();
            using var document = JsonDocument.Parse(bytes);
            var body = document.RootElement;
            var allowance = ExperimentDirectRequest.InputAllowance(settings);
            if (descriptor.SerializedCharacters > settings.MaxInputCharacters)
            {
                await _context.Trace(new("provider.request", new
                {
                    body = Sanitize(body, _credential),
                    hash = descriptor.BodyHash["sha256:".Length..],
                    wireBytes = bytes.Length,
                    serializedCharacters = descriptor.SerializedCharacters,
                    inputAllowance = allowance,
                    inputTokens = (int?)null,
                    estimator = "not counted; request character limit failed first",
                    dispatchState = "prepared; character-denied",
                    instructionsOmitted = !body.TryGetProperty("instructions", out _)
                }, callId)).ConfigureAwait(false);
                AdmissionFailure = "context.wire_limit";
                throw new ExperimentException(AdmissionFailure, "The complete provider request exceeds the configured character limit. No generation request was dispatched.");
            }
            if (_counter is null)
            {
                throw new InvalidOperationException("The turn-owned input counter must be attached before shared admission.");
            }
            ExperimentTokenCount measurement;
            try { measurement = await _counter.CountAsync(bytes, cancellationToken).ConfigureAwait(false); }
            catch (ExperimentException error) { AdmissionFailure = error.Code; throw; }
            if (measurement.RequestHash != descriptor.BodyHash["sha256:".Length..])
            {
                AdmissionFailure = "context.token_count_invalid";
                throw new ExperimentException(AdmissionFailure, "The input measurement does not identify the exact prepared request. No generation request was dispatched.");
            }
            await _context.Trace(new("provider.request", new
            {
                body = Sanitize(body, _credential),
                hash = descriptor.BodyHash["sha256:".Length..],
                continuity = purpose == "web-search-query" ? "query-only; no conversation history or custom instructions"
                    : settings.Mode == "clean-room" ? "transcript-only" : "harness-compiled-input; native state limited to this run",
                purpose,
                dispatchState = "prepared",
                mode = settings.Mode,
                method = "POST",
                url = descriptor.TransportEndpoint,
                estimatedInputUnits = measurement.Tokens,
                inputTokens = measurement.ProviderCount ? measurement.Tokens : (int?)null,
                tokenCount = measurement,
                wireBytes = bytes.Length,
                inputAllowance = allowance,
                estimator = measurement.Method,
                instructionsOmitted = !body.TryGetProperty("instructions", out _),
                prepared = descriptor
            }, callId)).ConfigureAwait(false);
            if (measurement.Tokens > allowance)
            {
                AdmissionFailure = "context.wire_limit";
                throw new ExperimentException(AdmissionFailure, $"The complete provider request has {measurement.Tokens} input tokens, exceeding the {allowance}-token allowance. No generation request was dispatched.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidateBinding();
            await _context.Trace(new("provider.dispatch", new
            {
                status = "admitted; awaiting single send",
                providerCalls = Calls + 1,
                borrowedTransportIdentity = _binding.Identity
            }, callId)).ConfigureAwait(false);
            ValidateBinding();
            lock (_gate)
                if (!_admitted.TryAdd(descriptor.BodyHash, (callId, purpose)))
                    throw new InvalidOperationException("An equal prepared request already owns an unused reservation.");
                else armed = true;
        }
        finally
        {
            if (!armed) lock (_gate) _reservedCalls--;
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal void AttachCounter(IExperimentInputTokenCounter counter, bool ownsCounter, string credential)
    { _counter = counter; _ownsCounter = ownsCounter; _credential = credential; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ValidateBinding();
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var hash = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        (string CallId, string Purpose) reservation;
        lock (_gate)
        {
            if (!_admitted.Remove(hash, out reservation))
                throw new InvalidOperationException("A chat provider request cannot bypass exact prepared admission.");
            _reservedCalls--;
            _calls++;
        }
        LatestCallId = reservation.CallId;
        if (request.Method != HttpMethod.Post || request.RequestUri != Agentica.Clients.OpenAI.OpenAiResponsesClientOptions.DefaultEndpoint)
            throw new InvalidOperationException("Unexpected chat provider route.");
        using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
        { Version = request.Version, VersionPolicy = request.VersionPolicy, Content = new ByteArrayContent(bytes) };
        foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
            foreach (var header in request.Content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        ValidateBinding();
        var response = await _inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            try { await ErrorAsync(response, reservation.CallId, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
            { TelemetryDeliveryFailed = true; }
        }
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            response.Content = new InspectedContent(response.Content, item => ObserveSafelyAsync(reservation.CallId, item), () => TelemetryDeliveryFailed = true);
        return response;
    }

    private sealed class TurnAdmission(ExperimentProviderTransport owner, LlmPreparedCall prepared, string purpose) : ILlmCallAdmission
    {
        public ValueTask AdmitAsync(LlmPreparedRequestDescriptor request, CancellationToken cancellationToken) =>
            owner.AdmitAsync(prepared, purpose, request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _ownsCounter) (_counter as IDisposable)?.Dispose();
        base.Dispose(disposing);
    }

    private async Task ErrorAsync(HttpResponseMessage response, string callId, CancellationToken cancellationToken)
    {
        const int maximumBytes = 16_384;
        var bytes = new byte[maximumBytes + 1];
        var used = 0;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        while (used < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(used), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            used += count;
        }
        string? providerCode = null, providerType = null, message = null;
        if (used <= maximumBytes)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes.AsMemory(0, used), new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    providerCode = String(error, "code"); providerType = String(error, "type"); message = String(error, "message");
                }
            }
            catch (JsonException) { /* An HTML/error gateway body is not safe display material. */ }
        }
        providerCode = SafeError(providerCode, 100);
        providerType = SafeError(providerType, 100);
        message = SafeError(message, 1200);
        ProviderErrorCode = providerCode is not null && Regex.IsMatch(providerCode, "^[a-zA-Z0-9_.-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            ? "provider." + providerCode : "provider.http_" + (int)response.StatusCode;
        ProviderErrorMessage = "OpenAI HTTP " + (int)response.StatusCode + (providerCode is null ? ": " : " [" + providerCode + "]: ") +
            (message ?? "The configured request was rejected. Check model access and reasoning settings.") + " No fallback or retry was made.";
        await _context.Trace(new("provider.error", new
        {
            httpStatus = (int)response.StatusCode,
            code = providerCode,
            type = providerType,
            message,
            bodyTruncated = used > maximumBytes,
            retried = false
        }, callId)).ConfigureAwait(false);
    }

    private string? SafeError(string? value, int maximumCharacters)
    {
        if (value is null) return null;
        value = Redact(value, _credential);
        value = Regex.Replace(value, "(?:sk-[A-Za-z0-9_-]{8,}|AIza[A-Za-z0-9_-]{20,}|Bearer\\s+[A-Za-z0-9._~+/-]+=*)",
            "[credential-redacted]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        value = new string(value.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray());
        return value.Length <= maximumCharacters ? value : value[..maximumCharacters] + " [truncated]";
    }

    private async Task ObserveSafelyAsync(string callId, JsonElement item)
    {
        try { await ObserveAsync(callId, item).ConfigureAwait(false); }
        catch (Exception error) when (error is not (OutOfMemoryException or AccessViolationException))
        {
            // Native inspection is optional telemetry. Shared provider parsing still
            // determines terminal evidence, and required admission remains fail-closed.
            TelemetryDeliveryFailed = true;
        }
    }

    private async Task ObserveAsync(string callId, JsonElement item)
    {
        var type = String(item, "type") ?? "unknown";
        var itemId = String(item, "item_id") ?? (item.TryGetProperty("item", out var native) ? String(native, "id") : null);
        // Keep the allowed source record separate from normalized display events. In particular,
        // text/summary normalization must not discard native indices or correlation identities.
        var record = type.StartsWith("response.reasoning_text.", StringComparison.Ordinal)
            ? PrivateReasoningMetadata(item, type)
            : Sanitize(item, _credential);
        await _context.Trace(new("provider.record", record, callId, itemId)).ConfigureAwait(false);
        if (type == "response.output_text.delta")
            await _context.Trace(new("provider.text.delta", new { text = Redact(String(item, "delta") ?? "", _credential) }, callId,
                itemId)).ConfigureAwait(false);
        else if (type == "response.reasoning_summary_text.delta")
            await _context.Trace(new("provider.summary.delta", new { text = Redact(String(item, "delta") ?? "", _credential) }, callId,
                itemId)).ConfigureAwait(false);
        else if (!type.StartsWith("response.reasoning_text.", StringComparison.Ordinal))
        {
            await _context.Trace(new("provider.item", Sanitize(item, _credential), callId, itemId)).ConfigureAwait(false);
        }
        else
            await _context.Trace(new("provider.item", new { type, visibility = "private-reasoning-not-retained" }, callId, itemId)).ConfigureAwait(false);
        if ((type is "response.output_item.added" or "response.output_item.done") &&
            item.TryGetProperty("item", out var searchItem) && String(searchItem, "type") == "web_search_call")
            await _context.Trace(new("provider.web-search", new { phase = type, item = Sanitize(searchItem, _credential) }, callId,
                String(searchItem, "id"))).ConfigureAwait(false);
    }

    private JsonElement PrivateReasoningMetadata(JsonElement item, string type)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["visibility"] = "private-reasoning-not-retained",
            ["contentRedacted"] = true
        };
        foreach (var name in new[] { "sequence_number", "item_id", "output_index", "content_index", "summary_index", "event_id", "id", "status" })
            if (item.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                metadata[name] = value.Clone();
        return Sanitize(JsonSerializer.SerializeToElement(metadata), _credential);
    }

    internal static string? String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() : null;

    internal static JsonElement Sanitize(JsonElement value, string? credential = null)
    {
        var node = JsonNode.Parse(value.GetRawText());
        Scrub(node, credential);
        return JsonSerializer.SerializeToElement(node);
    }

    private static string Redact(string value, string? credential) => string.IsNullOrEmpty(credential)
        ? value : value.Replace(credential, "[credential-redacted]", StringComparison.Ordinal);

    private static void Scrub(JsonNode? node, string? credential)
    {
        if (node is JsonObject map)
        {
            // Summary is an observable provider artifact. A reasoning item's private content
            // and its encrypted carrier are not display or export material.
            if (map["type"] is JsonValue type && type.TryGetValue<string>(out var typeName) && typeName == "reasoning")
                map.Remove("content");
            foreach (var key in map.Select(pair => pair.Key).ToArray())
            {
                if (key.Equals("encrypted_content", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("signature", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("api_key", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("access_token", StringComparison.OrdinalIgnoreCase)) map.Remove(key);
                else if (map[key] is JsonValue field && field.TryGetValue<string>(out var text)) map[key] = Redact(text, credential);
                else Scrub(map[key], credential);
            }
        }
        else if (node is JsonArray list)
            for (var i = 0; i < list.Count; i++)
                if (list[i] is JsonValue field && field.TryGetValue<string>(out var text)) list[i] = Redact(text, credential);
                else Scrub(list[i], credential);
    }

    private sealed class InspectedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Lazy<Task<Stream>> _stream;
        public InspectedContent(HttpContent inner, Func<JsonElement, Task> observer, Action omitted)
        {
            _inner = inner;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
            _stream = new(async () => new InspectionStream(await inner.ReadAsStreamAsync().ConfigureAwait(false), observer, omitted));
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => _stream.Value;
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => _stream.Value;
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await (await _stream.Value.ConfigureAwait(false)).CopyToAsync(stream).ConfigureAwait(false);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Incremental SSE observer. It does not buffer or delay the completed response.</summary>
    private sealed class InspectionStream(Stream inner, Func<JsonElement, Task> observer, Action omitted) : Stream
    {
        private const int MaximumEventCharacters = 1_048_576;
        private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _data = new();
        private bool _ended;
        private bool _skipEvent;
        private bool _inspectionDisabled;
        private int _lineCharacters;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (_inspectionDisabled) return count;
            var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
            int length;
            try { length = _decoder.GetChars(buffer.Span[..count], chars, count == 0); }
            catch (DecoderFallbackException)
            {
                _inspectionDisabled = true;
                _line.Clear(); _data.Clear();
                omitted();
                return count; // Shared decoding and parsing govern the original bytes.
            }
            for (var i = 0; i < length; i++)
            {
                var ch = chars[i];
                if (ch == '\n') await LineAsync().ConfigureAwait(false);
                else if (ch != '\r')
                {
                    _lineCharacters++;
                    if (!_skipEvent) _line.Append(ch);
                }
                if (_line.Length + _data.Length > MaximumEventCharacters)
                {
                    _skipEvent = true;
                    _line.Clear(); _data.Clear();
                    omitted(); // Display retention cannot invalidate a shared provider outcome.
                }
            }
            if (count == 0 && !_ended)
            {
                _ended = true;
                if (_line.Length != 0) await LineAsync().ConfigureAwait(false);
                await EventAsync().ConfigureAwait(false);
            }
            return count;
        }
        private async Task LineAsync()
        {
            var empty = _lineCharacters == 0;
            _lineCharacters = 0;
            if (_skipEvent)
            {
                if (empty) _skipEvent = false;
                _line.Clear();
                return;
            }
            var line = _line.ToString(); _line.Clear();
            if (empty) await EventAsync().ConfigureAwait(false);
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (_data.Length != 0) _data.Append('\n');
                _data.Append(line.AsSpan(5).TrimStart(' '));
            }
        }
        private async Task EventAsync()
        {
            if (_data.Length == 0) return;
            var data = _data.ToString(); _data.Clear();
            if (data == "[DONE]") return;
            try
            {
                using var document = JsonDocument.Parse(data);
                await observer(document.RootElement.Clone()).ConfigureAwait(false);
            }
            catch (JsonException) { omitted(); } // The shared parser still validates the original bytes.
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
