using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Microsoft.Data.Sqlite;

namespace Agentica.Lab.Web.Tests;

public sealed class ExperimentPersistenceTests
{
    [Fact]
    public void Conversation_settings_transcript_and_trace_survive_restart_without_cross_chat_context()
    {
        using var fixture = new StoreFixture();
        string id, turnId;
        const string instructions = "  exact experiment\r\nKeep whitespace.\n";
        using (var store = new ExperimentStore(fixture.Path))
        {
            var a = store.Create(new(), "A");
            var b = store.Create(new(), "B");
            Assert.Empty(a.Settings.Instructions);
            Assert.Empty(b.Settings.EnabledTools);
            Assert.False(a.Settings.MemoryEnabled);
            Assert.False(a.Settings.AmbientWebSearch);
            a = store.UpdateSettings(a.Id, new(1, a.Settings with { Instructions = instructions, ReasoningEffort = "low", AmbientWebSearch = true, MaxWebSearchCalls = 2 }));
            var (turn, _) = store.Begin(a.Id, new("remember only this dialogue", "request-1"));
            store.Output(a.Id, turn.Id, "partial answer");
            store.Append(a.Id, turn.Id, new("provider.item", new { type = "reasoning", encrypted_content = "opaque-private", summary = new[] { new { text = "Public summary" } } }, "call-1", "item-1"));
            store.Complete(a.Id, turn.Id, new("completed", Usage: new { inputTokens = 17, outputTokens = 12 }));
            store.Rename(a.Id, "Renamed experiment");
            Assert.Throws<ExperimentException>(() => store.ReadEvents(b.Id, turn.Id, 0, 100));
            Assert.Empty(store.Get(b.Id).Turns);
            id = a.Id; turnId = turn.Id;
        }
        using var restored = new ExperimentStore(fixture.Path);
        var actual = restored.Get(id);
        Assert.Equal("Renamed experiment", actual.Title);
        Assert.Equal(instructions, actual.Settings.Instructions);
        Assert.True(actual.Settings.AmbientWebSearch);
        Assert.Equal(2, actual.Settings.MaxWebSearchCalls);
        Assert.Equal(2, actual.SettingsRevision);
        Assert.Equal("partial answer", Assert.Single(actual.Turns).AssistantText);
        var trace = JsonSerializer.Serialize(restored.ReadEvents(id, turnId, 0, 100), HostProtocol.Json);
        Assert.Contains("Public summary", trace);
        Assert.DoesNotContain("opaque-private", trace);
        Assert.Contains("redacted", trace);
    }

    [Fact]
    public void Interrupted_dispatch_retains_partial_evidence_and_idempotent_identity_without_replay()
    {
        using var fixture = new StoreFixture();
        string id, turnId;
        using (var store = new ExperimentStore(fixture.Path))
        {
            var conversation = store.Create(new(), null);
            var (turn, _) = store.Begin(conversation.Id, new("question", "stable-request"));
            store.Output(conversation.Id, turn.Id, "partial");
            store.Append(conversation.Id, turn.Id, new("provider.started", new { providerCallId = "original-call" }));
            id = conversation.Id; turnId = turn.Id;
        }
        using var restored = new ExperimentStore(fixture.Path);
        var actual = restored.GetTurn(id, turnId);
        Assert.Equal("interrupted", actual.Status);
        Assert.Equal("partial", actual.AssistantText);
        Assert.Null(actual.Usage);
        var replay = restored.Begin(id, new("question", "stable-request"));
        Assert.False(replay.Created);
        Assert.Equal(turnId, replay.Turn.Id);
        Assert.Throws<ExperimentException>(() => restored.Begin(id, new("different question", "stable-request")));
        Assert.Contains(restored.ReadEvents(id, turnId, 0, 100), e => e.Type == "turn.interrupted");
    }

    [Fact]
    public void Explicit_recent_history_omits_complete_pairs_and_stop_policy_rejects_before_dispatch()
    {
        using var fixture = new StoreFixture();
        using var store = new ExperimentStore(fixture.Path);
        var conversation = store.Create(new() { MaxHistoryMessages = 2 }, null);
        for (var i = 0; i < 2; i++)
        {
            var turn = store.Begin(conversation.Id, new("user-" + i)).Turn;
            store.Complete(conversation.Id, turn.Id, new("completed", "answer-" + i));
        }
        var next = store.Begin(conversation.Id, new("next")).Turn;
        conversation = store.Get(conversation.Id);
        Assert.Throws<ExperimentException>(() => ExperimentContextCompiler.Prepare(conversation, next));
        var prepared = ExperimentContextCompiler.Prepare(conversation, next with { Settings = next.Settings with { HistoryPolicy = "recent" } });
        Assert.Equal(new[] { "user-1", "answer-1" }, prepared.Messages.Select(m => m.Content));
        Assert.Equal(2, prepared.Manifest.OmittedMessageIds.Count);
        Assert.False(prepared.Manifest.MemoryEnabled);
        Assert.Contains("not persisted", prepared.Manifest.Continuity);
    }

