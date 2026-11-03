using System.Net.WebSockets;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed class HostStartSocketReplayTests
{
    [Fact]
    public async Task Lost_original_start_reply_reconnect_reconciles_the_same_pending_action_without_new_work()
    {
        await using var fixture = await ReplayFixture.Open();
        var submission = BoundedSubmission();
        var originalSocket = await fixture.Connect();
        await Send(originalSocket, submission, "lost-original-start", fixture.Token);
        await fixture.WaitFor(() => fixture.Registry.List().Length == 1);
        var run = fixture.Registry.Get(HostProtocol.Element(fixture.Registry.List()[0]).GetProperty("runId").GetString()!);
        await fixture.WaitFor(() => run.HasUnresolvedActions);
        var admission = fixture.Registry.GetStart(new("host", "session", submission.AttemptId));
        var pending = Assert.Single(HostProtocol.Element(run.Snapshot(false)).GetProperty("pendingActions").EnumerateArray());
        Assert.True(pending.GetProperty("deliveryAttempted").GetBoolean());
        var original = pending.GetProperty("request").Deserialize<HostActionRequest>(HostProtocol.Json)!;
        var usage = run.Usage;
        Assert.Equal(new HostRunUsage(Steps: 1, ProviderCalls: 1), usage);
        // Neither the correlated start acknowledgement nor its original action is consumed.
        originalSocket.Abort();
        await fixture.WaitFor(() => !HostProtocol.Element(run.Snapshot(false)).GetProperty("connected").GetBoolean());

        var retry = await fixture.Connect();
        await Send(retry, submission, "replay-original-start", fixture.Token);
        AssertBinding(await ReceiveBinding(retry, "replay-original-start", fixture.Token), run, admission);
        var resumed = await Receive(retry, "resumed", fixture.Token, rejectActions: true);
        Assert.Equal(usage, resumed.GetProperty("payload").GetProperty("usage").Deserialize<HostRunUsage>(HostProtocol.Json));
        var reconciled = await Receive(retry, "action.reconcile", fixture.Token, rejectNewAction: true);
        Assert.Equal(run.RunId, reconciled.GetProperty("runId").GetString());
        Assert.Equal(ProtocolValidation.Digest(original), ProtocolValidation.Digest(reconciled.GetProperty("payload")));
        AssertRetained(fixture, run, admission, submission, usage);

        await SendMessage(retry, "action.result", Result(original), "reconciled-result", run.RunId, fixture.Token);
        await Receive(retry, "outcome", fixture.Token, rejectActions: true);
        await run.Completion.WaitAsync(fixture.Token);
        Assert.False(run.HasUnresolvedActions);
        AssertRetained(fixture, run, admission, submission, usage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnected_exact_start_replays_finished_or_parked_outcome_without_replanning(bool parked)
    {
        await using var fixture = await ReplayFixture.Open();
        var submission = BoundedSubmission();
        var originalSocket = await fixture.Connect();
        await Send(originalSocket, submission, "original-start", fixture.Token);
        var binding = await ReceiveBinding(originalSocket, "original-start", fixture.Token);
        var admission = binding.GetProperty("payload").GetProperty("startAdmission").Deserialize<HostStartEntry>(HostProtocol.Json)!;
        var run = fixture.Registry.Get(admission.RunId);
        var original = (await Receive(originalSocket, "action.request", fixture.Token)).GetProperty("payload")
            .Deserialize<HostActionRequest>(HostProtocol.Json)!;
        await SendMessage(originalSocket, "action.result", Result(original, operation: parked), "original-result", run.RunId, fixture.Token);
        JsonElement? originalParked = parked ? await Receive(originalSocket, "operation.parked", fixture.Token, rejectActions: true) : null;
        var outcome = await Receive(originalSocket, "outcome", fixture.Token, rejectActions: true);
        await run.Completion.WaitAsync(fixture.Token);
        var usage = run.Usage;
        Assert.Equal(new HostRunUsage(Steps: 1, ProviderCalls: 1), usage);
        originalSocket.Abort();
        await fixture.WaitFor(() => !HostProtocol.Element(run.Snapshot(false)).GetProperty("connected").GetBoolean());

        var retry = await fixture.Connect();
        await Send(retry, submission, "terminal-start-replay", fixture.Token);
        AssertBinding(await ReceiveBinding(retry, "terminal-start-replay", fixture.Token), run, admission);
        var resumed = await Receive(retry, "resumed", fixture.Token, rejectActions: true);
        Assert.Equal(usage, resumed.GetProperty("payload").GetProperty("usage").Deserialize<HostRunUsage>(HostProtocol.Json));
        if (parked)
            Assert.Equal(ProtocolValidation.Digest(originalParked!.Value),
                ProtocolValidation.Digest(await Receive(retry, "operation.parked", fixture.Token, rejectActions: true)));
        Assert.Equal(ProtocolValidation.Digest(outcome),
            ProtocolValidation.Digest(await Receive(retry, "outcome", fixture.Token, rejectActions: true)));
        AssertRetained(fixture, run, admission, submission, usage);
    }

    private static HostStartSubmission BoundedSubmission() => Submission() with
    {
        Request = Request() with
        {
            Limits = new(MaxSteps: 4, MaxRefinements: 3, MaxPlanContinuations: 2,
                TimeoutSeconds: 60, ActionTimeoutSeconds: 30)
        }
    };

    private static void AssertBinding(JsonElement message, HostRun run, HostStartEntry admission)
    {
        Assert.Equal(run.RunId, message.GetProperty("runId").GetString());
        var payload = message.GetProperty("payload");
        Assert.Equal(ProtocolValidation.Digest(admission), ProtocolValidation.Digest(payload.GetProperty("startAdmission")));
        Assert.Equal(run.Request.ObjectiveId, payload.GetProperty("objectiveId").GetString());
        Assert.Equal(run.Request.SessionEpoch, payload.GetProperty("sessionEpoch").GetString());
        Assert.Equal(run.ManifestHash, payload.GetProperty("manifestHash").GetString());
    }

    private static void AssertRetained(ReplayFixture fixture, HostRun run, HostStartEntry admission,
        HostStartSubmission submission, HostRunUsage usage)
    {
        Assert.Equal(1, fixture.Planners.Created);
        Assert.Equal(1, fixture.Planners.Planned);
        Assert.Equal(0, fixture.Planners.Refined);
        Assert.Single(fixture.Registry.List());
        Assert.Same(run, fixture.Registry.Get(admission.RunId));
        Assert.Equal(usage, run.Usage);
        Assert.Equal(submission.Request.Limits, run.Request.Limits);
        Assert.Equal(ProtocolValidation.Digest(admission),
            ProtocolValidation.Digest(fixture.Registry.GetStart(new("host", "session", submission.AttemptId))));
    }

    private static Task Send(ClientWebSocket socket, HostStartSubmission submission, string requestId, CancellationToken token) =>
        SendMessage(socket, "start.durable", submission, requestId, null, token);

    private static Task SendMessage(ClientWebSocket socket, string type, object payload, string requestId,
        string? runId, CancellationToken token) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(
            new HostMessage(type, HostProtocol.Element(payload), requestId, runId), HostProtocol.Json).AsMemory(),
            WebSocketMessageType.Text, true, token).AsTask();

    private static async Task<JsonElement> ReceiveBinding(ClientWebSocket socket, string requestId, CancellationToken token)
    {
        for (var i = 0; i < 256; i++)
        {
            var message = await ReceiveMessage(socket, token);
            var type = message.GetProperty("type").GetString();
            if (type == "started")
            {
                Assert.Equal(requestId, message.GetProperty("requestId").GetString());
                return message;
            }
            Assert.DoesNotContain(type, new[] { "error", "resumed", "action.request", "action.reconcile", "operation.parked", "outcome", "run.terminated" });
        }
        throw new InvalidOperationException("Missing correlated durable-start binding.");
    }

    private static async Task<JsonElement> Receive(ClientWebSocket socket, string expected, CancellationToken token,
        bool rejectActions = false, bool rejectNewAction = false)
    {
        for (var i = 0; i < 256; i++)
        {
            var message = await ReceiveMessage(socket, token);
            var type = message.GetProperty("type").GetString();
            if (type == expected) return message;
            Assert.DoesNotContain(type, new[] { "error", "run.terminated" });
            if (rejectActions) Assert.DoesNotContain(type, new[] { "action.request", "action.reconcile" });
            if (rejectNewAction) Assert.NotEqual("action.request", type);
        }
        throw new InvalidOperationException("Expected durable-start replay message missing: " + expected);
    }

    private static async Task<JsonElement> ReceiveMessage(ClientWebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), token);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            stream.Write(buffer, 0, received.Count);
            Assert.True(stream.Length <= HostProtocol.MaxMessageBytes);
            if (received.EndOfMessage) break;
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private sealed class ReplayFixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _storage = Directory.CreateTempSubdirectory("agentica-start-replay-");
        private readonly WebApplication _app;
        private readonly List<ClientWebSocket> _sockets = [];
        private readonly CancellationTokenSource _deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        public ReplayPlannerFactory Planners { get; } = new();
        public HostRunRegistry Registry => _app.Services.GetRequiredService<HostRunRegistry>();
        public CancellationToken Token => _deadline.Token;
        private ReplayFixture()
        {
            _deadline.CancelAfter(TimeSpan.FromSeconds(45));
            _app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], Planners, _storage.FullName);
        }
        public static async Task<ReplayFixture> Open()
        {
            var fixture = new ReplayFixture();
            await fixture._app.StartAsync(fixture.Token);
            return fixture;
        }
        public async Task<ClientWebSocket> Connect()
        {
            var socket = new ClientWebSocket();
            _sockets.Add(socket);
            await socket.ConnectAsync(new UriBuilder(_app.Urls.Single()) { Scheme = "ws", Path = "/api/host" }.Uri, Token);
            return socket;
        }
        public async Task WaitFor(Func<bool> predicate)
        {
            while (!predicate()) await Task.Delay(10, Token);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var socket in _sockets) { socket.Abort(); socket.Dispose(); }
            var runs = Registry.List().Select(snapshot => Registry.Get(HostProtocol.Element(snapshot).GetProperty("runId").GetString()!)).ToArray();
            foreach (var run in runs) if (!run.Terminal) run.Cancel();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.WhenAll(runs.Select(run => run.Completion)).WaitAsync(cleanup.Token);
            await _app.StopAsync(cleanup.Token);
            await _app.DisposeAsync();
            _deadline.Dispose();
            _storage.Delete(recursive: true);
        }
    }

    private sealed class ReplayPlannerFactory : ILabPlannerFactory
    {
        public int Created;
        public int Planned;
        public int Refined;
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
        {
            Interlocked.Increment(ref Created);
            return new ReplayPlanner(this, onStreamEvent);
        }
        public IReadOnlyList<ProviderMetadata> GetProviders() =>
            [new("fixture", "offline-model", true, null, "offline", DefaultThinkingEffort: "low")];
        private sealed class ReplayPlanner(ReplayPlannerFactory owner, Action<LlmStreamEvent> report) : IWorkflowPlanner
        {
            public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner.Planned);
                report(new(LlmStreamEventKind.Started));
                return Task.FromResult(new WorkflowPlan("plan", 1,
                    [new PlanStep("step", "host.operate", ToolKind.Action, ToolEffect.WritesLocalState, new Dictionary<string, object?>())],
                    "One retained host action."));
            }
            public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner.Refined);
                throw new InvalidOperationException("No refinement belongs in durable-start replay.");
            }
        }
    }
}
