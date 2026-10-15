using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Transport;

namespace Agentica.Lab.Web.Tests;

public sealed class HostStartTransportOrderingTests
{
    [Fact]
    public async Task Action_from_attachment_cannot_overtake_the_started_response()
    {
        var socket = new RecordingSocket();
        await using var connection = new HostSocket(socket);
        Task? action = null;
        await connection.SendAttachedAsync(new("started", new { runId = "original" }), () =>
        {
            action = connection.SendAsync(new("action.request", new { actionId = "first" }));
            Assert.False(action.IsCompleted);
            Assert.Empty(socket.Messages);
        });
        await action!.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(["started", "action.request"], socket.Messages);
    }

    [Fact]
    public async Task Failed_attachment_does_not_send_a_binding_or_poison_the_send_gate()
    {
        var socket = new RecordingSocket();
        await using var connection = new HostSocket(socket);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendAttachedAsync(new("started", new { }),
            () => throw new InvalidOperationException("Another owner is already attached.")));
        Assert.Empty(socket.Messages);
        await connection.SendAsync(new("error", new { code = "session.connected" }));
        Assert.Equal(["error"], socket.Messages);
    }

    private sealed class RecordingSocket : WebSocket
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(buffer);
            Messages.Enqueue(document.RootElement.GetProperty("type").GetString()!);
            return Task.CompletedTask;
        }
    }
}
