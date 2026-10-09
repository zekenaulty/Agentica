using System.Net.WebSockets;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Planning;
using Agentica.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentica.Lab.Web.Tests;

public sealed class HostStorageSocketTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_corrupt_initialized_custody_reports_storage_unavailable_before_admission(bool missing)
    {
        var storage = Directory.CreateTempSubdirectory("agentica-socket-storage-tests-");
        var planners = new CountingPlannerFactory();
        var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], planners, storage.FullName);
        using var socket = new ClientWebSocket();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await app.StartAsync(deadline.Token);
            var ledger = Path.Combine(storage.FullName, "custody", ActionCustodyStore.LedgerFileName);
            Assert.True(File.Exists(ledger));
            if (missing) File.Delete(ledger);
            else await File.WriteAllTextAsync(ledger, "private fixture corruption details", deadline.Token);

            var endpoint = new UriBuilder(app.Urls.Single()) { Scheme = "ws", Path = "/api/host" }.Uri;
            await socket.ConnectAsync(endpoint, deadline.Token);
            var request = new HostRunRequest(1, "fixture-host", "fixture-session", "fixture-epoch", "fixture-scope",
                "fixture-perspective", "fixture-objective", "Apply the bounded fixture transition.",
                new HostObservation("initial", 0, DateTimeOffset.UtcNow, HostProtocol.Element(new { ready = true })),
                [new HostCapability("host.apply", "Apply", "Apply the fixture transition.", ToolKind.Action,
                    ToolEffect.WritesLocalState, ToolInputSchema.Create())], new ProviderSettings("fixture"));
            ProtocolValidation.Validate(request);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var requestId = "valid-start-" + attempt;
                var message = new HostMessage("start", HostProtocol.Element(request), requestId);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, HostProtocol.Json).AsMemory(),
                    WebSocketMessageType.Text, true, deadline.Token);
                using var reply = await ReceiveAsync(socket, deadline.Token);
                Assert.Equal("error", reply.RootElement.GetProperty("type").GetString());
                Assert.Equal(requestId, reply.RootElement.GetProperty("requestId").GetString());
                Assert.Equal(JsonValueKind.Null, reply.RootElement.GetProperty("runId").ValueKind);
                var payload = reply.RootElement.GetProperty("payload");
                Assert.Equal("storage.unavailable", payload.GetProperty("code").GetString());
                Assert.Equal("Persistent evidence could not be read or written. Reconcile the original action after restoring storage.",
                    payload.GetProperty("message").GetString());
                Assert.DoesNotContain(storage.FullName, reply.RootElement.GetRawText(), StringComparison.Ordinal);
                Assert.DoesNotContain("private fixture corruption details", reply.RootElement.GetRawText(), StringComparison.Ordinal);
                Assert.Empty(app.Services.GetRequiredService<HostRunRegistry>().List());
                Assert.Equal(0, planners.CreateCalls);
            }
        }
        finally
        {
            socket.Abort();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(cleanup.Token);
            await app.DisposeAsync();
            storage.Delete(recursive: true);
        }
    }

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            Assert.Equal(WebSocketMessageType.Text, received.MessageType);
            stream.Write(buffer, 0, received.Count);
            Assert.True(stream.Length <= HostProtocol.MaxMessageBytes);
            if (received.EndOfMessage) return JsonDocument.Parse(stream.ToArray());
        }
    }

    private sealed class CountingPlannerFactory : ILabPlannerFactory
    {
        private int _createCalls;
        public int CreateCalls => Volatile.Read(ref _createCalls);
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
        {
            Interlocked.Increment(ref _createCalls);
            throw new InvalidOperationException("Storage admission guard was bypassed.");
        }
        public IReadOnlyList<ProviderMetadata> GetProviders() => [];
    }
}
