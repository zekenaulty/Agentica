namespace Agentica.Events;

/// <summary>
/// Receives best-effort event notifications. The authoritative event ledger is retained in the
/// outcome envelope; a sink exception is recorded and circuit-breaks delivery for that attempt.
/// This interface is not a durable audit/outbox guarantee.
/// </summary>
public interface IEventSink
{
    void Emit(ExecutionEvent executionEvent);
}
