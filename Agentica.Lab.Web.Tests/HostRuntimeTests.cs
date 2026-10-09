using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Agentica.Artifacts;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

public sealed class HostRuntimeTests
{
    [Fact]
    public async Task Real_socket_runs_full_streamed_demo_loop_and_completes_with_host_receipts()
    {
        await using var service = await SocketService.StartAsync();
        var socket = await service.ConnectAsync();
        await SendAsync(socket, "start", Request(demo: true));
        var progress = new List<JsonElement>();
        var actions = new List<HostActionRequest>();
        var revision = 0L;
        OutcomeEnvelope? outcome = null;
        while (outcome is null)
        {
            var message = await ReceiveAsync(socket);
            switch (Type(message))
            {
                case "started": service.Track(message.GetProperty("runId").GetString()!); break;
                case "progress": progress.Add(message.GetProperty("payload").Clone()); break;
                case "action.request":
                    Assert.NotEmpty(progress);
                    var action = message.GetProperty("payload").Deserialize<HostActionRequest>(HostProtocol.Json)!;
                    Assert.Equal(revision, action.ExpectedRevision);
                    actions.Add(action);
                    if (action.CapabilityId == "demo.accept") revision++;
                    await SendAsync(socket, "action.result", Applied(action, revision,
                        complete: action.CapabilityId == "demo.accept"), action.RunId);
                    break;
                case "outcome": outcome = message.GetProperty("payload").Deserialize<OutcomeEnvelope>(HostProtocol.Json); break;
                case "error": Assert.Fail(message.GetRawText()); break;
            }
        }
        Assert.Equal(["demo.inspect", "demo.accept"], actions.Select(action => action.CapabilityId));
        Assert.Equal(2, progress.Select(item => item.GetProperty("record").GetProperty("callId").GetString()).Distinct().Count());
        Assert.Contains(progress, item => item.GetProperty("record").GetProperty("outputCharacters").GetInt64() > 0);
        Assert.Equal(RunOutcomeStatus.Succeeded, outcome.Outcome.Status);
        Assert.Equal(2, outcome.Receipts.Items.Count);
        Assert.All(outcome.Receipts.Items, receipt => Assert.Equal(ReceiptStatus.Succeeded, receipt.Status));
        Assert.NotEmpty(outcome.Outcome.CompletionEvidence);
        var proof = Assert.Single(outcome.Details.Artifacts, artifact => artifact.Kind == "host.objective.completed");
        Assert.Contains(proof.Evidence, reference => outcome.Receipts.Items.Any(receipt => receipt.ReceiptId == reference.RefId));
        Assert.Equal(1, revision);
    }

    [Fact]
    public async Task Different_epoch_cannot_bypass_pending_session_writer()
    {
        await using var service = await SocketService.StartAsync();
        var first = await service.ConnectAsync();
        var request = Request();
        await SendAsync(first, "start", request);
        var started = await ReceiveUntilAsync(first, "started");
        service.Track(started.GetProperty("runId").GetString()!);
        await ReceiveUntilAsync(first, "action.request");
        var second = await service.ConnectAsync();
        await SendAsync(second, "start", request with { SessionEpoch = "epoch-two" });
        var rejection = await ReceiveUntilAsync(second, "error");
        Assert.Equal("session.unresolved", rejection.GetProperty("payload").GetProperty("code").GetString());
        Assert.Single(service.Registry.List());
    }

