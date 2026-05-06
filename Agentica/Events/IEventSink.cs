namespace Agentica.Events;

public interface IEventSink
{
    void Emit(ExecutionEvent executionEvent);
}
