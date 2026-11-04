using Agentica.Execution;
using Agentica.Projection;

namespace Agentica.Memory;

/// <summary>
/// Provider-independent bounded projection and retrieval. Vectors are search aids; only a fresh
/// host source read can supply quoted evidence. No retry, source admission or persistence is implicit.
/// </summary>
public sealed class SemanticMemoryEngine
{
    public async Task<MemoryIndexExport> CreateExportAsync(MemoryIndexRequest request,
        IReadOnlyList<MemorySource> sources, IMemoryEmbeddingProvider embeddingProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBound(request.Scope, request.Perspective, request.SourceKind, request.Profile, request.AsOfSequence);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        if (request.ChunkCharacters is < 2 or > MemoryGuard.MaximumChunkCharacters ||
            request.MaxSources is < 1 or > 1024 || request.MaxProjections is < 1 or > MemoryGuard.MaximumProjections ||
            sources.Count > 4096)
            throw new ArgumentException("Index request exceeds its bounded work limits.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        // Foreign scopes/perspectives/kinds never enter the selection ledger, embeddings or counts.
        var eligible = MemoryGuard.Snapshot(sources, 4096).Where(source => source is not null && InCut(source, request.Scope,
                request.Perspective, request.SourceKind, request.AsOfSequence, request.AsOfAt))
            .OrderBy(source => source.Source.Kind, StringComparer.Ordinal)
            .ThenBy(source => source.Source.RefId, StringComparer.Ordinal).ToArray();
        if (eligible.GroupBy(source => (source.Source.Kind, source.Source.RefId)).Any(group => group.Count() != 1))
            throw new ArgumentException("An index cut must contain one exact revision per canonical source.", nameof(sources));

        var reasons = new HashSet<string>(StringComparer.Ordinal);
        if (!request.SourcesComplete) reasons.Add("source_coverage_partial");
        var chunks = new List<Chunk>();
        var completeSources = 0;
        var omittedProjections = 0;
        var characters = 0;
        foreach (var source in eligible.Take(request.MaxSources))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = 0;
            while (start < source.Text.Length)
            {
                var length = Math.Min(request.ChunkCharacters, source.Text.Length - start);
                if (start + length < source.Text.Length && char.IsHighSurrogate(source.Text[start + length - 1])) length--;
                if (chunks.Count == request.MaxProjections || characters + length > MemoryGuard.MaximumInputCharacters)
                {
                    // A bounded lower bound: at least this projection was omitted; no unbounded tail scan.
                    omittedProjections++;
                    reasons.Add("projection_budget");
                    break;
                }
                var text = source.Text.Substring(start, length);
                chunks.Add(new(source, start, text));
                characters += length;
                start += length;
            }
            if (start == source.Text.Length) completeSources++;
        }
        if (eligible.Length > request.MaxSources) reasons.Add("source_budget");
        var omittedSources = eligible.Length - completeSources;
        var indexedSources = chunks.Select(chunk => chunk.Source.Source).Distinct().Count();
        var coverage = new MemoryCoverage(request.SourcesComplete && omittedSources == 0,
            eligible.Length, indexedSources, omittedSources, omittedProjections, reasons.ToArray());
        if (chunks.Count == 0)
            return new(request.Scope, request.Perspective, request.SourceKind, request.Profile,
                request.AsOfSequence, request.AsOfAt, [], coverage);

        var inputs = Array.AsReadOnly(chunks.Select(chunk => chunk.Text).ToArray());
        var batch = await EmbedAsync(embeddingProvider, request.Profile, MemoryEmbeddingPurpose.Document,
            inputs, cancellationToken).ConfigureAwait(false);
        if (batch is null)
            return new(request.Scope, request.Perspective, request.SourceKind, request.Profile,
                request.AsOfSequence, request.AsOfAt, [],
                new(false, eligible.Length, 0, eligible.Length, chunks.Count + omittedProjections,
                    reasons.Append("embedding_unavailable").ToArray()));

        var projections = chunks.Select((chunk, index) => new MemoryProjection(chunk.Source.Source,
            chunk.Source.Scope, chunk.Source.Perspective, chunk.Source.Kind, chunk.Source.AuthorId,
            chunk.Source.Revision, chunk.Source.Sequence, chunk.Source.CreatedAt, chunk.Source.ContentHash,
            chunk.Start, chunk.Text.Length, MemoryGuard.ContentHash(chunk.Text), request.Profile.ProfileId,
            batch.Vectors[index])).ToArray();
        return new(request.Scope, request.Perspective, request.SourceKind, request.Profile,
            request.AsOfSequence, request.AsOfAt, projections, coverage, batch.InputTokens);
    }

