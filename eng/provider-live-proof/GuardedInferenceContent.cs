using System.Net;
using System.Text;
using System.Text.Json;

namespace Agentica.ProviderLiveProof;

/// <summary>Transparent structural usage observation; private output is never journaled.</summary>
internal sealed class GuardedInferenceContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly Lazy<Task<Stream>> _stream;
    public GuardedInferenceContent(HttpContent inner, ProofJournal journal, string id, string? credential)
    {
        _inner = inner;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        _stream = new(async () => new UsageStream(await inner.ReadAsStreamAsync().ConfigureAwait(false), journal, id, credential));
    }
    protected override Task<Stream> CreateContentReadStreamAsync() => _stream.Value;
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => _stream.Value;
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        await (await _stream.Value.ConfigureAwait(false)).CopyToAsync(stream).ConfigureAwait(false);
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_stream.IsValueCreated && _stream.Value.IsCompletedSuccessfully) _stream.Value.Result.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    private sealed class UsageStream(Stream inner, ProofJournal journal, string id, string? credential) : Stream
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _line = new();
        private readonly StringBuilder _data = new();
        private bool _skip;
        private int _lineCharacters;
        private bool _ambiguous;
        private bool _terminal;
        private bool _ended;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                var characters = new char[Encoding.UTF8.GetMaxCharCount(count)];
                var used = _decoder.GetChars(buffer.Span[..count], characters, count == 0);
                for (var i = 0; i < used; i++)
                {
                    var value = characters[i];
                    if (value == '\n') Line();
                    else if (value != '\r') { _lineCharacters++; if (!_skip) _line.Append(value); }
                    if (_line.Length + _data.Length > 4_194_304)
                    { _skip = true; _ambiguous = true; _line.Clear(); _data.Clear(); }
                }
                if (count == 0 && !_ended)
                {
                    if (_line.Length != 0) Line();
                    Event(); Finish(true);
                }
                return count;
            }
            catch
            {
                Finish(false);
                throw;
            }
        }
        private void Line()
        {
            var empty = _lineCharacters == 0; _lineCharacters = 0;
            if (_skip) { if (empty) _skip = false; _line.Clear(); return; }
            var line = _line.ToString(); _line.Clear();
            if (empty) Event();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            { if (_data.Length != 0) _data.Append('\n'); _data.Append(line.AsSpan(5).TrimStart(' ')); }
        }
        private void Event()
        {
            if (_data.Length == 0) return;
            var data = _data.ToString(); _data.Clear();
            if (data == "[DONE]") return;
            try
            {
                using var document = JsonDocument.Parse(data); var root = document.RootElement;
                var type = DurableProviderGuard.Text(root, "type");
                if (_terminal) _ambiguous = true;
                if (type is "error" or "response.failed" or "response.incomplete") _ambiguous = true;
                if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object) return;
                var status = DurableProviderGuard.Text(response, "status");
                if (type is "response.completed" or "response.failed" or "response.incomplete") _terminal = true;
                journal.Observe(id, attempt =>
                {
                    var responseId = DurableProviderGuard.Safe(DurableProviderGuard.Text(response, "id"), credential);
                    if (responseId is not null && attempt.ResponseId is not null && attempt.ResponseId != responseId) _ambiguous = true;
                    attempt.ResponseId ??= responseId;
                    if (status is "completed" or "failed" or "incomplete" or "in_progress" or "queued") attempt.ProviderStatus = status;
                    if (response.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        attempt.InputTokens = Maximum(attempt.InputTokens, DurableProviderGuard.Number(usage, "input_tokens"));
                        attempt.OutputTokens = Maximum(attempt.OutputTokens, DurableProviderGuard.Number(usage, "output_tokens"));
                        if (usage.TryGetProperty("output_tokens_details", out var details))
                            attempt.ReasoningTokens = Maximum(attempt.ReasoningTokens, DurableProviderGuard.Number(details, "reasoning_tokens"));
                    }
                    if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                        attempt.NativeSearchCalls = Maximum(attempt.NativeSearchCalls,
                            output.EnumerateArray().Count(item => DurableProviderGuard.Text(item, "type") == "web_search_call"));
                });
            }
            catch (JsonException) { _ambiguous = true; }
        }
        private int? Maximum(int? previous, int? observed)
        {
            if (observed is null) return previous;
            if (previous is not null && observed < previous) _ambiguous = true;
            return previous is null ? observed : Math.Max(previous.Value, observed.Value);
        }
        private void Finish(bool drained)
        {
            if (_ended) return;
            _ended = true;
            journal.FinishInference(id, drained, _ambiguous || !_terminal);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing)
        { if (disposing) { Finish(false); inner.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
