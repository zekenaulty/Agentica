namespace Agentica.Events;

public sealed class ConsoleEventSink : IEventSink
{
    public void Emit(ExecutionEvent executionEvent)
    {
        var data = executionEvent.Data.Count == 0
            ? string.Empty
            : " " + string.Join(" ", executionEvent.Data.Select(pair => $"{pair.Key}={pair.Value}"));

        Console.WriteLine($"{executionEvent.Type,-18}{data}");
        PrintReason(executionEvent.UserFacingReason, executionEvent.Intent);
    }

    private static void PrintReason(UserFacingReason? reason, ExecutionIntent? fallbackIntent)
    {
        if (reason is null)
        {
            PrintIntent(fallbackIntent);
            return;
        }

        Console.WriteLine($"{"",-18}reason: {Compact(reason.Summary)}");
        if (!string.IsNullOrWhiteSpace(reason.Detail))
        {
            Console.WriteLine($"{"",-18}detail: {Compact(reason.Detail)}");
        }
    }

    private static void PrintIntent(ExecutionIntent? intent)
    {
        if (intent is null)
        {
            return;
        }

        Console.WriteLine($"{"",-18}action: {Compact(intent.Action)}");
        Console.WriteLine($"{"",-18}why:    {Compact(intent.Rationale)}");
        if (!string.IsNullOrWhiteSpace(intent.ExpectedOutcome))
        {
            Console.WriteLine($"{"",-18}expect: {Compact(intent.ExpectedOutcome)}");
        }
    }

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 220 ? compact : compact[..217] + "...";
    }
}
