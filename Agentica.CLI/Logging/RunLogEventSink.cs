using Agentica.Events;

namespace Agentica.CLI.Logging;

public sealed class RunLogEventSink : IEventSink
{
    private readonly IEventSink _inner;
    private readonly RunLogWriter _writer;

    public RunLogEventSink(IEventSink inner, RunLogWriter writer)
    {
        _inner = inner;
        _writer = writer;
    }

    public void Emit(ExecutionEvent executionEvent)
    {
        _writer.WriteEvent(executionEvent);
        _inner.Emit(executionEvent);
    }
}