    [Fact]
    public void Stale_settings_memory_and_clean_room_tool_bindings_are_rejected()
    {
        using var fixture = new StoreFixture();
        using var store = new ExperimentStore(fixture.Path);
        var conversation = store.Create(new(), null);
        store.UpdateSettings(conversation.Id, new(1, conversation.Settings with { Model = "exact-manual-model" }));
        Assert.Throws<ExperimentException>(() => store.UpdateSettings(conversation.Id, new(1, new())));
        Assert.Throws<ExperimentException>(() => ExperimentStore.ValidateSettings(new() { MemoryEnabled = true }));
        Assert.Throws<ExperimentException>(() => ExperimentStore.ValidateSettings(new() { MaxToolResultCharacters = 12001 }));
        Assert.Throws<ExperimentException>(() => ExperimentStore.ValidateSettings(new() { EnabledTools = [ExperimentTools.Capabilities[0].Id] }));
        Assert.Throws<ExperimentException>(() => ExperimentStore.ValidateSettings(new() { Mode = "harness", AmbientWebSearch = true }));
        Assert.Throws<ExperimentException>(() => ExperimentStore.ValidateSettings(new() { MaxWebSearchCalls = 0 }));
        Assert.Throws<ExperimentException>(() => ExperimentStore.ValidateSettings(new() { MaxWebSearchCalls = 21 }));
        Assert.Equal("exact-manual-model", store.Get(conversation.Id).Settings.Model);
        Assert.Throws<IOException>(() => new ExperimentStore(fixture.Path));
    }

    [Fact]
    public void Large_trace_payload_is_paged_and_readable_only_through_its_own_conversation()
    {
        using var fixture = new StoreFixture();
        using var store = new ExperimentStore(fixture.Path);
        var a = store.Create(new(), "A");
        var b = store.Create(new(), "B");
        var turn = store.Begin(a.Id, new("inspect")).Turn;
        var payload = new string('x', 60000);
        var trace = store.Append(a.Id, turn.Id, new("provider.request", new { body = payload, authorization = "secret-header" }));
        Assert.Contains("payloadRef", JsonSerializer.Serialize(trace, HostProtocol.Json));
        var restored = store.ReadPayload(a.Id, turn.Id, trace.Sequence);
        Assert.Equal(payload, restored.GetProperty("body").GetString());
        Assert.Equal("[redacted]", restored.GetProperty("authorization").GetString());
        Assert.Throws<ExperimentException>(() => store.ReadPayload(b.Id, turn.Id, trace.Sequence));
        var source = store.Append(a.Id, turn.Id, new("provider.record", new { type = "response.output_item.added", output_index = 7 }));
        Assert.Contains("providerRecord", JsonSerializer.Serialize(source, HostProtocol.Json));
        Assert.Equal(7, store.ReadPayload(a.Id, turn.Id, source.Sequence).GetProperty("output_index").GetInt32());
    }

    [Fact]
    public void Reopened_trace_tail_includes_the_latest_event_when_payload_size_shortens_each_page()
    {
        using var fixture = new StoreFixture();
        string chatId, turnId, otherId;
        long latest;
        using (var store = new ExperimentStore(fixture.Path))
        {
            chatId = store.Create(new(), "Trace owner").Id;
            otherId = store.Create(new(), "Other conversation").Id;
            var turn = store.Begin(chatId, new("inspect saved trace")).Turn;
            turnId = turn.Id;
            for (var index = 0; index < 40; index++)
                store.Append(chatId, turnId, new("provider.item", new { text = new string('x', 5000), index }));
            store.CompleteAndTrace(chatId, turnId, new("completed", "Saved answer"));
            latest = store.GetTurn(chatId, turnId).TraceCount;
        }
        using var reopened = new ExperimentStore(fixture.Path);
        var head = reopened.ReadEvents(chatId, turnId, 0, 100);
        var tail = reopened.ReadTraceTail(chatId, turnId, 100);
        Assert.Equal(1, head[0].Sequence);
        Assert.True(head[^1].Sequence < latest);
        Assert.True(tail[0].Sequence > head[^1].Sequence);
        Assert.Equal(latest, tail[^1].Sequence);
        Assert.Equal("turn.completed", tail[^1].Type);
        Assert.Equal(tail.OrderBy(item => item.Sequence), tail);
        Assert.InRange(tail.Sum(item => JsonSerializer.Serialize(item, HostProtocol.Json).Length), 1, 32000);
        Assert.Single(reopened.ReadTraceTail(chatId, turnId, 1));
        Assert.Throws<ExperimentException>(() => reopened.ReadTraceTail(otherId, turnId, 100));
        Assert.Equal("Saved answer", reopened.GetTurn(chatId, turnId).AssistantText);
    }

