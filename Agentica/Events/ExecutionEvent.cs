namespace Agentica.Events;

public sealed record ExecutionEvent(
    string EventId,
    string Type,
    DateTimeOffset At,
    IReadOnlyDictionary<string, string> Data);
