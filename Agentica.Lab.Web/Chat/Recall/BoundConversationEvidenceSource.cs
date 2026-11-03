using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Agentica.Lab.Web.Chat.Recall;

internal sealed class BoundConversationEvidenceSource(ExperimentStore store, string scopeRef, int asOfSequence)
    : IConversationEvidenceSource
{
    private const int MaximumCharacters = 32000;
    private const int MaximumFallbackSources = 1000;
    public string ScopeRef { get; } = scopeRef;
    public int AsOfSequence { get; } = asOfSequence;
    public ConversationEvidenceHead GetHead() => store.EvidenceHead(ScopeRef, AsOfSequence);
    public ConversationEvidenceCoverage GetCoverage() => store.EvidenceCoverage(ScopeRef, AsOfSequence);
    public ConversationSourcePage ListMessageHeadersPage(int? beforeOrder = null, int limit = 40) =>
        store.SourceHeaders(ScopeRef, AsOfSequence, beforeOrder, limit);

    public ConversationEvidenceResult ReadExact(string sourceId, int startOffset = 0, int maxCharacters = 4000,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew(); cancellationToken.ThrowIfCancellationRequested();
        Bounds(maxCharacters);
        var canonical = store.CanonicalSource(ScopeRef, AsOfSequence, sourceId);
        if (canonical is null) return Result(watch, "UNAVAILABLE", "canonical-exact", [], 0, 0, false,
            ["The source is missing, ineligible, or outside this bound scope/cutoff."]);
        var hit = Excerpt(canonical.Value, startOffset, maxCharacters, "exact canonical source range");
        return Result(watch, "COMPLETE", "canonical-exact", [hit], 1, 1, hit.EndOffset < canonical.Value.Content.Length,
            hit.EndOffset < canonical.Value.Content.Length ? ["Source text continues after the returned extent."] : []);
    }

    public ConversationEvidenceResult ReadNeighbors(string sourceId, int radius = 1, int maxCharacters = 8000,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew(); cancellationToken.ThrowIfCancellationRequested(); Bounds(maxCharacters);
        if (radius is < 0 or > 2) throw new ExperimentException("recall.range", "Neighbor radius must be 0–2 eligible messages.");
        var canonical = store.CanonicalSource(ScopeRef, AsOfSequence, sourceId);
        if (canonical is null) return Result(watch, "UNAVAILABLE", "canonical-neighbors", [], 0, 0, false,
            ["The source is missing, ineligible, or outside this bound scope/cutoff."]);
        var headers = Nearest(canonical.Value.Header, radius);
        var hits = new List<ConversationEvidenceHit>();
        var remaining = maxCharacters;
        // Protect a bounded anchor before admitting optional neighbors.
        foreach (var header in headers.OrderBy(header => header.SourceId == sourceId ? 0 : 1).ThenBy(header => header.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = store.CanonicalSource(ScopeRef, AsOfSequence, header.SourceId);
            if (source is null || remaining == 0 || remaining < 2 && header.SourceId != sourceId) continue;
            var hit = Excerpt(source.Value, 0, Math.Min(remaining, Math.Max(2, maxCharacters / Math.Max(1, headers.Count))),
                header.SourceId == sourceId ? "neighbor anchor" : "chronologically adjacent eligible source");
            hits.Add(hit); remaining -= hit.Excerpt.Length;
        }
        var refs = hits.Select(hit => hit.Source.SourceId).ToArray();
        return Result(watch, "COMPLETE", "canonical-neighbors", hits.OrderBy(hit => hit.Source.Order)
            .Select(hit => hit with { NeighborRefs = refs.Where(id => id != hit.Source.SourceId).ToArray() }).ToArray(),
            headers.Count, headers.Count, hits.Count < headers.Count || hits.Any(hit => hit.EndOffset < hit.Source.CharacterCount),
            hits.Count < headers.Count ? ["Neighbor evidence was omitted under the total character ceiling."] : []);
    }

    public ConversationEvidenceResult Search(ConversationEvidenceSearch query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Query) || query.Query.Length > 512 || query.TopK is < 1 or > 20 ||
            query.Offset is < 0 or > 1000 || query.NeighborRadius is < 0 or > 2 || query.Role is not (null or "user" or "assistant"))
            throw new ExperimentException("recall.query", "Use a nonempty query ≤512 characters, 1–20 hits, radius 0–2 and an optional user/assistant role.");
        Bounds(query.MaxCharacters); cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var originalHead = GetHead();
        var reasons = new List<string>();
        try { store.CatchUpIndex(ScopeRef, AsOfSequence, 256, cancellationToken); }
        catch (SqliteException) { reasons.Add("Lexical projection unavailable; bounded exact source fallback was used."); }
        var coverage = GetCoverage();
        var candidates = store.LexicalCandidates(ScopeRef, AsOfSequence, query.Query, query.Role, 1000);
        var keywordFallbackUsed = false;
        if (candidates.Count == 0 && store.FtsAvailable && ExperimentStore.RecallKeywords(query.Query).Count > 0)
        {
            candidates = store.LexicalCandidates(ScopeRef, AsOfSequence, query.Query, query.Role, 1000, keywordFallback: true);
            keywordFallbackUsed = candidates.Count > 0;
            if (keywordFallbackUsed)
                reasons.Add("The exact phrase returned no lexical candidates; a bounded OR query of at most 12 non-stopword keywords was used. Keyword overlap does not establish semantic relevance or current acceptance.");
        }
        var projectionExactComplete = false;
        var projectionExactUsed = false;
        if (coverage.Status == "COMPLETE" && (candidates.Count == 0 || !query.Query.Any(char.IsLetterOrDigit)))
        {
            var exactProjection = store.IndexedSubstringCandidates(ScopeRef, AsOfSequence, query.Query, query.Role, cancellationToken);
            projectionExactUsed = true;
            candidates = exactProjection.Candidates; projectionExactComplete = exactProjection.Complete;
            if (!projectionExactComplete) reasons.Add("Exact indexed-text fallback was bounded to 8 MiB / 1000 segment candidates.");
        }
        var candidateLimited = candidates.Count == 1000;
        if (candidateLimited) reasons.Add("FTS candidate inspection was bounded to 1000 segments; additional candidates may remain.");
        var found = new Dictionary<string, ConversationEvidenceHit>(StringComparer.Ordinal);
        var inspected = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.ContainsKey(candidate.SourceId)) continue;
            var canonical = store.CanonicalSource(ScopeRef, AsOfSequence, candidate.SourceId); inspected++;
            if (canonical is null || canonical.Value.Header.ContentHash != candidate.Hash)
            {
                store.ProjectSource(ScopeRef, AsOfSequence, candidate.SourceId);
                reasons.Add("A stale index candidate was discarded and reconciled against canonical source.");
                continue;
            }
            var exact = canonical.Value.Content.IndexOf(query.Query, StringComparison.OrdinalIgnoreCase);
            var start = exact >= 0 ? Math.Max(0, exact - 120) : Math.Min(candidate.Start, canonical.Value.Content.Length);
            found[candidate.SourceId] = Excerpt(canonical.Value, SafeStart(canonical.Value.Content, start),
                Math.Min(2400, query.MaxCharacters), projectionExactUsed ? "indexed exact substring candidate; canonical source verified" :
                    keywordFallbackUsed ? "FTS5 keyword OR candidate; canonical exact excerpt; relevance not established" :
                    exact >= 0 ? "FTS5 phrase candidate; exact substring verified" : "FTS5 token phrase candidate; canonical exact excerpt");
        }
        var fallbackComplete = false;
        if (!projectionExactComplete && (found.Count == 0 || coverage.Status != "COMPLETE" || !store.FtsAvailable || !query.Query.Any(char.IsLetterOrDigit)))
        {
            int? cursor = null; var scanned = 0; var all = false;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = ListMessageHeadersPage(cursor, Math.Min(100, MaximumFallbackSources - scanned));
                foreach (var header in page.Items)
                {
                    scanned++;
                    if (query.Role is not null && header.Role != query.Role || found.ContainsKey(header.SourceId)) continue;
                    var canonical = store.CanonicalSource(ScopeRef, AsOfSequence, header.SourceId); inspected++;
                    if (canonical is null) continue;
                    var position = canonical.Value.Content.IndexOf(query.Query, StringComparison.OrdinalIgnoreCase);
                    if (position >= 0) found[header.SourceId] = Excerpt(canonical.Value,
                        SafeStart(canonical.Value.Content, Math.Max(0, position - 120)), Math.Min(2400, query.MaxCharacters), "exact substring fallback; canonical source verified");
                }
                cursor = page.NextBeforeOrder; all = cursor is null;
            } while (!all && scanned < MaximumFallbackSources);
            fallbackComplete = all;
            if (!all) reasons.Add("Exact fallback inspected at most 1000 eligible sources; older sources remain unsearched by that fallback.");
        }
        if (fallbackComplete && coverage.Status != "COMPLETE")
            coverage = coverage with { Status = "COMPLETE", Reason = "The bounded exact fallback inspected every eligible canonical source; lexical projection may still be partial." };
        else if ((!store.FtsAvailable || !query.Query.Any(char.IsLetterOrDigit)) && !fallbackComplete && !projectionExactComplete)
            coverage = coverage with { Status = "PARTIAL", Reason = "Punctuation-only lookup requires exact fallback, whose source scan was bounded." };
        if (found.Count == 0 && !fallbackComplete && !projectionExactComplete)
            coverage = coverage with { Status = "PARTIAL", Reason = "Exact negative coverage is incomplete under the configured local scan bounds." };
        if (originalHead.Revision != GetHead().Revision)
            coverage = coverage with { Status = "PARTIAL", Reason = "Canonical source changed while this lookup was running." };
        coverage = coverage with { FtsAvailable = store.FtsAvailable };
        var ranked = found.Values.ToArray();
        var chosen = new List<ConversationEvidenceHit>(); var remaining = query.MaxCharacters;
        foreach (var hit in ranked.Skip(query.Offset).Take(query.TopK))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0) break;
            var canonical = store.CanonicalSource(ScopeRef, AsOfSequence, hit.Source.SourceId);
            if (canonical is null || canonical.Value.Header.ContentHash != hit.Source.ContentHash) continue;
            var exact = Excerpt(canonical.Value, hit.StartOffset, Math.Min(remaining, hit.Excerpt.Length), hit.MatchReason);
            var neighborRefs = query.NeighborRadius == 0 ? Array.Empty<string>() : Nearest(hit.Source, query.NeighborRadius)
                .Where(header => header.SourceId != hit.Source.SourceId).Select(header => header.SourceId).ToArray();
            chosen.Add(exact with { NeighborRefs = neighborRefs }); remaining -= exact.Excerpt.Length;
        }
        var next = query.Offset + chosen.Count < ranked.Length ? query.Offset + chosen.Count : (int?)null;
        var status = candidateLimited ? "PARTIAL" : coverage.Status == "COMPLETE" ? ranked.Length == 0 ? "NO_MATCH" : "COMPLETE" : coverage.Status;
        return Result(watch, status, projectionExactUsed ? "indexed-exact-substring+canonical-verification" : keywordFallbackUsed ? "fts5-keyword-or+canonical-verification" : candidates.Count > 0 ? "fts5-phrase+canonical-verification" : "exact-substring-fallback",
            chosen, ranked.Length, inspected, candidateLimited || next is not null || chosen.Count < Math.Min(query.TopK, Math.Max(0, ranked.Length - query.Offset)),
            reasons.Distinct(StringComparer.Ordinal).ToArray(), coverage, next);
    }

    private IReadOnlyList<ConversationSourceHeader> Nearest(ConversationSourceHeader anchor, int radius)
    {
        if (radius == 0) return [anchor];
        var before = ListMessageHeadersPage(anchor.Order, radius == 0 ? 1 : radius).Items;
        var after = store.SourceHeadersAfter(ScopeRef, AsOfSequence, anchor.Order, radius);
        return before.Concat([anchor]).Concat(after).OrderBy(header => header.Order).ToArray();
    }

    private static ConversationEvidenceHit Excerpt((ConversationSourceHeader Header, string Content) source,
        int start, int maximumCharacters, string reason)
    {
        if (start < 0 || start > source.Content.Length || start > 0 && start < source.Content.Length && char.IsLowSurrogate(source.Content[start]))
            throw new ExperimentException("recall.range", "Source offsets must be valid UTF-16 character boundaries.");
        var end = Math.Min(source.Content.Length, start + maximumCharacters);
        if (end < source.Content.Length && end > start && char.IsHighSurrogate(source.Content[end - 1])) end--;
        if (end == start && start < source.Content.Length)
            throw new ExperimentException("recall.range", "The requested extent must fit a complete Unicode character.");
        return new(source.Header, start, end, source.Content[start..end], reason, []);
    }

    private static int SafeStart(string text, int position) => position > 0 && position < text.Length && char.IsLowSurrogate(text[position]) ? position - 1 : position;
    private static void Bounds(int maximumCharacters)
    {
        if (maximumCharacters is < 1 or > MaximumCharacters) throw new ExperimentException("recall.range", "Exact evidence is bounded to 1–32000 characters.");
    }

    private ConversationEvidenceResult Result(Stopwatch watch, string status, string method,
        IReadOnlyList<ConversationEvidenceHit> hits, int candidates, int inspected, bool truncated, IReadOnlyList<string> reasons,
        ConversationEvidenceCoverage? coverage = null, int? nextOffset = null) =>
        new(Guid.NewGuid().ToString("N"), ScopeRef, AsOfSequence, status, method, coverage ?? GetCoverage(), candidates, inspected,
            hits, nextOffset, truncated, reasons, watch.ElapsedMilliseconds);
}
