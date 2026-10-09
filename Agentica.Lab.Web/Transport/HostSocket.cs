using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Transport;

public sealed class HostSocket : IHostConnection, IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ServiceMessage> _progress;
    private long _dropped;
    public HostSocket(WebSocket socket)
    {
        _socket = socket;
        _progress = Channel.CreateBounded<ServiceMessage>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        }, _ => Interlocked.Increment(ref _dropped));
    }
    private Task? _pump;
    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");

    public void StartProgress() => _pump = PumpAsync();
    public void TrySendProgress(ServiceMessage message) => _progress.Writer.TryWrite(message);

    public async Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, HostProtocol.Json);
        await _send.WaitAsync(timeout.Token).ConfigureAwait(false);
        try { await _socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    public async Task<HostMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var result = await _socket.ReceiveAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                _socket.Abort();
                throw new HostProtocolException("message.type", "Only JSON text messages are supported.");
            }
            if (buffer.Length + result.Count > HostProtocol.MaxMessageBytes)
            {
                _socket.Abort();
                throw new HostProtocolException("message.bounds", "Host message exceeds 256 KiB.");
            }
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<HostMessage>(buffer.ToArray(), HostProtocol.Json)
            ?? throw new HostProtocolException("message.empty", "A host message is required.");
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var item in _progress.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                    await SendAsync(new ServiceMessage("telemetry.gap", new
                    {
                        dropped,
                        snapshotUrl = item.RunId is null ? null : $"/api/runs/{item.RunId}"
                    }, item.RunId), _lifetime.Token).ConfigureAwait(false);
                await SendAsync(item, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        { _socket.Abort(); }
    }

    public async ValueTask DisposeAsync()
    {
        _progress.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        _socket.Abort();
        if (_pump is not null) await _pump.ConfigureAwait(false);
        // A racing authoritative send can still release the gate; do not dispose it underneath that send.
        _lifetime.Dispose();
        _socket.Dispose();
    }
}
