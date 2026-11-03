using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Agentica.Lab.Web.Chat.Recall;
using Microsoft.Data.Sqlite;

namespace Agentica.Lab.Web.Chat;

public sealed partial class ExperimentStore
{
    internal const string HistoryIndexVersion = "local-lexical-v1-u16-2048-overlap512";
    public bool FtsAvailable { get; private set; }

    // Store-owned canonical writes purge both disposable plaintext projections in
    // their transaction. Raw SQL maintenance is outside that API: its trigger
    // excludes stale normal segments immediately, then explicit search/rebuild
    // reconciles orphan FTS rows. The optional FTS table must never make a
    // canonical save impossible when its module/table is missing or damaged.
    private void PurgeTurnProjection(SqliteConnection connection, SqliteTransaction transaction, string conversationId, string turnId)
    {
        var degraded = false;
        if (FtsAvailable)
        {
            try
            {
                using var fts = Command(connection,
                    "DELETE FROM conversation_segments_fts WHERE conversation_id=$id AND segment_id LIKE $prefix",
                    ("$id", conversationId), ("$prefix", turnId + ":%"));
                fts.Transaction = transaction; fts.ExecuteNonQuery();
            }
            catch (SqliteException) { FtsAvailable = false; degraded = true; }
        }
        using (var segments = Command(connection, "DELETE FROM conversation_segments WHERE conversation_id=$id AND turn_id=$turn",
            ("$id", conversationId), ("$turn", turnId)))
        { segments.Transaction = transaction; segments.ExecuteNonQuery(); }
        using var state = Command(connection,
            "UPDATE conversation_index_state SET status='PARTIAL',error=COALESCE($error,error) WHERE conversation_id=$id",
            ("$id", conversationId), ("$error", degraded ? "history.fts_unavailable" : null));
        state.Transaction = transaction; state.ExecuteNonQuery();
    }

