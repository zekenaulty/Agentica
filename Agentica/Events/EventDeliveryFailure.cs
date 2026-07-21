namespace Agentica.Events;

/// <summary>
/// Describes the first failure to deliver an authoritative run event to its configured sink.
/// </summary>
public sealed record EventDeliveryFailure(
    string EventId,
    string EventType,
    long? EventSequence,
    string SinkType,
    string ExceptionType,
    string Message,
    DateTimeOffset FailedAt);
