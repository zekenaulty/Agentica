using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agentica.Lab.Web.Chat;

/// <summary>Turn-owned HTTP admission and inspection, including the planner's repair calls.</summary>
internal sealed class ExperimentProviderTransport : HttpMessageHandler
{
    private readonly HttpClient _inner;
    private readonly ExperimentExecutionContext _context;
    private readonly Func<string, JsonElement, Task>? _nativeObserver;
    private int _calls;
    private string? _credential;
    public int Calls => Volatile.Read(ref _calls);
    public string? LatestCallId { get; private set; }
    public string? AdmissionFailure { get; private set; }
    public string? ProviderErrorCode { get; private set; }
    public string? ProviderErrorMessage { get; private set; }
    public string? RedactText(string? text) => text is null ? null : Redact(text, _credential);

    public ExperimentProviderTransport(HttpClient inner, ExperimentExecutionContext context,
        Func<string, JsonElement, Task>? nativeObserver = null)
    {
        _inner = inner;
        _context = context;
        _nativeObserver = nativeObserver;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var settings = _context.Turn.Settings;
        if (Calls >= settings.MaxProviderCalls)
        {
            AdmissionFailure = "provider.call_limit";
            await _context.Trace(new("provider.admission.denied", new { code = AdmissionFailure, providerCalls = Calls, limit = settings.MaxProviderCalls })).ConfigureAwait(false);
            throw new ExperimentException("provider.call_limit", "The configured provider-call limit was reached. No further request was dispatched.");
        }
        var call = Calls + 1;
        var callId = _context.Turn.Id + "-call-" + call;
        LatestCallId = callId;
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        _credential = request.Headers.Authorization?.Parameter;
        var body = JsonSerializer.Deserialize<JsonElement>(bytes);
        var allowance = (long)settings.ContextWindowTokens - settings.MaxOutputTokens - 4096;
        var estimate = bytes.LongLength + 512;
        await _context.Trace(new("provider.request", new
        {
            body = Sanitize(body, _credential), hash = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            continuity = settings.Mode == "clean-room" ? "transcript-only" : "harness-compiled-input; native state limited to this run",
            dispatchState = "prepared", mode = settings.Mode, method = request.Method.Method,
            url = request.RequestUri?.GetLeftPart(UriPartial.Path), estimatedInputUnits = estimate,
            inputAllowance = allowance, estimator = "utf8-wire-byte-proxy-plus-512",
            instructionsOmitted = !body.TryGetProperty("instructions", out _)
        }, callId)).ConfigureAwait(false);
        if (Encoding.UTF8.GetCharCount(bytes) > settings.MaxInputCharacters || estimate > allowance)
        {
            AdmissionFailure = "context.wire_limit";
            throw new ExperimentException("context.wire_limit", "The complete provider request, including harness instructions and schemas, exceeds the configured input allowance. No request was dispatched.");
        }

        // HttpClient marks its request as sent before entering this handler. Forward a detached
        // message through the shared client; do not mutate or resend that marked instance.
        using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version, VersionPolicy = request.VersionPolicy,
            Content = new ByteArrayContent(bytes)
        };
        foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
            foreach (var header in request.Content.Headers) forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        Interlocked.Increment(ref _calls);
        await _context.Trace(new("provider.dispatch", new { status = "dispatching", providerCalls = Calls }, callId)).ConfigureAwait(false);
        var response = await _inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            await ErrorAsync(response, callId, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            response.Content = new InspectedContent(response.Content, item => ObserveAsync(callId, item));
        return response;
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
            httpStatus = (int)response.StatusCode, code = providerCode, type = providerType,
            message, bodyTruncated = used > maximumBytes, retried = false
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
        if (_nativeObserver is not null) await _nativeObserver(callId, Sanitize(item, _credential)).ConfigureAwait(false);
    }

    private JsonElement PrivateReasoningMetadata(JsonElement item, string type)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["type"] = type, ["visibility"] = "private-reasoning-not-retained", ["contentRedacted"] = true
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
        public InspectedContent(HttpContent inner, Func<JsonElement, Task> observer)
        {
            _inner = inner;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
            _stream = new(async () => new InspectionStream(await inner.ReadAsStreamAsync().ConfigureAwait(false), observer));
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => _stream.Value;
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => _stream.Value;
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await (await _stream.Value.ConfigureAwait(false)).CopyToAsync(stream).ConfigureAwait(false);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>Incremental SSE observer. It does not buffer or delay the completed response.</summary>
    private sealed class InspectionStream(Stream inner, Func<JsonElement, Task> observer) : Stream
    {
        private const int MaximumEventCharacters = 1_048_576;
        private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _data = new();
        private bool _ended;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
            var length = _decoder.GetChars(buffer.Span[..count], chars, count == 0);
            for (var i = 0; i < length; i++)
            {
                var ch = chars[i];
                if (ch == '\n') await LineAsync().ConfigureAwait(false);
                else if (ch != '\r') _line.Append(ch);
                if (_line.Length + _data.Length > MaximumEventCharacters)
                    throw new ExperimentException("provider.event_limit", "A provider event exceeded the retained event limit.");
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
            var line = _line.ToString(); _line.Clear();
            if (line.Length == 0) await EventAsync().ConfigureAwait(false);
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
            using var document = JsonDocument.Parse(data);
            await observer(document.RootElement.Clone()).ConfigureAwait(false);
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
