using Agentica.Memory;
using Agentica.Observations;
using System.Text;
using System.Text.Json;

namespace Agentica.Tests;

public sealed class SemanticMemoryTests
{
    private static readonly MemoryScope Scope = new("lab", "person.1", "agent.1", "conversation.1");
    private static readonly DateTimeOffset At = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly MemoryEmbeddingProfile Profile = new("fake", "two-dim", "config.1", 2, "document.1", "query.1");
    private readonly SemanticMemoryEngine _engine = new();

    [Fact]
    public async Task Export_filters_owner_perspective_kind_and_cut_before_embedding_or_counting()
    {
        var provider = new Embeddings();
        var kept = Source("allowed", "orchard");
        var sources = new[]
        {
            kept,
            Source("other-person", "secret", scope: new("lab", "person.2", "agent.1", "conversation.1")),
            Source("other-agent", "secret", scope: new("lab", "person.1", "agent.2", "conversation.1")),
            Source("other-conversation", "secret", scope: new("lab", "person.1", "agent.1", "conversation.2")),
            Source("agent-speech", "secret", perspective: MemoryPerspective.Agent),
            Source("governed", "secret", kind: MemorySourceKind.GovernedMemory),
            Source("future-sequence", "secret", sequence: 11),
            Source("future-time", "secret", createdAt: At.AddSeconds(1))
        };
        var export = await _engine.CreateExportAsync(Index(), sources, provider);
        Assert.Equal(new[] { "orchard" }, Assert.Single(provider.Calls).Inputs);
        Assert.Equal(1, export.Coverage.EligibleSources);
        Assert.True(export.Coverage.IsComplete);
        Assert.Equal(kept.Source, Assert.Single(export.Projections).Source);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(export), StringComparison.Ordinal);
    }

    [Fact]
    public void Source_requires_actual_bound_identity_and_valid_exact_text()
    {
        Assert.Throws<ArgumentException>(() => new MemorySource(new("message", "1"), Scope,
            MemoryPerspective.Participant, MemorySourceKind.Conversation, "user", "r1", 1, At, "text"));
        Assert.Throws<ArgumentException>(() => new MemorySource(new("message", "1"), Scope,
            MemoryPerspective.Agent, MemorySourceKind.Conversation, Scope.ParticipantId, "r1", 1, At, "text"));
        Assert.Throws<ArgumentException>(() => Source("invalid", "bad\ud800"));
        Assert.NotEqual(Source("a", "é").ContentHash, Source("a", "e\u0301").ContentHash);
    }

    [Fact]
    public async Task Export_identity_is_order_independent_profile_bound_and_round_trips_without_source_text()
    {
        var first = Source("a", "orchard");
        var second = Source("b", "satellite");
        var export = await _engine.CreateExportAsync(Index(), [first, second], new Embeddings());
        var reverse = await _engine.CreateExportAsync(Index(), [second, first], new Embeddings());
        Assert.Equal(export.ExportId, reverse.ExportId);
        var json = JsonSerializer.Serialize(export);
        Assert.DoesNotContain("orchard", json, StringComparison.Ordinal);
        Assert.DoesNotContain("satellite", json, StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<MemoryIndexExport>(json)!;
        Assert.Equal(export.ExportId, restored.ExportId);
        Assert.Equal(export.Projections.Select(item => item.ProjectionId), restored.Projections.Select(item => item.ProjectionId));
        var otherProfile = new MemoryEmbeddingProfile("fake", "two-dim", "config.1", 2, "document.2", "query.1");
        var changed = await _engine.CreateExportAsync(Index() with { Profile = otherProfile }, [first, second], new Embeddings());
        Assert.NotEqual(export.ExportId, changed.ExportId);
    }

    [Fact]
    public async Task Mutable_provider_and_caller_collections_cannot_rewrite_a_finished_export()
    {
        var vector = new float[] { 1, 0 };
        var provider = new Embeddings((profile, _, inputs) => new(profile.ProfileId,
            inputs.Select(_ => (IReadOnlyList<float>)vector).ToArray()));
        var sources = new List<MemorySource> { Source("a", "orchard") };
        var export = await _engine.CreateExportAsync(Index(), sources, provider);
        var identity = export.ExportId;
        vector[0] = 0;
        sources.Clear();
        Assert.Equal(1, Assert.Single(export.Projections).Vector[0]);
        Assert.Equal(identity, export.ExportId);
        Assert.Throws<NotSupportedException>(() => ((IList<float>)export.Projections[0].Vector)[0] = 99);
        Assert.Throws<NotSupportedException>(() => ((IList<MemoryProjection>)export.Projections).Clear());
    }

    [Fact]
    public async Task Semantic_search_returns_exact_canonical_quote_and_linked_export_not_a_generated_memory()
    {
        var sources = new[] { Source("a", "orchard fruit"), Source("b", "satellite orbit") };
        var export = await _engine.CreateExportAsync(Index(), sources, new Embeddings());
        var projections = new Projections(export);
        var sourceReader = new Sources(sources);
        var result = await _engine.SearchAsync(Search("fruit") with { TopK = 1 }, new Embeddings(), projections, sourceReader);
        var hit = Assert.Single(result.Hits);
        Assert.Equal(MemorySearchState.Completed, result.State);
        Assert.Equal("orchard fruit", hit.Segment.Text);
        Assert.Equal(sources[0].ContentHash, hit.SourceContentHash);
        Assert.Equal(Scope.ParticipantId, hit.Segment.AttributionId);
        Assert.Equal(export.ExportId, hit.ExportId);
        Assert.Equal(hit.ProjectionId, hit.Citation.SegmentId);
        Assert.Equal(sources[0].Text.Substring(hit.Citation.StartUtf16, hit.Citation.LengthUtf16), hit.Segment.Text);
        Assert.Equal(Scope, Assert.Single(sourceReader.Requests).Scope);
        Assert.Equal(Profile.ProfileId, projections.LastQuery!.ProfileId);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(1, result.InspectedCount);
        Assert.True(result.Coverage.IsComplete);
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("participant")]
    [InlineData("agent")]
    [InlineData("conversation")]
    [InlineData("namespace")]
    [InlineData("perspective")]
    [InlineData("kind")]
    public async Task Faulty_projection_reader_cannot_expose_other_ownership_or_perspective(string change)
    {
        var foreignScope = change switch
        {
            "participant" => new MemoryScope("lab", "person.2", "agent.1", "conversation.1"),
            "agent" => new MemoryScope("lab", "person.1", "agent.2", "conversation.1"),
            "conversation" => new MemoryScope("lab", "person.1", "agent.1", "conversation.2"),
            "namespace" => new MemoryScope("other", "person.1", "agent.1", "conversation.1"),
            _ => Scope
        };
        var perspective = change == "perspective" ? MemoryPerspective.Agent : MemoryPerspective.Participant;
        var kind = change == "kind" ? MemorySourceKind.GovernedMemory : MemorySourceKind.Conversation;
        var export = await _engine.CreateExportAsync(Index() with { Scope = foreignScope, Perspective = perspective, SourceKind = kind },
            [Source("foreign", "private", scope: foreignScope, perspective: perspective, kind: kind)], new Embeddings());
        var provider = new Embeddings();
        var reader = new Sources([]);
        var result = await _engine.SearchAsync(Search("private"), provider, new Projections(export), reader);
        Assert.Empty(result.Hits);
        Assert.Empty(provider.Calls);
        Assert.Empty(reader.Requests);
        Assert.False(result.Coverage.IsComplete);
        Assert.Equal(0, result.Coverage.EligibleSources);
        Assert.Equal(0, result.Coverage.OmittedProjections);
        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, result.InspectedCount);
        Assert.DoesNotContain("foreign", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mixed_profile_is_rejected_before_ranking_and_current_source_read()
    {
        var other = new MemoryEmbeddingProfile("fake", "other-model", "config.1", 2, "document.1", "query.1");
        var export = await _engine.CreateExportAsync(Index() with { Profile = other }, [Source("a", "orchard")], new Embeddings());
        var provider = new Embeddings();
        var reader = new Sources([]);
        var result = await _engine.SearchAsync(Search("fruit"), provider, new Projections(export), reader);
        Assert.Empty(result.Hits);
        Assert.Empty(provider.Calls);
        Assert.Empty(reader.Requests);
        Assert.Contains("profile_mismatch", result.Coverage.Reasons);
        Assert.Equal(0, result.CandidateCount);
        Assert.Equal(0, result.InspectedCount);
        Assert.False(result.Coverage.IsComplete);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("content")]
    [InlineData("scope")]
    [InlineData("perspective")]
    [InlineData("kind")]
    [InlineData("future")]
    [InlineData("deleted")]
    [InlineData("indeterminate")]
    public async Task Canonical_source_is_revalidated_after_ranking_before_any_quote(string change)
    {
        var original = Source("a", "orchard");
        var export = await _engine.CreateExportAsync(Index(), [original], new Embeddings());
        var replacement = change switch
        {
            "revision" => Source("a", "orchard", revision: "r2"),
            "content" => Source("a", "rewritten"),
            "scope" => Source("a", "orchard", scope: new("lab", "person.2", "agent.1", "conversation.1")),
            "perspective" => Source("a", "orchard", perspective: MemoryPerspective.Agent),
            "kind" => Source("a", "orchard", kind: MemorySourceKind.GovernedMemory),
            "future" => Source("a", "orchard", sequence: 11),
            _ => original
        };
        var reader = new Sources([replacement])
        {
            State = change == "deleted" ? MemoryReadState.Unavailable : change == "indeterminate"
                ? MemoryReadState.Indeterminate : MemoryReadState.Available
        };
        var result = await _engine.SearchAsync(Search("fruit"), new Embeddings(), new Projections(export), reader);
        Assert.Empty(result.Hits);
        Assert.False(result.Coverage.IsComplete);
        Assert.Equal(1, result.Coverage.OmittedSources);
        Assert.Single(reader.Requests);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(1, result.InspectedCount);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("dimension")]
    [InlineData("count")]
    [InlineData("profile")]
    [InlineData("usage")]
    public async Task Invalid_provider_vectors_or_batch_metadata_produce_explicit_unavailable_coverage(string failure)
    {
        var provider = new Embeddings((profile, _, inputs) => new(failure == "profile" ? "other" : profile.ProfileId,
            failure == "count" ? [] : inputs.Select(_ => (IReadOnlyList<float>)(failure switch
            {
                "zero" => new float[] { 0, 0 },
                "nan" => [float.NaN, 1],
                "infinity" => [float.PositiveInfinity, 1],
                "dimension" => [1],
                _ => [1, 0]
            })).ToArray(), failure == "usage" ? -1 : null));
        var export = await _engine.CreateExportAsync(Index(), [Source("a", "orchard")], provider);
        Assert.Empty(export.Projections);
        Assert.False(export.Coverage.IsComplete);
        Assert.Contains("embedding_unavailable", export.Coverage.Reasons);
        Assert.Single(provider.Calls);
        var valid = await _engine.CreateExportAsync(Index(), [Source("a", "orchard")], new Embeddings());
        var result = await _engine.SearchAsync(Search("fruit"), provider, new Projections(valid), new Sources([]));
        Assert.Equal(MemorySearchState.Unavailable, result.State);
        Assert.False(result.Coverage.IsComplete);
        Assert.Empty(result.Hits);
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(1, result.CandidateCount);
        Assert.Equal(0, result.InspectedCount);
    }

    [Theory]
    [InlineData(2, 3, 3)]
    [InlineData(2, 1, 1)]
    [InlineData(2, int.MaxValue, 3)]
    [InlineData(8193, 1, 0)]
    [InlineData(int.MaxValue, 1, 0)]
    [InlineData(-1, 1, 0)]
    public async Task Provider_vector_snapshot_rejects_lying_or_oversized_lists_before_cosine(
        int declaredCount, int actualCount, int maximumYielded)
    {
        var lists = new List<CountedList<float>>();
        var provider = new Embeddings((profile, _, _) =>
        {
            var list = new CountedList<float>(declaredCount,
                Enumerable.Range(0, actualCount).Select(index => index % 2 == 0 ? 1f : 0f));
            lists.Add(list);
            return new(profile.ProfileId, [list]);
        });
        var source = Source("a", "orchard");
        var invalid = await _engine.CreateExportAsync(Index(), [source], provider);
        Assert.Empty(invalid.Projections);
        Assert.False(invalid.Coverage.IsComplete);
        var valid = await _engine.CreateExportAsync(Index(), [source], new Embeddings());
        var result = await _engine.SearchAsync(Search("fruit"), provider, new Projections(valid), new Sources([source]));
        Assert.Equal(MemorySearchState.Unavailable, result.State);
        Assert.Empty(result.Hits);
        Assert.Equal(0, result.InspectedCount);
        Assert.Equal(2, lists.Count);
        Assert.All(lists, list => Assert.Equal(maximumYielded, list.Yielded));
    }

    [Theory]
    [InlineData(1, 2, 2)]
    [InlineData(1, 0, 0)]
    [InlineData(1, int.MaxValue, 2)]
    [InlineData(513, 1, 0)]
    [InlineData(int.MaxValue, 1, 0)]
    [InlineData(-1, 1, 0)]
    public async Task Provider_batch_snapshot_rejects_lying_or_oversized_outer_lists_without_unbounded_enumeration(
        int declaredCount, int actualCount, int maximumYielded)
    {
        var outer = new CountedList<IReadOnlyList<float>>(declaredCount,
            Enumerable.Repeat<IReadOnlyList<float>>(new float[] { 1, 0 }, actualCount));
        var provider = new Embeddings((profile, _, _) => new(profile.ProfileId, outer));
        var export = await _engine.CreateExportAsync(Index(), [Source("a", "orchard")], provider);
        Assert.Empty(export.Projections);
        Assert.False(export.Coverage.IsComplete);
        Assert.Equal(maximumYielded, outer.Yielded);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task Public_export_reasons_sources_and_projection_reader_snapshots_enforce_actual_counts()
    {
        var source = Source("a", "orchard");
        var valid = await _engine.CreateExportAsync(Index(), [source], new Embeddings());
        var projection = Assert.Single(valid.Projections);
        var projections = new CountedList<MemoryProjection>(1, Enumerable.Repeat(projection, int.MaxValue));
        Assert.Throws<ArgumentException>(() => new MemoryIndexExport(Scope, MemoryPerspective.Participant,
            MemorySourceKind.Conversation, Profile, 10, At, projections, valid.Coverage));
        Assert.Equal(2, projections.Yielded);
        var reasons = new CountedList<string>(1, Enumerable.Repeat("partial", int.MaxValue));
        Assert.Throws<ArgumentException>(() => new MemoryCoverage(false, 1, 0, 1, 1, reasons));
        Assert.Equal(2, reasons.Yielded);
        var oversizedReasons = new CountedList<string>(int.MaxValue, []);
        Assert.Throws<ArgumentException>(() => new MemoryCoverage(false, 1, 0, 1, 1, oversizedReasons));
        Assert.Equal(0, oversizedReasons.Yielded);
        var sourceList = new CountedList<MemorySource>(1, Enumerable.Repeat(source, int.MaxValue));
        var provider = new Embeddings();
        await Assert.ThrowsAsync<ArgumentException>(() => _engine.CreateExportAsync(Index(), sourceList, provider));
        Assert.Equal(2, sourceList.Yielded);
        Assert.Empty(provider.Calls);
        var links = new CountedList<MemoryLinkedProjection>(1,
            Enumerable.Repeat(new MemoryLinkedProjection(valid.ExportId, projection), int.MaxValue));
        var result = await _engine.SearchAsync(Search("fruit"), provider, new Projections(links), new Sources([source]));
        Assert.Equal(MemorySearchState.Unavailable, result.State);
        Assert.Equal(2, links.Yielded);
        Assert.Equal(0, result.CandidateCount);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Valid_maximum_dimension_and_projection_batches_remain_supported()
    {
        var wideProfile = new MemoryEmbeddingProfile("fake", "wide", "config.1", 8192, "document.1", "query.1");
        var provider = new Embeddings((profile, _, inputs) => new(profile.ProfileId,
            inputs.Select(_ => (IReadOnlyList<float>)Enumerable.Repeat(1f, profile.Dimensions).ToArray()).ToArray()));
        var wide = await _engine.CreateExportAsync(Index() with { Profile = wideProfile }, [Source("wide", "text")], provider);
        Assert.Equal(8192, Assert.Single(wide.Projections).Vector.Count);
        var sources = Enumerable.Range(0, 512).Select(index => Source($"source-{index:D3}", "text")).ToArray();
        var full = await _engine.CreateExportAsync(Index() with { MaxSources = 512 }, sources, new Embeddings());
        Assert.Equal(512, full.Projections.Count);
        Assert.True(full.Coverage.IsComplete);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryEmbeddingProfile("fake", "wide", "config.1", 8193, "document.1", "query.1"));
    }

    [Fact]
    public async Task Wrapped_fatal_during_provider_vector_enumeration_escapes_unchanged()
    {
#pragma warning disable CA2201 // Synthetic integrity failure tests propagation without allocating memory.
        var fatal = new InvalidOperationException("wrapper", new OutOfMemoryException("synthetic"));
#pragma warning restore CA2201
        var values = new CountedList<float>(2, FailAfterOne(fatal));
        var provider = new Embeddings((profile, _, _) => new(profile.ProfileId, [values]));
        Assert.Same(fatal, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.CreateExportAsync(Index(), [Source("a", "orchard")], provider)));
        Assert.Equal(1, values.Yielded);
    }

    [Fact]
    public async Task Chunking_and_quote_budget_preserve_unicode_and_absolute_source_coordinates()
    {
        var source = Source("a", "a😀b😀c");
        var export = await _engine.CreateExportAsync(Index() with { ChunkCharacters = 2 }, [source], new Embeddings());
        var pieces = export.Projections.OrderBy(item => item.StartUtf16).ToArray();
        Assert.Equal(new[] { 0, 1, 3, 4, 6 }, pieces.Select(item => item.StartUtf16));
        Assert.Equal(source.Text, string.Concat(pieces.Select(item => source.Text.Substring(item.StartUtf16, item.LengthUtf16))));
        Assert.All(pieces, item => Assert.Equal(item.LengthUtf16,
            new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(
                source.Text.Substring(item.StartUtf16, item.LengthUtf16))).Length));
        var result = await _engine.SearchAsync(Search("fruit") with { MaxCitationCharacters = 2 },
            new Embeddings(), new Projections(export), new Sources([source]));
        Assert.True(result.Truncated);
        Assert.True(result.Hits.Sum(hit => hit.Segment.Text.Length) <= 2);
        Assert.All(result.Hits, hit => Assert.Equal(hit.Segment.Text,
            source.Text.Substring(hit.Citation.StartUtf16, hit.Citation.LengthUtf16)));
    }

    [Fact]
    public async Task Reconstructed_projection_with_split_surrogate_span_never_returns_corrupt_quote()
    {
        var source = Source("a", "a😀b");
        var export = await _engine.CreateExportAsync(Index(), [source], new Embeddings());
        var projection = Assert.Single(export.Projections);
        var split = new MemoryProjection(projection.Source, projection.Scope, projection.Perspective, projection.Kind,
            projection.AuthorId, projection.SourceRevision, projection.Sequence, projection.CreatedAt, projection.SourceContentHash,
            2, 1, projection.SliceContentHash, projection.ProfileId, projection.Vector);
        var page = new Projections([new(export.ExportId, split)]);
        var result = await _engine.SearchAsync(Search("fruit"), new Embeddings(), page, new Sources([source]));
        Assert.Empty(result.Hits);
        Assert.False(result.Coverage.IsComplete);
    }

    [Fact]
    public async Task Source_and_projection_budgets_and_partial_reader_never_claim_complete_absence()
    {
        var export = await _engine.CreateExportAsync(Index() with { MaxSources = 1, MaxProjections = 1, ChunkCharacters = 2 },
            [Source("a", "orchard"), Source("b", "satellite")], new Embeddings());
        Assert.False(export.Coverage.IsComplete);
        Assert.Equal(2, export.Coverage.EligibleSources);
        Assert.Equal(2, export.Coverage.OmittedSources);
        Assert.Contains("source_budget", export.Coverage.Reasons);
        Assert.Contains("projection_budget", export.Coverage.Reasons);
        var empty = new Projections([]) { IsComplete = false };
        var result = await _engine.SearchAsync(Search("fruit"), new Embeddings(), empty, new Sources([]));
        Assert.Empty(result.Hits);
        Assert.Equal(MemorySearchState.Completed, result.State);
        Assert.False(result.Coverage.IsComplete);
        Assert.Contains("projection_coverage_partial", result.Coverage.Reasons);
    }

    [Fact]
    public async Task Equal_similarity_ties_are_stable_across_reader_order_and_duplicate_exports()
    {
        var sources = new[] { Source("a", "orchard"), Source("b", "orchard again") };
        var export = await _engine.CreateExportAsync(Index(), sources, new Embeddings());
        var items = export.Projections.Select(item => new MemoryLinkedProjection(export.ExportId, item)).ToArray();
        var first = await _engine.SearchAsync(Search("fruit"), new Embeddings(), new Projections(items), new Sources(sources));
        var second = await _engine.SearchAsync(Search("fruit"), new Embeddings(), new Projections(items.Reverse().Concat(items).ToArray()), new Sources(sources));
        Assert.Equal(first.Hits.Select(hit => hit.ProjectionId), second.Hits.Select(hit => hit.ProjectionId));
        Assert.Equal(2, second.Hits.Count);
        Assert.Equal(2, second.CandidateCount);
        Assert.Equal(2, second.InspectedCount);
    }

    [Fact]
    public async Task Cancellation_before_or_during_embedding_is_not_absence_and_never_retries()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var provider = new Embeddings();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.CreateExportAsync(Index(),
            [Source("a", "orchard")], provider, cancellation.Token));
        Assert.Empty(provider.Calls);
        var canceled = new Embeddings((_, _, _) => throw new OperationCanceledException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.CreateExportAsync(Index(), [Source("a", "orchard")], canceled));
        Assert.Single(canceled.Calls);
    }

    [Fact]
    public async Task Provider_failure_is_unavailable_but_wrapped_process_integrity_failure_escapes()
    {
        var failed = new Embeddings((_, _, _) => throw new InvalidOperationException("provider unavailable"));
        var export = await _engine.CreateExportAsync(Index(), [Source("a", "orchard")], failed);
        Assert.False(export.Coverage.IsComplete);
        Assert.Empty(export.Projections);
#pragma warning disable CA2201 // Synthetic integrity failure proves the wrapper escapes unchanged.
        var fatal = new InvalidOperationException("wrapper", new OutOfMemoryException("synthetic"));
#pragma warning restore CA2201
        var provider = new Embeddings((_, _, _) => throw fatal);
        Assert.Same(fatal, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _engine.CreateExportAsync(Index(), [Source("a", "orchard")], provider)));
    }

    [Fact]
    public async Task Linked_export_mapping_and_acknowledgement_keep_exact_export_identity_without_retry()
    {
        var export = await _engine.CreateExportAsync(Index(), [Source("a", "orchard")], new Embeddings());
        var mapper = new Mapper();
        var storage = new Storage();
        var writer = new LinkedMemoryExporter<string>(mapper, storage);
        var acknowledgement = await writer.WriteAsync(export);
        Assert.Equal(export.ExportId, acknowledgement.ExportId);
        Assert.Equal(export.ExportId, mapper.Seen!.ExportId);
        Assert.Equal(export.ExportId, Assert.Single(storage.Writes).ExportId);
        Assert.Equal(export.ExportId, JsonSerializer.Deserialize<MemoryIndexExport>(storage.Writes[0].Model)!.ExportId);
        storage.WrongLink = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(export));
        Assert.Equal(2, storage.Writes.Count);
    }

    private static MemorySource Source(string id, string text, MemoryScope? scope = null,
        MemoryPerspective perspective = MemoryPerspective.Participant, MemorySourceKind kind = MemorySourceKind.Conversation,
        string revision = "r1", long sequence = 1, DateTimeOffset? createdAt = null)
    {
        var bound = scope ?? Scope;
        return new(new EvidenceRef("message", id), bound, perspective, kind,
            perspective == MemoryPerspective.Participant ? bound.ParticipantId : bound.AgentId,
            revision, sequence, createdAt ?? At, text);
    }

    private static MemoryIndexRequest Index() => new(Scope, MemoryPerspective.Participant,
        MemorySourceKind.Conversation, Profile, 10, At);
    private static MemorySearchRequest Search(string query) => new(Scope, MemoryPerspective.Participant,
        MemorySourceKind.Conversation, Profile, 10, At, query);

    private static IEnumerable<float> FailAfterOne(Exception failure)
    {
        yield return 1;
        throw failure;
    }

    private sealed class CountedList<T>(int declaredCount, IEnumerable<T> actual) : IReadOnlyList<T>
    {
        public int Count => declaredCount;
        public T this[int index] => actual.ElementAt(index);
        public int Yielded { get; private set; }
        public IEnumerator<T> GetEnumerator()
        {
            foreach (var item in actual)
            {
                Yielded++;
                yield return item;
            }
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Embeddings(Func<MemoryEmbeddingProfile, MemoryEmbeddingPurpose, IReadOnlyList<string>, MemoryEmbeddingBatch>? response = null)
        : IMemoryEmbeddingProvider
    {
        public List<(MemoryEmbeddingPurpose Purpose, string[] Inputs)> Calls { get; } = [];
        public Task<MemoryEmbeddingBatch> EmbedAsync(MemoryEmbeddingProfile profile, MemoryEmbeddingPurpose purpose,
            IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((purpose, inputs.ToArray()));
            var batch = response is not null ? response(profile, purpose, inputs) :
                new(profile.ProfileId, inputs.Select(text => (IReadOnlyList<float>)(text.Contains("satellite", StringComparison.Ordinal)
                    ? new float[] { 0, 1 } : [1, 0])).ToArray(), inputs.Sum(text => (long)text.Length));
            return Task.FromResult(batch);
        }
    }

    private sealed class Projections : IMemoryProjectionReader
    {
        private readonly IReadOnlyList<MemoryLinkedProjection> _items;
        public Projections(MemoryIndexExport export)
        {
            _items = export.Projections.Select(item => new MemoryLinkedProjection(export.ExportId, item)).ToArray();
            IsComplete = export.Coverage.IsComplete;
        }
        public Projections(IReadOnlyList<MemoryLinkedProjection> items) => _items = items;
        public bool IsComplete { get; init; } = true;
        public MemoryProjectionQuery? LastQuery { get; private set; }
        public Task<MemoryProjectionPage> ReadAsync(MemoryProjectionQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(new MemoryProjectionPage(_items, IsComplete));
        }
    }

    private sealed class Sources(IReadOnlyList<MemorySource> sources) : IMemorySourceReader
    {
        public List<MemorySourceLookup> Requests { get; } = [];
        public MemoryReadState State { get; init; } = MemoryReadState.Available;
        public Task<MemorySourceRead> ReadAsync(MemorySourceLookup lookup, CancellationToken cancellationToken = default)
        {
            Requests.Add(lookup);
            return Task.FromResult(new MemorySourceRead(State, sources.FirstOrDefault(source => source.Source == lookup.Source)));
        }
    }

    private sealed class Mapper : IMemoryExportMapper<string>
    {
        public MemoryIndexExport? Seen { get; private set; }
        public string Map(MemoryIndexExport export) { Seen = export; return JsonSerializer.Serialize(export); }
    }

    private sealed class Storage : IMemoryStorageProvider<string>
    {
        public List<(string ExportId, string Model)> Writes { get; } = [];
        public bool WrongLink { get; set; }
        public Task<MemoryStorageAcknowledgement> WriteAsync(string exportId, string model, CancellationToken cancellationToken = default)
        {
            Writes.Add((exportId, model));
            return Task.FromResult(new MemoryStorageAcknowledgement(WrongLink ? "wrong-export" : exportId, "fixture.store"));
        }
    }
}
