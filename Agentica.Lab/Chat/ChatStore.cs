using Microsoft.Data.Sqlite;

internal sealed class ChatStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public ChatStore(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath
        };
        _connectionString = builder.ToString();
    }

    public string DatabasePath => _databasePath;

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath) ?? Environment.CurrentDirectory);

        using var connection = OpenConnection();
        ExecuteNonQuery(
            connection,
            """
            create table if not exists conversations (
                id text primary key,
                title text not null,
                persona_id text not null,
                workspace_root text not null,
                created_at text not null,
                updated_at text not null
            );

            create table if not exists messages (
                id text primary key,
                conversation_id text not null,
                role text not null,
                content text not null,
                created_at text not null,
                metadata_json text null,
                foreign key (conversation_id) references conversations(id)
            );

            create table if not exists context_items (
                id text primary key,
                conversation_id text not null,
                kind text not null,
                content text not null,
                source text null,
                created_at text not null,
                metadata_json text null,
                foreign key (conversation_id) references conversations(id)
            );

            create table if not exists runs (
                id text primary key,
                conversation_id text not null,
                message_id text not null,
                objective text not null,
                status text not null,
                created_at text not null,
                outcome_json text not null,
                foreign key (conversation_id) references conversations(id),
                foreign key (message_id) references messages(id)
            );

            create table if not exists run_events (
                id text primary key,
                run_id text not null,
                sequence integer null,
                event_type text not null,
                created_at text not null,
                payload_json text not null,
                foreign key (run_id) references runs(id)
            );
            """);
    }

    public ChatConversation CreateConversation(
        string title,
        string personaId,
        string workspaceRoot,
        string? conversationId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new ChatConversation(
            conversationId ?? NewId("conv"),
            title,
            personaId,
            Path.GetFullPath(workspaceRoot),
            now,
            now);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into conversations (id, title, persona_id, workspace_root, created_at, updated_at)
            values ($id, $title, $persona_id, $workspace_root, $created_at, $updated_at);
            """;
        Add(command, "$id", conversation.ConversationId);
        Add(command, "$title", conversation.Title);
        Add(command, "$persona_id", conversation.PersonaId);
        Add(command, "$workspace_root", conversation.WorkspaceRoot);
        Add(command, "$created_at", SerializeDate(conversation.CreatedAt));
        Add(command, "$updated_at", SerializeDate(conversation.UpdatedAt));
        command.ExecuteNonQuery();
        return conversation;
    }

    public ChatConversation? GetConversation(string conversationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, title, persona_id, workspace_root, created_at, updated_at
            from conversations
            where id = $id;
            """;
        Add(command, "$id", conversationId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    public ChatConversation? GetLatestConversation(string workspaceRoot)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, title, persona_id, workspace_root, created_at, updated_at
            from conversations
            where workspace_root = $workspace_root
            order by updated_at desc
            limit 1;
            """;
        Add(command, "$workspace_root", Path.GetFullPath(workspaceRoot));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    public ChatConversation? GetLatestConversation()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, title, persona_id, workspace_root, created_at, updated_at
            from conversations
            order by updated_at desc
            limit 1;
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    public ChatConversation UpdateConversationPersona(ChatConversation conversation, string personaId)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            update conversations
            set persona_id = $persona_id,
                updated_at = $updated_at
            where id = $id;
            """;
        Add(command, "$persona_id", personaId);
        Add(command, "$updated_at", SerializeDate(now));
        Add(command, "$id", conversation.ConversationId);
        command.ExecuteNonQuery();
        return conversation with { PersonaId = personaId, UpdatedAt = now };
    }

    public ChatMessage AddMessage(
        string conversationId,
        string role,
        string content,
        string? metadataJson = null)
    {
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage(
            NewId("msg"),
            conversationId,
            role,
            content,
            now,
            metadataJson);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into messages (id, conversation_id, role, content, created_at, metadata_json)
            values ($id, $conversation_id, $role, $content, $created_at, $metadata_json);

            update conversations
            set updated_at = $created_at
            where id = $conversation_id;
            """;
        Add(command, "$id", message.MessageId);
        Add(command, "$conversation_id", conversationId);
        Add(command, "$role", role);
        Add(command, "$content", content);
        Add(command, "$created_at", SerializeDate(now));
        Add(command, "$metadata_json", metadataJson);
        command.ExecuteNonQuery();
        return message;
    }

    public ChatContextItem AddContextItem(
        string conversationId,
        string kind,
        string content,
        string? source,
        string? metadataJson = null)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new ChatContextItem(
            NewId("ctx"),
            conversationId,
            kind,
            content,
            source,
            now,
            metadataJson);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            insert into context_items (id, conversation_id, kind, content, source, created_at, metadata_json)
            values ($id, $conversation_id, $kind, $content, $source, $created_at, $metadata_json);

            update conversations
            set updated_at = $created_at
            where id = $conversation_id;
            """;
        Add(command, "$id", item.ContextItemId);
        Add(command, "$conversation_id", conversationId);
        Add(command, "$kind", kind);
        Add(command, "$content", content);
        Add(command, "$source", source);
        Add(command, "$created_at", SerializeDate(now));
        Add(command, "$metadata_json", metadataJson);
        command.ExecuteNonQuery();
        return item;
    }

    public void AddRun(
        string runId,
        string conversationId,
        string messageId,
        string objective,
        string status,
        string outcomeJson,
        IReadOnlyList<Agentica.Events.ExecutionEvent> events)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                insert into runs (id, conversation_id, message_id, objective, status, created_at, outcome_json)
                values ($id, $conversation_id, $message_id, $objective, $status, $created_at, $outcome_json);
                """;
            Add(command, "$id", runId);
            Add(command, "$conversation_id", conversationId);
            Add(command, "$message_id", messageId);
            Add(command, "$objective", objective);
            Add(command, "$status", status);
            Add(command, "$created_at", SerializeDate(DateTimeOffset.UtcNow));
            Add(command, "$outcome_json", outcomeJson);
            command.ExecuteNonQuery();
        }

        foreach (var executionEvent in events)
        {
            using var eventCommand = connection.CreateCommand();
            eventCommand.Transaction = transaction;
            eventCommand.CommandText =
                """
                insert into run_events (id, run_id, sequence, event_type, created_at, payload_json)
                values ($id, $run_id, $sequence, $event_type, $created_at, $payload_json);
                """;
            Add(eventCommand, "$id", executionEvent.EventId);
            Add(eventCommand, "$run_id", runId);
            Add(eventCommand, "$sequence", executionEvent.Sequence);
            Add(eventCommand, "$event_type", executionEvent.Type);
            Add(eventCommand, "$created_at", SerializeDate(executionEvent.At));
            Add(eventCommand, "$payload_json", System.Text.Json.JsonSerializer.Serialize(executionEvent, JsonOptions.Create()));
            eventCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<ChatMessage> GetRecentMessages(string conversationId, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, conversation_id, role, content, created_at, metadata_json
            from messages
            where conversation_id = $conversation_id
            order by created_at desc
            limit $limit;
            """;
        Add(command, "$conversation_id", conversationId);
        Add(command, "$limit", Math.Max(1, limit));

        var messages = new List<ChatMessage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            messages.Add(ReadMessage(reader));
        }

        messages.Reverse();
        return messages;
    }

    public IReadOnlyList<ChatContextItem> GetContextItems(string conversationId, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, conversation_id, kind, content, source, created_at, metadata_json
            from context_items
            where conversation_id = $conversation_id
            order by created_at desc
            limit $limit;
            """;
        Add(command, "$conversation_id", conversationId);
        Add(command, "$limit", Math.Max(1, limit));

        var items = new List<ChatContextItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadContextItem(reader));
        }

        return items;
    }

    public IReadOnlyList<ChatRunRecord> GetRecentRuns(string conversationId, int limit)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            select id, conversation_id, message_id, objective, status, created_at, outcome_json
            from runs
            where conversation_id = $conversation_id
            order by created_at desc
            limit $limit;
            """;
        Add(command, "$conversation_id", conversationId);
        Add(command, "$limit", Math.Max(1, limit));

        var runs = new List<ChatRunRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(new ChatRunRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseDate(reader.GetString(5)),
                reader.GetString(6)));
        }

        return runs;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static ChatConversation ReadConversation(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseDate(reader.GetString(4)),
            ParseDate(reader.GetString(5)));

    private static ChatMessage ReadMessage(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ParseDate(reader.GetString(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5));

    private static ChatContextItem ReadContextItem(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            ParseDate(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6));

    private static string SerializeDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O");

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static string NewId(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 12, prefix.Length + 1 + 32)];
}
