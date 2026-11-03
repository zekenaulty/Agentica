using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Chat.Recall;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

public sealed class ExperimentRecallApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Turn_deadline_and_explicit_stop_retain_distinct_terminal_results(bool stopExplicitly)
    {
        var directory = Directory.CreateTempSubdirectory("agentica-recall-deadline-").FullName;
        try
        {
            using var store = new ExperimentStore(directory);
            var chat = store.Create(new() { TimeoutSeconds = 10, ThreadRecallEnabled = true, ThreadRecallMode = "manual" }, null);
            var executor = new WaitingExecutor();
            using var service = new ExperimentService(store, executor, new OpenAiProviderConfiguration(_ => null));
            var turn = service.Send(chat.Id, new("Synthetic deadline check"));
            await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            if (stopExplicitly) service.Cancel(chat.Id, turn.Id);
            var until = DateTimeOffset.UtcNow.AddSeconds(20);
            while (store.GetTurn(chat.Id, turn.Id).Status == "running" && DateTimeOffset.UtcNow < until)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            var final = store.GetTurn(chat.Id, turn.Id);
            Assert.Equal(stopExplicitly ? "cancelled" : "failed", final.Status);
            Assert.Equal(stopExplicitly ? "turn.cancelled" : "turn.timeout", final.ErrorCode);
            Assert.Equal("partial evidence", final.AssistantText);
            Assert.Equal(1, executor.Calls);
            Assert.Equal("turn." + final.Status, store.ReadEvents(chat.Id, turn.Id, 0, 100)[^1].Type);
            await service.StopAsync(TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class WaitingExecutor : IExperimentExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task<ExperimentExecutionResult> ExecuteAsync(ExperimentExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            await context.Output("partial evidence");
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return new("cancelled", Usage: new { providerCalls = 0 }, ErrorCode: "turn.cancelled"); }
            throw new InvalidOperationException("The synthetic wait must end by cancellation.");
        }
    }

    [Fact]
    public async Task Conversation_and_source_pages_are_bounded_disjoint_and_scope_bound()
    {
        await using var fixture = await ApiFixture.Start();
        var chat = await fixture.Create("Paged");
        for (var i = 1; i <= 27; i++) fixture.Complete(chat.Id, "user-" + i, "answer-" + i);
        var other = await fixture.Create("Other"); fixture.Complete(other.Id, "foreign user", "foreign answer");

        var latest = await fixture.Get<ExperimentConversation>($"/api/chats/{chat.Id}?limit=20");
        Assert.Equal(Enumerable.Range(8, 20), latest.Turns.Select(turn => turn.Sequence));
        Assert.Equal(27, latest.HistoryPage!.TotalTurns);
        Assert.Equal(8, latest.HistoryPage.NextBeforeSequence);
        var earlier = await fixture.Get<ExperimentConversation>($"/api/chats/{chat.Id}?limit=20&beforeSequence=8");
        Assert.Equal(Enumerable.Range(1, 7), earlier.Turns.Select(turn => turn.Sequence));
        Assert.Null(earlier.HistoryPage!.NextBeforeSequence);
        Assert.Empty((await fixture.Get<ExperimentConversation>($"/api/chats/{chat.Id}?limit=20&beforeSequence=0")).Turns);
        using (var invalid = await fixture.Http.GetAsync($"/api/chats/{chat.Id}?beforeSequence=invalid", fixture.Token))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var sources = await fixture.Get<ConversationSourcePage>($"/api/chats/{chat.Id}/history/messages?limit=5&asOfSequence=3");
        Assert.Equal(6, sources.TotalSources); Assert.Equal(3, sources.AsOfSequence);
        Assert.Equal(5, sources.Items.Count); Assert.All(sources.Items, item => Assert.InRange(item.TurnSequence, 1, 3));
        var preceding = await fixture.Get<ConversationSourcePage>($"/api/chats/{chat.Id}/history/messages?limit=5&asOfSequence=3&beforeOrder={sources.NextBeforeOrder}");
        Assert.Single(preceding.Items); Assert.DoesNotContain(preceding.Items[0].SourceId, sources.Items.Select(item => item.SourceId));
        Assert.Empty((await fixture.Get<ConversationSourcePage>($"/api/chats/{chat.Id}/history/messages?beforeOrder=0")).Items);
        Assert.Equal(0L, fixture.IndexRows());
    }

    [Fact]
    public async Task Exact_HTTP_ranges_keep_the_requested_anchor_with_neighbors_and_do_not_reveal_foreign_or_future_sources()
    {
        await using var fixture = await ApiFixture.Start();
        var chat = await fixture.Create("A"); var other = await fixture.Create("B");
        var first = fixture.Complete(chat.Id, "prefix selected evidence suffix", "first response");
        var second = fixture.Complete(chat.Id, "correction beside earlier evidence", "second response");
        var foreign = fixture.Complete(other.Id, "other chat private source", "private answer");
        var exact = await fixture.Get<ConversationEvidenceResult>($"/api/chats/{chat.Id}/history/messages/{first.Id}:user?startOffset=7&maxCharacters=100&neighborRadius=1");
        var anchor = Assert.Single(exact.Hits, hit => hit.Source.SourceId == first.Id + ":user");
        Assert.Equal(first.UserText[7..], anchor.Excerpt); Assert.Equal(7, anchor.StartOffset);
        Assert.Equal(first.UserText.Length, anchor.EndOffset);
        Assert.Contains(exact.Hits, hit => hit.Source.SourceId == first.Id + ":assistant");
        Assert.InRange(exact.Hits.Sum(hit => hit.Excerpt.Length), 1, 100);
        Assert.Equal(chat.Id, exact.ScopeRef); Assert.Equal(2, exact.AsOfSequence);
        foreach (var url in new[]
        {
            $"/api/chats/{chat.Id}/history/messages/{foreign.Id}:user",
            $"/api/chats/{chat.Id}/history/messages/{second.Id}:user?asOfSequence=1"
        })
        {
            var denied = await fixture.Get<ConversationEvidenceResult>(url);
            Assert.Equal("UNAVAILABLE", denied.Status); Assert.Empty(denied.Hits);
            Assert.DoesNotContain("private", JsonSerializer.Serialize(denied, HostProtocol.Json), StringComparison.OrdinalIgnoreCase);
        }
        using var range = await fixture.Http.GetAsync($"/api/chats/{chat.Id}/history/messages/{first.Id}:user?startOffset=-1", fixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, range.StatusCode);
        Assert.Equal("recall.range", (await range.Content.ReadFromJsonAsync<ApiError>(HostProtocol.Json, fixture.Token))!.Code);
        foreach (var cut in new[] { -1, 3 })
        {
            using var response = await fixture.Http.GetAsync($"/api/chats/{chat.Id}/history/status?asOfSequence={cut}", fixture.Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("history.as_of", (await response.Content.ReadFromJsonAsync<ApiError>(HostProtocol.Json, fixture.Token))!.Code);
        }
        Assert.Equal(0L, fixture.IndexRows());
    }

    [Fact]
    public async Task Explicit_search_reconciles_local_projection_but_cannot_choose_another_scope_or_invalid_cursor()
    {
        await using var fixture = await ApiFixture.Start();
        var chat = await fixture.Create("A"); var other = await fixture.Create("B");
        var source = fixture.Complete(chat.Id, "exact lexical needle", "local answer");
        fixture.Complete(other.Id, "foreign secret needle", "foreign answer");
        var result = await fixture.Post<ConversationEvidenceResult>($"/api/chats/{chat.Id}/history/search", new ConversationEvidenceSearch("needle"));
        Assert.Equal("COMPLETE", result.Status); Assert.Equal(chat.Id, result.ScopeRef);
        Assert.Equal(source.Id + ":user", Assert.Single(result.Hits).Source.SourceId);
        Assert.Equal("NO_MATCH", (await fixture.Post<ConversationEvidenceResult>($"/api/chats/{chat.Id}/history/search", new ConversationEvidenceSearch("foreign secret"))).Status);
        using (var cursor = await fixture.Http.PostAsJsonAsync($"/api/chats/{chat.Id}/history/search", new ConversationEvidenceSearch("needle", Offset: -1), HostProtocol.Json, fixture.Token))
            Assert.Equal(HttpStatusCode.BadRequest, cursor.StatusCode);
        using (var scope = await fixture.Http.PostAsJsonAsync($"/api/chats/{chat.Id}/history/search", new { query = "needle", conversationId = other.Id }, HostProtocol.Json, fixture.Token))
            Assert.Equal(HttpStatusCode.BadRequest, scope.StatusCode);
        var otherStatus = await fixture.Get<HistoryStatus>($"/api/chats/{other.Id}/history/status");
        Assert.Equal("NOT_READY", otherStatus.Coverage.Status);
    }

    [Fact]
    public async Task Explicit_rebuild_cancel_status_and_service_restart_preserve_canonical_sources()
    {
        await using var fixture = await ApiFixture.Start(arrangeBeforeExercise: true);
        var chat = await fixture.Create("Maintenance");
        var first = fixture.Complete(chat.Id, new string('x', 8000) + " source-0", "answer-0");
        for (var i = 1; i < 32; i++) fixture.Complete(chat.Id, new string('x', 8000) + " source-" + i, "answer-" + i);
        // Canonical SQLite arrangement competes with the large-corpus fixture on
        // Windows CI. It must not spend the unchanged HTTP exercise deadline.
        fixture.BeginExercise();
        Assert.Equal("NOT_READY", (await fixture.Get<HistoryStatus>($"/api/chats/{chat.Id}/history/status")).Coverage.Status);
        var started = await fixture.Post<ConversationIndexProgress>($"/api/chats/{chat.Id}/history/rebuild", null);
        Assert.Equal("INDEXING", started.Status);
        var cancelled = await fixture.Post<ConversationIndexProgress>($"/api/chats/{chat.Id}/history/rebuild/cancel", null);
        Assert.Contains(cancelled.Status, new[] { "CANCELLING", "CANCELLED", "COMPLETE" });
        var stopped = await fixture.WaitForMaintenance(chat.Id);
        Assert.Contains(stopped.Maintenance!.Status, new[] { "CANCELLED", "COMPLETE" });
        Assert.Equal(32, fixture.Store.GetHead(chat.Id).CompletedTurns);
        await fixture.Post<ConversationIndexProgress>($"/api/chats/{chat.Id}/history/rebuild", null);
        var completed = await fixture.WaitForMaintenance(chat.Id);
        Assert.Equal("COMPLETE", completed.Maintenance!.Status); Assert.Equal(64, completed.Coverage.IndexedSourceCount);
        var before = await fixture.Get<ConversationEvidenceResult>($"/api/chats/{chat.Id}/history/messages/{first.Id}:user?startOffset=7995&maxCharacters=20");
        await fixture.Restart();
        var restored = await fixture.Get<HistoryStatus>($"/api/chats/{chat.Id}/history/status");
        Assert.Equal("COMPLETE", restored.Coverage.Status); Assert.Null(restored.Maintenance);
        var after = await fixture.Get<ConversationEvidenceResult>($"/api/chats/{chat.Id}/history/messages/{first.Id}:user?startOffset=7995&maxCharacters=20");
        Assert.Equal(Assert.Single(before.Hits).Source, Assert.Single(after.Hits).Source);
        Assert.Equal(before.Hits[0].Excerpt, after.Hits[0].Excerpt);
        Assert.Equal(before.Hits[0].StartOffset, after.Hits[0].StartOffset);
        Assert.Equal(before.Hits[0].EndOffset, after.Hits[0].EndOffset);
        Assert.Empty(fixture.Executor.Contexts);
    }

    [Fact]
    public async Task Fixture_arrangement_and_HTTP_exercise_have_independent_bounded_deadlines()
    {
        var clock = new FixtureTimeProvider();
        await using (var fixture = await ApiFixture.Start(arrangeBeforeExercise: true, timeProvider: clock))
        {
            var chat = await fixture.Create("Deadline phases");
            fixture.Complete(chat.Id, "canonical arrangement", "original response");
            clock.Advance(TimeSpan.FromSeconds(46));
            Assert.False(fixture.Token.IsCancellationRequested);
            fixture.BeginExercise();
            Assert.Equal("NOT_READY", (await fixture.Get<HistoryStatus>($"/api/chats/{chat.Id}/history/status")).Coverage.Status);
            clock.Advance(TimeSpan.FromSeconds(44));
            Assert.False(fixture.Token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(fixture.Token.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Get<HistoryStatus>($"/api/chats/{chat.Id}/history/status"));
            Assert.Equal(1, fixture.Store.GetHead(chat.Id).CompletedTurns);
        }

        var expiredClock = new FixtureTimeProvider();
        await using var expired = await ApiFixture.Start(arrangeBeforeExercise: true, timeProvider: expiredClock);
        expiredClock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(expired.Token.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(expired.BeginExercise);
    }

    [Fact]
    public async Task Off_HTTP_send_has_no_historical_source_retrieval_or_local_index()
    {
        await using var fixture = await ApiFixture.Start();
        var chat = await fixture.Create("Off"); fixture.Complete(chat.Id, "old private source", "old answer");
        var turn = await fixture.SendAndWait(chat.Id, "new question");
        Assert.Equal("completed", turn.Status);
        var context = Assert.Single(fixture.Executor.Contexts);
        Assert.Null(context.EvidenceSource); Assert.Null(context.Recall);
        Assert.False(context.Turn.Settings.ThreadRecallEnabled); Assert.Empty(context.Turn.Settings.Instructions);
        Assert.Equal(0L, fixture.IndexRows());
        var status = await fixture.Get<HistoryStatus>($"/api/chats/{chat.Id}/history/status");
        Assert.Equal("NOT_READY", status.Coverage.Status);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("manual")]
    public async Task Enabled_HTTP_send_passes_exact_older_source_beyond_the_recent_floor_to_the_actual_execution_context(string mode)
    {
        await using var fixture = await ApiFixture.Start();
        var chat = await fixture.Create("Recall");
        var old = fixture.Complete(chat.Id, "The original code is cobalt mango.", "old answer");
        fixture.Complete(chat.Id, "middle dialogue", "middle answer");
        fixture.Complete(chat.Id, "recent dialogue", "recent answer");
        var other = await fixture.Create("Foreign"); fixture.Complete(other.Id, "cobalt mango foreign source", "private answer");
        var settings = chat.Settings with
        {
            ThreadRecallEnabled = true,
            ThreadRecallMode = mode,
            ThreadRecallQuery = mode == "manual" ? "cobalt mango" : "",
            ThreadRecallRecentTurns = 1,
            ThreadRecallNeighborRadius = 0,
            ThreadRecallSelections = mode == "manual" ? [new(old.Id + ":user", 0, old.UserText.Length)] : []
        };
        await fixture.Put<ExperimentConversation>($"/api/chats/{chat.Id}/settings", new ExperimentSettingsUpdate(chat.SettingsRevision, settings));
        var turn = await fixture.SendAndWait(chat.Id, "What was the original code we chose?");
        Assert.Equal("completed", turn.Status);
        var context = Assert.Single(fixture.Executor.Contexts);
        Assert.NotNull(context.EvidenceSource); Assert.Equal(chat.Id, context.EvidenceSource.ScopeRef);
        Assert.Equal(turn.Sequence - 1, context.EvidenceSource.AsOfSequence);
        Assert.DoesNotContain(context.History, message => message.Id.StartsWith(old.Id, StringComparison.Ordinal));
        Assert.Equal(2, context.History.Count);
        var evidence = Assert.Single(context.Recall!.Evidence);
        if (mode == "auto") Assert.Equal("fts5-keyword-or+canonical-verification", context.Recall.Manifest.QueryMethod);
        Assert.Equal(old.Id + ":user", evidence.SourceId); Assert.Equal("user", evidence.Role);
        Assert.Equal(old.UserText[evidence.StartOffset..evidence.EndOffset], evidence.Content);
        Assert.Equal("UNAVAILABLE", context.EvidenceSource.ReadExact(turn.Id + ":user").Status);
        Assert.All(context.Recall.Evidence, item => Assert.InRange(item.TurnSequence, 1, turn.Sequence - 1));
        Assert.Equal(mode == "manual" ? 0L : 1L, fixture.IndexRows());
        var manifest = fixture.Store.ReadEvents(chat.Id, turn.Id, 0, 100);
        Assert.Contains(manifest, item => item.Type == "context.recall");
    }

    private sealed record ApiError(string Code, string? Message);
    private sealed record HistoryStatus(ConversationEvidenceHead Head, ConversationEvidenceCoverage Coverage,
        ConversationIndexProgress? Maintenance);

    private sealed class CaptureExecutor : IExperimentExecutor
    {
        internal ConcurrentQueue<ExperimentExecutionContext> Contexts { get; } = new();
        public Task<ExperimentExecutionResult> ExecuteAsync(ExperimentExecutionContext context, CancellationToken cancellationToken)
        {
            Contexts.Enqueue(context);
            return Task.FromResult(new ExperimentExecutionResult("completed", "fixture answer; no provider called"));
        }
    }

    private sealed class ApiFixture : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("agentica-recall-api-").FullName;
        private readonly TimeProvider _timeProvider;
        private CancellationTokenSource _phaseDeadline = null!;
        private CancellationTokenSource _deadline = null!;
        private WebApplication? _app;
        internal CaptureExecutor Executor { get; } = new();
        internal HttpClient Http { get; private set; } = null!;
        internal ExperimentStore Store => _app!.Services.GetRequiredService<ExperimentStore>();
        internal CancellationToken Token => _deadline.Token;
        private ApiFixture(bool arrangeBeforeExercise, TimeProvider? timeProvider)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
            SetDeadline(arrangeBeforeExercise ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(45));
        }

        internal static async Task<ApiFixture> Start(bool arrangeBeforeExercise = false, TimeProvider? timeProvider = null)
        {
            var fixture = new ApiFixture(arrangeBeforeExercise, timeProvider);
            try { await fixture.StartApplication(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal void BeginExercise()
        {
            Token.ThrowIfCancellationRequested();
            SetDeadline(TimeSpan.FromSeconds(45));
        }

        private void SetDeadline(TimeSpan budget)
        {
            var phase = new CancellationTokenSource(budget, _timeProvider);
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, phase.Token);
            _deadline?.Dispose(); _phaseDeadline?.Dispose();
            _phaseDeadline = phase; _deadline = deadline;
        }

        private async Task StartApplication()
        {
            _app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], storageDirectory: _directory,
                openAiConfiguration: new OpenAiProviderConfiguration(_ => null), experimentExecutor: Executor);
            await _app.StartAsync(Token);
            Http = new HttpClient { BaseAddress = new Uri(_app.Urls.Single()) };
        }

        internal async Task Restart()
        {
            Http.Dispose(); await _app!.StopAsync(Token); await _app.DisposeAsync(); _app = null;
            await StartApplication();
        }

        internal Task<ExperimentConversation> Create(string title) => Post<ExperimentConversation>("/api/chats", new ExperimentCreateRequest(title));
        internal ExperimentTurn Complete(string id, string user, string answer)
        {
            Token.ThrowIfCancellationRequested();
            var turn = Store.Begin(id, new(user)).Turn;
            return Store.Complete(id, turn.Id, new("completed", answer));
        }

        internal async Task<T> Get<T>(string url) => (await Http.GetFromJsonAsync<T>(url, HostProtocol.Json, Token))!;
        internal async Task<T> Post<T>(string url, object? value)
        {
            using var response = value is null ? await Http.PostAsync(url, null, Token) : await Http.PostAsJsonAsync(url, value, HostProtocol.Json, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<T>(HostProtocol.Json, Token))!;
        }
        internal async Task<T> Put<T>(string url, object value)
        {
            using var response = await Http.PutAsJsonAsync(url, value, HostProtocol.Json, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<T>(HostProtocol.Json, Token))!;
        }
        internal async Task<ExperimentTurn> SendAndWait(string id, string text)
        {
            var turn = await Post<ExperimentTurn>($"/api/chats/{id}/turns", new ExperimentSendRequest(text));
            var stream = await Http.GetStringAsync($"/api/chats/{id}/turns/{turn.Id}/events", Token);
            Assert.Contains("turn.completed", stream);
            return await Get<ExperimentTurn>($"/api/chats/{id}/turns/{turn.Id}");
        }
        internal async Task<HistoryStatus> WaitForMaintenance(string id)
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                var status = await Get<HistoryStatus>($"/api/chats/{id}/history/status");
                if (status.Maintenance is { Status: not ("INDEXING" or "CANCELLING") }) return status;
                await Task.Delay(20, Token);
            }
            throw new TimeoutException("The fixture's explicit local index maintenance did not finish.");
        }
        internal long IndexRows()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(_directory, "chat", "experiments.sqlite3"), Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM conversation_index_state";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        public async ValueTask DisposeAsync()
        {
            Http?.Dispose();
            if (_app is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await _app.StopAsync(cleanup.Token); }
                finally { await _app.DisposeAsync(); _app = null; }
            }
            _deadline.Dispose(); _phaseDeadline.Dispose(); Directory.Delete(_directory, recursive: true);
        }
    }

    // Only one-shot fixture deadlines use this clock. Advancing it exercises
    // cancellation boundaries deterministically without delaying HTTP or storage.
    private sealed class FixtureTimeProvider : TimeProvider
    {
        private readonly List<FixtureTimer> _timers = [];
        private TimeSpan _elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FixtureTimer(this, callback, state);
            timer.Change(dueTime, period); _timers.Add(timer); return timer;
        }

        internal void Advance(TimeSpan elapsed)
        {
            _elapsed += elapsed;
            foreach (var timer in _timers.ToArray()) timer.FireIfDue();
        }

        private sealed class FixtureTimer(FixtureTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan _due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("The fixture supports one-shot deadlines only.");
                _due = dueTime == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : owner._elapsed + dueTime;
                return true;
            }
            internal void FireIfDue()
            {
                if (_disposed || _due > owner._elapsed) return;
                _due = TimeSpan.MaxValue; callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
