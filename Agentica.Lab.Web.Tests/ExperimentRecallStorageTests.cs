using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Chat.Recall;
using Agentica.Lab.Web.Contracts;
using Microsoft.Data.Sqlite;

namespace Agentica.Lab.Web.Tests;

public sealed class ExperimentRecallStorageTests(ITestOutputHelper output)
{
    [Fact]
    public void Fresh_and_completed_Off_conversations_do_not_materialize_any_index()
    {
        using var fixture = new Fixture(); using var store = new ExperimentStore(fixture.Path);
        var chat = store.Create(new(), "Off");
        var turn = Complete(store, chat.Id, "canonical user text", "canonical answer");
        var source = store.BindEvidenceSource(chat.Id, turn.Sequence);
        Assert.Equal("NOT_READY", source.GetCoverage().Status);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM conversation_index_state"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM conversation_segments"));
        Assert.Equal(2, source.GetHead().EligibleSourceCount);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM conversation_index_state"));
    }

    [Fact]
    public void Version_one_canonical_bytes_migrate_additively_and_newer_schema_is_refused()
    {
        using var fixture = new Fixture();
        var id = Guid.NewGuid().ToString("N"); var turnId = Guid.NewGuid().ToString("N");
        var settings = new ExperimentSettings(); var now = DateTimeOffset.UtcNow;
        var conversation = new ExperimentConversation(id, "Legacy", now, now, 1, settings, []);
        var turn = new ExperimentTurn(turnId, id, 1, "legacy α\nuser", "legacy assistant 😀", "completed", now, now, 1, settings, "transcript-only");
        var body = JsonSerializer.Serialize(turn, HostProtocol.Json);
        using (var connection = fixture.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE conversations(id TEXT PRIMARY KEY,body_json TEXT NOT NULL,updated_at TEXT NOT NULL);
                CREATE TABLE turns(id TEXT PRIMARY KEY,conversation_id TEXT NOT NULL REFERENCES conversations(id),sequence INTEGER NOT NULL,
                  request_key TEXT NOT NULL,body_json TEXT NOT NULL,UNIQUE(conversation_id,sequence),UNIQUE(conversation_id,request_key));
                CREATE TABLE trace_events(turn_id TEXT NOT NULL REFERENCES turns(id),sequence INTEGER NOT NULL,body_json TEXT NOT NULL,payload_json TEXT,PRIMARY KEY(turn_id,sequence));
                INSERT INTO conversations VALUES($chat,$conversation,$now);
                INSERT INTO turns VALUES($turn,$chat,1,'legacy-request',$body);
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue("$chat", id); command.Parameters.AddWithValue("$turn", turnId);
            command.Parameters.AddWithValue("$conversation", JsonSerializer.Serialize(conversation, HostProtocol.Json));
            command.Parameters.AddWithValue("$now", now.ToString("O")); command.Parameters.AddWithValue("$body", body);
            command.ExecuteNonQuery();
        }
        using (var upgraded = new ExperimentStore(fixture.Path))
        {
            Assert.Equal(body, fixture.Text("SELECT body_json FROM turns WHERE id='" + turnId + "'"));
            Assert.Equal(2L, fixture.Scalar("PRAGMA user_version"));
            var hit = Assert.Single(upgraded.BindEvidenceSource(id, 1).ReadExact(turnId + ":assistant").Hits);
            Assert.Equal(turn.AssistantText, hit.Excerpt); Assert.Equal(Hash(turn.AssistantText), hit.Source.ContentHash);
            Assert.Equal("NOT_READY", upgraded.BindEvidenceSource(id, 1).GetCoverage().Status);
        }
        using (var restarted = new ExperimentStore(fixture.Path)) Assert.Equal(body, fixture.Text("SELECT body_json FROM turns WHERE id='" + turnId + "'"));
        fixture.Execute("PRAGMA user_version=3");
        Assert.Throws<InvalidDataException>(() => new ExperimentStore(fixture.Path));
        fixture.Execute("PRAGMA user_version=2");
        using var repaired = new ExperimentStore(fixture.Path);
        Assert.Equal(turnId, Assert.Single(repaired.GetTurnsPage(id).Turns).Id);
    }

    [Fact]
    public void Migration_failure_rolls_back_schema_and_preserves_original_canonical_data()
    {
        using var fixture = new Fixture();
        fixture.Execute("""
            CREATE TABLE conversations(id TEXT PRIMARY KEY,body_json TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE turns(id TEXT PRIMARY KEY,conversation_id TEXT NOT NULL,sequence INTEGER NOT NULL,request_key TEXT NOT NULL,body_json TEXT NOT NULL);
            INSERT INTO turns VALUES('invalid','invalid',1,'key','malformed-canonical-json');
            PRAGMA user_version=1;
            """);
        Assert.Throws<JsonException>(() => new ExperimentStore(fixture.Path));
        Assert.Equal(1L, fixture.Scalar("PRAGMA user_version"));
        Assert.Equal("malformed-canonical-json", fixture.Text("SELECT body_json FROM turns WHERE id='invalid'"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM pragma_table_info('turns') WHERE name='metadata_version'"));
    }

    [Fact]
    public void Exact_sources_neighbors_cutoff_and_scope_are_enforced_for_every_read()
    {
        using var fixture = new Fixture(); using var store = new ExperimentStore(fixture.Path);
        var a = store.Create(new(), "A"); var b = store.Create(new(), "B");
        var first = Complete(store, a.Id, "old assertion", "early hypothesis");
        var correction = Complete(store, a.Id, "correction: reject the hypothesis", "corrected position");
        var foreign = Complete(store, b.Id, "same assertion", "foreign private response");
        var pending = store.Begin(a.Id, new("uncommitted user")).Turn;
        var source = store.BindEvidenceSource(a.Id, correction.Sequence);
        var hit = Assert.Single(source.ReadExact(first.Id + ":user", 4, 9).Hits);
        Assert.Equal("assertion", hit.Excerpt); Assert.Equal(4, hit.StartOffset); Assert.Equal(13, hit.EndOffset);
        Assert.Equal(Hash(first.UserText), hit.Source.ContentHash);
        Assert.Equal(first.CreatedAt, hit.Source.CreatedAt); Assert.Equal("user", hit.Source.Role);
        Assert.Equal("UNAVAILABLE", source.ReadExact(foreign.Id + ":assistant").Status);
        Assert.Equal("UNAVAILABLE", source.ReadExact(pending.Id + ":user").Status);
        Assert.Equal("UNAVAILABLE", store.BindEvidenceSource(a.Id, first.Sequence).ReadExact(correction.Id + ":user").Status);
        var neighbors = source.ReadNeighbors(first.Id + ":assistant", 1, 4000);
        Assert.Equal(new[] { first.Id + ":user", first.Id + ":assistant", correction.Id + ":user" }, neighbors.Hits.Select(item => item.Source.SourceId));
        Assert.Contains(neighbors.Hits, item => item.Excerpt.Contains("correction", StringComparison.Ordinal));
    }

    [Fact]
    public void Bundled_SQLite_FTS5_matches_unicode_diacritics_quotes_and_cross_segment_spans()
    {
        using var fixture = new Fixture(); using var store = new ExperimentStore(fixture.Path);
        Assert.True(store.FtsAvailable); // Qualification of the actual bundled SQLite build, not a mock.
        var chat = store.Create(new(), null);
        var text = new string('x', 2020) + " café déjà-vu \"quoted\" 東京 😀 cross boundary phrase " + new string('y', 2400);
        var turn = Complete(store, chat.Id, text, "reply");
        var progress = store.RebuildIndex(chat.Id);
        Assert.Equal("COMPLETE", progress.Status); Assert.Equal(2, progress.IndexedSources);
        var source = store.BindEvidenceSource(chat.Id, 1);
        foreach (var query in new[] { "café", "cafe", "déjà-vu", "\"quoted\"", "東京", "cross boundary phrase", "😀" })
        {
            var result = source.Search(new(query, MaxCharacters: 6000));
            var hit = Assert.Single(result.Hits, item => item.Source.SourceId == turn.Id + ":user");
            Assert.Equal(text[hit.StartOffset..hit.EndOffset], hit.Excerpt);
            Assert.Equal(Hash(text), hit.Source.ContentHash);
        }
        Assert.Throws<ExperimentException>(() => source.ReadExact(turn.Id + ":user", text.IndexOf("😀", StringComparison.Ordinal) + 1, 3));
        Assert.Equal("NO_MATCH", source.Search(new("unrepresentedkeyword7f612c")).Status);
    }

    [Fact]
    public void Canonical_changes_and_deletion_invalidate_old_hits_and_cannot_reopen_stale_text()
    {
        using var fixture = new Fixture(); using var store = new ExperimentStore(fixture.Path);
        var chat = store.Create(new(), null); var turn = Complete(store, chat.Id, "obsolete exact phrase", "answer");
        store.RebuildIndex(chat.Id); var source = store.BindEvidenceSource(chat.Id, 1);
        var old = Assert.Single(source.Search(new("obsolete")).Hits); var head = source.GetHead();
        var updated = turn with { UserText = "replacement exact phrase" };
        fixture.UpdateTurn(updated);
        Assert.Empty(source.Search(new("obsolete")).Hits);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM conversation_segments_fts WHERE text LIKE '%obsolete%'"));
        var current = Assert.Single(source.Search(new("replacement")).Hits);
        Assert.NotEqual(old.Source.ContentHash, current.Source.ContentHash);
        Assert.NotEqual(head.Revision, source.GetHead().Revision);
        fixture.Execute("DELETE FROM turns WHERE id='" + turn.Id + "'");
        Assert.Equal("UNAVAILABLE", source.ReadExact(turn.Id + ":user").Status);
        Assert.Empty(source.Search(new("replacement")).Hits);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM conversation_segments"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM conversation_segments_fts"));
    }

    [Fact]
    public void Projection_loss_rebuild_restart_and_profile_invalidation_preserve_canonical_sources()
    {
        using var fixture = new Fixture(); string id, sourceId, digest;
        using (var store = new ExperimentStore(fixture.Path))
        {
            var chat = store.Create(new(), null); var turn = Complete(store, chat.Id, "stable history phrase", "answer");
            id = chat.Id; sourceId = turn.Id + ":user"; digest = Hash(turn.UserText);
            store.RebuildIndex(id); store.DeleteIndex(id);
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM conversation_segments_fts"));
            Assert.Equal("NOT_READY", store.BindEvidenceSource(id, 1).GetCoverage().Status);
            Assert.Equal(digest, Assert.Single(store.BindEvidenceSource(id, 1).ReadExact(sourceId).Hits).Source.ContentHash);
            store.RebuildIndex(id);
            fixture.Execute("DELETE FROM conversation_segments_fts");
            Assert.Equal("PARTIAL", store.BindEvidenceSource(id, 1).GetCoverage().Status);
            Assert.Single(store.BindEvidenceSource(id, 1).Search(new("stable history")).Hits);
            fixture.Execute("UPDATE conversation_index_state SET version='incompatible-profile'");
            Assert.Equal("PARTIAL", store.BindEvidenceSource(id, 1).GetCoverage().Status);
            Assert.Single(store.BindEvidenceSource(id, 1).Search(new("stable history")).Hits);
        }
        using var restarted = new ExperimentStore(fixture.Path);
        Assert.Equal(digest, Assert.Single(restarted.BindEvidenceSource(id, 1).ReadExact(sourceId).Hits).Source.ContentHash);
        Assert.Single(restarted.BindEvidenceSource(id, 1).Search(new("stable history")).Hits);
    }

    [Fact]
    public void Rebuild_cancellation_preserves_sources_and_can_resume_after_restart()
    {
        using var fixture = new Fixture(); string id;
        using (var store = new ExperimentStore(fixture.Path))
        {
            var chat = store.Create(new(), null); id = chat.Id; fixture.Seed(chat, 50);
            Assert.ThrowsAny<OperationCanceledException>(() => store.RebuildIndex(id, new CancellationToken(true)));
            Assert.Equal(50, store.GetHead(id).TotalTurns);
        }
        using var restarted = new ExperimentStore(fixture.Path);
        Assert.Equal("COMPLETE", restarted.RebuildIndex(id).Status);
        Assert.Equal(100, restarted.BindEvidenceSource(id, 50).GetCoverage().IndexedSourceCount);
    }

    [Fact]
    public void Broken_disposable_FTS_does_not_block_canonical_conversation_completion()
    {
        using var fixture = new Fixture(); using var store = new ExperimentStore(fixture.Path);
        var chat = store.Create(new(), null); Complete(store, chat.Id, "first canonical", "first answer");
        store.RebuildIndex(chat.Id); fixture.Execute("DROP TABLE conversation_segments_fts");
        var next = Complete(store, chat.Id, "second canonical", "second answer");
        Assert.Equal("second answer", store.GetTurn(chat.Id, next.Id).AssistantText);
        var result = store.BindEvidenceSource(chat.Id, next.Sequence).Search(new("second canonical"));
        Assert.Single(result.Hits); Assert.False(result.Coverage.FtsAvailable);
    }

    [Fact]
    public void Natural_question_uses_transparent_bounded_keyword_fallback_and_reopens_exact_canonical_spans()
    {
        using var fixture = new Fixture(); using var store = new ExperimentStore(fixture.Path);
        var chat = store.Create(new(), null);
        var turn = Complete(store, chat.Id, "The launch codename is Copper Finch.", "Launch detail recorded as an earlier statement.");
        var source = store.BindEvidenceSource(chat.Id, turn.Sequence);
        var result = source.Search(new("What was the launch codename we chose?"));
        Assert.Equal("fts5-keyword-or+canonical-verification", result.QueryMethod);
        var hit = Assert.Single(result.Hits, item => item.Source.SourceId == turn.Id + ":user");
        Assert.Equal(turn.UserText[hit.StartOffset..hit.EndOffset], hit.Excerpt);
        Assert.Contains("Copper Finch", hit.Excerpt);
        Assert.Contains(result.Reasons, reason => reason.Contains("12 non-stopword keywords", StringComparison.Ordinal));
        Assert.Contains("relevance not established", hit.MatchReason);
        var exact = source.Search(new("launch codename"));
        Assert.Equal("fts5-phrase+canonical-verification", exact.QueryMethod);
    }

    [Fact]
    public void Five_thousand_turn_baseline_uses_bounded_pages_and_retrieves_an_early_exact_source()
    {
        using var fixture = new Fixture(); using var store = new ExperimentStore(fixture.Path);
        var chat = store.Create(new(), "5000-turn fixture"); var early = fixture.Seed(chat, 5000);
        var clock = Stopwatch.StartNew(); var page = store.GetTurnsPage(chat.Id); var pageMs = clock.ElapsedMilliseconds;
        clock.Restart(); var recent = store.GetRecentCommittedTurns(chat.Id, 5001, 17); var recentMs = clock.ElapsedMilliseconds;
        Assert.Equal(20, page.Turns.Count); Assert.Equal(17, recent.Count); Assert.Equal(5000, page.TotalTurns);
        Assert.Equal(4981, page.NextBeforeSequence); Assert.Equal(4981, page.Turns[0].Sequence); Assert.Equal(5000, page.Turns[^1].Sequence);
        Assert.Equal(4961, store.GetTurnsPage(chat.Id, page.NextBeforeSequence).Turns[0].Sequence);
        Assert.Empty(store.GetHeader(chat.Id).Turns); Assert.Null(store.GetHeader(chat.Id).HistoryPage);
        clock.Restart(); var full = store.Get(chat.Id); var fullMs = clock.ElapsedMilliseconds;
        var pageBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(page, HostProtocol.Json));
        var fullBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(full, HostProtocol.Json));
        Assert.True(pageBytes < fullBytes / 20); // Deterministic size gate, not a guessed hardware latency threshold.
        var source = store.BindEvidenceSource(chat.Id, 5000);
        var partial = source.Search(new("not present anywhere"));
        Assert.Equal("PARTIAL", partial.Status); Assert.True(partial.InspectedCount <= 1256);
        clock.Restart(); var rebuilt = store.RebuildIndex(chat.Id); var rebuildMs = clock.ElapsedMilliseconds;
        Assert.Equal("COMPLETE", rebuilt.Status); Assert.Equal(10000, rebuilt.IndexedSources);
        clock.Restart(); var result = source.Search(new("needle-early-episodic")); var searchMs = clock.ElapsedMilliseconds;
        var hit = Assert.Single(result.Hits);
        Assert.Equal(early + ":user", hit.Source.SourceId); Assert.Equal("COMPLETE", result.Status);
        Assert.Equal("NO_MATCH", source.Search(new("not present anywhere")).Status);
        output.WriteLine($"5000 turns: newest20={pageMs}ms/{pageBytes}bytes; recent17={recentMs}ms; full5000={fullMs}ms/{fullBytes}bytes; rebuild={rebuildMs}ms; early-search={searchMs}ms.");
    }

    private static ExperimentTurn Complete(ExperimentStore store, string id, string user, string answer)
    { var turn = store.Begin(id, new(user)).Turn; return store.Complete(id, turn.Id, new("completed", answer)); }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("agentica-thread-recall-").FullName;
        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = System.IO.Path.Combine(Path, "experiments.sqlite3"), ForeignKeys = true, Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Execute(string sql) { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public long Scalar(string sql) { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public string Text(string sql) { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return (string)command.ExecuteScalar()!; }
        public void UpdateTurn(ExperimentTurn turn)
        {
            using var connection = Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE turns SET body_json=$body WHERE id=$id";
            command.Parameters.AddWithValue("$body", JsonSerializer.Serialize(turn, HostProtocol.Json)); command.Parameters.AddWithValue("$id", turn.Id); command.ExecuteNonQuery();
        }
        public string Seed(ExperimentConversation chat, int count)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO turns(id,conversation_id,sequence,request_key,body_json,user_characters,assistant_characters,user_hash,assistant_hash)
                  VALUES($id,$chat,$sequence,$id,$body,$uc,$ac,$uh,$ah);
                UPDATE turns SET metadata_version=1 WHERE id=$id;
                """;
            foreach (var name in new[] { "$id", "$chat", "$sequence", "$body", "$uc", "$ac", "$uh", "$ah" }) command.Parameters.AddWithValue(name, "");
            var first = "";
            for (var sequence = 1; sequence <= count; sequence++)
            {
                var id = Guid.NewGuid().ToString("N"); if (sequence == 1) first = id;
                var user = sequence == 1 ? "needle-early-episodic " + new string('a', 240) : "canonical user " + sequence + new string('u', 240);
                var answer = "canonical answer " + sequence + new string('r', 240);
                var turn = new ExperimentTurn(id, chat.Id, sequence, user, answer, "completed", chat.CreatedAt.AddSeconds(sequence), chat.CreatedAt.AddSeconds(sequence),
                    chat.SettingsRevision, chat.Settings, "transcript-only");
                command.Parameters["$id"].Value = id; command.Parameters["$chat"].Value = chat.Id; command.Parameters["$sequence"].Value = sequence;
                command.Parameters["$body"].Value = JsonSerializer.Serialize(turn, HostProtocol.Json); command.Parameters["$uc"].Value = user.Length;
                command.Parameters["$ac"].Value = answer.Length; command.Parameters["$uh"].Value = Hash(user); command.Parameters["$ah"].Value = Hash(answer);
                command.ExecuteNonQuery();
            }
            transaction.Commit(); return first;
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
