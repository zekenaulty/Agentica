using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Agentica.Clients.Llm;

/// <summary>A bounded operator view. It never contains a response or native continuation.</summary>
public sealed record LlmTelemetryRecord(
    int SchemaVersion,
    string CallId,
    long Sequence,
    string Provider,
    string Kind,
    DateTimeOffset At,
    long ElapsedMs,
    long OutputCharacters,
    long ThoughtSummaryCharacters,
    long? FirstOutputElapsedMs,
    string? Activity = null,
    string? ThoughtSummaryDelta = null,
    bool ThoughtSummaryTruncated = false,
    LlmUsage? Usage = null,
    string? FinishReason = null,
    string? Reason = null);

/// <summary>DroppedRecords is the cumulative feed loss observed when this record was read.</summary>
public sealed record LlmTelemetryDelivery(LlmTelemetryRecord Record, long DroppedRecords);

/// <summary>
/// Nonblocking, bounded bridge from a planner's stream callback to one asynchronous UI consumer.
/// Slow consumers lose oldest updates; sequence numbers, cumulative counters, loss accounting,
/// Latest and LastTerminal allow the display to recover without affecting provider execution.
/// This is observational telemetry, not an execution receipt or a durable event log.
/// </summary>
public sealed class LlmTelemetryFeed : IDisposable
{
    private readonly object _gate = new();
    private readonly Channel<LlmTelemetryRecord> _channel;
    private readonly string _provider;
    private readonly bool _includeThoughtSummaries;
    private readonly int _maxSummaryCharacters;
    private readonly Stopwatch _clock = new();
    private LlmTelemetryRecord? _latest;
    private LlmTelemetryRecord? _lastTerminal;
    private string _callId = "";
    private long _sequence;
    private long _outputCharacters;
    private long _summaryCharacters;
    private long? _firstOutputElapsedMs;
    private long _droppedRecords;
    private int _readerActive;
    private bool _inCall;
    private bool _closed;

    public LlmTelemetryFeed(string provider, int capacity = 64,
        bool includeThoughtSummaries = false, int maxSummaryCharacters = 2048)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (provider.Length > 128) throw new ArgumentOutOfRangeException(nameof(provider));
        if (capacity is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maxSummaryCharacters is < 1 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(maxSummaryCharacters));
        _provider = provider;
        _includeThoughtSummaries = includeThoughtSummaries;
        _maxSummaryCharacters = maxSummaryCharacters;
        _channel = Channel.CreateBounded<LlmTelemetryRecord>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            }, _ => Interlocked.Increment(ref _droppedRecords));
    }

    public LlmTelemetryRecord? Latest => Volatile.Read(ref _latest);
    public LlmTelemetryRecord? LastTerminal => Volatile.Read(ref _lastTerminal);
    public long DroppedRecords => Interlocked.Read(ref _droppedRecords);

    /// <summary>Pass this method as LlmWorkflowPlanner's onStreamEvent callback.</summary>
    public void Report(LlmStreamEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            if (_closed) return;
            if (item.Kind == LlmStreamEventKind.Started || !_inCall)
            {
                _clock.Restart();
                _callId = Guid.NewGuid().ToString("N");
                _sequence = 0;
                _outputCharacters = 0;
                _summaryCharacters = 0;
                _firstOutputElapsedMs = null;
                _inCall = true;
            }

            string? activity = null;
            string? summary = null;
            string? reason = null;
            var truncated = false;
            switch (item.Kind)
            {
                case LlmStreamEventKind.Activity:
                    activity = Code(item.Text);
                    break;
                case LlmStreamEventKind.TextDelta:
                    _outputCharacters += item.Text?.Length ?? 0;
                    if (_firstOutputElapsedMs is null && _outputCharacters > 0)
                        _firstOutputElapsedMs = _clock.ElapsedMilliseconds;
                    break;
                case LlmStreamEventKind.ThoughtSummaryDelta:
                    _summaryCharacters += item.Text?.Length ?? 0;
                    if (_includeThoughtSummaries && item.Text is { } text)
                    {
                        var length = Math.Min(text.Length, _maxSummaryCharacters);
                        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1]))
                            length--;
                        summary = text[..length];
                        truncated = length < text.Length;
                    }
                    break;
                case LlmStreamEventKind.Completed:
                    _outputCharacters = Math.Max(_outputCharacters, item.Response?.Text.Length ?? 0);
                    _inCall = false;
                    break;
                case LlmStreamEventKind.Failed:
                case LlmStreamEventKind.Cancelled:
                    reason = Code(item.Text);
                    _inCall = false;
                    break;
            }

            var record = new LlmTelemetryRecord(1, _callId, ++_sequence, _provider,
                KindCode(item.Kind), DateTimeOffset.UtcNow, _clock.ElapsedMilliseconds,
                _outputCharacters, _summaryCharacters, _firstOutputElapsedMs,
                activity, summary, truncated,
                item.Kind == LlmStreamEventKind.Completed ? item.Response?.Usage : null,
                item.Kind == LlmStreamEventKind.Completed ? item.Response?.FinishReason.ToString() : null,
                reason);
            Volatile.Write(ref _latest, record);
            if (!_inCall) Volatile.Write(ref _lastTerminal, record);
            _channel.Writer.TryWrite(record);
        }
    }

    /// <summary>
    /// Reads until Complete or cancellation. Only one active reader is allowed; a disconnected
    /// consumer may reconnect. Call Complete after the planner operation ends, including failure.
    /// Closing the feed never manufactures a successful provider completion.
    /// </summary>
    public async IAsyncEnumerable<LlmTelemetryDelivery> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _readerActive, 1, 0) != 0)
            throw new InvalidOperationException("A telemetry feed supports one active consumer.");
        try
        {
            await foreach (var record in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return new LlmTelemetryDelivery(record, DroppedRecords);
        }
        finally
        {
            Volatile.Write(ref _readerActive, 0);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _channel.Writer.TryComplete();
        }
    }

    public void Dispose() => Complete();

    private static string? Code(string? value) => value is null ? null :
        value.Length is > 0 and <= 128 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':')
                ? value : "unclassified";

    private static string KindCode(LlmStreamEventKind kind) => kind switch
    {
        LlmStreamEventKind.Started => "started",
        LlmStreamEventKind.Activity => "activity",
        LlmStreamEventKind.TextDelta => "text_delta",
        LlmStreamEventKind.ThoughtSummaryDelta => "thought_summary_delta",
        LlmStreamEventKind.Completed => "completed",
        LlmStreamEventKind.Failed => "failed",
        LlmStreamEventKind.Cancelled => "cancelled",
        _ => "unknown"
    };
}
