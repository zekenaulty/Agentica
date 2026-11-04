using System.Diagnostics;
using System.Data.Common;
using Agentica.Memory;
using Agentica.Observations;
using Agentica.Clients.Embeddings;

namespace Agentica.Lab.Web.Chat.Recall;

/// <summary>Lab composition of the core engine with canonical dialogue and a separate projection store.</summary>
public sealed class ExperimentSemanticRecall(
    ExperimentStore canonical, Func<SemanticMemoryStorageProvider> storageFactory, Func<IMemoryEmbeddingProvider> providerFactory)
{
    public ExperimentSemanticRecall(ExperimentStore canonical, SemanticMemoryStorageProvider storage,
        Func<IMemoryEmbeddingProvider> providerFactory) : this(canonical, () => storage, providerFactory) { }

    public static MemoryEmbeddingProfile Profile { get; } = new("openai", "text-embedding-3-small",
        "lab-default-v1", 1536, "exact-dialogue-u16-2000-v1", "exact-query-v1");

    public IConversationEvidenceSource Bind(string conversationId, int asOfSequence,
        Func<ExperimentTraceInput, Task> trace, bool allowEmbedding = true)
    {
        var scope = new MemoryScope("agentica-lab", "local-owner", "lab-assistant", conversationId);
        return new Bound(canonical, storageFactory, providerFactory, scope, asOfSequence, DateTimeOffset.UtcNow, trace, allowEmbedding);
    }

    private sealed class Bound(ExperimentStore canonical, Func<SemanticMemoryStorageProvider> storageFactory,
        Func<IMemoryEmbeddingProvider> providerFactory, MemoryScope scope, int asOf, DateTimeOffset asOfAt,
        Func<ExperimentTraceInput, Task> trace, bool allowEmbedding) : IConversationEvidenceSource, IDisposable
    {
        private readonly IConversationEvidenceSource _local = canonical.BindEvidenceSource(scope.ConversationId, asOf);
        private readonly SemanticMemoryEngine _engine = new();
        private readonly SemaphoreSlim _searchGate = new(1, 1);
        private BudgetProvider? _provider;
        private bool _indexAttempted;
        private bool _indexComplete;
        private bool _unavailable;
        private int _indexedSources;
        private int _lastIndexed;
        private SemanticMemoryStorageProvider Storage => storageFactory();
        public string ScopeRef => scope.ConversationId;
        public void Dispose() => _searchGate.Dispose();
        public int AsOfSequence => asOf;
        public ConversationEvidenceHead GetHead() => _local.GetHead();
        public ConversationSourcePage ListMessageHeadersPage(int? beforeOrder = null, int limit = 40) => _local.ListMessageHeadersPage(beforeOrder, limit);
        public ConversationEvidenceResult ReadExact(string sourceId, int startOffset = 0, int maxCharacters = 4000,
            CancellationToken cancellationToken = default) => _local.ReadExact(sourceId, startOffset, maxCharacters, cancellationToken);
        public ConversationEvidenceResult ReadNeighbors(string sourceId, int radius = 1, int maxCharacters = 8000,
            CancellationToken cancellationToken = default) => _local.ReadNeighbors(sourceId, radius, maxCharacters, cancellationToken);
        public ConversationEvidenceCoverage GetCoverage() => new(_unavailable ? "UNAVAILABLE" : _indexComplete ? "COMPLETE" : "PARTIAL", asOf, _lastIndexed,
            _local.GetHead().EligibleSourceCount, _indexedSources, false, Profile.ProfileId,
            allowEmbedding ? "Semantic coverage is bounded to the inspected canonical source cut." : "Semantic lookup deferred until an admitted turn; no embedding request made.");

        // Synchronous diagnostic callers never cross a paid provider boundary.
        public ConversationEvidenceResult Search(ConversationEvidenceSearch query, CancellationToken cancellationToken = default)
            => Result(Stopwatch.StartNew(), "NOT_READY", [], 0, true, ["Use an admitted asynchronous turn for semantic retrieval; no lexical fallback was performed."]);

        public async Task<ConversationEvidenceResult> SearchAsync(ConversationEvidenceSearch query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query.Query) || query.Query.Length > 512 || query.TopK is < 1 or > 20 ||
                query.MaxCharacters is < 1 or > 32000 || query.Offset != 0 ||
                query.Role is not (null or "user" or "assistant") || query.NeighborRadius is < 0 or > 2)
                throw new ExperimentException("recall.query", "Supply a bounded semantic query and optional user/assistant perspective. This semantic capability returns one bounded result window; offset must be zero.");
            cancellationToken.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            if (!allowEmbedding) return Search(query, cancellationToken);
            await _searchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_unavailable) throw new IOException("Semantic retrieval remains unavailable for this bound turn.");
                _provider ??= new BudgetProvider(providerFactory(), trace);
                await IndexAsync(cancellationToken).ConfigureAwait(false);
                var binding = Storage.Bind(scope, asOf, asOfAt);
                var found = new List<MemorySearchHit>();
                var complete = _indexComplete;
                var candidates = 0;
                var inspected = 0;
                foreach (var perspective in Enum.GetValues<MemoryPerspective>())
                {
                    if (query.Role is not null && (query.Role == "user") != (perspective == MemoryPerspective.Participant)) continue;
                    var result = await _engine.SearchAsync(new(scope, perspective, MemorySourceKind.Conversation, Profile,
                        asOf, asOfAt, query.Query, TopK: 20, MaxCandidates: 512, MaxCitationCharacters: 32000),
                        _provider, binding.ProjectionReader, binding.SourceReader, cancellationToken).ConfigureAwait(false);
                    if (result.State != MemorySearchState.Completed) return Result(watch, "UNAVAILABLE", [], candidates, true, ["Canonical source or semantic projection unavailable; no lexical fallback was performed."]);
                    complete &= result.Coverage.IsComplete && !result.Truncated;
                    candidates += result.CandidateCount;
                    inspected += result.InspectedCount;
                    found.AddRange(result.Hits);
                }
                var ranked = found.OrderByDescending(item => item.Similarity).ThenBy(item => item.ProjectionId, StringComparer.Ordinal).ToArray();
                var hits = new List<ConversationEvidenceHit>();
                var remaining = query.MaxCharacters;
                foreach (var hit in ranked.Skip(query.Offset).Take(query.TopK))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Recheck again at presentation, after ranking and any concurrent canonical changes.
                    var current = canonical.CanonicalSource(ScopeRef, asOf, hit.Segment.Source.RefId);
                    if (current is null || "sha256:" + current.Value.Header.ContentHash != hit.SourceContentHash ||
                        current.Value.Header.CreatedAt > asOfAt ||
                        (current.Value.Header.Role == "user") != (hit.Perspective == MemoryPerspective.Participant) ||
                        hit.Segment.AttributionId != (hit.Perspective == MemoryPerspective.Participant ? scope.ParticipantId : scope.AgentId))
                    { complete = false; continue; }
                    var start = hit.Citation.StartUtf16;
                    var relative = start - hit.Segment.StartUtf16;
                    if (relative < 0 || (long)relative + hit.Citation.LengthUtf16 > hit.Segment.Text.Length ||
                        (long)start + hit.Citation.LengthUtf16 > current.Value.Content.Length ||
                        !current.Value.Content.AsSpan(start, hit.Citation.LengthUtf16).SequenceEqual(hit.Segment.Text.AsSpan(relative, hit.Citation.LengthUtf16)))
                    { complete = false; continue; }
                    var text = ExperimentRecallAllocator.ExactPrefix(hit.Segment.Text.Substring(relative,
                        hit.Citation.LengthUtf16), remaining);
                    if (text.Length == 0) { complete = false; break; }
                    hits.Add(new(current.Value.Header, start, start + text.Length, text, "semantic cosine; canonical exact source", [])
                    {
                        SemanticCitation = new(hit.ExportId, hit.ProjectionId, Profile.ProfileId, scope.ParticipantId,
                            scope.AgentId, hit.Segment.AttributionId, hit.Perspective.ToString(), hit.Similarity)
                    });
                    remaining -= text.Length;
                    if (text.Length < hit.Citation.LengthUtf16) complete = false;
                }
                var more = ranked.Length > query.TopK;
                return Result(watch, hits.Count == 0 ? complete ? "NO_MATCH" : "PARTIAL" : "FOUND", hits, candidates,
                    !complete || more, complete ? [] : ["Incomplete semantic coverage, candidate bounds or presentation omissions; no completeness claim."],
                    inspected: inspected);
            }
            catch (Exception error) when ((error is HttpRequestException or InvalidDataException or IOException or ArgumentException or EmbeddingClientException or DbException)
                && Recoverable(error))
            {
                _unavailable = true;
                await trace(new("recall.semantic.unavailable", new
                {
                    scopeRef = ScopeRef,
                    asOfSequence = asOf,
                    profileId = Profile.ProfileId,
                    automaticRetry = false,
                    lexicalFallback = false
                })).ConfigureAwait(false);
                return Result(watch, "UNAVAILABLE", [], 0, true, ["Semantic retrieval unavailable; provider outcome may be unknown. No automatic retry or lexical fallback occurred."]);
            }
            finally { _searchGate.Release(); }
        }

        private async Task IndexAsync(CancellationToken cancellationToken)
        {
            if (_indexAttempted) return;
            _indexAttempted = true; // No implicit repeat if dispatch or persistence has an uncertain result.
            var missing = new Dictionary<MemoryPerspective, List<MemorySource>>
            {
                [MemoryPerspective.Participant] = [],
                [MemoryPerspective.Agent] = []
            };
            var scanned = 0;
            var selected = 0;
            var characters = 0;
            int? before = null;
            var complete = true;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = _local.ListMessageHeadersPage(before, 100);
                foreach (var header in page.Items)
                {
                    if (scanned == 512) { complete = false; break; }
                    scanned++;
                    if (header.CharacterCount == 0) continue;
                    var perspective = header.Role == "user" ? MemoryPerspective.Participant : MemoryPerspective.Agent;
                    var reference = new EvidenceRef("conversation", header.SourceId);
                    var hash = "sha256:" + header.ContentHash;
                    if (Storage.HasCurrentSource(new(scope, perspective, MemorySourceKind.Conversation, Profile.ProfileId, asOf, asOfAt, 512),
                        reference, hash, hash, header.CharacterCount)) { _indexedSources++; _lastIndexed = Math.Max(_lastIndexed, header.TurnSequence); continue; }
                    if (selected >= 32 || header.CharacterCount > 64000 - characters) { complete = false; continue; }
                    var source = canonical.CanonicalSource(ScopeRef, asOf, header.SourceId);
                    if (source is null || source.Value.Header.ContentHash != header.ContentHash) { complete = false; continue; }
                    missing[perspective].Add(new(reference, scope, perspective, MemorySourceKind.Conversation,
                        perspective == MemoryPerspective.Participant ? scope.ParticipantId : scope.AgentId, hash,
                        header.TurnSequence, header.CreatedAt, source.Value.Content));
                    selected++; characters += source.Value.Content.Length;
                }
                before = page.NextBeforeOrder;
            } while (before is not null && scanned < 512);
            if (before is not null) complete = false;
            foreach (var (perspective, sources) in missing)
            {
                if (sources.Count == 0) continue;
                var export = await _engine.CreateExportAsync(new(scope, perspective, MemorySourceKind.Conversation, Profile,
                    asOf, asOfAt, MaxSources: 32, MaxProjections: 64, SourcesComplete: complete), sources, _provider!, cancellationToken).ConfigureAwait(false);
                if (export.Coverage.Reasons.Contains("embedding_unavailable", StringComparer.Ordinal))
                    throw new IOException("Semantic document encoding unavailable.");
                var acknowledgement = await new LinkedMemoryExporter<SemanticMemoryStorageExport>(new SemanticMemoryStorageMapper(), Storage)
                    .WriteAsync(export, cancellationToken).ConfigureAwait(false);
                _indexedSources += export.Coverage.IndexedSources;
                if (export.Projections.Count != 0) _lastIndexed = Math.Max(_lastIndexed, export.Projections.Max(item => (int)item.Sequence));
                complete &= export.Coverage.IsComplete;
                await trace(new("recall.semantic.export", new
                {
                    export.ExportId,
                    profileId = Profile.ProfileId,
                    scopeId = scope.ScopeId,
                    perspective,
                    sources = export.Coverage.IndexedSources,
                    projections = export.Projections.Count,
                    storageReference = acknowledgement.StorageReference,
                    canonicalTextCopied = false
                })).ConfigureAwait(false);
            }
            _indexComplete = complete;
        }

        private ConversationEvidenceResult Result(Stopwatch watch, string status, IReadOnlyList<ConversationEvidenceHit> hits,
            int candidates, bool truncated, IReadOnlyList<string> reasons, int? nextOffset = null, int inspected = 0) => new(Guid.NewGuid().ToString("N"), ScopeRef,
                asOf, status, "semantic-cosine; canonical-revalidated", GetCoverage(), candidates, inspected, hits, nextOffset, truncated, reasons, watch.ElapsedMilliseconds);
    }

    private static bool Recoverable(Exception error)
    {
        var pending = new Stack<Exception>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            if (seen.Count > 256 || current is OutOfMemoryException or StackOverflowException or AccessViolationException) return false;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (pending.Count >= 256) return false;
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner) pending.Push(inner);
        }
        return true;
    }

    private sealed class BudgetProvider(IMemoryEmbeddingProvider provider, Func<ExperimentTraceInput, Task> trace) : IMemoryEmbeddingProvider
    {
        private int _attempts;
        private bool _failed;
        private readonly Dictionary<string, MemoryEmbeddingBatch> _queries = new(StringComparer.Ordinal);
        public async Task<MemoryEmbeddingBatch> EmbedAsync(MemoryEmbeddingProfile profile, MemoryEmbeddingPurpose purpose,
            IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = purpose == MemoryEmbeddingPurpose.Query && inputs.Count == 1 ? inputs[0] : null;
            if (key is not null && _queries.TryGetValue(key, out var cached)) return cached;
            if (_failed || _attempts >= 4) throw new IOException("Embedding turn allowance unavailable.");
            var attempt = ++_attempts;
            await trace(new("recall.embedding.started", new
            {
                attempt,
                purpose,
                profileId = profile.ProfileId,
                inputs = inputs.Count,
                characters = inputs.Sum(item => item.Length),
                maximumCalls = 4
            })).ConfigureAwait(false);
            try
            {
                var batch = await provider.EmbedAsync(profile, purpose, inputs, cancellationToken).ConfigureAwait(false);
                if (key is not null) _queries.Add(key, batch);
                await trace(new("recall.embedding.completed", new
                {
                    attempt,
                    purpose,
                    profileId = profile.ProfileId,
                    inputTokens = batch.InputTokens,
                    measuredUsage = batch.InputTokens is not null
                })).ConfigureAwait(false);
                return batch;
            }
            catch
            {
                _failed = true;
                await trace(new("recall.embedding.indeterminate", new
                {
                    attempt,
                    purpose,
                    automaticRetry = false,
                    usageKnown = false
                })).ConfigureAwait(false);
                throw;
            }
        }
    }
}
