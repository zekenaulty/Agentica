namespace Agentica.Events;

/// <summary>
/// Receives best-effort event notifications. The authoritative event ledger is retained in the
/// outcome envelope; a sink exception is recorded and circuit-breaks delivery for that attempt.
/// Each callback is run behind the execution policy's bounded delivery wait. A callback that
/// exceeds the bound is detached and may finish later, so implementations must be observer-only,
/// thread-safe, and must not perform authoritative business effects. This interface is not a
/// durable audit/outbox guarantee.
/// </summary>
public interface IEventSink
{
    void Emit(ExecutionEvent executionEvent);
}