    [Fact]
    public void Failed_schema_open_releases_the_process_lease_for_repair()
    {
        using var fixture = new StoreFixture();
        using (var initial = new ExperimentStore(fixture.Path)) initial.Create(new(), null);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = System.IO.Path.Combine(fixture.Path, "experiments.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version=3"; command.ExecuteNonQuery();
        Assert.Throws<InvalidDataException>(() => new ExperimentStore(fixture.Path));
        command.CommandText = "PRAGMA user_version=1"; command.ExecuteNonQuery();
        using var repaired = new ExperimentStore(fixture.Path);
        Assert.Single(repaired.List());
    }

    [Fact]
    public void Failed_workspace_initialization_releases_the_lease_for_repair()
    {
        using var fixture = new StoreFixture();
        var blocked = System.IO.Path.Combine(fixture.Path, "workspaces");
        File.WriteAllText(blocked, "fixture blocks workspace directory");
        Assert.Throws<IOException>(() => new ExperimentStore(fixture.Path));
        File.Delete(blocked);
        using var repaired = new ExperimentStore(fixture.Path);
        Assert.Empty(repaired.List());
    }

    [Fact]
    public void Failed_turn_admission_rolls_back_the_running_row()
    {
        using var fixture = new StoreFixture();
        using var store = new ExperimentStore(fixture.Path);
        var conversation = store.Create(new(), null);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = System.IO.Path.Combine(fixture.Path, "experiments.sqlite3"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_update BEFORE UPDATE ON conversations BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;";
        command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => store.Begin(conversation.Id, new("must roll back")));
        Assert.Empty(store.Get(conversation.Id).Turns);
    }

    [Fact]
    public void Admission_requires_the_same_saved_configuration_and_history_as_preflight_but_preserves_request_replay()
    {
        using var fixture = new StoreFixture(); using var store = new ExperimentStore(fixture.Path);
        var chat = store.Create(new(), null); var preflight = store.GetHead(chat.Id);
        chat = store.UpdateSettings(chat.Id, new(1, chat.Settings with { MaxHistoryMessages = 0 }));
        Assert.Equal("settings.conflict", Assert.Throws<ExperimentException>(() =>
            store.Begin(chat.Id, new("retain my draft"), preflight.Conversation.SettingsRevision, preflight.LatestSequence)).Code);
        Assert.Empty(store.GetTurnsPage(chat.Id).Turns);
        var accepted = store.Begin(chat.Id, new("first", "stable-request"), chat.SettingsRevision, 0).Turn;
        store.CompleteAndTrace(chat.Id, accepted.Id, new("completed", "answer"));
        Assert.Equal("chat.history_changed", Assert.Throws<ExperimentException>(() =>
            store.Begin(chat.Id, new("second"), chat.SettingsRevision, 0)).Code);
        var replay = store.Begin(chat.Id, new("first", "stable-request"), 1, 0);
        Assert.False(replay.Created); Assert.Equal(accepted.Id, replay.Turn.Id);
        Assert.Single(store.GetTurnsPage(chat.Id).Turns);
    }

    [Fact]
    public async Task Completed_publication_always_exposes_its_terminal_event_to_concurrent_readers()
    {
        using var fixture = new StoreFixture(); using var store = new ExperimentStore(fixture.Path);
        var chat = store.Create(new(), null); var turn = store.Begin(chat.Id, new("terminal publication")).Turn;
        var writer = Task.Run(() => store.CompleteAndTrace(chat.Id, turn.Id, new("completed", "answer")), TestContext.Current.CancellationToken);
        while (!writer.IsCompleted)
        {
            var snapshot = store.GetTurn(chat.Id, turn.Id);
            if (snapshot.Status == "completed")
                Assert.Contains(store.ReadEvents(chat.Id, turn.Id, 0, 100), item => item.Type == "turn.completed");
            await Task.Yield();
        }
        await writer;
        Assert.Equal("turn.completed", store.ReadEvents(chat.Id, turn.Id, 0, 100)[^1].Type);
    }

    [Fact]
    public async Task Http_turn_stream_replays_after_reconnect_and_full_service_restart()
    {
        using var fixture = new StoreFixture();
        var configuration = new OpenAiProviderConfiguration(_ => null);
        var executor = new FixtureExecutor();
        string conversationId, turnId;
        var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], storageDirectory: fixture.Path,
            openAiConfiguration: configuration, experimentExecutor: executor);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await app.StartAsync(deadline.Token);
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using (var foreign = new HttpRequestMessage(HttpMethod.Get, "/api/chats"))
            {
                foreign.Headers.Add("Origin", "https://unrelated.invalid");
                using var denied = await http.SendAsync(foreign, deadline.Token);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }
            var conversation = await Post<ExperimentConversation>(http, "/api/chats", new { title = "HTTP experiment" }, deadline.Token);
            var turn = await Post<ExperimentTurn>(http, $"/api/chats/{conversation.Id}/turns", new { content = "initial", requestId = "request-1" }, deadline.Token);
            var stream = await http.GetStringAsync($"/api/chats/{conversation.Id}/turns/{turn.Id}/events", deadline.Token);
            Assert.Contains("provider.summary.delta", stream);
            Assert.Contains("assistant.delta", stream);
            Assert.Contains("turn.completed", stream);
            var duplicate = await Post<ExperimentTurn>(http, $"/api/chats/{conversation.Id}/turns", new { content = "initial", requestId = "request-1" }, deadline.Token);
            Assert.Equal(turn.Id, duplicate.Id);
            Assert.Equal(1, executor.Calls);
            using var stop = await http.PostAsync($"/api/chats/{conversation.Id}/turns/{turn.Id}/cancel", null, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
            conversationId = conversation.Id; turnId = turn.Id;
        }
        finally
        {
            await app.StopAsync(deadline.Token);
            await app.DisposeAsync();
        }
        var restarted = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], storageDirectory: fixture.Path,
            openAiConfiguration: configuration, experimentExecutor: executor);
        try
        {
            await restarted.StartAsync(deadline.Token);
            using var http = new HttpClient { BaseAddress = new Uri(restarted.Urls.Single()) };
            var restored = await http.GetFromJsonAsync<ExperimentConversation>($"/api/chats/{conversationId}", HostProtocol.Json, deadline.Token);
            Assert.Equal("Hello, experiment.", Assert.Single(restored!.Turns).AssistantText);
            var replay = await http.GetStringAsync($"/api/chats/{conversationId}/turns/{turnId}/events?after=2", deadline.Token);
            Assert.Contains("turn.completed", replay);
            using var citations = JsonDocument.Parse(await http.GetStringAsync($"/api/chats/{conversationId}/turns/{turnId}/citations", deadline.Token));
            var citation = Assert.Single(citations.RootElement.GetProperty("events").EnumerateArray());
            Assert.Equal("provider.citations", citation.GetProperty("type").GetString());
            Assert.Equal("https://example.com/source", citation.GetProperty("data").GetProperty("citations")[0].GetProperty("url").GetString());
            Assert.False(citations.RootElement.GetProperty("hasMore").GetBoolean());
            var other = await Post<ExperimentConversation>(http, "/api/chats", new { title = "Other experiment" }, deadline.Token);
            using var foreignCitations = await http.GetAsync($"/api/chats/{other.Id}/turns/{turnId}/citations", deadline.Token);
            Assert.Equal(HttpStatusCode.NotFound, foreignCitations.StatusCode);
            Assert.Equal(1, executor.Calls);
        }
        finally
        {
            await restarted.StopAsync(deadline.Token);
            await restarted.DisposeAsync();
        }
    }

    private static async Task<T> Post<T>(HttpClient http, string url, object data, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(url, data, HostProtocol.Json, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(HostProtocol.Json, cancellationToken))!;
    }

    private sealed class FixtureExecutor : IExperimentExecutor
    {
        public int Calls { get; private set; }
        public async Task<ExperimentExecutionResult> ExecuteAsync(ExperimentExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            await context.Trace(new("provider.request", new { body = new { model = context.Turn.Settings.Model, store = false }, hash = "fixture-request" }));
            await context.Trace(new("provider.summary.delta", new { text = "Fixture summary", itemId = "reason-1" }));
            await context.Output("Hello, ");
            await context.Output("experiment.");
            await context.Trace(new("provider.citations", new
            {
                target = "assistant",
                citations = new[] { new { url = "https://example.com/source", title = "Fixture source", startIndex = 0, endIndex = 5 } },
                sources = new[] { new { url = "https://example.com/source", title = "Fixture source" } }
            }));
            return new("completed", Usage: new { inputTokens = 12, outputTokens = 8 });
        }
    }

    private sealed class StoreFixture : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("agentica-experiment-tests-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
