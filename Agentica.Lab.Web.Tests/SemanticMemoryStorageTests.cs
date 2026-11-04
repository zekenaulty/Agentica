using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Chat.Recall;
using Agentica.Memory;
using Agentica.Observations;
using Microsoft.Data.Sqlite;

namespace Agentica.Lab.Web.Tests;

public sealed class SemanticMemoryStorageTests
{
    private static readonly DateTimeOffset CutAt = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Linked_export_roundtrips_after_reopen_without_copying_canonical_text()
    {
        using var fixture = new Fixture();
        var source = Source(fixture.Scope, text: "canonical secret: Copper Finch belongs only in the transcript");
        var export = Export(source, Profile(), inputTokens: 17);
        var mapper = new SemanticMemoryStorageMapper();
        var facade = new LinkedMemoryExporter<SemanticMemoryStorageExport>(mapper, fixture.Storage);
        var ack = await facade.WriteAsync(export);
        Assert.Equal(export.ExportId, ack.ExportId);
        Assert.Contains("semantic-memory.sqlite3#export/", ack.StorageReference);
        Assert.Equal(1, fixture.Count("semantic_memory_exports"));
        Assert.False(fixture.Storage.Write(export));
        Assert.Equal(1, fixture.Count("semantic_memory_exports"));
        Assert.Equal(1, fixture.Count("semantic_memory_projection_links"));

        var reopened = new SemanticMemoryStorageProvider(fixture.Path, fixture.Canonical);
        var linked = Assert.Single((await reopened.ReadAsync(Query(export))).Items);
        Assert.Equal(export.ExportId, linked.ExportId);
        Assert.Equal(export.Projections[0].ProjectionId, linked.Projection.ProjectionId);
        Assert.Equal(export.Projections[0].Vector, linked.Projection.Vector);
        Assert.Equal(export.Projections[0].SourceContentHash, linked.Projection.SourceContentHash);
        var body = fixture.Text("SELECT body_json FROM semantic_memory_exports");
        Assert.DoesNotContain(source.Text, body);
        Assert.Equal(export.ExportId, mapper.ToCore(mapper.Map(export)).ExportId);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(17, json.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.False(json.RootElement.TryGetProperty("text", out _));
        Assert.False(json.RootElement.GetProperty("projections")[0].TryGetProperty("text", out _));
    }

    [Fact]
    public async Task Mapper_and_writer_reject_changed_identity_instead_of_overwriting()
    {
        using var fixture = new Fixture();
        var export = Export(Source(fixture.Scope), Profile());
        var mapper = new SemanticMemoryStorageMapper(); var model = mapper.Map(export);
        fixture.Storage.Write(export);
        var changed = model.Projections[0] with { StartUtf16 = 1 };
        Assert.Throws<InvalidDataException>(() => mapper.ToCore(model with { Projections = [changed] }));
        Assert.Throws<InvalidDataException>(() => mapper.ToCore(model with { Scope = model.Scope with { ParticipantId = "other" } }));
        Assert.Throws<InvalidDataException>(() => mapper.ToCore(model with { Profile = model.Profile with { ModelId = "different" } }));
        Assert.Throws<InvalidDataException>(() => mapper.ToCore(model with { Perspective = "0" }));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await fixture.Storage.WriteAsync("sha256:" + new string('0', 64), model));
        Assert.Equal(1, fixture.Count("semantic_memory_exports"));
        Assert.Equal(export.ExportId, Assert.Single((await fixture.Storage.ReadAsync(Query(export))).Items).ExportId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Corrupt_export_is_rejected_even_when_its_outer_digest_is_recomputed(bool recomputeDigest)
    {
        using var fixture = new Fixture();
        var export = Export(Source(fixture.Scope), Profile()); fixture.Storage.Write(export);
        var body = fixture.Text("SELECT body_json FROM semantic_memory_exports");
        using var document = JsonDocument.Parse(body);
        var model = JsonSerializer.Deserialize<SemanticMemoryStorageExport>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var changed = model with { Projections = [model.Projections[0] with { SliceContentHash = Hash("tampered slice") }] };
        var forged = JsonSerializer.Serialize(changed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        fixture.Execute("UPDATE semantic_memory_exports SET body_json=$body,model_hash=$hash",
            ("$body", forged), ("$hash", recomputeDigest ? PlainHash(forged) : PlainHash(body)));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await fixture.Storage.ReadAsync(Query(export)));
        Assert.Throws<InvalidDataException>(() => fixture.Storage.Write(export));
    }

    [Fact]
    public async Task Every_scope_dimension_perspective_kind_profile_and_cut_is_filtered_before_limit()
    {
        using var fixture = new Fixture(); var profile = Profile();
        var expected = Export(Source(fixture.Scope, id: "wanted"), profile, asOfSequence: 4);
        fixture.Storage.Write(expected);
        MemoryScope[] foreignScopes =
        [
            new("other-namespace", fixture.Scope.ParticipantId, fixture.Scope.AgentId, fixture.Scope.ConversationId),
            new(fixture.Scope.NamespaceId, "other-participant", fixture.Scope.AgentId, fixture.Scope.ConversationId),
            new(fixture.Scope.NamespaceId, fixture.Scope.ParticipantId, "other-agent", fixture.Scope.ConversationId),
            new(fixture.Scope.NamespaceId, fixture.Scope.ParticipantId, fixture.Scope.AgentId, "other-conversation")
        ];
        foreach (var scope in foreignScopes) fixture.Storage.Write(Export(Source(scope, id: "foreign", sequence: 3), profile));
        fixture.Storage.Write(Export(Source(fixture.Scope, MemoryPerspective.Agent, id: "agent", sequence: 3), profile));
        fixture.Storage.Write(Export(Source(fixture.Scope, kind: MemorySourceKind.GovernedMemory, id: "governed", sequence: 3), profile));
        fixture.Storage.Write(Export(Source(fixture.Scope, id: "other-profile", sequence: 3), Profile(model: "same-dimensions-different-space")));
        fixture.Storage.Write(Export(Source(fixture.Scope, id: "future-sequence", sequence: 5), profile, asOfSequence: 5));
        fixture.Storage.Write(Export(Source(fixture.Scope, id: "future-timestamp", createdAt: CutAt.AddSeconds(1)), profile,
            asOfAt: CutAt.AddSeconds(1)));
        fixture.Storage.Write(Export(Source(fixture.Scope, id: "future-export-cut"), profile, asOfSequence: 5));
        var page = await fixture.Storage.ReadAsync(Query(expected) with { Limit = 1 });
        Assert.True(page.IsComplete);
        Assert.Equal("wanted", Assert.Single(page.Items).Projection.Source.RefId);
    }

    [Fact]
    public async Task Duplicate_projection_is_deduplicated_before_limit_and_links_to_retained_export()
    {
        using var fixture = new Fixture();
        var first = Export(Source(fixture.Scope), Profile()); fixture.Storage.Write(first);
        var later = new MemoryIndexExport(first.Scope, first.Perspective, first.SourceKind, first.Profile, 3, CutAt,
            first.Projections, first.Coverage, 23);
        fixture.Storage.Write(later);
        var page = await fixture.Storage.ReadAsync(Query(later) with { Limit = 1 });
        Assert.True(page.IsComplete);
        var linked = Assert.Single(page.Items);
        Assert.Equal(first.Projections[0].ProjectionId, linked.Projection.ProjectionId);
        Assert.Equal(later.ExportId, linked.ExportId);
        Assert.Equal(2, fixture.Count("semantic_memory_projection_links"));
    }

    [Fact]
    public async Task Source_replacement_retires_old_profile_coverage_and_late_exports_cannot_revive_it()
    {
        using var fixture = new Fixture(); var profile = Profile();
        var original = Source(fixture.Scope, text: "old source");
        var old = Export(original, profile, asOfSequence: 1); fixture.Storage.Write(old);
        var otherProfile = Export(original, Profile(model: "other-space"), asOfSequence: 1); fixture.Storage.Write(otherProfile);
        var changed = Source(fixture.Scope, text: "new source");
        var current = Export(changed, profile, asOfSequence: 3); fixture.Storage.Write(current);
        Assert.False(fixture.Storage.Write(old)); // Exact replay cannot change retired flags.
        var late = Export(original, profile, asOfSequence: 2, inputTokens: 9); fixture.Storage.Write(late);
        var query = Query(current);
        Assert.Equal(changed.ContentHash, Assert.Single((await fixture.Storage.ReadAsync(query)).Items).Projection.SourceContentHash);
        Assert.Equal(original.ContentHash, Assert.Single((await fixture.Storage.ReadAsync(Query(otherProfile))).Items).Projection.SourceContentHash);
        Assert.False(fixture.Storage.HasCurrentSource(query, original.Source, original.Revision, original.ContentHash, original.Text.Length));
        Assert.True(fixture.Storage.HasCurrentSource(query, changed.Source, changed.Revision, changed.ContentHash, changed.Text.Length));
        Assert.Equal(2, fixture.Scalar("SELECT COUNT(*) FROM semantic_memory_projection_links WHERE retired=1"));
    }

    [Fact]
    public async Task Conflicting_revision_at_same_cut_rolls_back_the_whole_export()
    {
        using var fixture = new Fixture(); var profile = Profile();
        var original = Source(fixture.Scope, id: "shared", text: "current");
        var retained = Export(original, profile); fixture.Storage.Write(retained);
        var extra = Projection(Source(fixture.Scope, id: "new-unrelated"), profile);
        var conflict = Projection(Source(fixture.Scope, id: "shared", text: "conflict"), profile);
        var bad = new MemoryIndexExport(fixture.Scope, MemoryPerspective.Participant, MemorySourceKind.Conversation,
            profile, retained.AsOfSequence, retained.AsOfAt, [extra, conflict], new(true, 2, 2, 0, 0, []));
        Assert.Throws<InvalidDataException>(() => fixture.Storage.Write(bad));
        Assert.Equal(1, fixture.Count("semantic_memory_exports"));
        Assert.Equal(1, fixture.Count("semantic_memory_source_heads"));
        Assert.Equal("shared", Assert.Single((await fixture.Storage.ReadAsync(Query(retained))).Items).Projection.Source.RefId);
    }

    [Fact]
    public void Indexed_source_detection_requires_complete_exact_ranges_and_exact_revision()
    {
        using var fixture = new Fixture(); var profile = Profile(); var source = Source(fixture.Scope, text: "0123456789");
        var first = Export(source, profile, [Projection(source, profile, 0, 4)], coverage: new(false, 1, 1, 1, 1, ["projection_budget"]));
        fixture.Storage.Write(first);
        Assert.False(fixture.Storage.HasCurrentSource(Query(first), source.Source, source.Revision, source.ContentHash, 10));
        var gapped = Export(source, profile, [Projection(source, profile, 5, 5)]); fixture.Storage.Write(gapped);
        Assert.False(fixture.Storage.HasCurrentSource(Query(first), source.Source, source.Revision, source.ContentHash, 10));
        var bridge = Export(source, profile, [Projection(source, profile, 4, 1)]); fixture.Storage.Write(bridge);
        Assert.True(fixture.Storage.HasCurrentSource(Query(first), source.Source, source.Revision, source.ContentHash, 10));
        Assert.False(fixture.Storage.HasCurrentSource(Query(first), source.Source, "different-revision", source.ContentHash, 10));
        Assert.False(fixture.Storage.HasCurrentSource(Query(first), source.Source, source.Revision, Hash("different"), 10));
        Assert.False(fixture.Storage.HasCurrentSource(Query(first), source.Source, source.Revision, source.ContentHash, 9));
    }

    [Fact]
    public async Task Bound_canonical_reader_preserves_original_actor_and_rejects_scope_role_and_horizon_changes()
    {
        using var fixture = new Fixture();
        var turn = fixture.Complete("owner statement", "agent hypothesis");
        var at = turn.FinishedAt!.Value.AddTicks(1);
        var binding = fixture.Storage.Bind(fixture.Scope, turn.Sequence, at);
        var user = await binding.SourceReader.ReadAsync(Lookup(fixture.Scope, turn.Id + ":user", turn.Sequence, at));
        Assert.Equal(MemoryReadState.Available, user.State);
        Assert.Equal(fixture.Scope.ParticipantId, user.Source!.AuthorId);
        Assert.Equal(MemoryPerspective.Participant, user.Source.Perspective);
        Assert.Equal(turn.UserText, user.Source.Text);
        Assert.Equal(Hash(turn.UserText), user.Source.ContentHash);
        Assert.Equal(user.Source.ContentHash, user.Source.Revision);
        var agent = await binding.SourceReader.ReadAsync(Lookup(fixture.Scope, turn.Id + ":assistant", turn.Sequence, at, MemoryPerspective.Agent));
        Assert.Equal(fixture.Scope.AgentId, agent.Source!.AuthorId);
        Assert.Equal(turn.AssistantText, agent.Source.Text);
        Assert.Equal(MemoryReadState.Unavailable, (await binding.SourceReader.ReadAsync(
            Lookup(fixture.Scope, turn.Id + ":assistant", turn.Sequence, at))).State);
        var foreign = new MemoryScope(fixture.Scope.NamespaceId, "another-owner", fixture.Scope.AgentId, fixture.Scope.ConversationId);
        Assert.Equal(MemoryReadState.Unavailable, (await binding.SourceReader.ReadAsync(Lookup(foreign, turn.Id + ":user", turn.Sequence, at))).State);
        Assert.Equal(MemoryReadState.Unavailable, (await binding.SourceReader.ReadAsync(Lookup(fixture.Scope, turn.Id + ":user", turn.Sequence + 1, at))).State);
        Assert.Equal(MemoryReadState.Unavailable, (await binding.SourceReader.ReadAsync(Lookup(fixture.Scope, turn.Id + ":user", turn.Sequence, at.AddTicks(1)))).State);
        Assert.Equal(MemoryReadState.Unavailable, (await binding.SourceReader.ReadAsync(Lookup(fixture.Scope, turn.Id + ":user", 0, at))).State);
        await Assert.ThrowsAsync<ArgumentException>(async () => await binding.ProjectionReader.ReadAsync(
            new(foreign, MemoryPerspective.Participant, MemorySourceKind.Conversation, Profile().ProfileId, turn.Sequence, at, 4)));
    }

    [Fact]
    public async Task Revocation_after_export_blocks_a_stale_citation_at_actual_source_use()
    {
        using var fixture = new Fixture(); var turn = fixture.Complete("exact retained source", "agent answer");
        var at = turn.FinishedAt!.Value.AddTicks(1); var profile = Profile();
        var binding = fixture.Storage.Bind(fixture.Scope, turn.Sequence, at);
        var source = (await binding.SourceReader.ReadAsync(Lookup(fixture.Scope, turn.Id + ":user", turn.Sequence, at))).Source!;
        var engine = new SemanticMemoryEngine(); var embedding = new Embeddings();
        var export = await engine.CreateExportAsync(new(fixture.Scope, MemoryPerspective.Participant,
            MemorySourceKind.Conversation, profile, turn.Sequence, at), [source], embedding);
        fixture.Storage.Write(export);
        var request = new MemorySearchRequest(fixture.Scope, MemoryPerspective.Participant,
            MemorySourceKind.Conversation, profile, turn.Sequence, at, "retained source");
        var first = await engine.SearchAsync(request, embedding, binding.ProjectionReader, binding.SourceReader);
        Assert.Equal(source.Text, Assert.Single(first.Hits).Segment.Text);
        fixture.Execute("UPDATE turns SET body_json=json_set(body_json,'$.status','failed') WHERE id=$id", true, ("$id", turn.Id));
        Assert.Equal(MemoryReadState.Unavailable, (await binding.SourceReader.ReadAsync(Lookup(fixture.Scope, turn.Id + ":user", turn.Sequence, at))).State);
        var stale = await engine.SearchAsync(request, embedding, binding.ProjectionReader, binding.SourceReader);
        Assert.Empty(stale.Hits);
        Assert.False(stale.Coverage.IsComplete);
        Assert.Contains("source_unavailable_or_stale", stale.Coverage.Reasons);
        Assert.Single((await fixture.Storage.ReadAsync(Query(export))).Items); // A retained vector cannot bypass the canonical check.
    }

    [Fact]
    public async Task Canonical_source_changes_are_reread_and_old_vector_cannot_quote_changed_text()
    {
        using var fixture = new Fixture(); var turn = fixture.Complete("old exact source", "answer");
        var at = turn.FinishedAt!.Value.AddTicks(1); var binding = fixture.Storage.Bind(fixture.Scope, turn.Sequence, at);
        var source = (await binding.SourceReader.ReadAsync(Lookup(fixture.Scope, turn.Id + ":user", turn.Sequence, at))).Source!;
        var profile = Profile(); var export = Export(source, profile, asOfSequence: turn.Sequence, asOfAt: at);
        fixture.Storage.Write(export);
        fixture.Execute("UPDATE turns SET body_json=json_set(body_json,'$.userText','corrected exact source') WHERE id=$id", true, ("$id", turn.Id));
        var current = await binding.SourceReader.ReadAsync(Lookup(fixture.Scope, turn.Id + ":user", turn.Sequence, at));
        Assert.Equal("corrected exact source", current.Source!.Text);
        Assert.NotEqual(source.ContentHash, current.Source.ContentHash);
        var result = await new SemanticMemoryEngine().SearchAsync(new(fixture.Scope, MemoryPerspective.Participant,
            MemorySourceKind.Conversation, profile, turn.Sequence, at, "source"), new Embeddings(), binding.ProjectionReader, binding.SourceReader);
        Assert.Empty(result.Hits);
        Assert.Contains("source_unavailable_or_stale", result.Coverage.Reasons);
    }

    [Fact]
    public async Task Limit_reports_incomplete_and_cancelled_write_leaves_no_partial_export()
    {
        using var fixture = new Fixture(); var profile = Profile();
        var source = Source(fixture.Scope, text: "0123456789");
        var export = Export(source, profile, [Projection(source, profile, 0, 5), Projection(source, profile, 5, 5)]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Storage.Write(export, cancellation.Token));
        Assert.Equal(0, fixture.Count("semantic_memory_exports"));
        fixture.Storage.Write(export);
        var page = await fixture.Storage.ReadAsync(Query(export) with { Limit = 1 });
        Assert.Single(page.Items); Assert.False(page.IsComplete);
    }

    [Fact]
    public void Newer_or_foreign_database_versions_fail_without_touching_canonical_storage()
    {
        using var fixture = new Fixture();
        var before = fixture.Scalar("PRAGMA user_version", true);
        fixture.Execute("PRAGMA user_version=2");
        Assert.Throws<InvalidDataException>(() => new SemanticMemoryStorageProvider(fixture.Path, fixture.Canonical));
        Assert.Equal(before, fixture.Scalar("PRAGMA user_version", true));
    }

    private static MemoryEmbeddingProfile Profile(string model = "fixture-embedding", string configuration = "v1") =>
        new("fixture", model, configuration, 2, "plain-document.v1", "plain-query.v1");
    private static MemorySource Source(MemoryScope scope, MemoryPerspective perspective = MemoryPerspective.Participant,
        MemorySourceKind kind = MemorySourceKind.Conversation, string id = "source", string text = "exact canonical source",
        long sequence = 1, DateTimeOffset? createdAt = null) =>
        new(new("conversation", id), scope, perspective, kind,
            perspective == MemoryPerspective.Participant ? scope.ParticipantId : scope.AgentId,
            Hash(text), sequence, createdAt ?? CutAt.AddMinutes(-1), text);
    private static MemoryProjection Projection(MemorySource source, MemoryEmbeddingProfile profile,
        int start = 0, int? length = null) =>
        new(source.Source, source.Scope, source.Perspective, source.Kind, source.AuthorId, source.Revision,
            source.Sequence, source.CreatedAt, source.ContentHash, start, length ?? source.Text.Length,
            Hash(source.Text.Substring(start, length ?? source.Text.Length)), profile.ProfileId, [1, 0]);
    private static MemoryIndexExport Export(MemorySource source, MemoryEmbeddingProfile profile,
        IReadOnlyList<MemoryProjection>? projections = null, long asOfSequence = 4, DateTimeOffset? asOfAt = null,
        MemoryCoverage? coverage = null, long? inputTokens = null) =>
        new(source.Scope, source.Perspective, source.Kind, profile, asOfSequence, asOfAt ?? CutAt,
            projections ?? [Projection(source, profile)], coverage ?? new(true, 1, 1, 0, 0, []), inputTokens);
    private static MemoryProjectionQuery Query(MemoryIndexExport export) =>
        new(export.Scope, export.Perspective, export.SourceKind, export.Profile.ProfileId, export.AsOfSequence, export.AsOfAt, 512);
    private static MemorySourceLookup Lookup(MemoryScope scope, string id, long sequence, DateTimeOffset at,
        MemoryPerspective perspective = MemoryPerspective.Participant) =>
        new(new("conversation", id), scope, perspective, MemorySourceKind.Conversation, sequence, at);
    private static string Hash(string text) => "sha256:" + PlainHash(text);
    private static string PlainHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Embeddings : IMemoryEmbeddingProvider
    {
        public Task<MemoryEmbeddingBatch> EmbedAsync(MemoryEmbeddingProfile profile, MemoryEmbeddingPurpose purpose,
            IReadOnlyList<string> inputs, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEmbeddingBatch(profile.ProfileId,
                inputs.Select(_ => (IReadOnlyList<float>)new float[] { 1, 0 }).ToArray(), inputs.Count));
    }

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("agentica-semantic-storage-").FullName;
        public ExperimentStore Canonical { get; }
        public ExperimentConversation Conversation { get; }
        public MemoryScope Scope { get; }
        public SemanticMemoryStorageProvider Storage { get; }
        public Fixture()
        {
            Canonical = new ExperimentStore(Path); Conversation = Canonical.Create(new(), "semantic storage fixture");
            Scope = new("agentica-lab", "owner-identity", "agent-identity", Conversation.Id);
            Storage = new(Path, Canonical);
        }
        public ExperimentTurn Complete(string user, string answer)
        { var turn = Canonical.Begin(Conversation.Id, new(user)).Turn; return Canonical.Complete(Conversation.Id, turn.Id, new("completed", answer)); }
        private SqliteConnection Open(bool canonical)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = System.IO.Path.Combine(Path, canonical ? "experiments.sqlite3" : "semantic-memory.sqlite3"),
                ForeignKeys = true,
                Pooling = false
            }.ToString()); connection.Open(); return connection;
        }
        public void Execute(string sql, params (string, object?)[] values) => Execute(sql, false, values);
        public void Execute(string sql, bool canonical, params (string, object?)[] values)
        {
            using var connection = Open(canonical); using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
        public long Scalar(string sql, bool canonical = false)
        { using var connection = Open(canonical); using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public long Count(string table) => Scalar("SELECT COUNT(*) FROM " + table);
        public string Text(string sql)
        { using var connection = Open(false); using var command = connection.CreateCommand(); command.CommandText = sql; return (string)command.ExecuteScalar()!; }
        public void Dispose() { Canonical.Dispose(); Directory.Delete(Path, recursive: true); }
    }
}