    [Fact]
    public async Task Reconnect_reconciles_original_action_and_retained_effect_result_once()
    {
        await using var service = await SocketService.StartAsync();
        var socket = await service.ConnectAsync();
        var request = Request();
        await SendAsync(socket, "start", request);
        var started = await ReceiveUntilAsync(socket, "started");
        var runId = started.GetProperty("runId").GetString()!;
        var run = service.Track(runId);
        var original = (await ReceiveUntilAsync(socket, "action.request")).GetProperty("payload")
            .Deserialize<HostActionRequest>(HostProtocol.Json)!;
        // The host committed once, but the connection disappears before its result reaches Lab.
        var appliedActionIds = new HashSet<string>(StringComparer.Ordinal) { original.ActionId };
        var retained = Applied(original, 1, complete: true);
        socket.Abort();
        await WaitUntilAsync(() => !Snapshot(run).GetProperty("connected").GetBoolean());
        Assert.True(run.HasUnresolvedActions);

        var resumed = await service.ConnectAsync();
        await SendAsync(resumed, "resume", new ResumeRequest(runId, request.SessionId, request.SessionEpoch), runId);
        var reconcile = await ReceiveUntilAsync(resumed, "action.reconcile");
        var replayed = reconcile.GetProperty("payload").Deserialize<HostActionRequest>(HostProtocol.Json)!;
        Assert.Equal(ProtocolValidation.Digest(original), ProtocolValidation.Digest(replayed));
        Assert.Contains(replayed.ActionId, appliedActionIds);
        await SendAsync(resumed, "action.result", retained, runId);
        var outcome = (await ReceiveUntilAsync(resumed, "outcome")).GetProperty("payload").Deserialize<OutcomeEnvelope>(HostProtocol.Json)!;
        Assert.Equal(RunOutcomeStatus.Succeeded, outcome.Outcome.Status);
        Assert.Single(outcome.Receipts.Items);
        Assert.Single(appliedActionIds);
        Assert.False(run.HasUnresolvedActions);
    }

    [Fact]
    public async Task Wrong_identity_stale_revision_and_changed_duplicates_are_rejected()
    {
        await using var harness = new RuntimeHarness();
        var connection = new CaptureConnection();
        var run = harness.Create(Request(), connection);
        var action = await connection.NextActionAsync();
        var valid = Applied(action, 1, complete: true);
        Assert.Equal("action.unknown", Assert.Throws<HostProtocolException>(() => run.AcceptResult(valid with { ActionId = "another-action" })).Code);
        Assert.Equal("action.session", Assert.Throws<HostProtocolException>(() => run.AcceptResult(valid with { SessionEpoch = "another-epoch" })).Code);
        Assert.Equal("action.revision", Assert.Throws<HostProtocolException>(() => run.AcceptResult(valid with
        {
            BeforeRevision = 1,
            AfterRevision = 2,
            Observation = Observation("stale", 2)
        })).Code);
        Assert.Equal("completion.binding", Assert.Throws<HostProtocolException>(() => run.AcceptResult(valid with
        {
            Completion = valid.Completion! with { ObjectiveId = "another-objective" }
        })).Code);
        Assert.True(run.AcceptResult(valid));
        Assert.False(run.AcceptResult(valid));
        Assert.Equal("action.duplicate_changed", Assert.Throws<HostProtocolException>(() => run.AcceptResult(valid with { Summary = "Changed receipt" })).Code);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(Outcome(run).Receipts.Items);
        Assert.Equal(RunOutcomeStatus.Succeeded, Outcome(run).Outcome.Status);
    }

    [Fact]
    public async Task Cancellation_preserves_effect_custody_until_late_receipt_without_retroactive_success()
    {
        await using var harness = new RuntimeHarness();
        var request = Request();
        var connection = new CaptureConnection();
        var run = harness.Create(request, connection);
        var action = await connection.NextActionAsync();
        run.Cancel();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunOutcomeStatus.Cancelled, Outcome(run).Outcome.Status);
        Assert.True(run.HasUnresolvedActions);
        Assert.Equal("session.unresolved", Assert.Throws<HostProtocolException>(() => harness.Registry.Create(request with { SessionEpoch = "new-epoch" })).Code);

