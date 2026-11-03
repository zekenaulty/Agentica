using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentica.Lab.Web.Chat;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

public sealed class ExperimentNativeAdmissionTests
{
    [Fact]
    public async Task Real_HTTP_chat_preserves_large_input_four_turn_floor_and_recall_using_native_token_counts()
    {
        await using var fixture = await Fixture.Start(arrangeBeforeExercise: true);
        var chat = fixture.Store.Create(Settings(), "Measured synthetic chat");
        for (var i = 1; i <= 22; i++)
        {
            fixture.Complete(chat.Id, i == 1 ? "needle exact historical source" : new string('u', i == 22 ? 28338 : 500),
                new string('a', i == 21 ? 4509 : 1400));
        }
        var other = fixture.Store.Create(new(), "Other");
        fixture.Complete(other.Id, "needle foreign-owned source", "foreign-owned answer");
        var current = new string('x', 62427);
        fixture.BeginExercise();
        using var sent = await fixture.Http.PostAsJsonAsync($"/api/chats/{chat.Id}/turns", new ExperimentSendRequest(current, "native-count-test"), HostProtocol.Json, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var admitted = (await sent.Content.ReadFromJsonAsync<ExperimentTurn>(HostProtocol.Json, fixture.Token))!;
        var stream = await fixture.Http.GetStringAsync($"/api/chats/{chat.Id}/turns/{admitted.Id}/events", fixture.Token);
        Assert.Contains("turn.completed", stream, StringComparison.Ordinal);
        Assert.Equal("completed", fixture.Store.GetTurn(chat.Id, admitted.Id).Status);
        var wire = Assert.Single(fixture.Handler.GenerationBodies);
        Assert.True(Encoding.UTF8.GetByteCount(wire) > 62976);
        Assert.DoesNotContain("foreign-owned", wire, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(wire);
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("instructions", out _));
        var input = body.RootElement.GetProperty("input");
        Assert.Equal(9, input.GetArrayLength()); // Four exact complete pairs plus current input/evidence.
        var parts = input[8].GetProperty("content");
        Assert.Equal(current, parts[1].GetProperty("text").GetString());
        Assert.Contains("needle exact historical source", parts[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal(2, fixture.Handler.CountBodies.Count); // Mandatory floor reused at dispatch; optional evidence counted once.
        Assert.Equal("count", fixture.Handler.Order[0]);
        Assert.Equal("generate", fixture.Handler.Order[^1]);
        var manifest = fixture.Store.ReadEvents(chat.Id, admitted.Id, 0, 100).Single(item => item.Type == "context.manifest");
        var projection = JsonSerializer.SerializeToElement(manifest.Data, HostProtocol.Json);
        Assert.Equal(35000, projection.GetProperty("tokenCount").GetProperty("tokens").GetInt32());
        Assert.True(projection.GetProperty("tokenCount").GetProperty("providerCount").GetBoolean());
        Assert.Equal(62976, projection.GetProperty("inputAllowance").GetInt32());
        Assert.Equal(4, chat.Settings.ThreadRecallRecentTurns);
    }

    [Fact]
    public async Task Large_fixture_arrangement_does_not_consume_the_bounded_native_count_exercise()
    {
        var clock = new FixtureTimeProvider();
        await using (var fixture = await Fixture.Start(arrangeBeforeExercise: true, timeProvider: clock))
        {
            var chat = fixture.Store.Create(Settings(), null);
            fixture.Complete(chat.Id, "canonical user source", "canonical assistant source");
            clock.Advance(TimeSpan.FromSeconds(31));
            Assert.False(fixture.Token.IsCancellationRequested);
            fixture.BeginExercise();
            using var response = await fixture.Http.PostAsJsonAsync($"/api/chats/{chat.Id}/context-check",
                new ExperimentSendRequest("exact current input"), HostProtocol.Json, fixture.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(fixture.Handler.GenerationBodies);
            Assert.Equal(1, fixture.Store.GetHead(chat.Id).CompletedTurns);
            clock.Advance(TimeSpan.FromSeconds(29));
            Assert.False(fixture.Token.IsCancellationRequested);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(fixture.Token.IsCancellationRequested);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Http.GetAsync("/api/chats", fixture.Token));
        }

        var expiredClock = new FixtureTimeProvider();
        await using var expired = await Fixture.Start(arrangeBeforeExercise: true, timeProvider: expiredClock);
        expiredClock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(expired.Token.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(expired.BeginExercise);
    }

    [Fact]
    public async Task Context_check_uses_the_production_counter_without_creating_a_turn_or_generating_a_response()
    {
        await using var fixture = await Fixture.Start();
        var chat = fixture.Store.Create(Settings() with { ThreadRecallRecentTurns = 0 }, null);
        var text = new string('x', 62427);
        using var response = await fixture.Http.PostAsJsonAsync($"/api/chats/{chat.Id}/context-check", new ExperimentSendRequest(text), HostProtocol.Json, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(fixture.Token));
        Assert.False(body.RootElement.GetProperty("inferenceDispatched").GetBoolean());
        Assert.False(body.RootElement.GetProperty("turnCreated").GetBoolean());
        Assert.True(body.RootElement.GetProperty("manifest").GetProperty("tokenCount").GetProperty("providerCount").GetBoolean());
        Assert.Equal(29000, body.RootElement.GetProperty("manifest").GetProperty("tokenCount").GetProperty("tokens").GetInt32());
        Assert.Empty(fixture.Store.GetTurnsPage(chat.Id).Turns);
        Assert.Empty(fixture.Handler.GenerationBodies);
        Assert.Single(fixture.Handler.CountBodies);
        Assert.Equal(1, fixture.Store.GetHeader(chat.Id).SettingsRevision);
    }

    [Fact]
    public async Task Count_failure_rejects_before_admission_without_inference_or_heuristic_fallback()
    {
        await using var fixture = await Fixture.Start();
        fixture.Handler.FailCount = true;
        var chat = fixture.Store.Create(Settings(), null);
        using var response = await fixture.Http.PostAsJsonAsync($"/api/chats/{chat.Id}/turns", new ExperimentSendRequest(new string('x', 62427)), HostProtocol.Json, fixture.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(fixture.Token));
        Assert.Equal("context.token_count_unavailable", body.RootElement.GetProperty("code").GetString());
        Assert.Empty(fixture.Store.GetTurnsPage(chat.Id).Turns);
        Assert.Empty(fixture.Handler.GenerationBodies);
        Assert.Single(fixture.Handler.CountBodies);
    }

    [Fact]
    public async Task Saved_configuration_changes_during_count_are_rejected_before_turn_admission()
    {
        await using var fixture = await Fixture.Start();
        var chat = fixture.Store.Create(Settings(), null);
        fixture.Handler.DuringCount = () => fixture.Store.UpdateSettings(chat.Id, new(1, chat.Settings with { ReasoningEffort = "high" }));
        using var response = await fixture.Http.PostAsJsonAsync($"/api/chats/{chat.Id}/turns", new ExperimentSendRequest("exact current input"), HostProtocol.Json, fixture.Token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(fixture.Token));
        Assert.Equal("settings.conflict", body.RootElement.GetProperty("code").GetString());
        Assert.Empty(fixture.Store.GetTurnsPage(chat.Id).Turns);
        Assert.Empty(fixture.Handler.GenerationBodies);
    }

    private static ExperimentSettings Settings() => new()
    {
        Model = "gpt-6-sol",
        ReasoningEffort = "xhigh",
        AmbientWebSearch = true,
        MaxWebSearchCalls = 20,
        ThreadRecallEnabled = true,
        ThreadRecallMode = "auto",
        ThreadRecallQuery = "needle",
        ThreadRecallRecentTurns = 4,
        ThreadRecallNeighborRadius = 0,
        ThreadRecallMaxCalls = 15,
        MaxOutputTokens = 64000,
        ContextWindowTokens = 131072,
        MaxInputCharacters = 256000,
        MaxHistoryMessages = 128,
        HistoryPolicy = "recent"
    };

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> CountBodies { get; } = [];
        public List<string> GenerationBodies { get; } = [];
        public List<string> Order { get; } = [];
        public bool FailCount { get; set; }
        public Action? DuringCount { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/input_tokens", StringComparison.Ordinal))
            {
                CountBodies.Add(body); Order.Add("count");
                DuringCount?.Invoke(); DuringCount = null;
                return new(FailCount ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = JsonContent.Create(new { @object = "response.input_tokens", input_tokens = body.Contains("historicalThreadEvidence", StringComparison.Ordinal) ? 35000 : 29000 }) };
            }
            GenerationBodies.Add(body); Order.Add("generate");
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("data: " + JsonSerializer.Serialize(new
                {
                    type = "response.completed",
                    response = new
                    {
                        id = "synthetic-response",
                        status = "completed",
                        output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "Measured admission completed." } } } }
                    }
                }) + "\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("agentica-native-admission-").FullName;
        private readonly TimeProvider _timeProvider;
        private CancellationTokenSource _phaseDeadline = null!;
        private CancellationTokenSource _deadline = null!;
        private bool _arranging;
        private WebApplication? _app;
        private OpenAiExperimentExecutor _executor = null!;
        private HttpClient _provider = null!;
        public Handler Handler { get; } = new();
        public ExperimentStore Store => _app!.Services.GetRequiredService<ExperimentStore>();
        public HttpClient Http { get; private set; } = null!;
        public CancellationToken Token => _deadline.Token;
        private Fixture(bool arrangeBeforeExercise, TimeProvider? timeProvider)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
            _arranging = arrangeBeforeExercise;
            SetDeadline(arrangeBeforeExercise ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(30));
        }

        public static async Task<Fixture> Start(bool arrangeBeforeExercise = false, TimeProvider? timeProvider = null)
        {
            var fixture = new Fixture(arrangeBeforeExercise, timeProvider);
            try
            {
                fixture._provider = new HttpClient(fixture.Handler);
                var config = new OpenAiProviderConfiguration(name => name == "OPENAI_API_KEY" ? "synthetic-fixture-key" : null);
                fixture._executor = new OpenAiExperimentExecutor(config, fixture._provider);
                fixture._app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default", "Warning"],
                    storageDirectory: fixture._directory, openAiConfiguration: config, experimentExecutor: fixture._executor);
                await fixture._app.StartAsync(fixture.Token);
                fixture.Http = new HttpClient { BaseAddress = new Uri(fixture._app.Urls.Single()) };
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public void BeginExercise()
        {
            Token.ThrowIfCancellationRequested();
            if (!_arranging) throw new InvalidOperationException("The fixture exercise already owns its deadline.");
            SetDeadline(TimeSpan.FromSeconds(30));
            _arranging = false;
        }

        private void SetDeadline(TimeSpan budget)
        {
            var phase = new CancellationTokenSource(budget, _timeProvider);
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, phase.Token);
            _deadline?.Dispose(); _phaseDeadline?.Dispose();
            _phaseDeadline = phase; _deadline = deadline;
        }

        public void Complete(string chatId, string user, string answer)
        {
            Token.ThrowIfCancellationRequested();
            var turn = Store.Begin(chatId, new(user)).Turn;
            Token.ThrowIfCancellationRequested();
            Store.Complete(chatId, turn.Id, new("completed", answer));
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
            _executor?.Dispose(); _provider?.Dispose(); _deadline.Dispose(); _phaseDeadline.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    // Only fixture phase deadlines use this clock; HTTP, storage and the executor
    // retain their actual implementations and do not wait for simulated delays.
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
