using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Runtime;

public sealed record RunEventRecord(long Sequence, DateTimeOffset At, ServiceMessage Message);

/// <summary>Bounded display history. Commands are delivered separately through IHostConnection.</summary>
public sealed class RunEventFeed
{
    private readonly object _gate = new();
    private readonly Queue<RunEventRecord> _history = new();
    private readonly HashSet<Channel<RunEventRecord>> _readers = [];
    private long _sequence;

    public void Publish(ServiceMessage message)
    {
        lock (_gate)
        {
            var item = new RunEventRecord(++_sequence, DateTimeOffset.UtcNow, message);
            _history.Enqueue(item);
            while (_history.Count > 512) _history.Dequeue();
            foreach (var reader in _readers) reader.Writer.TryWrite(item);
        }
    }

    public async IAsyncEnumerable<RunEventRecord> ReadAsync(long after = 0,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<RunEventRecord>(new BoundedChannelOptions(128)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        RunEventRecord[] history;
        lock (_gate)
        {
            if (_readers.Count >= 32) throw new HostProtocolException("observers.limit", "Too many run observers.");
            history = _history.Where(item => item.Sequence > after).ToArray();
            _readers.Add(channel);
        }
        try
        {
            foreach (var item in history) yield return item;
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            lock (_gate) _readers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }
}
