using Agentica.Events;

internal sealed class ChatEventSink : IEventSink
{
    private readonly bool _verbose;
    private readonly ConsoleEventSink _console = new();
    private readonly List<ExecutionEvent> _events = [];

    public ChatEventSink(bool verbose)
    {
        _verbose = verbose;
    }

    public IReadOnlyList<ExecutionEvent> Events => _events;

    public void Emit(ExecutionEvent executionEvent)
    {
        _events.Add(executionEvent);
        if (_verbose)
        {
            _console.Emit(executionEvent);
        }
    }
}