    private void InitializeHistorySchema(SqliteConnection connection)
    {
        using var version = Command(connection, "PRAGMA user_version;");
        if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) > 2)
            throw new InvalidDataException("This chat database requires a newer Lab version.");
        using (var wal = Command(connection, "PRAGMA journal_mode=WAL;")) wal.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        void Execute(string sql)
        {
            using var command = Command(connection, sql);
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        Execute("""
            CREATE TABLE IF NOT EXISTS conversations(id TEXT PRIMARY KEY,body_json TEXT NOT NULL,updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS turns(id TEXT PRIMARY KEY,conversation_id TEXT NOT NULL REFERENCES conversations(id),
              sequence INTEGER NOT NULL,request_key TEXT NOT NULL,body_json TEXT NOT NULL,
              UNIQUE(conversation_id,sequence),UNIQUE(conversation_id,request_key));
            CREATE TABLE IF NOT EXISTS trace_events(turn_id TEXT NOT NULL REFERENCES turns(id),sequence INTEGER NOT NULL,
              body_json TEXT NOT NULL,payload_json TEXT,PRIMARY KEY(turn_id,sequence));
            """);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        using (var inspect = Command(connection, "PRAGMA table_info(turns);"))
        {
            inspect.Transaction = transaction;
            using var reader = inspect.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        foreach (var (name, declaration) in new[]
        {
            ("status", "TEXT NOT NULL DEFAULT ''"), ("created_at", "TEXT NOT NULL DEFAULT ''"),
            ("finished_at", "TEXT"), ("user_characters", "INTEGER NOT NULL DEFAULT 0"),
            ("assistant_characters", "INTEGER NOT NULL DEFAULT 0"), ("user_hash", "TEXT NOT NULL DEFAULT ''"),
            ("assistant_hash", "TEXT NOT NULL DEFAULT ''"), ("metadata_version", "INTEGER NOT NULL DEFAULT 0"),
            ("source_revision", "INTEGER NOT NULL DEFAULT 0")
        }) if (!columns.Contains(name)) Execute($"ALTER TABLE turns ADD COLUMN {name} {declaration};");
        Execute("""
            CREATE INDEX IF NOT EXISTS turns_conversation_status_sequence ON turns(conversation_id,status,sequence);
            CREATE TABLE IF NOT EXISTS conversation_index_state(
              conversation_id TEXT PRIMARY KEY REFERENCES conversations(id) ON DELETE CASCADE,
              version TEXT NOT NULL,status TEXT NOT NULL,last_indexed_sequence INTEGER NOT NULL DEFAULT 0,
              updated_at TEXT NOT NULL,error TEXT);
            CREATE TABLE IF NOT EXISTS conversation_segments(
              rowid INTEGER PRIMARY KEY,conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
              turn_id TEXT NOT NULL REFERENCES turns(id) ON DELETE CASCADE,segment_id TEXT NOT NULL,
              source_id TEXT NOT NULL,turn_sequence INTEGER NOT NULL,role TEXT NOT NULL,content_hash TEXT NOT NULL,
              start_offset INTEGER NOT NULL,end_offset INTEGER NOT NULL,text TEXT NOT NULL,
              UNIQUE(conversation_id,segment_id));
            CREATE INDEX IF NOT EXISTS conversation_segments_source ON conversation_segments(conversation_id,source_id,content_hash);
            CREATE INDEX IF NOT EXISTS conversation_segments_sequence ON conversation_segments(conversation_id,turn_sequence);
            CREATE TRIGGER IF NOT EXISTS turns_history_insert AFTER INSERT ON turns BEGIN
              UPDATE turns SET status=json_extract(NEW.body_json,'$.status'),created_at=json_extract(NEW.body_json,'$.createdAt'),
                finished_at=json_extract(NEW.body_json,'$.finishedAt'),metadata_version=0,source_revision=1 WHERE id=NEW.id;
            END;
            CREATE TRIGGER IF NOT EXISTS turns_history_changed AFTER UPDATE OF body_json ON turns
            WHEN json_extract(OLD.body_json,'$.userText') IS NOT json_extract(NEW.body_json,'$.userText')
              OR json_extract(OLD.body_json,'$.assistantText') IS NOT json_extract(NEW.body_json,'$.assistantText')
              OR json_extract(OLD.body_json,'$.status') IS NOT json_extract(NEW.body_json,'$.status')
              OR json_extract(OLD.body_json,'$.createdAt') IS NOT json_extract(NEW.body_json,'$.createdAt')
              OR json_extract(OLD.body_json,'$.finishedAt') IS NOT json_extract(NEW.body_json,'$.finishedAt')
            BEGIN
              UPDATE turns SET status=json_extract(NEW.body_json,'$.status'),created_at=json_extract(NEW.body_json,'$.createdAt'),
                finished_at=json_extract(NEW.body_json,'$.finishedAt'),metadata_version=0,source_revision=source_revision+1 WHERE id=NEW.id;
              DELETE FROM conversation_segments WHERE turn_id=NEW.id;
              UPDATE conversation_index_state SET status='PARTIAL' WHERE conversation_id=NEW.conversation_id;
            END;
            CREATE TRIGGER IF NOT EXISTS turns_history_deleted AFTER DELETE ON turns BEGIN
              UPDATE conversation_index_state SET status='PARTIAL' WHERE conversation_id=OLD.conversation_id;
            END;
            """);
        RefreshMetadata(connection, transaction, null);
        Execute("PRAGMA user_version=2;");
        transaction.Commit();
        // Projection availability cannot prevent canonical storage from opening.
        try
        {
            using var fts = Command(connection, """
                CREATE VIRTUAL TABLE IF NOT EXISTS conversation_segments_fts USING fts5(
                  segment_id UNINDEXED,conversation_id UNINDEXED,text,tokenize='unicode61 remove_diacritics 2');
                DROP TRIGGER IF EXISTS conversation_segments_fts_insert;
                DROP TRIGGER IF EXISTS conversation_segments_fts_delete;
                """);
            fts.ExecuteNonQuery();
            using var probe = Command(connection, "SELECT COUNT(*) FROM conversation_segments_fts WHERE conversation_segments_fts MATCH 'availabilityprobe';");
            _ = probe.ExecuteScalar();
            FtsAvailable = true;
        }
        catch (SqliteException) { FtsAvailable = false; }
    }

    private static string ContentHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void RefreshMetadata(SqliteConnection connection, SqliteTransaction? transaction, string? conversationId, string? turnId = null)
    {
        while (true)
        {
            var rows = new List<ExperimentTurn>();
            using (var select = Command(connection, "SELECT body_json FROM turns WHERE metadata_version=0 AND ($scope IS NULL OR conversation_id=$scope) AND ($turn IS NULL OR id=$turn) LIMIT 100", ("$scope", conversationId), ("$turn", turnId)))
            {
                select.Transaction = transaction;
                using var reader = select.ExecuteReader();
                while (reader.Read()) rows.Add(Deserialize<ExperimentTurn>(reader.GetString(0)));
            }
            if (rows.Count == 0) return;
            foreach (var turn in rows)
            {
                using var update = Command(connection, """
                    UPDATE turns SET status=$status,created_at=$created,finished_at=$finished,
                      user_characters=$users,assistant_characters=$assistants,user_hash=$uh,assistant_hash=$ah,metadata_version=1,
                      source_revision=MAX(source_revision,1) WHERE id=$id;
                    """, ("$id", turn.Id), ("$status", turn.Status), ("$created", turn.CreatedAt.ToString("O")),
                    ("$finished", turn.FinishedAt?.ToString("O")), ("$users", turn.UserText.Length),
                    ("$assistants", turn.AssistantText.Length), ("$uh", ContentHash(turn.UserText)), ("$ah", ContentHash(turn.AssistantText)));
                update.Transaction = transaction;
                update.ExecuteNonQuery();
            }
        }
    }

    private int ConversationCount()
    {
        using var connection = Open();
        using var count = Command(connection, "SELECT COUNT(*) FROM conversations");
        return Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public ExperimentConversation GetHeader(string id)
    {
        lock (_gate)
        {
            Identifier(id);
            using var connection = Open();
            using var command = Command(connection, "SELECT body_json FROM conversations WHERE id=$id", ("$id", id));
            return command.ExecuteScalar() is string body ? Deserialize<ExperimentConversation>(body) with { Turns = [], HistoryPage = null } : throw Missing();
        }
    }

    public ExperimentConversationHead GetHead(string id)
    {
        lock (_gate)
        {
            var conversation = GetHeader(id);
            using var connection = Open();
            using var command = Command(connection, """
                SELECT COUNT(*),COALESCE(MAX(sequence),0),COALESCE(SUM(status='completed'),0),
                  MAX(CASE WHEN status='running' THEN sequence END) FROM turns WHERE conversation_id=$id
                """, ("$id", id));
            using var reader = command.ExecuteReader(); reader.Read();
            return new(conversation, reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetInt32(3));
        }
    }

    public ExperimentTurnPage GetTurnsPage(string id, int? beforeSequence = null, int limit = 20)
    {
        lock (_gate)
        {
            var head = GetHead(id);
            var count = Math.Clamp(limit, 1, 100);
            using var connection = Open();
            using var command = Command(connection, "SELECT body_json FROM turns WHERE conversation_id=$id AND sequence<$before ORDER BY sequence DESC LIMIT $count",
                ("$id", id), ("$before", beforeSequence ?? head.LatestSequence + 1), ("$count", count + 1));
            using var reader = command.ExecuteReader();
            var rows = new List<ExperimentTurn>();
            while (reader.Read()) rows.Add(Deserialize<ExperimentTurn>(reader.GetString(0)));
            var more = rows.Count > count;
            var selected = rows.Take(count).Reverse().ToArray();
            return new(selected, more && selected.Length > 0 ? selected[0].Sequence : null, head.TotalTurns, head.LatestSequence);
        }
    }

    public IReadOnlyList<ExperimentTurn> GetRecentCommittedTurns(string id, int beforeSequence, int count)
    {
        lock (_gate)
        {
            _ = GetHeader(id);
            using var connection = Open();
            using var command = Command(connection, "SELECT body_json FROM turns WHERE conversation_id=$id AND sequence<$before AND status='completed' ORDER BY sequence DESC LIMIT $count",
                ("$id", id), ("$before", beforeSequence), ("$count", Math.Clamp(count, 0, 501)));
            using var reader = command.ExecuteReader();
            var result = new List<ExperimentTurn>();
            while (reader.Read()) result.Add(Deserialize<ExperimentTurn>(reader.GetString(0)));
            result.Reverse(); return result;
        }
    }

    private IReadOnlyList<(string ConversationId, string TurnId)> RunningTurnIdentities()
    {
        using var connection = Open();
        using var command = Command(connection, "SELECT conversation_id,id FROM turns WHERE status='running'");
        using var reader = command.ExecuteReader();
        var result = new List<(string, string)>();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public IConversationEvidenceSource BindEvidenceSource(string conversationId, int asOfSequence)
    {
        var head = GetHead(conversationId);
        if (asOfSequence < 0 || asOfSequence > head.LatestSequence)
            throw new ExperimentException("recall.as_of", "The historical source cutoff is outside this conversation.");
        return new BoundConversationEvidenceSource(this, conversationId, asOfSequence);
    }

    internal ConversationEvidenceHead EvidenceHead(string id, int asOf)
    {
        lock (_gate)
        {
            _ = GetHeader(id);
            using var connection = Open();
            using var command = Command(connection, "SELECT COUNT(*),COALESCE(MAX(sequence),0),COALESCE(SUM(source_revision),0) FROM turns WHERE conversation_id=$id AND sequence<=$asof AND status='completed'",
                ("$id", id), ("$asof", asOf));
            using var reader = command.ExecuteReader(); reader.Read();
            var count = reader.GetInt32(0); var last = reader.GetInt32(1);
            return new(id, asOf, last, count * 2, $"{asOf}:{last}:{count}:{reader.GetInt64(2)}");
        }
    }

    internal ConversationSourcePage SourceHeaders(string id, int asOf, int? beforeOrder, int limit)
    {
        lock (_gate)
        {
            using var connection = Open(); RefreshMetadata(connection, null, id);
            var count = Math.Clamp(limit, 1, 100);
            using var command = Command(connection, """
                SELECT source_id,turn_id,sequence,role,created,hash,characters,source_order FROM (
                  SELECT id||':user' source_id,id turn_id,sequence,'user' role,created_at created,user_hash hash,user_characters characters,sequence*2 source_order
                    FROM turns WHERE conversation_id=$id AND status='completed' AND sequence<=$asof
                  UNION ALL
                  SELECT id||':assistant',id,sequence,'assistant',COALESCE(finished_at,created_at),assistant_hash,assistant_characters,sequence*2+1
                    FROM turns WHERE conversation_id=$id AND status='completed' AND sequence<=$asof)
                WHERE source_order<$before ORDER BY source_order DESC LIMIT $limit;
                """, ("$id", id), ("$asof", asOf), ("$before", beforeOrder ?? int.MaxValue), ("$limit", count + 1));
            using var reader = command.ExecuteReader();
            var rows = new List<ConversationSourceHeader>();
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7)));
            var selected = rows.Take(count).Reverse().ToArray();
            return new(selected, rows.Count > count && selected.Length > 0 ? selected[0].Order : null, EvidenceHead(id, asOf).EligibleSourceCount, asOf);
        }
    }

    internal IReadOnlyList<ConversationSourceHeader> SourceHeadersAfter(string id, int asOf, int afterOrder, int limit)
    {
        lock (_gate)
        {
            if (limit <= 0) return [];
            using var connection = Open(); RefreshMetadata(connection, null, id);
            using var command = Command(connection, """
                SELECT source_id,turn_id,sequence,role,created,hash,characters,source_order FROM (
                  SELECT id||':user' source_id,id turn_id,sequence,'user' role,created_at created,user_hash hash,user_characters characters,sequence*2 source_order
                    FROM turns WHERE conversation_id=$id AND status='completed' AND sequence<=$asof
                  UNION ALL
                  SELECT id||':assistant',id,sequence,'assistant',COALESCE(finished_at,created_at),assistant_hash,assistant_characters,sequence*2+1
                    FROM turns WHERE conversation_id=$id AND status='completed' AND sequence<=$asof)
                WHERE source_order>$after ORDER BY source_order LIMIT $limit;
                """, ("$id", id), ("$asof", asOf), ("$after", afterOrder), ("$limit", Math.Clamp(limit, 1, 2)));
            using var reader = command.ExecuteReader();
            var result = new List<ConversationSourceHeader>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7)));
            return result;
        }
    }

    internal (ConversationSourceHeader Header, string Content)? CanonicalSource(string id, int asOf, string sourceId)
    {
        lock (_gate)
        {
            var parts = sourceId.Split(':');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _) || parts[1] is not ("user" or "assistant")) return null;
            using var connection = Open();
            using var command = Command(connection, "SELECT body_json FROM turns WHERE conversation_id=$scope AND id=$id AND sequence<=$asof",
                ("$scope", id), ("$id", parts[0]), ("$asof", asOf));
            if (command.ExecuteScalar() is not string body) return null;
            var turn = Deserialize<ExperimentTurn>(body);
            if (turn.Status != "completed") return null;
            var user = parts[1] == "user";
            var content = user ? turn.UserText : turn.AssistantText;
            return (new(sourceId, turn.Id, turn.Sequence, parts[1], user ? turn.CreatedAt : turn.FinishedAt ?? turn.CreatedAt,
                ContentHash(content), content.Length, turn.Sequence * 2 + (user ? 0 : 1)), content);
        }
    }

    internal ConversationEvidenceCoverage EvidenceCoverage(string id, int asOf)
    {
        lock (_gate)
        {
            var head = EvidenceHead(id, asOf);
            using var connection = Open();
            using var state = Command(connection, "SELECT version,last_indexed_sequence,error FROM conversation_index_state WHERE conversation_id=$id", ("$id", id));
            using var reader = state.ExecuteReader();
            if (!reader.Read()) return new("NOT_READY", asOf, 0, head.EligibleSourceCount, 0, FtsAvailable, HistoryIndexVersion, "No lexical projection has been requested.");
            var version = reader.GetString(0); var last = Math.Min(asOf, reader.GetInt32(1));
            var issue = reader.IsDBNull(2) ? null : reader.GetString(2);
            reader.Close();
            using var count = Command(connection, "SELECT COUNT(DISTINCT source_id) FROM conversation_segments WHERE conversation_id=$id AND turn_sequence<=$asof",
                ("$id", id), ("$asof", asOf));
            var indexed = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            var complete = version == HistoryIndexVersion && indexed == head.EligibleSourceCount;
            if (FtsAvailable)
            {
                try
                {
                    using var counts = Command(connection, """
                        SELECT (SELECT COUNT(*) FROM conversation_segments WHERE conversation_id=$id AND turn_sequence<=$asof),
                          (SELECT COUNT(*) FROM conversation_segments_fts f JOIN conversation_segments s ON s.rowid=f.rowid WHERE s.conversation_id=$id AND s.turn_sequence<=$asof)
                        """, ("$id", id), ("$asof", asOf));
                    using var pair = counts.ExecuteReader(); pair.Read();
                    complete &= pair.GetInt64(0) == pair.GetInt64(1);
                }
                catch (SqliteException) { complete = false; }
            }
            return new(complete ? "COMPLETE" : "PARTIAL", asOf, last, head.EligibleSourceCount, indexed, FtsAvailable, HistoryIndexVersion,
                issue ?? (complete ? null : "The disposable lexical projection does not cover every eligible source at this cutoff."));
        }
    }

    public ConversationIndexProgress GetIndexProgress(string id)
    {
        var head = GetHead(id); var coverage = EvidenceCoverage(id, head.LatestSequence);
        return new(id, coverage.Status, coverage.IndexedSourceCount, coverage.EligibleSourceCount,
            coverage.LastIndexedSequence, coverage.FtsAvailable, HistoryIndexVersion, coverage.Reason);
    }

    public ConversationIndexProgress RebuildIndex(string id, CancellationToken cancellationToken = default)
    {
        _ = GetHeader(id);
        var asOf = GetHead(id).LatestSequence;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { CatchUpIndex(id, asOf, 0, cancellationToken); }
            catch (SqliteException) { FtsAvailable = false; }
            int? cursor = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = SourceHeaders(id, asOf, cursor, 100);
                foreach (var header in page.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_gate)
                        if (!ProjectionMatchesCanonical(id, asOf, header.SourceId)) ProjectSource(id, asOf, header.SourceId);
                }
                cursor = page.NextBeforeOrder;
            } while (cursor is not null);
            var coverage = EvidenceCoverage(id, asOf);
            SetIndexStatus(id, coverage.Status, coverage.Reason);
            return GetIndexProgress(id);
        }
        catch (OperationCanceledException)
        {
            SetIndexStatus(id, "PARTIAL", "Rebuild cancelled; completed source projections remain reusable.");
            throw;
        }
    }

    private bool ProjectionMatchesCanonical(string id, int asOf, string sourceId)
    {
        var canonical = CanonicalSource(id, asOf, sourceId);
        if (canonical is null) return false;
        using var connection = Open();
        using var command = Command(connection, "SELECT rowid,segment_id,content_hash,start_offset,end_offset,text FROM conversation_segments WHERE conversation_id=$id AND source_id=$source ORDER BY start_offset",
            ("$id", id), ("$source", sourceId));
        using var reader = command.ExecuteReader();
        var expectedStart = 0; var completed = false;
        while (reader.Read())
        {
            if (completed) return false;
            var expectedEnd = Math.Min(canonical.Value.Content.Length, expectedStart + 2048);
            if (expectedEnd < canonical.Value.Content.Length && expectedEnd > expectedStart && char.IsHighSurrogate(canonical.Value.Content[expectedEnd - 1])) expectedEnd--;
            var text = canonical.Value.Content[expectedStart..expectedEnd];
            if (reader.GetString(2) != canonical.Value.Header.ContentHash || reader.GetInt32(3) != expectedStart ||
                reader.GetInt32(4) != expectedEnd || reader.GetString(5) != text) return false;
            if (FtsAvailable)
            {
                try
                {
                    using var fts = Command(connection, "SELECT COUNT(*) FROM conversation_segments_fts WHERE rowid=$row AND conversation_id=$id AND segment_id=$segment AND text=$text",
                        ("$row", reader.GetInt64(0)), ("$id", id), ("$segment", reader.GetString(1)), ("$text", text));
                    if (Convert.ToInt32(fts.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) return false;
                }
                catch (SqliteException) { FtsAvailable = false; return false; }
            }
            completed = expectedEnd == canonical.Value.Content.Length;
            expectedStart = expectedEnd - 512;
            if (!completed && expectedStart > 0 && char.IsLowSurrogate(canonical.Value.Content[expectedStart])) expectedStart--;
        }
        return completed;
    }

    public void DeleteIndex(string id)
    {
        lock (_gate)
        {
            _ = GetHeader(id);
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            if (FtsAvailable)
                try
                {
                    using var fts = Command(connection, "DELETE FROM conversation_segments_fts WHERE conversation_id=$id", ("$id", id));
                    fts.Transaction = transaction; fts.ExecuteNonQuery();
                }
                catch (SqliteException) { FtsAvailable = false; }
            using var segments = Command(connection, "DELETE FROM conversation_segments WHERE conversation_id=$id", ("$id", id));
            segments.Transaction = transaction; segments.ExecuteNonQuery();
            using var state = Command(connection, "DELETE FROM conversation_index_state WHERE conversation_id=$id", ("$id", id));
            state.Transaction = transaction; state.ExecuteNonQuery(); transaction.Commit();
        }
    }

    private void SetIndexStatus(string id, string status, string? error)
    {
        using var connection = Open();
        using var command = Command(connection, "UPDATE conversation_index_state SET status=$status,error=$error,updated_at=$now WHERE conversation_id=$id",
            ("$id", id), ("$status", status), ("$error", error), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.ExecuteNonQuery();
    }

    internal int CatchUpIndex(string id, int asOf, int maximumSources, CancellationToken cancellationToken)
    {
        List<string> sources;
        var repairedFts = 0;
        lock (_gate)
        {
            using var connection = Open(); RefreshMetadata(connection, null, id);
            using (var previous = Command(connection, "SELECT version FROM conversation_index_state WHERE conversation_id=$id", ("$id", id)))
                if (previous.ExecuteScalar() is string profile && profile != HistoryIndexVersion) DeleteIndex(id);
            using (var state = Command(connection, """
                INSERT INTO conversation_index_state(conversation_id,version,status,updated_at) VALUES($id,$version,'PARTIAL',$now)
                ON CONFLICT(conversation_id) DO UPDATE SET version=$version,updated_at=$now;
                """, ("$id", id), ("$version", HistoryIndexVersion), ("$now", DateTimeOffset.UtcNow.ToString("O")))) state.ExecuteNonQuery();
            using var command = Command(connection, """
                SELECT candidates.source_id FROM (
                  SELECT id||':user' source_id,user_hash hash,sequence FROM turns WHERE conversation_id=$id AND status='completed' AND sequence<=$asof
                  UNION ALL SELECT id||':assistant',assistant_hash,sequence FROM turns WHERE conversation_id=$id AND status='completed' AND sequence<=$asof) candidates
                WHERE NOT EXISTS(SELECT 1 FROM conversation_segments s WHERE s.conversation_id=$id AND s.source_id=candidates.source_id AND s.content_hash=candidates.hash)
                ORDER BY sequence,source_id LIMIT $limit;
                """, ("$id", id), ("$asof", asOf), ("$limit", maximumSources));
            using var reader = command.ExecuteReader(); sources = [];
            while (reader.Read()) sources.Add(reader.GetString(0));
            reader.Close();
            if (FtsAvailable)
            {
                using (var stale = Command(connection, "DELETE FROM conversation_segments_fts WHERE conversation_id=$id AND rowid NOT IN(SELECT rowid FROM conversation_segments WHERE conversation_id=$id)", ("$id", id)))
                    stale.ExecuteNonQuery();
                using var repair = Command(connection, """
                    INSERT INTO conversation_segments_fts(rowid,segment_id,conversation_id,text)
                    SELECT s.rowid,s.segment_id,s.conversation_id,s.text FROM conversation_segments s
                    LEFT JOIN conversation_segments_fts f ON f.rowid=s.rowid
                    WHERE s.conversation_id=$id AND s.turn_sequence<=$asof AND f.rowid IS NULL LIMIT $limit;
                    """, ("$id", id), ("$asof", asOf), ("$limit", maximumSources));
                repairedFts = repair.ExecuteNonQuery();
            }
        }
        foreach (var sourceId in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectSource(id, asOf, sourceId);
        }
        var coverage = EvidenceCoverage(id, asOf);
        SetIndexStatus(id, coverage.Status, coverage.Reason);
        return sources.Count + repairedFts;
    }

    internal void ProjectSource(string id, int asOf, string sourceId)
    {
        lock (_gate)
        {
            var source = CanonicalSource(id, asOf, sourceId);
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            if (FtsAvailable)
            {
                using var fts = Command(connection, "DELETE FROM conversation_segments_fts WHERE conversation_id=$id AND segment_id LIKE $prefix", ("$id", id), ("$prefix", sourceId + ":%"));
                fts.Transaction = transaction; fts.ExecuteNonQuery();
            }
            using (var delete = Command(connection, "DELETE FROM conversation_segments WHERE conversation_id=$id AND source_id=$source", ("$id", id), ("$source", sourceId)))
            { delete.Transaction = transaction; delete.ExecuteNonQuery(); }
            if (source is { } canonical)
            {
                var start = 0;
                do
                {
                    var end = Math.Min(canonical.Content.Length, start + 2048);
                    if (end < canonical.Content.Length && end > start && char.IsHighSurrogate(canonical.Content[end - 1])) end--;
                    using var insert = Command(connection, """
                        INSERT INTO conversation_segments(conversation_id,turn_id,segment_id,source_id,turn_sequence,role,content_hash,start_offset,end_offset,text)
                          VALUES($id,$turn,$segment,$source,$sequence,$role,$hash,$start,$end,$text);
                        """, ("$id", id), ("$turn", canonical.Header.TurnId), ("$segment", sourceId + ":" + start), ("$source", sourceId),
                        ("$sequence", canonical.Header.TurnSequence), ("$role", canonical.Header.Role), ("$hash", canonical.Header.ContentHash),
                        ("$start", start), ("$end", end), ("$text", canonical.Content[start..end]));
                    insert.Transaction = transaction; insert.ExecuteNonQuery();
                    if (FtsAvailable)
                    {
                        using var rowId = Command(connection, "SELECT last_insert_rowid();"); rowId.Transaction = transaction;
                        var value = rowId.ExecuteScalar();
                        using var fts = Command(connection, "INSERT INTO conversation_segments_fts(rowid,segment_id,conversation_id,text) VALUES($row,$segment,$scope,$text)",
                            ("$row", value), ("$segment", sourceId + ":" + start), ("$scope", id), ("$text", canonical.Content[start..end]));
                        fts.Transaction = transaction; fts.ExecuteNonQuery();
                    }
                    if (end == canonical.Content.Length) break;
                    start = end - 512;
                    if (start > 0 && char.IsLowSurrogate(canonical.Content[start])) start--;
                } while (true);
                using var update = Command(connection, "UPDATE conversation_index_state SET last_indexed_sequence=MAX(last_indexed_sequence,$sequence) WHERE conversation_id=$id",
                    ("$sequence", canonical.Header.TurnSequence), ("$id", id));
                update.Transaction = transaction; update.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    internal static IReadOnlyList<string> RecallKeywords(string query)
    {
        var result = new List<string>(); var word = new StringBuilder();
        void Add()
        {
            var token = word.ToString(); word.Clear();
            if (token.Length is >= 2 and <= 64 && !RecallStopWords.Contains(token) &&
                !result.Contains(token, StringComparer.OrdinalIgnoreCase) && result.Count < 12) result.Add(token);
        }
        foreach (var character in query)
            if (char.IsLetterOrDigit(character)) word.Append(character); else Add();
        Add(); return result;
    }

    private static readonly HashSet<string> RecallStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "been", "but", "by", "can", "could", "did", "do", "does",
        "for", "from", "had", "has", "have", "how", "i", "if", "in", "into", "is", "it", "its", "me", "my",
        "of", "on", "or", "our", "please", "that", "the", "their", "them", "there", "these", "they", "this",
        "those", "to", "us", "was", "we", "were", "what", "when", "where", "which", "who", "why", "will", "with",
        "would", "you", "your", "chose", "choose", "chosen", "remember", "recall", "tell", "about"
    };

    internal IReadOnlyList<(string SourceId, string Hash, int Start, string Text)> LexicalCandidates(string id, int asOf,
        string query, string? role, int limit, bool keywordFallback = false)
    {
        lock (_gate)
        {
            if (!FtsAvailable) return [];
            var expression = "\"" + query.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            if (keywordFallback)
            {
                var keywords = RecallKeywords(query);
                if (keywords.Count == 0) return [];
                expression = string.Join(" OR ", keywords.Select(keyword => "\"" + keyword + "\""));
            }
            using var connection = Open();
            using var command = Command(connection, """
                SELECT s.source_id,s.content_hash,s.start_offset,s.text FROM conversation_segments_fts
                JOIN conversation_segments s ON s.rowid=conversation_segments_fts.rowid
                WHERE conversation_segments_fts MATCH $query AND s.conversation_id=$id AND s.turn_sequence<=$asof
                  AND ($role IS NULL OR s.role=$role)
                ORDER BY bm25(conversation_segments_fts),s.turn_sequence,s.start_offset LIMIT $limit;
                """, ("$query", expression),
                ("$id", id), ("$asof", asOf), ("$role", role), ("$limit", limit));
            try
            {
                using var reader = command.ExecuteReader();
                var result = new List<(string, string, int, string)>();
                while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
                return result;
            }
            catch (SqliteException) { FtsAvailable = false; return []; }
        }
    }

    internal (IReadOnlyList<(string SourceId, string Hash, int Start, string Text)> Candidates, bool Complete)
        IndexedSubstringCandidates(string id, int asOf, string query, string? role, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = Command(connection, "SELECT source_id,content_hash,start_offset,text FROM conversation_segments WHERE conversation_id=$id AND turn_sequence<=$asof AND ($role IS NULL OR role=$role) ORDER BY turn_sequence,source_id,start_offset",
                ("$id", id), ("$asof", asOf), ("$role", role));
            using var reader = command.ExecuteReader();
            var matches = new List<(string, string, int, string)>(); var inspectedCharacters = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = reader.GetString(3);
                inspectedCharacters += text.Length;
                if (inspectedCharacters > 8 * 1024 * 1024 || matches.Count >= 1000) return (matches, false);
                if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
                    matches.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), text));
            }
            return (matches, true);
        }
    }
}