        Assert.True(run.AcceptResult(Applied(action, 1, complete: true)));
        Assert.False(run.HasUnresolvedActions);
        Assert.Equal(RunOutcomeStatus.Cancelled, Outcome(run).Outcome.Status);
        Assert.Empty(Outcome(run).Outcome.CompletionEvidence);
        Assert.Empty(Snapshot(run).GetProperty("pendingActions").EnumerateArray());
        Assert.NotNull(harness.Registry.Create(request with { SessionEpoch = "new-epoch" }));
    }

    [Fact]
    public async Task Disposed_terminal_connection_cannot_overwrite_successful_outcome()
    {
        await using var harness = new RuntimeHarness();
        var connection = new CaptureConnection(throwOnOutcome: true);
        var run = harness.Create(Request(), connection);
        var action = await connection.NextActionAsync();
        run.AcceptResult(Applied(action, 1, complete: true));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunOutcomeStatus.Succeeded, Outcome(run).Outcome.Status);
        Assert.Equal("succeeded", Snapshot(run).GetProperty("status").GetString());
        Assert.True(run.Terminal);
        Assert.False(run.HasUnresolvedActions);
    }

    [Fact]
    public async Task Delayed_resume_does_not_dispatch_an_expired_removed_action()
    {
        await using var harness = new RuntimeHarness();
        var run = harness.Create(Request() with { Limits = new HostRunLimits(ActionTimeoutSeconds: 1) });
        await WaitUntilAsync(() => Snapshot(run).GetProperty("pendingActions").GetArrayLength() == 1);
        var connection = new CaptureConnection(blockResume: true);
        run.Attach(connection);
        var replay = run.ReplayPendingAsync(CancellationToken.None);
        await connection.ResumeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        connection.ReleaseResume.TrySetResult();
        await replay.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(connection.Messages, message => message.Type is "action.request" or "action.reconcile");
        Assert.False(run.HasUnresolvedActions);
    }

    [Fact]
    public async Task Oversized_fragmented_socket_is_closed_without_processing_trailing_fragment()
    {
        await using var service = await SocketService.StartAsync();
        var socket = await service.ConnectAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var oversized = new byte[HostProtocol.MaxMessageBytes + 1];
        Array.Fill(oversized, (byte)' ');
        var tail = JsonSerializer.SerializeToUtf8Bytes(new HostMessage("start", HostProtocol.Element(Request())), HostProtocol.Json);
        var closed = false;
        try
        {
            await socket.SendAsync(oversized.AsMemory(), WebSocketMessageType.Text, false, deadline.Token);
            await socket.SendAsync(tail.AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
        }
        catch (WebSocketException) { closed = true; }
        while (!closed)
        {
            try
            {
                var buffer = new byte[65536];
                var response = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
                closed = response.MessageType == WebSocketMessageType.Close;
            }
            catch (WebSocketException) { closed = true; }
        }
        Assert.True(closed);
        Assert.Empty(service.Registry.List());
    }

    [Fact]
    public async Task Service_restart_fences_session_until_exact_original_result_is_recovered_over_http()
    {
        var storage = Path.Combine(Path.GetTempPath(), "agentica-restart-tests", Guid.NewGuid().ToString("N"));
        var request = Request();
        HostActionRequest original;
        await using (var first = await SocketService.StartAsync(storage, deleteStorage: false))
        {
            var socket = await first.ConnectAsync();
            await SendAsync(socket, "start", request);
            var started = await ReceiveUntilAsync(socket, "started");
            first.Track(started.GetProperty("runId").GetString()!);
            original = (await ReceiveUntilAsync(socket, "action.request")).GetProperty("payload")
                .Deserialize<HostActionRequest>(HostProtocol.Json)!;
            socket.Abort(); // Host committed; service has no result.
        }
        var retainedResult = Applied(original, 1, complete: true);
        await using var restarted = await SocketService.StartAsync(storage);
        using var http = new HttpClient { BaseAddress = restarted.BaseAddress };
        var recoveryPath = $"/api/recovery?hostId={request.HostId}&sessionId={request.SessionId}";
        var pending = await http.GetFromJsonAsync<ActionCustodyEntry[]>(recoveryPath, HostProtocol.Json);
        Assert.Equal(original.ActionId, Assert.Single(pending!).Request.ActionId);
        var nextSocket = await restarted.ConnectAsync();
        await SendAsync(nextSocket, "start", request with { SessionEpoch = "different-epoch" });
        var rejection = await ReceiveUntilAsync(nextSocket, "error");
        Assert.Equal("session.unresolved", rejection.GetProperty("payload").GetProperty("code").GetString());
        Assert.Empty(restarted.Registry.List());

        using var wrong = await http.PostAsJsonAsync("/api/recovery", new HostRecoveryRequest(request.HostId, request.SessionId,
            retainedResult with { SessionEpoch = "wrong" }), HostProtocol.Json);
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        using var resolution = await http.PostAsJsonAsync("/api/recovery",
            new HostRecoveryRequest(request.HostId, request.SessionId, retainedResult), HostProtocol.Json);
        Assert.Equal(HttpStatusCode.OK, resolution.StatusCode);
        Assert.Empty((await http.GetFromJsonAsync<ActionCustodyEntry[]>(recoveryPath, HostProtocol.Json))!);
        // Recovery clears custody; it neither resurrects an old run nor claims a new run's completion.
        Assert.Empty(restarted.Registry.List());
        await SendAsync(nextSocket, "start", request with { Observation = Observation("fresh-after-recovery", 1) });
        var admitted = await ReceiveUntilAsync(nextSocket, "started");
        var newRun = restarted.Track(admitted.GetProperty("runId").GetString()!);
        Assert.NotEqual(original.RunId, newRun.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_custody_reservation_cannot_leave_a_replayable_undelivered_action(bool deleteLedger)
    {
        await using var harness = new RuntimeHarness();
        var connection = new CaptureConnection();
        var run = harness.Create(Request(), connection, start: false);
        if (deleteLedger) File.Delete(harness.CustodyLedgerPath);
        else await File.WriteAllTextAsync(harness.CustodyLedgerPath, "corrupt fixture ledger");
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RunOutcomeStatus.Failed, Outcome(run).Outcome.Status);
        Assert.Empty(Snapshot(run).GetProperty("pendingActions").EnumerateArray());
        Assert.False(run.HasUnresolvedActions);
        Assert.DoesNotContain(connection.Messages, message => message.Type is "action.request" or "action.reconcile");
        await run.ReplayPendingAsync(CancellationToken.None);
        Assert.DoesNotContain(connection.Messages, message => message.Type is "action.request" or "action.reconcile");
    }

    [Fact]
    public async Task Large_outcome_uses_bounded_socket_summary_and_preserves_complete_http_evidence()
    {
        await using var service = await SocketService.StartAsync();
        var socket = await service.ConnectAsync();
        await SendAsync(socket, "start", Request() with { Provider = new ProviderSettings("fixture", "large-outcome") });
        var started = await ReceiveUntilAsync(socket, "started");
        var runId = started.GetProperty("runId").GetString()!;
        service.Track(runId);
        const int observationSize = 48000;
        for (var revision = 1; revision <= 3; revision++)
        {
            var action = (await ReceiveUntilAsync(socket, "action.request")).GetProperty("payload")
                .Deserialize<HostActionRequest>(HostProtocol.Json)!;
            var result = Applied(action, revision, complete: revision == 3);
            result = result with
            {
                Observation = result.Observation! with
                {
                    Data = HostProtocol.Element(new { revision, detail = new string((char)('a' + revision), observationSize) })
                }
            };
            await SendAsync(socket, "action.result", result, runId);
        }
        var terminal = await ReceiveUntilAsync(socket, "outcome");
        var summary = terminal.GetProperty("payload");
        Assert.True(summary.GetProperty("truncated").GetBoolean());
        Assert.Equal("succeeded", summary.GetProperty("outcome").GetProperty("status").GetString());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(terminal).Length < HostProtocol.MaxMessageBytes / 2);
        Assert.Equal($"/api/runs/{runId}", summary.GetProperty("snapshotUrl").GetString());
        using var http = new HttpClient { BaseAddress = service.BaseAddress };
        var snapshot = await http.GetFromJsonAsync<JsonElement>(summary.GetProperty("snapshotUrl").GetString(), HostProtocol.Json);
        var fullOutcome = snapshot.GetProperty("outcome");
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(fullOutcome).Length > HostProtocol.MaxMessageBytes / 2);
        var observations = fullOutcome.GetProperty("details").GetProperty("observations").EnumerateArray().ToArray();
        Assert.Equal(3, observations.Length);
        Assert.All(observations, observation => Assert.Equal(observationSize,
            observation.GetProperty("data").GetProperty("observation").GetProperty("data").GetProperty("detail").GetString()!.Length));
        Assert.Equal(3, fullOutcome.GetProperty("receipts").GetProperty("items").GetArrayLength());
        Assert.NotEmpty(fullOutcome.GetProperty("outcome").GetProperty("completionEvidence").EnumerateArray());
    }

    [Fact]
    public async Task Planner_setup_failure_sends_reliable_termination_and_replays_it_without_an_outcome()
    {
        await using var harness = new RuntimeHarness(new UnavailablePlannerFactory());
        var connection = new CaptureConnection();
        var run = harness.Create(Request(), connection);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(run.Terminal);
        Assert.False(run.HasUnresolvedActions);
        Assert.Equal("failed", Snapshot(run).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Snapshot(run).GetProperty("outcome").ValueKind);
        // CaptureConnection ignores TrySendProgress; this assertion requires awaited SendAsync delivery.
        var message = Assert.Single(connection.Messages, item => item.Type == "run.terminated");
        var payload = HostProtocol.Element(message.Payload);
        Assert.Equal("failed", payload.GetProperty("status").GetString());
        Assert.Equal("run.failed", payload.GetProperty("code").GetString());
        Assert.Equal($"/api/runs/{run.RunId}", payload.GetProperty("snapshotUrl").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("message").GetString()));
        Assert.DoesNotContain("fixture internal setup details", payload.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(connection.Messages, item => item.Type is "outcome" or "action.request");

        run.Detach(connection);
        var resumed = new CaptureConnection();
        run.Attach(resumed);
        await run.ReplayPendingAsync(CancellationToken.None);
        var replayed = Assert.Single(resumed.Messages, item => item.Type == "run.terminated");
        Assert.Equal(ProtocolValidation.Digest(message.Payload), ProtocolValidation.Digest(replayed.Payload));
        Assert.Equal(JsonValueKind.Null, Snapshot(run).GetProperty("outcome").ValueKind);
    }

    private static HostRunRequest Request(bool demo = false) => new(
        1, "fixture-host", "fixture-session", "fixture-epoch", "fixture-scope", "fixture-perspective",
        "fixture-objective", "Accept one eligible item and verify its resulting state.", Observation("initial", 0),
        demo ? [
            new HostCapability("demo.inspect", "Inspect", "Inspect scoped inventory.", ToolKind.Query, ToolEffect.ReadOnly, ToolInputSchema.Create()),
            new HostCapability("demo.accept", "Accept", "Accept one eligible item.", ToolKind.Action, ToolEffect.WritesLocalState,
                ToolInputSchema.Create(new ToolInputField("itemId", Required: true)))
        ] : [new HostCapability("host.apply", "Apply", "Apply the fixture transition.", ToolKind.Action, ToolEffect.WritesLocalState, ToolInputSchema.Create())],
        new ProviderSettings(demo ? "demo" : "fixture"), new HostRunLimits(TimeoutSeconds: 20, ActionTimeoutSeconds: 10));

    private static HostObservation Observation(string id, long revision) => new(id, revision, DateTimeOffset.UtcNow,
        HostProtocol.Element(new { items = new[] { new { id = "sample-1", eligible = true, status = revision > 0 ? "accepted" : "pending" } } }));

    private static HostActionResult Applied(HostActionRequest action, long after, bool complete = false) => new(
        action.ActionId, action.SessionId, action.SessionEpoch, "applied", action.ExpectedRevision, after,
        "evidence-" + action.ActionId, "The authoritative host applied or observed the transition.",
        Observation("observation-" + action.ActionId, after),
        complete ? new HostCompletion("fixture-objective", "completion-" + action.ActionId, "The host verified the objective.") : null);

    private static JsonElement Snapshot(HostRun run) => HostProtocol.Element(run.Snapshot());
    private static OutcomeEnvelope Outcome(HostRun run) => Snapshot(run).GetProperty("outcome").Deserialize<OutcomeEnvelope>(HostProtocol.Json)!;
    private static string Type(JsonElement message) => message.GetProperty("type").GetString()!;

    private static async Task SendAsync(ClientWebSocket socket, string type, object payload, string? runId = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var message = new HostMessage(type, HostProtocol.Element(payload), Guid.NewGuid().ToString("N"), runId);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, HostProtocol.Json).AsMemory(), WebSocketMessageType.Text, true, deadline.Token);
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stream = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(chunk.AsMemory(), deadline.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            stream.Write(chunk, 0, result.Count);
            Assert.True(stream.Length < 4 * 1024 * 1024, "Unexpectedly large service message.");
            if (result.EndOfMessage) break;
        }
        using var json = JsonDocument.Parse(stream.ToArray());
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> ReceiveUntilAsync(ClientWebSocket socket, string type)
    {
        for (var i = 0; i < 256; i++)
        {
            var message = await ReceiveAsync(socket);
            if (Type(message) == type) return message;
            if (Type(message) == "error") Assert.Fail(message.GetRawText());
        }
        throw new InvalidOperationException("Expected service message did not arrive.");
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, deadline.Token);
    }

    private sealed class FixturePlannerFactory : ILabPlannerFactory
    {
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent) =>
            new FixturePlanner(continueAfterResults: settings.Model == "large-outcome");
        public IReadOnlyList<ProviderMetadata> GetProviders() => [];
    }

    private sealed class UnavailablePlannerFactory : ILabPlannerFactory
    {
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent) =>
            throw new InvalidOperationException("fixture internal setup details");
        public IReadOnlyList<ProviderMetadata> GetProviders() => [];
    }

    private sealed class FixturePlanner(bool continueAfterResults = false) : IWorkflowPlanner
    {
        private int _calls;
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
        {
            var call = ++_calls;
            return Task.FromResult(new WorkflowPlan("fixture-plan-" + call, call,
                [new PlanStep("fixture-step-" + call, "host.apply", ToolKind.Action, ToolEffect.WritesLocalState,
                    new Dictionary<string, object?>())], "Request one host transition."));
        }
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default) =>
            continueAfterResults ? CreatePlanAsync(request, cancellationToken) :
                throw new InvalidOperationException("A one-step fixture should complete through host evidence.");
    }

    private sealed class CaptureConnection(bool throwOnOutcome = false, bool blockResume = false) : IHostConnection
    {
        private readonly Channel<HostActionRequest> _actions = Channel.CreateUnbounded<HostActionRequest>();
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public ConcurrentQueue<ServiceMessage> Messages { get; } = new();
        public TaskCompletionSource ResumeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseResume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(throwOnOutcome && message.Type == "outcome", this);
            if (blockResume && message.Type == "resumed")
            {
                ResumeEntered.TrySetResult();
                await ReleaseResume.Task.WaitAsync(cancellationToken);
            }
            Messages.Enqueue(message);
            if (message.Type is "action.request" or "action.reconcile")
                _actions.Writer.TryWrite((HostActionRequest)message.Payload);
        }
        public async Task<HostActionRequest> NextActionAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _actions.Reader.ReadAsync(deadline.Token);
        }
    }

    private sealed class RuntimeHarness : IAsyncDisposable
    {
        private readonly string _storage = Path.Combine(Path.GetTempPath(), "agentica-runtime-tests", Guid.NewGuid().ToString("N"));
        private readonly List<HostRun> _runs = [];
        public HostRunRegistry Registry { get; }
        public string CustodyLedgerPath => Path.Combine(_storage, "custody", ActionCustodyStore.LedgerFileName);
        public RuntimeHarness(ILabPlannerFactory? planners = null) => Registry = new HostRunRegistry(new HostContextStore(_storage), planners ?? new FixturePlannerFactory(),
            new ActionCustodyStore(Path.Combine(_storage, "custody")));
        public HostRun Create(HostRunRequest request, IHostConnection? connection = null, bool start = true)
        {
            var run = Registry.Create(request);
            _runs.Add(run);
            if (connection is not null) run.Attach(connection);
            if (start) run.Start();
            return run;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var run in _runs) if (!run.Terminal) run.Cancel();
            await Task.WhenAll(_runs.Select(run => run.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            Registry.Dispose();
            if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
        }
    }

    private sealed class SocketService : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _storage;
        private readonly bool _deleteStorage;
        private readonly List<ClientWebSocket> _sockets = [];
        private readonly List<HostRun> _runs = [];
        private SocketService(WebApplication app, string storage, bool deleteStorage)
        { _app = app; _storage = storage; _deleteStorage = deleteStorage; }
        public HostRunRegistry Registry => _app.Services.GetRequiredService<HostRunRegistry>();
        public Uri BaseAddress => new(_app.Urls.Single());
        public static async Task<SocketService> StartAsync(string? storage = null, bool deleteStorage = true)
        {
            storage ??= Path.Combine(Path.GetTempPath(), "agentica-socket-tests", Guid.NewGuid().ToString("N"));
            var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], new FixturePlannerFactory(), storage);
            await app.StartAsync();
            return new SocketService(app, storage, deleteStorage);
        }
        public async Task<ClientWebSocket> ConnectAsync()
        {
            var socket = new ClientWebSocket();
            _sockets.Add(socket);
            var uri = new UriBuilder(_app.Urls.Single()) { Scheme = "ws", Path = "/api/host" }.Uri;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(uri, deadline.Token);
            return socket;
        }
        public HostRun Track(string runId)
        {
            var run = Registry.Get(runId);
            _runs.Add(run);
            return run;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var socket in _sockets) { socket.Abort(); socket.Dispose(); }
            foreach (var run in _runs) if (!run.Terminal) run.Cancel();
            await Task.WhenAll(_runs.Select(run => run.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
            if (_deleteStorage && Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
        }
    }
}
