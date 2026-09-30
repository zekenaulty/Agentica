using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Clients.Llm;

namespace Agentica.Lab.Configuration;

/// <summary>Operator-facing progress for one or more streamed planner calls.
/// JSONL records omit visible output text, native reasoning, and signatures.</summary>
internal sealed class StreamTelemetryReporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _provider;
    private readonly bool _jsonLines;
    private readonly bool _includeThoughtSummaries;
    private readonly TextWriter _writer;
    private readonly Stopwatch _clock = new();
    private string _callId = "";
    private int _sequence;
    private int _outputCharacters;
    private int _thoughtSummaryCharacters;
    private long? _firstOutputElapsedMs;
    private bool _inCall;

    public StreamTelemetryReporter(string provider, bool jsonLines = false,
        bool includeThoughtSummaries = false, TextWriter? writer = null)
    {
        _provider = provider;
        _jsonLines = jsonLines;
        _includeThoughtSummaries = includeThoughtSummaries;
        _writer = writer ?? Console.Error;
    }

    public void Report(LlmStreamEvent item)
    {
        if (item.Kind == LlmStreamEventKind.Started || !_inCall)
        {
            _clock.Restart();
            _callId = Guid.NewGuid().ToString("N");
            _sequence = 0;
            _outputCharacters = 0;
            _thoughtSummaryCharacters = 0;
            _firstOutputElapsedMs = null;
            _inCall = true;
        }

        var emit = true;
        var firstOutputInThisEvent = false;
        string? activity = null;
        string? summaryDelta = null;
        string? reason = null;
        switch (item.Kind)
        {
            case LlmStreamEventKind.Activity:
                activity = BoundedCode(item.Text);
                break;
            case LlmStreamEventKind.TextDelta:
                var previousCharacters = _outputCharacters;
                _outputCharacters += item.Text?.Length ?? 0;
                if (_firstOutputElapsedMs is null && _outputCharacters > 0)
                {
                    _firstOutputElapsedMs = _clock.ElapsedMilliseconds;
                    firstOutputInThisEvent = true;
                }
                else if (_outputCharacters / 512 == previousCharacters / 512)
                    emit = false;
                break;
            case LlmStreamEventKind.ThoughtSummaryDelta:
                _thoughtSummaryCharacters += item.Text?.Length ?? 0;
                summaryDelta = _includeThoughtSummaries ? item.Text : null;
                break;
            case LlmStreamEventKind.Completed:
                _outputCharacters = Math.Max(_outputCharacters,
                    item.Response?.Text.Length ?? 0);
                _inCall = false;
                break;
            case LlmStreamEventKind.Failed:
            case LlmStreamEventKind.Cancelled:
                reason = BoundedCode(item.Text);
                _inCall = false;
                break;
        }
        if (!emit) return;

        if (_jsonLines)
        {
            _writer.WriteLine(JsonSerializer.Serialize(new TelemetryRecord(
                SchemaVersion: 1,
                CallId: _callId,
                Sequence: ++_sequence,
                Provider: _provider,
                Kind: KindCode(item.Kind),
                At: DateTimeOffset.UtcNow,
                ElapsedMs: _clock.ElapsedMilliseconds,
                OutputCharacters: _outputCharacters,
                ThoughtSummaryCharacters: _thoughtSummaryCharacters,
                FirstOutputElapsedMs: _firstOutputElapsedMs,
                Activity: activity,
                ThoughtSummaryDelta: summaryDelta,
                Usage: item.Kind == LlmStreamEventKind.Completed
                    ? item.Response?.Usage : null,
                FinishReason: item.Kind == LlmStreamEventKind.Completed
                    ? item.Response?.FinishReason.ToString() : null,
                Reason: reason), JsonOptions));
            return;
        }

        switch (item.Kind)
        {
            case LlmStreamEventKind.Started:
                _writer.WriteLine($"[{_provider}] call started");
                break;
            case LlmStreamEventKind.Activity:
                _writer.WriteLine($"[{_provider}] {activity}");
                break;
            case LlmStreamEventKind.TextDelta:
                _writer.WriteLine(firstOutputInThisEvent
                    ? $"[{_provider}] first output at {_firstOutputElapsedMs} ms"
                    : $"[{_provider}] streaming {_outputCharacters} planner characters; " +
                      $"elapsed={_clock.ElapsedMilliseconds} ms");
                break;
            case LlmStreamEventKind.ThoughtSummaryDelta:
                _writer.WriteLine($"[{_provider}] thought summary: {item.Text}");
                break;
            case LlmStreamEventKind.Completed:
                _writer.WriteLine(
                    $"[{_provider}] completed in {_clock.ElapsedMilliseconds} ms; " +
                    $"plannerChars={_outputCharacters}; " +
                    $"input={item.Response?.Usage?.PromptTokens?.ToString() ?? "unknown"}; " +
                    $"output={item.Response?.Usage?.OutputTokens?.ToString() ?? "unknown"}; " +
                    $"thought={item.Response?.Usage?.ThinkingTokens?.ToString() ?? "unknown"}; " +
                    $"finish={item.Response?.FinishReason}");
                break;
            case LlmStreamEventKind.Failed:
            case LlmStreamEventKind.Cancelled:
                _writer.WriteLine(
                    $"[{_provider}] {item.Kind.ToString().ToLowerInvariant()} " +
                    $"after {_clock.ElapsedMilliseconds} ms; " +
                    $"reason={reason ?? "unknown"}; plannerChars={_outputCharacters}");
                break;
        }
    }

    private static string? BoundedCode(string? value) =>
        value is { Length: > 128 } ? value[..128] : value;

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

    private sealed record TelemetryRecord(
        int SchemaVersion,
        string CallId,
        int Sequence,
        string Provider,
        string Kind,
        DateTimeOffset At,
        long ElapsedMs,
        int OutputCharacters,
        int ThoughtSummaryCharacters,
        long? FirstOutputElapsedMs,
        string? Activity,
        string? ThoughtSummaryDelta,
        LlmUsage? Usage,
        string? FinishReason,
        string? Reason);
}
