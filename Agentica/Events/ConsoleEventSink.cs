namespace Agentica.Events;

public sealed class ConsoleEventSink : IEventSink
{
    public void Emit(ExecutionEvent executionEvent)
    {
        var data = executionEvent.Data.Count == 0
            ? string.Empty
            : " " + string.Join(" ", executionEvent.Data.Select(pair => $"{pair.Key}={pair.Value}"));

        Console.WriteLine($"{executionEvent.Type,-18}{data}");
    }
}
