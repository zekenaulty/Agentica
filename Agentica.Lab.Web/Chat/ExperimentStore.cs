using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics.CodeAnalysis;
using Agentica.Lab.Web.Contracts;
using Microsoft.Data.Sqlite;

namespace Agentica.Lab.Web.Chat;

/// <summary>Lab-owned transcript/configuration and diagnostic evidence. Never a participant memory store.</summary>
public sealed class ExperimentStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _connectionString;
    private readonly FileStream _lease;
    public string WorkspaceRoot { get; }
    private static JsonSerializerOptions Json => HostProtocol.Json;

    public ExperimentStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _lease = new FileStream(Path.Combine(directory, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            WorkspaceRoot = Path.Combine(Path.GetFullPath(directory), "workspaces");
            Directory.CreateDirectory(WorkspaceRoot);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory, "experiments.sqlite3"),
                ForeignKeys = true,
                Pooling = false
            }.ToString();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(command.ExecuteScalar()) > 1)
                throw new InvalidDataException("This chat database requires a newer Lab version.");
            command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS conversations (
                id TEXT PRIMARY KEY, body_json TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS turns (
                id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(id),
                sequence INTEGER NOT NULL, request_key TEXT NOT NULL, body_json TEXT NOT NULL,
                UNIQUE(conversation_id, sequence), UNIQUE(conversation_id, request_key));
            CREATE TABLE IF NOT EXISTS trace_events (
                turn_id TEXT NOT NULL REFERENCES turns(id), sequence INTEGER NOT NULL,
                body_json TEXT NOT NULL, payload_json TEXT,
                PRIMARY KEY(turn_id, sequence));
            PRAGMA user_version=1;
            """;
            command.ExecuteNonQuery();
            // No replay after a process died around dispatch. Preserve partial output and unknown usage.
            foreach (var conversation in List())
                foreach (var turn in Get(conversation.Id).Turns.Where(t => t.Status == "running"))
                {
                    Complete(conversation.Id, turn.Id, new("interrupted", ErrorCode: "service.restarted",
                        ErrorMessage: "The service restarted during this turn. Partial evidence is retained; provider usage may be unknown. Send a new turn deliberately."));
                    Append(conversation.Id, turn.Id, new("turn.interrupted", GetTurn(conversation.Id, turn.Id)), true);
                }
        }
        catch
        {
            _lease.Dispose();
            throw;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string, object?)[] values)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    public IReadOnlyList<ExperimentConversation> List()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = Command(connection, "SELECT body_json FROM conversations ORDER BY updated_at DESC LIMIT 1000");
            using var reader = command.ExecuteReader();
            var result = new List<ExperimentConversation>();
            while (reader.Read()) result.Add(Deserialize<ExperimentConversation>(reader.GetString(0)));
            return result;
        }
    }

    public ExperimentConversation Create(ExperimentSettings defaults, string? title)
    {
        lock (_gate)
        {
            ValidateSettings(defaults);
            if (List().Count >= 1000) throw new ExperimentException("chat.quota", "This Lab store has reached its 1000-conversation quota.", 409);
            var now = DateTimeOffset.UtcNow;
            var conversation = new ExperimentConversation(Guid.NewGuid().ToString("N"), ValidateTitle(title ?? "New experiment"), now, now, 1, defaults, []);
            using var connection = Open();
            using var command = Command(connection, "INSERT INTO conversations(id,body_json,updated_at) VALUES($id,$body,$updated)",
                ("$id", conversation.Id), ("$body", Serialize(conversation)), ("$updated", now.ToString("O")));
            command.ExecuteNonQuery();
            return conversation;
        }
    }

    public ExperimentConversation Get(string id)
    {
        lock (_gate)
        {
            Identifier(id);
            using var connection = Open();
            using var command = Command(connection, "SELECT body_json FROM conversations WHERE id=$id", ("$id", id));
            var body = command.ExecuteScalar() as string ?? throw Missing();
            var conversation = Deserialize<ExperimentConversation>(body);
            using var turns = Command(connection, "SELECT body_json FROM turns WHERE conversation_id=$id ORDER BY sequence", ("$id", id));
            using var reader = turns.ExecuteReader();
            var rows = new List<ExperimentTurn>();
            while (reader.Read()) rows.Add(Deserialize<ExperimentTurn>(reader.GetString(0)));
            return conversation with { Turns = rows };
        }
    }

    public ExperimentConversation Rename(string id, string title)
    {
        lock (_gate)
        {
            var conversation = Get(id) with { Title = ValidateTitle(title), UpdatedAt = DateTimeOffset.UtcNow };
            SaveConversation(conversation);
            return conversation;
        }
    }

    public ExperimentConversation UpdateSettings(string id, ExperimentSettingsUpdate update)
    {
        lock (_gate)
        {
            ValidateSettings(update.Settings);
            var conversation = Get(id);
            if (conversation.SettingsRevision != update.ExpectedRevision)
                throw new ExperimentException("settings.conflict", "Settings changed elsewhere. Reload before saving.", 409);
            if (conversation.Turns.Any(t => t.Status == "running"))
                throw new ExperimentException("chat.busy", "Finish or cancel the active turn before changing settings.", 409);
            conversation = conversation with { Settings = update.Settings, SettingsRevision = conversation.SettingsRevision + 1, UpdatedAt = DateTimeOffset.UtcNow };
            SaveConversation(conversation);
            return conversation;
        }
    }

    private void SaveConversation(ExperimentConversation conversation)
    {
        using var connection = Open();
        using var command = Command(connection, "UPDATE conversations SET body_json=$body,updated_at=$updated WHERE id=$id",
            ("$id", conversation.Id), ("$body", Serialize(conversation with { Turns = [] })), ("$updated", conversation.UpdatedAt.ToString("O")));
        command.ExecuteNonQuery();
    }

    public (ExperimentTurn Turn, bool Created) Begin(string id, ExperimentSendRequest request)
    {
        lock (_gate)
        {
            var conversation = Get(id);
            if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 128000)
                throw new ExperimentException("turn.content", "Supply a nonempty message of at most 128000 characters.");
            var requestKey = request.RequestId ?? Guid.NewGuid().ToString("N");
            if (requestKey.Length is < 1 or > 120 || requestKey.Any(char.IsControl))
                throw new ExperimentException("turn.request_id", "Invalid request identity.");
            using var connection = Open();
            using (var existing = Command(connection, "SELECT body_json FROM turns WHERE conversation_id=$chat AND request_key=$key", ("$chat", id), ("$key", requestKey)))
            {
                if (existing.ExecuteScalar() is string body)
                {
                    var old = Deserialize<ExperimentTurn>(body);
                    if (old.UserText != request.Content) throw new ExperimentException("turn.request_conflict", "This request identity belongs to different content.", 409);
                    return (old, false);
                }
            }
            if (conversation.Turns.Any(t => t.Status == "running"))
                throw new ExperimentException("chat.busy", "This conversation already has an active turn.", 409);
            if (conversation.Turns.Count >= 5000) throw new ExperimentException("chat.turn_quota", "This conversation has reached its 5000-turn quota.", 409);
            var turn = new ExperimentTurn(Guid.NewGuid().ToString("N"), id, conversation.Turns.Count + 1, request.Content, "", "running",
                DateTimeOffset.UtcNow, null, conversation.SettingsRevision, conversation.Settings, "transcript-only; native reasoning state is not persisted");
            using var transaction = connection.BeginTransaction();
            using var insert = Command(connection, "INSERT INTO turns(id,conversation_id,sequence,request_key,body_json) VALUES($id,$chat,$sequence,$key,$body)",
                ("$id", turn.Id), ("$chat", id), ("$sequence", turn.Sequence), ("$key", requestKey), ("$body", Serialize(turn)));
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
            using var update = Command(connection, "UPDATE conversations SET body_json=$body,updated_at=$updated WHERE id=$id",
                ("$id", id), ("$body", Serialize(conversation with { Turns = [], UpdatedAt = turn.CreatedAt })), ("$updated", turn.CreatedAt.ToString("O")));
            update.Transaction = transaction;
            update.ExecuteNonQuery();
            transaction.Commit();
            return (turn, true);
        }
    }

    public ExperimentTurn? FindRequest(string conversationId, string requestKey, string content)
    {
        lock (_gate)
        {
            _ = Get(conversationId);
            using var connection = Open();
            using var command = Command(connection, "SELECT body_json FROM turns WHERE conversation_id=$id AND request_key=$key", ("$id", conversationId), ("$key", requestKey));
            if (command.ExecuteScalar() is not string body) return null;
            var turn = Deserialize<ExperimentTurn>(body);
            if (turn.UserText != content) throw new ExperimentException("turn.request_conflict", "This request identity belongs to different content.", 409);
            return turn;
        }
    }

    public ExperimentTurn GetTurn(string conversationId, string turnId)
    {
        lock (_gate)
        {
            Identifier(conversationId); Identifier(turnId);
            using var connection = Open();
            using var command = Command(connection, "SELECT body_json FROM turns WHERE id=$id AND conversation_id=$chat", ("$id", turnId), ("$chat", conversationId));
            return command.ExecuteScalar() is string body ? Deserialize<ExperimentTurn>(body) : throw Missing();
        }
    }

    private void SaveTurn(ExperimentTurn turn)
    {
        using var connection = Open();
        using var command = Command(connection, "UPDATE turns SET body_json=$body WHERE id=$id AND conversation_id=$chat",
            ("$id", turn.Id), ("$chat", turn.ConversationId), ("$body", Serialize(turn)));
        if (command.ExecuteNonQuery() != 1) throw Missing();
    }

    public void Output(string conversationId, string turnId, string delta)
    {
        lock (_gate)
        {
            var turn = GetTurn(conversationId, turnId);
            if (turn.Status != "running") return;
            if (turn.AssistantText.Length + delta.Length > 524288)
                throw new ExperimentException("turn.output_quota", "Assistant output exceeded the retained character limit.");
            SaveTurn(turn with { AssistantText = turn.AssistantText + delta });
        }
    }

    public void OutputAndTrace(string conversationId, string turnId, string delta)
    {
        lock (_gate)
        {
            Output(conversationId, turnId, delta);
            Append(conversationId, turnId, new("assistant.delta", new { text = delta }));
        }
    }

    public ExperimentTurn Complete(string conversationId, string turnId, ExperimentExecutionResult result)
    {
        lock (_gate)
        {
            var turn = GetTurn(conversationId, turnId);
            if (turn.Status != "running") return turn;
            turn = turn with
            {
                Status = result.Status,
                AssistantText = result.AssistantText ?? turn.AssistantText,
                FinishedAt = DateTimeOffset.UtcNow,
                Usage = result.Usage,
                ErrorCode = result.ErrorCode,
                ErrorMessage = result.ErrorMessage
            };
            SaveTurn(turn);
            return turn;
        }
    }

    public ExperimentEvent Append(string conversationId, string turnId, ExperimentTraceInput input, bool terminal = false)
    {
        lock (_gate)
        {
            var turn = GetTurn(conversationId, turnId);
            var payload = Sanitize(JsonSerializer.SerializeToNode(input.Data, Json));
            var full = payload?.ToJsonString(Json) ?? "null";
            using var connection = Open();
            using var retained = Command(connection, "SELECT COALESCE(SUM(length(body_json)+length(COALESCE(payload_json,''))),0) FROM trace_events WHERE turn_id=$id", ("$id", turnId));
            var used = Convert.ToInt64(retained.ExecuteScalar());
            if (!terminal && (turn.TraceCount >= 4000 || used + full.Length > 8 * 1024 * 1024))
            {
                SaveTurn(turn with { DroppedTraceCount = turn.DroppedTraceCount + 1 });
                return new(turn.TraceCount, "trace.quota", DateTimeOffset.UtcNow, new { dropped = true });
            }
            var sequence = turn.TraceCount + 1L;
            string? large = null;
            object? data = payload;
            if (full.Length > 1024 * 1024)
                data = new { truncated = true, reason = "Per-event 1 MiB retention ceiling", originalCharacters = full.Length, preview = full[..24000] };
            else if (full.Length > 24000 || input.Type == "provider.record")
            {
                large = full;
                data = new
                {
                    payloadRef = $"/api/chats/{conversationId}/turns/{turnId}/events/{sequence}/payload",
                    originalCharacters = full.Length,
                    preview = input.Type == "provider.record" ? null : full[..Math.Min(full.Length, 12000)],
                    truncated = false,
                    providerRecord = input.Type == "provider.record",
                    providerType = input.Type == "provider.record" ? payload?["type"]?.GetValue<string>() : null
                };
            }
            var item = new ExperimentEvent(sequence, input.Type, DateTimeOffset.UtcNow, data, input.CallId, input.ItemId);
            using var transaction = connection.BeginTransaction();
            using var command = Command(connection, "INSERT INTO trace_events(turn_id,sequence,body_json,payload_json) VALUES($id,$seq,$body,$payload)",
                ("$id", turnId), ("$seq", sequence), ("$body", Serialize(item)), ("$payload", large));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
            using var update = Command(connection, "UPDATE turns SET body_json=$body WHERE id=$id", ("$id", turnId), ("$body", Serialize(turn with { TraceCount = (int)sequence })));
            update.Transaction = transaction;
            update.ExecuteNonQuery();
            transaction.Commit();
            return item;
        }
    }

    public IReadOnlyList<ExperimentEvent> ReadEvents(string conversationId, string turnId, long after, int limit) =>
        ReadEventPage(conversationId, turnId, after, limit, citationsOnly: false);

    public IReadOnlyList<ExperimentEvent> ReadCitationEvents(string conversationId, string turnId, long after, int limit) =>
        ReadEventPage(conversationId, turnId, after, limit, citationsOnly: true);

    private IReadOnlyList<ExperimentEvent> ReadEventPage(string conversationId, string turnId, long after, int limit, bool citationsOnly)
    {
        lock (_gate)
        {
            _ = GetTurn(conversationId, turnId);
            using var connection = Open();
            using var command = Command(connection, "SELECT body_json FROM trace_events WHERE turn_id=$id AND sequence>$after AND ($citations=0 OR json_extract(body_json,'$.type')='provider.citations') ORDER BY sequence LIMIT $limit",
                ("$id", turnId), ("$after", Math.Max(0, after)), ("$limit", Math.Clamp(limit, 1, 100)), ("$citations", citationsOnly ? 1 : 0));
            using var reader = command.ExecuteReader();
            var result = new List<ExperimentEvent>();
            var characters = 0;
            while (reader.Read())
            {
                var body = reader.GetString(0);
                if (result.Count > 0 && characters + body.Length > 32000) break;
                result.Add(Deserialize<ExperimentEvent>(body));
                characters += body.Length;
            }
            return result;
        }
    }

    public JsonElement ReadPayload(string conversationId, string turnId, long sequence)
    {
        lock (_gate)
        {
            _ = GetTurn(conversationId, turnId);
            using var connection = Open();
            using var command = Command(connection, "SELECT payload_json,body_json FROM trace_events WHERE turn_id=$id AND sequence=$seq", ("$id", turnId), ("$seq", sequence));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw Missing();
            if (!reader.IsDBNull(0)) return JsonDocument.Parse(reader.GetString(0)).RootElement.Clone();
            return JsonSerializer.SerializeToElement(Deserialize<ExperimentEvent>(reader.GetString(1)).Data, Json);
        }
    }

    private static JsonNode? Sanitize(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonValue type && type.TryGetValue<string>(out var value) && value == "reasoning") obj.Remove("content");
            foreach (var key in obj.Select(x => x.Key).ToArray())
                if (key.Equals("encrypted_content", StringComparison.OrdinalIgnoreCase) || key.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("api_key", StringComparison.OrdinalIgnoreCase) || key.Equals("apiKey", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("signature", StringComparison.OrdinalIgnoreCase) || key.Equals("access_token", StringComparison.OrdinalIgnoreCase)) obj[key] = "[redacted]";
                else obj[key] = Sanitize(obj[key]?.DeepClone());
        }
        else if (node is JsonArray array)
            for (var i = 0; i < array.Count; i++) array[i] = Sanitize(array[i]?.DeepClone());
        return node;
    }

    public static void ValidateSettings(ExperimentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Mode is not ("clean-room" or "harness")) Invalid("Select Clean Room or Harness.");
        if (string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 200 || settings.Model.Any(char.IsControl)) Invalid("Supply an exact model ID of at most 200 characters.");
        if (settings.ReasoningEffort is not (null or "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max")) Invalid("Unknown reasoning effort.");
        if (settings.Instructions is null || settings.Instructions.Length > 128000) Invalid("Instructions exceed the character limit.");
        if (settings.EnabledTools is null || settings.EnabledTools.Count > 32 || settings.EnabledTools.Distinct(StringComparer.Ordinal).Count() != settings.EnabledTools.Count ||
            settings.EnabledTools.Any(id => !ExperimentTools.Capabilities.Any(c => c.Id == id))) Invalid("Unknown or duplicate tool binding.");
        if (settings.Mode == "clean-room" && settings.EnabledTools.Count != 0) Invalid("Select Harness mode to bind application tools.");
        if (settings.Mode == "harness" && settings.AmbientWebSearch) Invalid("Ambient web search is available in Clean Room mode. Bind web.search for Harness mode.");
        if (settings.MaxWebSearchCalls is < 1 or > 20) Invalid("Web search call budget must be 1–20 calls per turn.");
        if (settings.MemoryEnabled) Invalid("Memory is disabled for these isolated experiments.");
        if (settings.MaxOutputTokens is < 256 or > 65536 || settings.ContextWindowTokens is < 8192 or > 1048576 || settings.ContextWindowTokens <= settings.MaxOutputTokens + 4096)
            Invalid("Context must leave input headroom after the selected output budget and 4096-unit safety reserve.");
        if (settings.MaxInputCharacters is < 1024 or > 262144 || settings.MaxHistoryMessages is < 0 or > 500 || settings.HistoryPolicy is not ("stop" or "recent")) Invalid("Invalid history budget or policy.");
        if (settings.ContextDepth is not ("minimal" or "focused" or "normal" or "deep" or "exhaustive")) Invalid("Unknown context depth.");
        if (settings.MaxToolResultCharacters is < 256 or > 12000 || settings.MaxToolEvidenceCharacters is < 256 or > 262144) Invalid("Tool result budget must be 256–12000 characters; cumulative evidence must be 256–262144 characters.");
        if (settings.MaxSteps is < 1 or > 100 || settings.MaxContinuations is < 0 or > 32 || settings.MaxProviderCalls is < 1 or > 100 || settings.TimeoutSeconds is < 10 or > 1800) Invalid("Invalid execution limits.");
    }

    private static string ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 160 || title.Any(char.IsControl)) Invalid("Supply a title of at most 160 characters.");
        return title.Trim();
    }

    [DoesNotReturn]
    private static void Invalid(string message) => throw new ExperimentException("chat.settings", message);
    private static void Identifier(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw Missing();
    }
    private static ExperimentException Missing() => new("chat.not_found", "Conversation, turn or trace does not exist in this scope.", 404);
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string body) => JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("Malformed chat storage.");
    public void Dispose() => _lease.Dispose();
}
