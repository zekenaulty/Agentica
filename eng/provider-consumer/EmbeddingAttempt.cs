using Agentica.Clients.Embeddings;
using Agentica.Memory;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentica.ProviderConsumer;

// This is a host example, not a new Agentica embedding API or durable job store.
internal sealed record SourceInput(string SourceId, string Revision, string Text);
internal sealed record SourceReference(string SourceId, string Revision);
internal sealed record LinkedVector(SourceReference Source, IReadOnlyList<float> Vector);
internal sealed record LinkedEmbeddingBatch(string ProfileId, IReadOnlyList<LinkedVector> Items, long? InputTokens);

internal enum DispatchState { Admitted, DispatchedUnknown, ResponseObserved }
internal enum AcceptanceState { Pending, Accepted, Rejected, Cancelled }

internal sealed record AttemptEvidence(string AttemptId, string OperationId, string ScopeId,
    string ProfileId, MemoryEmbeddingPurpose Purpose, IReadOnlyList<SourceReference> Sources,
    string RequestIdentity, string BodyHash, DispatchState Dispatch, AcceptanceState Acceptance,
    int? StatusCode = null, string? ProviderRequestId = null, long? InputTokens = null, string? ErrorCode = null);

internal sealed class InMemoryAttemptJournal
{
    private readonly Dictionary<string, AttemptEvidence> _records = new(StringComparer.Ordinal);
    public bool RejectReservation { get; init; }
    public IReadOnlyCollection<AttemptEvidence> Records => _records.Values;
    public AttemptEvidence this[string id] => _records[id];

    public void Reserve(AttemptEvidence evidence)
    {
        if (RejectReservation) throw new InvalidOperationException("reservation_rejected");
        if (!_records.TryAdd(evidence.AttemptId, evidence)) throw new InvalidOperationException("attempt_already_reserved");
    }

    public void Update(string id, Func<AttemptEvidence, AttemptEvidence> update) => _records[id] = update(_records[id]);
}

internal sealed class EmbeddingAttempt : IDisposable
{
    private const string FixtureKey = "synthetic-embedding-consumer-key";
    private readonly string _attemptId;
    private readonly string _operationId;
    private readonly string _scopeId;
    private readonly MemoryEmbeddingProfile _profile;
    private readonly MemoryEmbeddingPurpose _purpose;
    private readonly SourceInput[] _sources;
    private readonly InMemoryAttemptJournal _journal;
    private readonly HttpClient _http;
    private readonly OpenAiEmbeddingClient _client;
    private int _started;
    private bool _reserved;

    public EmbeddingAttempt(string attemptId, string operationId, string scopeId,
        MemoryEmbeddingProfile profile, MemoryEmbeddingPurpose purpose, IReadOnlyList<SourceInput> sources,
        InMemoryAttemptJournal journal, HttpMessageHandler physicalTransport)
    {
        _attemptId = Id(attemptId);
        _operationId = Id(operationId);
        _scopeId = Id(scopeId);
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _purpose = purpose;
        if (!Enum.IsDefined(purpose)) throw new ArgumentOutOfRangeException(nameof(purpose));
        _sources = CaptureSources(sources);
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        var handler = new AdmittedObservedTransport(this) { InnerHandler = physicalTransport };
        _http = new HttpClient(handler);
        // A real host supplies its existing credential. All calls here terminate in an offline fixture.
        _client = new OpenAiEmbeddingClient(new(profile, FixtureKey), _http);
    }

    public Task<LinkedEmbeddingBatch> ExecuteAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(_profile, _purpose, _sources.Select(source => source.Text).ToArray(), cancellationToken);

