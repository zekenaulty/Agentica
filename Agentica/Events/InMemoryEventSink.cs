namespace Agentica.Events;

public sealed class InMemoryEventSink : IEventSink
{
    private readonly List<ExecutionEvent> _events = [];

    public IReadOnlyList<ExecutionEvent> Events => _events;

    public void Emit(ExecutionEvent executionEvent)
    {
        _events.Add(executionEvent);
    }
}
