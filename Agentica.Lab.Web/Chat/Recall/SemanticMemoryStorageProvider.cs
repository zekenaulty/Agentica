using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Memory;
using Agentica.Observations;
using Microsoft.Data.Sqlite;

namespace Agentica.Lab.Web.Chat.Recall;

/// <summary>Lab-owned disposable vector projections; canonical conversation text stays in ExperimentStore.</summary>
public sealed class SemanticMemoryStorageProvider : IMemoryProjectionReader,
    IMemoryStorageProvider<SemanticMemoryStorageExport>
{
    public const int MaximumExportBytes = 16 * 1024 * 1024;
    private const int MaximumReadBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly ExperimentStore _canonical;
    private readonly object _gate = new();
    private readonly SemanticMemoryStorageMapper _mapper = new();

    public SemanticMemoryStorageProvider(string chatStorageDirectory, ExperimentStore canonicalStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatStorageDirectory);
        _canonical = canonicalStore ?? throw new ArgumentNullException(nameof(canonicalStore));
        Directory.CreateDirectory(chatStorageDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetFullPath(chatStorageDirectory), "semantic-memory.sqlite3"),
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        using var connection = Open();
        using var version = Command(connection, "PRAGMA user_version");
        var current = Convert.ToInt64(version.ExecuteScalar());
        if (current is not (0 or 1)) throw new InvalidDataException("Unsupported semantic memory database version.");
        if (current == 1) return;
        using var existing = Command(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
        if (Convert.ToInt64(existing.ExecuteScalar()) != 0)
            throw new InvalidDataException("An unversioned nonempty database is not a semantic memory store.");
        using var transaction = connection.BeginTransaction();
        using var create = Command(connection, """
            CREATE TABLE semantic_memory_exports(
              export_id TEXT PRIMARY KEY, scope_id TEXT NOT NULL, perspective TEXT NOT NULL,
              source_kind TEXT NOT NULL, profile_id TEXT NOT NULL, as_of_sequence INTEGER NOT NULL,
              as_of_ticks INTEGER NOT NULL, model_hash TEXT NOT NULL, body_json TEXT NOT NULL);
            CREATE TABLE semantic_memory_projection_links(
              export_id TEXT NOT NULL REFERENCES semantic_memory_exports(export_id), projection_id TEXT NOT NULL,
              ordinal INTEGER NOT NULL, scope_id TEXT NOT NULL, perspective TEXT NOT NULL, source_kind TEXT NOT NULL,
              profile_id TEXT NOT NULL, reference_kind TEXT NOT NULL, reference_id TEXT NOT NULL,
              source_revision TEXT NOT NULL, source_hash TEXT NOT NULL, source_sequence INTEGER NOT NULL,
              source_ticks INTEGER NOT NULL, retired INTEGER NOT NULL DEFAULT 0 CHECK(retired IN(0,1)),
              PRIMARY KEY(export_id,projection_id));
            CREATE INDEX semantic_memory_projection_cut ON semantic_memory_projection_links(
              scope_id,perspective,source_kind,profile_id,retired,source_sequence,source_ticks);
            CREATE TABLE semantic_memory_source_heads(
              scope_id TEXT NOT NULL,perspective TEXT NOT NULL,source_kind TEXT NOT NULL,profile_id TEXT NOT NULL,
              reference_kind TEXT NOT NULL,reference_id TEXT NOT NULL,source_revision TEXT NOT NULL,
              source_hash TEXT NOT NULL,as_of_sequence INTEGER NOT NULL,as_of_ticks INTEGER NOT NULL,
              PRIMARY KEY(scope_id,perspective,source_kind,profile_id,reference_kind,reference_id));
            PRAGMA user_version=1;
            """);
        create.Transaction = transaction; create.ExecuteNonQuery(); transaction.Commit();
    }

    public SemanticMemoryBinding Bind(MemoryScope scope, long asOfSequence, DateTimeOffset asOfAt) =>
        new(this, _canonical, scope, asOfSequence, asOfAt);

    public bool Write(MemoryIndexExport export, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(export);
        return Write(export.ExportId, _mapper.Map(export), cancellationToken);
    }

    public Task<MemoryStorageAcknowledgement> WriteAsync(string exportId, SemanticMemoryStorageExport model,
        CancellationToken cancellationToken = default)
    {
        Write(exportId, model, cancellationToken);
        return Task.FromResult(new MemoryStorageAcknowledgement(exportId, "semantic-memory.sqlite3#export/" + exportId));
    }

    private bool Write(string exportId, SemanticMemoryStorageExport model, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var export = _mapper.ToCore(model); // Snapshot and validate every caller-owned value before opening a transaction.
        if (exportId != export.ExportId) throw new InvalidDataException("The write identity differs from its mapped export.");
        var frozen = _mapper.Map(export);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frozen, Json);
        if (bytes.Length > MaximumExportBytes) throw new InvalidDataException("Semantic memory export exceeds its storage byte limit.");
        var body = Encoding.UTF8.GetString(bytes); var hash = Hash(bytes);
        lock (_gate)
        {
            using var connection = Open(); using var transaction = connection.BeginTransaction();
            using (var prior = Command(connection, "SELECT model_hash,body_json FROM semantic_memory_exports WHERE export_id=$id", ("$id", exportId)))
            {
                prior.Transaction = transaction; using var reader = prior.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetString(0) != hash || reader.GetString(1) != body)
                        throw new InvalidDataException("A retained export identity has conflicting storage content.");
                    return false; // Exact replay never resurrects retired source links.
                }
            }
            token.ThrowIfCancellationRequested();
            using (var insert = Command(connection, """
                INSERT INTO semantic_memory_exports VALUES($id,$scope,$perspective,$kind,$profile,$seq,$at,$hash,$body)
                """, ("$id", exportId), ("$scope", export.Scope.ScopeId), ("$perspective", export.Perspective.ToString()),
                ("$kind", export.SourceKind.ToString()), ("$profile", export.Profile.ProfileId), ("$seq", export.AsOfSequence),
                ("$at", export.AsOfAt.UtcTicks), ("$hash", hash), ("$body", body)))
            { insert.Transaction = transaction; insert.ExecuteNonQuery(); }
            var active = new Dictionary<EvidenceRef, bool>();
            foreach (var source in export.Projections.GroupBy(item => item.Source))
            {
                token.ThrowIfCancellationRequested();
                active.Add(source.Key, AdmitSource(connection, transaction, export, source.First()));
            }
            for (var ordinal = 0; ordinal < export.Projections.Count; ordinal++)
            {
                token.ThrowIfCancellationRequested(); var item = export.Projections[ordinal];
                using var insert = Command(connection, """
                    INSERT INTO semantic_memory_projection_links VALUES(
                      $export,$projection,$ordinal,$scope,$perspective,$kind,$profile,$referenceKind,$referenceId,
                      $revision,$hash,$sequence,$ticks,$retired)
                    """, ("$export", exportId), ("$projection", item.ProjectionId), ("$ordinal", ordinal),
                    ("$scope", export.Scope.ScopeId), ("$perspective", export.Perspective.ToString()),
                    ("$kind", export.SourceKind.ToString()), ("$profile", export.Profile.ProfileId),
                    ("$referenceKind", item.Source.Kind), ("$referenceId", item.Source.RefId),
                    ("$revision", item.SourceRevision), ("$hash", item.SourceContentHash), ("$sequence", item.Sequence),
                    ("$ticks", item.CreatedAt.UtcTicks), ("$retired", active[item.Source] ? 0 : 1));
                insert.Transaction = transaction; insert.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
        }
    }

    private static bool AdmitSource(SqliteConnection connection, SqliteTransaction transaction,
        MemoryIndexExport export, MemoryProjection source)
    {
        var values = SourceValues(export.Scope, export.Perspective, export.SourceKind, export.Profile.ProfileId, source.Source);
        long sequence = -1, ticks = -1; string? revision = null, hash = null;
        using (var prior = Command(connection, "SELECT source_revision,source_hash,as_of_sequence,as_of_ticks FROM semantic_memory_source_heads WHERE " + SourceWhere, values))
        {
            prior.Transaction = transaction; using var reader = prior.ExecuteReader();
            if (reader.Read()) { revision = reader.GetString(0); hash = reader.GetString(1); sequence = reader.GetInt64(2); ticks = reader.GetInt64(3); }
        }
        var comparison = export.AsOfSequence.CompareTo(sequence);
        if (comparison == 0) comparison = export.AsOfAt.UtcTicks.CompareTo(ticks);
        var sameSource = revision == source.SourceRevision && hash == source.SourceContentHash;
        if (revision is not null && !sameSource && comparison == 0)
            throw new InvalidDataException("Conflicting source revisions share the same indexing cut.");
        if (revision is not null && !sameSource && comparison < 0) return false;
        if (revision is not null && !sameSource)
        {
            using var retire = Command(connection, "UPDATE semantic_memory_projection_links SET retired=1 WHERE " + SourceWhere, values);
            retire.Transaction = transaction; retire.ExecuteNonQuery();
        }
        if (revision is null || comparison >= 0)
        {
            using var save = Command(connection, """
                INSERT INTO semantic_memory_source_heads VALUES($scope,$perspective,$kind,$profile,$referenceKind,$referenceId,$revision,$hash,$sequence,$ticks)
                ON CONFLICT(scope_id,perspective,source_kind,profile_id,reference_kind,reference_id) DO UPDATE SET
                  source_revision=excluded.source_revision,source_hash=excluded.source_hash,
                  as_of_sequence=excluded.as_of_sequence,as_of_ticks=excluded.as_of_ticks
                """, values.Concat(new (string, object?)[] { ("$revision", source.SourceRevision), ("$hash", source.SourceContentHash),
                    ("$sequence", export.AsOfSequence), ("$ticks", export.AsOfAt.UtcTicks) }).ToArray());
            save.Transaction = transaction; save.ExecuteNonQuery();
        }
        return true;
    }

    public Task<MemoryProjectionPage> ReadAsync(MemoryProjectionQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(Read(query, null, null, null, cancellationToken));

    public bool HasCurrentSource(MemoryProjectionQuery cut, EvidenceRef source, string sourceRevision,
        string sourceContentHash, int sourceCharacters, CancellationToken cancellationToken = default)
    {
        if (sourceCharacters is < 1 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(sourceCharacters));
        ArgumentNullException.ThrowIfNull(source);
        var page = Read(cut with { Limit = 512 }, source, sourceRevision, sourceContentHash, cancellationToken);
        var end = 0;
        foreach (var projection in page.Items.Select(item => item.Projection).OrderBy(item => item.StartUtf16))
        {
            if (projection.StartUtf16 > end || (long)projection.StartUtf16 + projection.LengthUtf16 > sourceCharacters) return false;
            end = Math.Max(end, projection.StartUtf16 + projection.LengthUtf16);
        }
        return end == sourceCharacters;
    }

    private MemoryProjectionPage Read(MemoryProjectionQuery query, EvidenceRef? source, string? revision,
        string? sourceHash, CancellationToken token)
    {
        ValidateQuery(query); token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var connection = Open();
            using var command = Command(connection, """
                WITH selected AS (
                  SELECT p.export_id,p.projection_id,p.ordinal,p.source_sequence,p.source_ticks,
                    ROW_NUMBER() OVER(PARTITION BY p.projection_id ORDER BY e.as_of_sequence DESC,e.as_of_ticks DESC,e.export_id) choice
                  FROM semantic_memory_projection_links p JOIN semantic_memory_exports e ON e.export_id=p.export_id
                  WHERE p.scope_id=$scope AND p.perspective=$perspective AND p.source_kind=$kind AND p.profile_id=$profile
                    AND p.retired=0 AND p.source_sequence<=$sequence AND p.source_ticks<=$ticks
                    AND e.as_of_sequence<=$sequence AND e.as_of_ticks<=$ticks
                    AND ($referenceId IS NULL OR (p.reference_kind=$referenceKind AND p.reference_id=$referenceId
                      AND p.source_revision=$revision AND p.source_hash=$hash)))
                SELECT s.export_id,s.projection_id,s.ordinal,e.model_hash,length(e.body_json),e.body_json
                FROM selected s JOIN semantic_memory_exports e ON e.export_id=s.export_id WHERE s.choice=1
                ORDER BY s.source_sequence DESC,s.source_ticks DESC,s.projection_id LIMIT $limit
                """, ("$scope", query.Scope.ScopeId), ("$perspective", query.Perspective.ToString()),
                ("$kind", query.SourceKind.ToString()), ("$profile", query.ProfileId), ("$sequence", query.AsOfSequence),
                ("$ticks", query.AsOfAt.UtcTicks), ("$referenceKind", source?.Kind), ("$referenceId", source?.RefId),
                ("$revision", revision), ("$hash", sourceHash), ("$limit", query.Limit + 1));
            using var reader = command.ExecuteReader();
            var loaded = new Dictionary<string, MemoryIndexExport>(StringComparer.Ordinal);
            var items = new List<MemoryLinkedProjection>(); var complete = true; var bytesRead = 0;
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (items.Count >= query.Limit) { complete = false; break; }
                var exportId = reader.GetString(0);
                if (!loaded.TryGetValue(exportId, out var export))
                {
                    if (reader.GetInt64(4) > MaximumExportBytes) throw new InvalidDataException("Oversized stored semantic memory export.");
                    var body = reader.GetString(5); var bytes = Encoding.UTF8.GetBytes(body);
                    if (bytes.Length > MaximumExportBytes || Hash(bytes) != reader.GetString(3))
                        throw new InvalidDataException("Stored semantic memory bytes do not match their digest.");
                    if ((long)bytesRead + bytes.Length > MaximumReadBytes) { complete = false; break; }
                    bytesRead += bytes.Length;
                    try
                    {
                        export = _mapper.ToCore(JsonSerializer.Deserialize<SemanticMemoryStorageExport>(bytes, Json)
                            ?? throw new InvalidDataException("Missing semantic memory storage model."));
                    }
                    catch (Exception exception) when (exception is ArgumentException or JsonException)
                    { throw new InvalidDataException("Invalid stored semantic memory model.", exception); }
                    if (export.ExportId != exportId || export.Scope != query.Scope || export.Perspective != query.Perspective ||
                        export.SourceKind != query.SourceKind || export.Profile.ProfileId != query.ProfileId ||
                        export.AsOfSequence > query.AsOfSequence || export.AsOfAt > query.AsOfAt)
                        throw new InvalidDataException("Stored semantic export crossed its exact query binding.");
                    loaded.Add(exportId, export); complete &= export.Coverage.IsComplete;
                }
                var ordinal = reader.GetInt32(2);
                if (ordinal < 0 || ordinal >= export.Projections.Count || export.Projections[ordinal].ProjectionId != reader.GetString(1))
                    throw new InvalidDataException("Stored projection link does not resolve in its original export.");
                var projection = export.Projections[ordinal];
                if (projection.Sequence > query.AsOfSequence || projection.CreatedAt > query.AsOfAt ||
                    source is not null && (projection.Source != source || projection.SourceRevision != revision || projection.SourceContentHash != sourceHash))
                    throw new InvalidDataException("Stored source link crossed its query binding.");
                items.Add(new(exportId, projection));
            }
            return new(Array.AsReadOnly(items.ToArray()), complete);
        }
    }

    internal static void ValidateQuery(MemoryProjectionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(query.Scope);
        if (!Enum.IsDefined(query.Perspective) || !Enum.IsDefined(query.SourceKind) || query.AsOfSequence < 0 ||
            query.Limit is < 1 or > 512 || string.IsNullOrWhiteSpace(query.ProfileId) || query.ProfileId.Length > 256 ||
            query.ProfileId != query.ProfileId.Trim() || query.ProfileId.Any(char.IsControl))
            throw new ArgumentException("Semantic memory queries require bounded exact scope, profile and cut.", nameof(query));
    }

    private const string SourceWhere = "scope_id=$scope AND perspective=$perspective AND source_kind=$kind AND profile_id=$profile AND reference_kind=$referenceKind AND reference_id=$referenceId";
    private static (string, object?)[] SourceValues(MemoryScope scope, MemoryPerspective perspective,
        MemorySourceKind kind, string profileId, EvidenceRef source) =>
        [("$scope", scope.ScopeId), ("$perspective", perspective.ToString()), ("$kind", kind.ToString()),
            ("$profile", profileId), ("$referenceKind", source.Kind), ("$referenceId", source.RefId)];
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string, object?)[] values)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
}