    internal async Task<LinkedEmbeddingBatch> ExecuteAsync(MemoryEmbeddingProfile profile,
        MemoryEmbeddingPurpose purpose, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(profile.ProfileId, _profile.ProfileId, StringComparison.Ordinal) || purpose != _purpose)
            throw new ArgumentException("captured_profile_or_purpose_mismatch");
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("attempt_already_consumed");
        try
        {
            var batch = await _client.EmbedAsync(profile, purpose, inputs, cancellationToken).ConfigureAwait(false);
            _journal.Update(_attemptId, evidence => evidence with { Acceptance = AcceptanceState.Accepted });
            var items = _sources.Select((source, index) => new LinkedVector(
                new SourceReference(source.SourceId, source.Revision), batch.Vectors[index])).ToArray();
            return new(batch.ProfileId, Array.AsReadOnly(items), batch.InputTokens);
        }
        catch (OperationCanceledException)
        {
            if (_reserved)
                _journal.Update(_attemptId, evidence => evidence with { Acceptance = AcceptanceState.Cancelled, ErrorCode = "cancelled" });
            throw;
        }
        catch (Exception error) when (error is EmbeddingClientException or ArgumentException or InvalidOperationException)
        {
            if (_reserved)
                _journal.Update(_attemptId, evidence => evidence with
                {
                    Acceptance = AcceptanceState.Rejected,
                    ErrorCode = error is EmbeddingClientException embedding ? embedding.ErrorCode : "host_rejected"
                });
            throw;
        }
    }

    private void Reserve(HttpRequestMessage request, byte[] bytes)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri != OpenAiEmbeddingClientOptions.DefaultEndpoint)
            throw new InvalidOperationException("unexpected_embedding_endpoint");
        using var body = JsonDocument.Parse(bytes);
        var root = body.RootElement;
        var inputs = root.GetProperty("input").EnumerateArray().Select(item => item.GetString()).ToArray();
        if (root.GetProperty("model").GetString() != _profile.ModelId ||
            root.GetProperty("dimensions").GetInt32() != _profile.Dimensions ||
            root.GetProperty("encoding_format").GetString() != "float" ||
            !inputs.SequenceEqual(_sources.Select(source => source.Text), StringComparer.Ordinal))
            throw new InvalidOperationException("prepared_input_or_profile_mismatch");
        var sourceRefs = Array.AsReadOnly(_sources.Select(source => new SourceReference(source.SourceId, source.Revision)).ToArray());
        var bodyHash = Hash(bytes);
        // Credential, source text and consumer/attempt identity are absent from the receipt.
        // Scope, operation, exact source revisions and purpose bind how those same bytes may be used.
        var identity = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, method = request.Method.Method, endpoint = request.RequestUri.AbsoluteUri,
            profile = _profile.ProfileId, purpose = _purpose.ToString(), scope = _scopeId,
            operation = _operationId, sources = sourceRefs, bodyHash
        }));
        _journal.Reserve(new(_attemptId, _operationId, _scopeId, _profile.ProfileId, _purpose, sourceRefs,
            identity, bodyHash, DispatchState.Admitted, AcceptanceState.Pending));
        _reserved = true;
    }

    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static SourceInput[] CaptureSources(IReadOnlyList<SourceInput> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var count = sources.Count;
        if (count is < 1 or > OpenAiEmbeddingClient.MaximumInputs)
            throw new ArgumentException("source_count", nameof(sources));
        var copy = new List<SourceInput>(count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (copy.Count >= count || source is null || !ids.Add(Id(source.SourceId)))
                throw new ArgumentException("source_reference_mapping", nameof(sources));
            copy.Add(new(Id(source.SourceId), Id(source.Revision), source.Text));
        }
        if (copy.Count != count) throw new ArgumentException("source_reference_mapping", nameof(sources));
        return copy.ToArray();
    }

    private static string Id(string value) => !string.IsNullOrEmpty(value) && value.Length <= 160 && value.All(character => character is >= ' ' and <= '~')
        ? value : throw new ArgumentException("invalid_host_identifier");
    public void Dispose() => _http.Dispose();

    private sealed class AdmittedObservedTransport(EmbeddingAttempt owner) : DelegatingHandler
    {
        private const int MaximumObservedBodyBytes = OpenAiEmbeddingClientOptions.MaximumSupportedResponseBytes;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length > OpenAiEmbeddingClient.MaximumRequestBytes) throw new InvalidOperationException("request_too_large");
            owner.Reserve(request, bytes);
            cancellationToken.ThrowIfCancellationRequested();
            owner._journal.Update(owner._attemptId, evidence => evidence with { Dispatch = DispatchState.DispatchedUnknown });
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                var ids = response.Headers.TryGetValues("x-request-id", out var values) ? values.Take(2).ToArray() : [];
                var requestId = ids.Length == 1 ? ids[0] : null;
                if (requestId is not null && (requestId.Length > 160 || requestId.Any(character => character is < ' ' or > '~')))
                    requestId = null;
                owner._journal.Update(owner._attemptId, evidence => evidence with
                {
                    Dispatch = DispatchState.ResponseObserved, StatusCode = (int)response.StatusCode, ProviderRequestId = requestId
                });
                var original = response.Content;
                var content = await ReadBoundedAsync(original, cancellationToken).ConfigureAwait(false);
                var usage = ReadObservedUsage(content);
                owner._journal.Update(owner._attemptId, evidence => evidence with { InputTokens = usage });
                // Observe safely before downstream vector validation, then preserve the same body for the client.
                var replacement = new ByteArrayContent(content);
                foreach (var header in original.Headers)
                    replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = replacement;
                original.Dispose();
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength > MaximumObservedBodyBytes) throw new IOException("observed_body_too_large");
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var remaining = MaximumObservedBodyBytes - (int)bytes.Length;
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken).ConfigureAwait(false);
                if (count == 0) return bytes.ToArray();
                if (count > remaining) throw new IOException("observed_body_too_large");
                bytes.Write(buffer, 0, count);
            }
        }

        private static long? ReadObservedUsage(byte[] content)
        {
            try
            {
                using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("usage", out var usage) &&
                    usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var prompt) &&
                    prompt.ValueKind == JsonValueKind.Number && prompt.TryGetInt64(out var count) && count >= 0)
                    return count;
            }
            catch (JsonException) { /* malformed response has no trustworthy measured usage */ }
            return null;
        }
    }
}
