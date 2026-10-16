using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HttpActionRecoveryTests : IDisposable
{
    private readonly DirectoryInfo _storage = Directory.CreateTempSubdirectory("agentica-http-recovery-");

    private sealed class Service : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly List<ClientWebSocket> _sockets = [];
        public CountingFactory Planners { get; }
        public HttpClient Http { get; }
        public HostRunRegistry Registry => _app.Services.GetRequiredService<HostRunRegistry>();
        public ActionCustodyStore Custody => _app.Services.GetRequiredService<ActionCustodyStore>();
        private Service(WebApplication app, CountingFactory planners)
        {
            _app = app;
            Planners = planners;
            Http = new() { BaseAddress = new(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
        }
        public static async Task<Service> Open(string root)
        {
            var planners = new CountingFactory();
            var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], planners, root);
            await app.StartAsync();
            return new(app, planners);
        }
        public async Task<(HostRun Run, HostStartEntry Entry, HostActionRequest Action)> Dispatch()
        {
            var socket = new ClientWebSocket();
            _sockets.Add(socket);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(new UriBuilder(Http.BaseAddress!) { Scheme = "ws", Path = "/api/host" }.Uri, timeout.Token);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new HostMessage("start.durable",
                HostProtocol.Element(Submission()), "original-start"), HostProtocol.Json).AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
            var started = await Receive(socket, "started", timeout.Token);
            var entry = started.GetProperty("payload").GetProperty("startAdmission").Deserialize<HostStartEntry>(HostProtocol.Json)!;
            var action = (await Receive(socket, "action.request", timeout.Token)).GetProperty("payload").Deserialize<HostActionRequest>(HostProtocol.Json)!;
            return (Registry.Get(entry.RunId), entry, action);
        }
        public Task<HttpResponseMessage> Recover(HostActionResult result, string hostId = "host", string sessionId = "session") =>
            Http.PostAsJsonAsync("api/recovery", new HostRecoveryRequest(hostId, sessionId, result), HostProtocol.Json);

        public async Task<HostStartEntry> Stop(HostStartEntry entry)
        {
            using var response = await Http.PostAsJsonAsync("api/starts/stop", Reference(entry), HostProtocol.Json);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<HostStartEntry>(HostProtocol.Json))!;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var socket in _sockets) { socket.Abort(); socket.Dispose(); }
            var runs = Registry.List().Select(snapshot => Registry.Get(HostProtocol.Element(snapshot).GetProperty("runId").GetString()!)).ToArray();
            foreach (var run in runs) if (!run.Terminal) run.Cancel();
            await Task.WhenAll(runs.Select(run => run.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            Http.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(timeout.Token);
            await _app.DisposeAsync();
        }
    }

    private static async Task<JsonElement> Receive(ClientWebSocket socket, string type, CancellationToken token)
    {
        for (var i = 0; i < 256; i++)
        {
            using var stream = new MemoryStream();
            var buffer = new byte[8192];
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(buffer, token);
                Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                stream.Write(buffer, 0, received.Count);
                Assert.True(stream.Length <= HostProtocol.MaxMessageBytes);
            } while (!received.EndOfMessage);
            using var json = JsonDocument.Parse(stream.ToArray());
            var message = json.RootElement;
            Assert.NotEqual("error", message.GetProperty("type").GetString());
            if (message.GetProperty("type").GetString() == type) return message.Clone();
        }
        throw new InvalidOperationException("Missing expected socket message.");
    }

    private static HostActionResult Fenced(HostActionRequest action) => new(action.ActionId, action.SessionId,
        action.SessionEpoch, "unavailable", 5, 5, "cancel-proof-" + action.ActionId, "Irreversible host fence proved no execution.");

    private static async Task AssertReply(HttpResponseMessage response, string actionId, bool duplicate)
    {
        using (response)
        {
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(HostProtocol.Json);
            Assert.Equal(actionId, body.GetProperty("actionId").GetString());
            Assert.True(body.GetProperty("resolved").GetBoolean());
            Assert.Equal(duplicate, body.GetProperty("duplicate").GetBoolean());
        }
    }

    private static async Task AssertError(HttpResponseMessage response, string code)
    {
        using (response)
        {
            Assert.False(response.IsSuccessStatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(HostProtocol.Json);
            Assert.Equal(code, body.GetProperty("code").GetString());
        }
    }

    public void Dispose()
    {
        _storage.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }
}
