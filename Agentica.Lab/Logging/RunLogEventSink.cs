using Agentica.Events;

namespace Agentica.Lab.Logging;

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
        try
        {
            _writer.WriteEvent(executionEvent);
        }
        catch (Exception)
        {
            // The run log is observability only. The authoritative sink must still see the event.
        }
        finally
        {
            _inner.Emit(executionEvent);
        }
    }
}
