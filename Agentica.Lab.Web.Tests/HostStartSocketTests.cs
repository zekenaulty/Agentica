using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed class HostStartSocketTests
{
    [Fact]
    public async Task Real_socket_replay_returns_original_run_and_http_stop_survives_service_restart()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentica-start-socket", Guid.NewGuid().ToString("N"));
        try
        {
            HostStartEntry entry;
            HostStartEntry stopped;
            await using (var service = await Service.Open(root))
            {
                using var socket = await service.Connect();
                await Send(socket, "start.durable", Submission());
                var started = await Receive(socket, "started");
                entry = started.GetProperty("payload").GetProperty("startAdmission").Deserialize<HostStartEntry>(HostProtocol.Json)!;
                var run = service.Track(entry.RunId);
                var action = (await Receive(socket, "action.request")).GetProperty("payload").Deserialize<HostActionRequest>(HostProtocol.Json)!;
                await Send(socket, "action.result", Result(action), run.RunId);
                await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                await Send(socket, "start.durable", Submission());
                var repeated = await Receive(socket, "started");
                Assert.Equal(run.RunId, repeated.GetProperty("runId").GetString());
                Assert.Equal(1, service.Planners.Created);
                var response = await service.Http.PostAsJsonAsync("api/starts/stop", Reference(entry), HostProtocol.Json);
                response.EnsureSuccessStatusCode();
                stopped = (await response.Content.ReadFromJsonAsync<HostStartEntry>(HostProtocol.Json))!;
                Assert.Equal("stopped", stopped.Terminal!.Reason);
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                socket.Abort();
            }
            await using (var service = await Service.Open(root))
            {
                var response = await service.Http.GetAsync("api/starts?hostId=host&sessionId=session&attemptId=attempt-one");
                Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
                var read = (await response.Content.ReadFromJsonAsync<HostStartEntry>(HostProtocol.Json))!;
                Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(read));
                using var socket = await service.Connect();
                await Send(socket, "start.durable", Submission());
                var error = await Receive(socket, "error");
                Assert.Equal("start.stopped", error.GetProperty("payload").GetProperty("code").GetString());
                Assert.Equal(0, service.Planners.Created);
                Assert.Equal(HttpStatusCode.NotFound, (await service.Http.GetAsync("api/starts?hostId=host&sessionId=session&attemptId=missing")).StatusCode);
                socket.Abort();
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Failed_context_open_reports_error_but_retains_exact_http_recovery_without_planning()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentica-start-socket", Guid.NewGuid().ToString("N"));
        try
        {
            await using var service = await Service.Open(root);
            File.WriteAllText(Path.Combine(root, "context"), "Block context initialization after admission.");
            using var socket = await service.Connect();
            await Send(socket, "start.durable", Submission());
            var error = await Receive(socket, "error");
            Assert.Equal("storage.unavailable", error.GetProperty("payload").GetProperty("code").GetString());
            var entry = (await service.Http.GetFromJsonAsync<HostStartEntry>("api/starts?hostId=host&sessionId=session&attemptId=attempt-one", HostProtocol.Json))!;
            Assert.Equal(ProtocolValidation.Digest(Submission().Request), entry.OriginalRequestHash);
            var response = await service.Http.PostAsJsonAsync("api/starts/stop", Reference(entry), HostProtocol.Json);
            var stopped = (await response.Content.ReadFromJsonAsync<HostStartEntry>(HostProtocol.Json))!;
            Assert.Equal("interrupted", stopped.Terminal!.Reason);
            Assert.True(stopped.Terminal.UsageUnknown);
            Assert.Null(stopped.Terminal.Usage);
            Assert.Equal(0, service.Planners.Created);
            socket.Abort();
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task Send(ClientWebSocket socket, string type, object payload, string? runId = null) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new HostMessage(type, HostProtocol.Element(payload),
            Guid.NewGuid().ToString("N"), runId), HostProtocol.Json).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> Receive(ClientWebSocket socket, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (var i = 0; i < 256; i++)
        {
            using var stream = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                stream.Write(buffer, 0, received.Count);
                Assert.True(stream.Length <= HostProtocol.MaxMessageBytes);
                if (received.EndOfMessage) break;
            }
            using var parsed = JsonDocument.Parse(stream.ToArray());
            var message = parsed.RootElement;
            var receivedType = message.GetProperty("type").GetString();
            if (receivedType == type) return message.Clone();
            Assert.NotEqual("error", receivedType);
        }
        throw new InvalidOperationException("Expected response missing.");
    }

    private sealed class Service : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly List<HostRun> _runs = [];
        public CountingFactory Planners { get; }
        public HttpClient Http { get; }
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
        public async Task<ClientWebSocket> Connect()
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new UriBuilder(Http.BaseAddress!) { Scheme = "ws", Path = "/api/host" }.Uri, CancellationToken.None);
            return socket;
        }
        public HostRun Track(string runId)
        {
            var run = _app.Services.GetRequiredService<HostRunRegistry>().Get(runId);
            _runs.Add(run);
            return run;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var run in _runs) if (!run.Terminal) run.Cancel();
            await Task.WhenAll(_runs.Select(run => run.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            Http.Dispose();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _app.StopAsync(timeout.Token);
            await _app.DisposeAsync();
        }
    }
}
