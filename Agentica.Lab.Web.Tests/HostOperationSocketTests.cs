using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

public sealed class HostOperationSocketTests
{
    [Fact]
    public async Task Socket_handoff_http_progress_and_duplicate_active_wake_preserve_one_continuation()
    {
        var storage = Directory.CreateTempSubdirectory("agentica-operation-socket-");
        var planners = new FixturePlannerFactory();
        var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], planners, storage.FullName);
        using var firstSocket = new ClientWebSocket();
        using var wakeSocket = new ClientWebSocket();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var runs = new List<HostRun>();
        try
        {
            await app.StartAsync(deadline.Token);
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var endpoint = new UriBuilder(http.BaseAddress) { Scheme = "ws", Path = "/api/host" }.Uri;
            var registry = app.Services.GetRequiredService<HostRunRegistry>();
            await firstSocket.ConnectAsync(endpoint, deadline.Token);
            await SendAsync(firstSocket, "start", Request(), "start-first", null, deadline.Token);
            var firstStarted = await ReceiveUntilAsync(firstSocket, "started", deadline.Token);
            var firstId = firstStarted.GetProperty("runId").GetString()!;
            runs.Add(registry.Get(firstId));
            var firstAction = (await ReceiveUntilAsync(firstSocket, "action.request", deadline.Token))
                .GetProperty("payload").Deserialize<HostActionRequest>(HostProtocol.Json)!;
            await SendAsync(firstSocket, "action.result", Admission(firstAction), "admit-first", firstId, deadline.Token);
            await ReceiveUntilAsync(firstSocket, "operation.parked", deadline.Token);
            await ReceiveUntilAsync(firstSocket, "outcome", deadline.Token);
            await runs[0].Completion.WaitAsync(deadline.Token);
            Assert.Equal(1, planners.Created);
            Assert.Equal(1, planners.Planned);

            var progress = Event(firstAction, "progress", 1, 2);
            using (var reply = await http.PostAsJsonAsync("/api/operations/events", progress, HostProtocol.Json, deadline.Token))
            {
                reply.EnsureSuccessStatusCode();
                var entry = await reply.Content.ReadFromJsonAsync<HostOperationEntry>(HostProtocol.Json, deadline.Token);
                Assert.Equal("parked", entry!.State);
                Assert.Equal(progress.EventId, entry.LatestEvent!.EventId);
            }
            Assert.Equal(1, planners.Created);
            Assert.Equal(1, planners.Planned);

            // Reconnect through a new socket. Retrying the same decision while its run is active
            // represents a lost wake acknowledgement and must not invoke a second planner.
            await wakeSocket.ConnectAsync(endpoint, deadline.Token);
            var wake = Event(firstAction, "decision", 2, 3);
            await SendAsync(wakeSocket, "operation.wake", wake, "wake-first", null, deadline.Token);
            var wakeStarted = await ReceiveUntilAsync(wakeSocket, "started", deadline.Token);
            var wakeId = wakeStarted.GetProperty("runId").GetString()!;
            Assert.NotEqual(firstId, wakeId);
            runs.Add(registry.Get(wakeId));
            var secondAction = (await ReceiveUntilAsync(wakeSocket, "action.request", deadline.Token))
                .GetProperty("payload").Deserialize<HostActionRequest>(HostProtocol.Json)!;
            Assert.Equal(3, secondAction.ExpectedRevision);
            await SendAsync(wakeSocket, "operation.wake", wake, "wake-replay", null, deadline.Token);
            var wakeReplay = await ReceiveUntilAsync(wakeSocket, "started", deadline.Token, "wake-replay");
            Assert.Equal(wakeId, wakeReplay.GetProperty("runId").GetString());
            Assert.Equal(2, planners.Created);
            Assert.Equal(2, planners.Planned);
            Assert.Equal(2, registry.List().Length);

            await SendAsync(wakeSocket, "action.result", Admission(secondAction), "admit-second", wakeId, deadline.Token);
            await ReceiveUntilAsync(wakeSocket, "operation.parked", deadline.Token);
            await ReceiveUntilAsync(wakeSocket, "outcome", deadline.Token);
            await runs[1].Completion.WaitAsync(deadline.Token);

            var completed = Event(secondAction, "completed", 1, 5) with
            {
                Completion = new HostCompletion("socket-objective", "final-host-proof", "The host verified completion in canonical state.")
            };
            using (var reply = await http.PostAsJsonAsync("/api/operations/events", completed, HostProtocol.Json, deadline.Token))
            {
                reply.EnsureSuccessStatusCode();
                var entry = await reply.Content.ReadFromJsonAsync<HostOperationEntry>(HostProtocol.Json, deadline.Token);
                Assert.Equal("completed", entry!.State);
                Assert.Equal("final-host-proof", entry.LatestEvent!.Completion!.EvidenceId);
            }
            var retained = await http.GetFromJsonAsync<HostOperationEntry[]>(
                "/api/operations?hostId=socket-host&sessionId=socket-session", HostProtocol.Json, deadline.Token);
            Assert.Equal(2, retained!.Length);
            Assert.Equal("continued", Assert.Single(retained, entry => entry.Action.ActionId == firstAction.ActionId).State);
            Assert.Equal("completed", Assert.Single(retained, entry => entry.Action.ActionId == secondAction.ActionId).State);
            Assert.Equal(2, planners.Created);
            Assert.Equal(2, planners.Planned);
            Assert.Equal(0, planners.Refined);
        }
        finally
        {
            firstSocket.Abort();
            wakeSocket.Abort();
            foreach (var run in runs) if (!run.Terminal) run.Cancel();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.WhenAll(runs.Select(run => run.Completion)).WaitAsync(cleanup.Token);
            await app.StopAsync(cleanup.Token);
            await app.DisposeAsync();
            storage.Delete(recursive: true);
        }
    }

    private static HostRunRequest Request() => new(1, "socket-host", "socket-session", "socket-epoch",
        "socket-scope", "socket-perspective", "socket-objective", "Complete durable host work with verified final evidence.",
        Observation("initial", 0),
        [new HostCapability("host.operate", "Operate", "Admit durable host work.", ToolKind.Action,
            ToolEffect.WritesLocalState, ToolInputSchema.Create(), DurableHandoff: true)],
        new ProviderSettings("fixture"), new HostRunLimits(TimeoutSeconds: 20, ActionTimeoutSeconds: 15));

    private static HostObservation Observation(string id, long revision) => new(id, revision,
        new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero).AddSeconds(revision),
        HostProtocol.Element(new { revision }));

    private static HostActionResult Admission(HostActionRequest action) => new(action.ActionId, action.SessionId,
        action.SessionEpoch, "applied", action.ExpectedRevision, action.ExpectedRevision + 1,
        "admission-" + action.ActionId, "The host admitted the durable operation.",
        Observation("admitted-" + action.ActionId, action.ExpectedRevision + 1),
        Operation: new HostOperationAdmission("operation-" + action.ActionId, "Host execution continues independently."));

    private static HostOperationEvent Event(HostActionRequest action, string kind, long sequence, long revision) => new(
        "socket-host", action.SessionId, action.SessionEpoch, action.ActionId, "operation-" + action.ActionId,
        sequence, "event-" + action.ActionId + "-" + sequence, kind, Observation("event-" + action.ActionId + "-" + sequence, revision),
        "The host reported " + kind + ".");

    private static Task SendAsync(ClientWebSocket socket, string type, object payload, string requestId,
        string? runId, CancellationToken cancellationToken) => socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(new HostMessage(type, HostProtocol.Element(payload), requestId, runId), HostProtocol.Json).AsMemory(),
            WebSocketMessageType.Text, true, cancellationToken).AsTask();

    private static async Task<JsonElement> ReceiveUntilAsync(ClientWebSocket socket, string type,
        CancellationToken cancellationToken, string? requestId = null)
    {
        for (var count = 0; count < 256; count++)
        {
            using var stream = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                stream.Write(buffer, 0, received.Count);
                Assert.True(stream.Length <= HostProtocol.MaxMessageBytes);
                if (received.EndOfMessage) break;
            }
            using var json = JsonDocument.Parse(stream.ToArray());
            var message = json.RootElement;
            if (message.GetProperty("type").GetString() == "error") Assert.Fail(message.GetRawText());
            if (message.GetProperty("type").GetString() == type &&
                (requestId is null || message.GetProperty("requestId").GetString() == requestId)) return message.Clone();
        }
        throw new InvalidOperationException("Expected operation service message did not arrive.");
    }

    private sealed class FixturePlannerFactory : ILabPlannerFactory
    {
        public int Created;
        public int Planned;
        public int Refined;
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
        {
            Interlocked.Increment(ref Created);
            return new FixturePlanner(this);
        }
        public IReadOnlyList<ProviderMetadata> GetProviders() => [];
    }

    private sealed class FixturePlanner(FixturePlannerFactory owner) : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref owner.Planned);
            return Task.FromResult(new WorkflowPlan("socket-plan-" + call, call,
                [new PlanStep("socket-step-" + call, "host.operate", ToolKind.Action, ToolEffect.WritesLocalState,
                    new Dictionary<string, object?>())], "Start one durable host operation."));
        }
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.Refined);
            throw new InvalidOperationException("A durable admission must park without refining.");
        }
    }
}