    public async Task<MemorySearchResult> SearchAsync(MemorySearchRequest request,
        IMemoryEmbeddingProvider embeddingProvider, IMemoryProjectionReader projectionReader,
        IMemorySourceReader sourceReader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateBound(request.Scope, request.Perspective, request.SourceKind, request.Profile, request.AsOfSequence);
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(projectionReader);
        ArgumentNullException.ThrowIfNull(sourceReader);
        MemoryGuard.Text(request.Query, 32_768);
        if (request.TopK is < 1 or > 128 || request.MaxCandidates is < 1 or > MemoryGuard.MaximumProjections ||
            request.MaxCitationCharacters is < 1 or > 131_072 ||
            !double.IsFinite(request.MinimumSimilarity) || request.MinimumSimilarity is < -1 or > 1)
            throw new ArgumentException("Search request exceeds its bounded work limits.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        MemoryProjectionPage page;
        try
        {
            page = await projectionReader.ReadAsync(new(request.Scope, request.Perspective, request.SourceKind,
                request.Profile.ProfileId, request.AsOfSequence, request.AsOfAt, request.MaxCandidates), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
        { return Unavailable(request, "projection_unavailable"); }
        if (page?.Items is null) return Unavailable(request, "projection_unavailable");
        MemoryLinkedProjection[] entries;
        try { entries = MemoryGuard.Snapshot(page.Items, request.MaxCandidates); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
        { return Unavailable(request, "projection_reader_invalid"); }

        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var complete = page.IsComplete;
        if (!complete) reasons.Add("projection_coverage_partial");
        var candidates = new Dictionary<string, MemoryLinkedProjection>(StringComparer.Ordinal);
        var omitted = 0;
        foreach (var linked in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projection = linked?.Projection;
            if (projection is null || !InCut(projection, request))
            {
                // Do not disclose foreign identities or their counts, even for a faulty reader.
                complete = false;
                reasons.Add("projection_reader_invalid");
                continue;
            }
            if (projection.ProfileId != request.Profile.ProfileId || projection.Vector.Count != request.Profile.Dimensions)
            {
                complete = false;
                omitted++;
                reasons.Add("profile_mismatch");
                continue;
            }
            try { MemoryGuard.Digest(linked!.ExportId); }
            catch (ArgumentException)
            {
                complete = false;
                omitted++;
                reasons.Add("export_link_invalid");
                continue;
            }
            // Identical projection references from several exports have one stable winning export link.
            if (!candidates.TryGetValue(projection.ProjectionId, out var prior) ||
                string.CompareOrdinal(linked!.ExportId, prior.ExportId) < 0)
                candidates[projection.ProjectionId] = linked!;
        }
        if (candidates.Count == 0)
            return new(MemorySearchState.Completed, request.Scope.ScopeId, request.Profile.ProfileId, [],
                new(complete, 0, 0, 0, omitted, reasons.ToArray()), false);

        var batch = await EmbedAsync(embeddingProvider, request.Profile, MemoryEmbeddingPurpose.Query,
            Array.AsReadOnly(new[] { request.Query }), cancellationToken).ConfigureAwait(false);
        if (batch is null) return Unavailable(request, "embedding_unavailable", candidates.Count);
        var ranked = candidates.Values.Select(linked => (Linked: linked,
                Similarity: Cosine(batch.Vectors[0], linked.Projection.Vector)))
            .Where(item => item.Similarity >= request.MinimumSimilarity)
            .OrderByDescending(item => item.Similarity)
            .ThenBy(item => item.Linked.Projection.ProjectionId, StringComparer.Ordinal).ToArray();
        var hits = new List<MemorySearchHit>();
        var retainedCharacters = 0;
        var truncated = false;
        var inspected = 0;
        var unavailableSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in ranked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hits.Count == request.TopK) { truncated = true; break; }
            var projection = item.Linked.Projection;
            MemorySourceRead? read;
            try
            {
                inspected++;
                read = await sourceReader.ReadAsync(new(projection.Source, request.Scope, request.Perspective,
                    request.SourceKind, request.AsOfSequence, request.AsOfAt), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception)) { read = null; }
            var source = read?.State == MemoryReadState.Available ? read.Source : null;
            if (source is null || !Matches(source, projection, request))
            {
                complete = false;
                omitted++;
                unavailableSources.Add(SourceKey(projection));
                reasons.Add(read?.State == MemoryReadState.Indeterminate ? "source_indeterminate" : "source_unavailable_or_stale");
                continue;
            }
            var text = source.Text.Substring(projection.StartUtf16, projection.LengthUtf16);
            // A chunk can be larger than a query's entire quote budget; return a valid exact prefix.
            var remaining = request.MaxCitationCharacters - retainedCharacters;
            if (remaining == 0) { truncated = true; break; }
            if (text.Length > remaining)
            {
                var length = remaining;
                if (char.IsHighSurrogate(text[length - 1])) length--;
                if (length == 0) { truncated = true; continue; }
                text = text[..length];
                truncated = true;
            }
            var segment = new PresentedEvidenceSegment(projection.ProjectionId, source.Source, source.Revision,
                projection.StartUtf16, text, source.AuthorId);
            var citation = new PresentedEvidenceCitation(segment.SegmentId, segment.StartUtf16, text.Length);
            hits.Add(new(item.Linked.ExportId, projection.ProjectionId, item.Similarity, source.Scope,
                source.Perspective, source.Kind, source.ContentHash, segment, citation));
            retainedCharacters += text.Length;
        }
        return new(MemorySearchState.Completed, request.Scope.ScopeId, request.Profile.ProfileId,
            Array.AsReadOnly(hits.ToArray()), new(complete, candidates.Values.Select(item => SourceKey(item.Projection)).Distinct().Count(),
                hits.Select(hit => hit.Segment.Source).Distinct().Count(), unavailableSources.Count, omitted, reasons.ToArray()),
            truncated, batch.InputTokens)
        { CandidateCount = candidates.Count, InspectedCount = inspected };
    }

    private static void ValidateBound(MemoryScope scope, MemoryPerspective perspective, MemorySourceKind kind,
        MemoryEmbeddingProfile profile, long asOfSequence)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(profile);
        MemoryGuard.Defined(perspective);
        MemoryGuard.Defined(kind);
        ArgumentOutOfRangeException.ThrowIfNegative(asOfSequence);
    }

    private static bool InCut(MemorySource source, MemoryScope scope, MemoryPerspective perspective,
        MemorySourceKind kind, long sequence, DateTimeOffset at) => source.Scope == scope &&
        source.Perspective == perspective && source.Kind == kind && source.Sequence <= sequence && source.CreatedAt <= at;

    private static bool InCut(MemoryProjection source, MemorySearchRequest request) => source.Scope == request.Scope &&
        source.Perspective == request.Perspective && source.Kind == request.SourceKind &&
        source.Sequence <= request.AsOfSequence && source.CreatedAt <= request.AsOfAt;

    private static bool Matches(MemorySource source, MemoryProjection projection, MemorySearchRequest request)
    {
        if (!InCut(source, request.Scope, request.Perspective, request.SourceKind, request.AsOfSequence, request.AsOfAt) ||
            source.Source != projection.Source || source.Revision != projection.SourceRevision ||
            source.ContentHash != projection.SourceContentHash || source.AuthorId != projection.AuthorId ||
            source.Sequence != projection.Sequence || source.CreatedAt != projection.CreatedAt ||
            (long)projection.StartUtf16 + projection.LengthUtf16 > source.Text.Length)
            return false;
        var start = projection.StartUtf16;
        var end = start + projection.LengthUtf16;
        if (start > 0 && char.IsLowSurrogate(source.Text[start]) ||
            end < source.Text.Length && char.IsHighSurrogate(source.Text[end - 1])) return false;
        return MemoryGuard.ContentHash(source.Text.Substring(start, projection.LengthUtf16)) == projection.SliceContentHash;
    }

    private static async Task<MemoryEmbeddingBatch?> EmbedAsync(IMemoryEmbeddingProvider provider,
        MemoryEmbeddingProfile profile, MemoryEmbeddingPurpose purpose, IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        MemoryEmbeddingBatch batch;
        try
        {
            batch = await provider.EmbedAsync(profile, purpose, inputs, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception)) { return null; }
        if (batch is null || batch.ProfileId != profile.ProfileId || batch.Vectors is null || batch.InputTokens < 0) return null;
        try
        {
            var outer = MemoryGuard.Snapshot(batch.Vectors, MemoryGuard.MaximumProjections);
            if (outer.Length != inputs.Count) return null;
            var vectors = Array.AsReadOnly(outer.Select(vector => MemoryGuard.Vector(vector, profile.Dimensions)).ToArray());
            return new(batch.ProfileId, vectors, batch.InputTokens);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception)) { return null; }
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0, leftSquared = 0, rightSquared = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += (double)left[index] * right[index];
            leftSquared += (double)left[index] * left[index];
            rightSquared += (double)right[index] * right[index];
        }
        return Math.Clamp(dot / (Math.Sqrt(leftSquared) * Math.Sqrt(rightSquared)), -1, 1);
    }

    private static string SourceKey(MemoryProjection projection) => MemoryGuard.Identity(projection.Source.Kind, projection.Source.RefId);
    private static MemorySearchResult Unavailable(MemorySearchRequest request, string reason, int candidates = 0) =>
        new(MemorySearchState.Unavailable, request.Scope.ScopeId, request.Profile.ProfileId, [],
            new(false, 0, 0, 0, 0, [reason]), false)
        { CandidateCount = candidates };
    private sealed record Chunk(MemorySource Source, int Start, string Text);
}
