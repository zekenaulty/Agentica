using System.Net.WebSockets;
using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HostOperationSocketTests
{
    private sealed class WakeSocketFixture : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly DirectoryInfo _storage;
        private readonly bool _ownsStorage;
        private readonly List<ClientWebSocket> _sockets = [];
        public FixturePlannerFactory Planners { get; } = new();
        public HostRunRegistry Registry => _app.Services.GetRequiredService<HostRunRegistry>();
        public HostOperationStore Operations => _app.Services.GetRequiredService<HostOperationStore>();
        public CancellationToken Token => _deadline.Token;
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(25));

        private WakeSocketFixture(DirectoryInfo? storage)
        {
            _ownsStorage = storage is null;
            _storage = storage ?? Directory.CreateTempSubdirectory("agentica-wake-replay-");
            _app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], Planners, _storage.FullName);
        }

        public static async Task<WakeSocketFixture> Open(DirectoryInfo? storage = null)
        {
            var fixture = new WakeSocketFixture(storage);
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

        public async Task<HostActionRequest> Park()
        {
            var socket = await Connect();
            await SendAsync(socket, "start", Request(), "initial-start", null, Token);
            var started = await ReceiveUntilAsync(socket, "started", Token);
            var run = Registry.Get(started.GetProperty("runId").GetString()!);
            var action = (await ReceiveUntilAsync(socket, "action.request", Token)).GetProperty("payload")
                .Deserialize<HostActionRequest>(HostProtocol.Json)!;
            await SendAsync(socket, "action.result", Admission(action), "initial-admission", run.RunId, Token);
            await ReceiveUntilAsync(socket, "operation.parked", Token);
            await ReceiveUntilAsync(socket, "outcome", Token);
            await run.Completion.WaitAsync(Token);
            return action;
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
            if (_ownsStorage) _storage.Delete(recursive: true);
        }
    }

    private static (string Type, object Payload) WakeMessage(HostOperationEvent decision, bool adopt) => adopt
        ? ("operation.adopt", new HostDecisionWake(decision,
            new HostDecisionAdoption("wake-adoption", "adopted-epoch", Observation("adopted-checkpoint", 3))))
        : ("operation.wake", decision);

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
        using var json = JsonDocument.Parse(stream.ToArray());
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> ReceiveWakeBinding(ClientWebSocket socket, string requestId, CancellationToken token)
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
        throw new InvalidOperationException("Missing correlated wake binding.");
    }

    private static void AssertWakeBinding(JsonElement message, HostRun run, HostOperationEvent decision, bool adopt)
    {
        Assert.Equal(run.RunId, message.GetProperty("runId").GetString());
        var payload = message.GetProperty("payload");
        Assert.True(payload.GetProperty("connected").GetBoolean());
        var expected = HostProtocol.Element(run.Snapshot(false));
        foreach (var name in new[] { "runId", "hostId", "sessionId", "sessionEpoch", "objectiveId", "scopeId", "perspectiveId", "manifestHash", "continuation" })
            Assert.Equal(expected.GetProperty(name).GetRawText(), payload.GetProperty(name).GetRawText());
        Assert.Equal(adopt ? "adopted-epoch" : decision.SessionEpoch, payload.GetProperty("sessionEpoch").GetString());
        Assert.Equal(decision.EventId, payload.GetProperty("continuation").GetProperty("wakeEventId").GetString());
        Assert.Equal(decision.ActionId, payload.GetProperty("continuation").GetProperty("admissionActionId").GetString());
        Assert.Equal(decision.OperationId, payload.GetProperty("continuation").GetProperty("operationId").GetString());
    }

    private static async Task<HostRun> LoseWakeReply(WakeSocketFixture fixture, HostActionRequest original, bool adopt)
    {
        var socket = await fixture.Connect();
        var wake = WakeMessage(Event(original, "decision", 1, 2), adopt);
        await SendAsync(socket, wake.Type, wake.Payload, "lost-wake-reply", null, fixture.Token);
        await fixture.WaitFor(() => fixture.Operations.Get("socket-host", original.SessionId, original.ActionId).WakeRunId is not null);
        var run = fixture.Registry.Get(fixture.Operations.Get("socket-host", original.SessionId, original.ActionId).WakeRunId!);
        await fixture.WaitFor(() => run.HasUnresolvedActions);
        // Do not consume any frame: the correlated reply and original action are lost to the host.
        socket.Abort();
        await fixture.WaitFor(() => !HostProtocol.Element(run.Snapshot(false)).GetProperty("connected").GetBoolean());
        return run;
    }
}
