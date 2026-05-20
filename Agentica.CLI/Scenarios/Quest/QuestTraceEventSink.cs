using Agentica.Events;

namespace Agentica.CLI.Scenarios.Quest;

public sealed class QuestTraceEventSink : IEventSink
{
    private readonly QuestSession _session;
    private readonly Dictionary<string, ExecutionIntent> _stepIntents = new(StringComparer.Ordinal);
    private int _stepNumber;

    public QuestTraceEventSink(QuestSession session)
    {
        _session = session;
    }

    public void Emit(ExecutionEvent executionEvent)
    {
        switch (executionEvent.Type)
        {
            case "run.created":
                Console.WriteLine($"[run] started {Data(executionEvent, "run")}");
                break;
            case "request.accepted":
                Console.WriteLine("[request] accepted");
                break;
            case "plan.creation.started":
            case "plan.continuation.started":
            case "plan.refinement.started":
                PrintContextSlice(executionEvent);
                break;
            case "plan.created":
            case "plan.refined":
                PrintPlanSlice(executionEvent);
                break;
            case "step.started":
                PrintStepStarted(executionEvent);
                break;
            case "receipt.emitted":
                PrintReceipt(executionEvent);
                break;
            case "outcome.reported":
                Console.WriteLine();
                Console.WriteLine($"[outcome] {executionEvent.UserFacingReason?.Summary ?? "reported"}");
                break;
            case "run.succeeded":
            case "run.blocked":
            case "run.failed":
            case "run.stopped":
                Console.WriteLine($"[run] {executionEvent.Type["run.".Length..]}");
                break;
        }
    }

    private void PrintContextSlice(ExecutionEvent executionEvent)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Quest Context Slice | {Operation(executionEvent)} ===");
        Console.WriteLine($"surface: {SurfaceText(executionEvent)}");
        Console.WriteLine($"state: location={_session.State.Location} inventory={ListText(_session.State.Inventory)} openedLocks={ListText(_session.State.OpenedLocks)}");
        Console.WriteLine($"progress: visited={_session.State.VisitedRooms.Count} flags={_session.State.Flags.Count} completed={_session.State.ObjectiveCompleted}");
        PrintReason(executionEvent);
    }

    private static void PrintPlanSlice(ExecutionEvent executionEvent)
    {
        var stepIntents = ReadStepIntents(executionEvent.Payload).ToArray();
        var toolIds = ReadStringList(executionEvent.Payload, "toolIds");
        if (toolIds.Count == 0)
        {
            toolIds = stepIntents.Select(step => step.ToolId).Distinct(StringComparer.Ordinal).ToArray();
        }

        Console.WriteLine();
        Console.WriteLine($"--- Quest Plan Slice | {Data(executionEvent, "plan") ?? "unknown"} ---");
        Console.WriteLine($"steps: {Data(executionEvent, "steps") ?? (stepIntents.Length == 0 ? "?" : stepIntents.Length.ToString())}; tools: {ListText(toolIds)}");
        PrintReason(executionEvent);
        PrintStepIntents(stepIntents);
    }

    private void PrintStepStarted(ExecutionEvent executionEvent)
    {
        var stepId = executionEvent.Context?.StepId;
        if (stepId is not null && executionEvent.Intent is not null)
        {
            _stepIntents[stepId] = executionEvent.Intent;
        }

        Console.WriteLine();
        Console.WriteLine($"--- Quest Step {++_stepNumber:000} | {executionEvent.Context?.ToolId ?? Data(executionEvent, "tool") ?? "tool"} ---");
        PrintIntent(executionEvent.Intent);
    }

    private void PrintReceipt(ExecutionEvent executionEvent)
    {
        var turn = FindTurn(executionEvent.Context?.ReceiptId);
        var receipt = turn?.Result.Receipt;
        var status = receipt?.Status.ToString() ?? Data(executionEvent, "status") ?? "unknown";
        var message = receipt?.Message ?? executionEvent.UserFacingReason?.Summary ?? "receipt emitted";

        Console.WriteLine($"result: {status} - {Compact(message)}");
        if (turn is not null)
        {
            PrintToolSummary(turn);
        }

        Console.WriteLine($"state: location={_session.State.Location} inventory={ListText(_session.State.Inventory)} openedLocks={ListText(_session.State.OpenedLocks)} completed={_session.State.ObjectiveCompleted}");
    }

    private void PrintToolSummary(QuestToolTurn turn)
    {
        var data = turn.Result.Receipt.Data;
        switch (turn.Invocation.ToolId)
        {
            case QuestToolIds.ListLegalActions:
                Console.WriteLine($"context: legalActions={CountItems(data, "legalActions")} blockedActions={CountItems(data, "blockedActions")}");
                break;
            case QuestToolIds.Inspect:
                Console.WriteLine($"context: inspected={ReadString(data, "target") ?? "room"} visibleItems={ListText(ReadStringList(data, "items"))} exits={CountItems(data, "exits")}");
                break;
            case QuestToolIds.Move:
                Console.WriteLine($"action: moved {ReadString(data, "direction") ?? "unknown"} to {ReadString(data, "to") ?? _session.State.Location}");
                break;
            case QuestToolIds.Take:
                Console.WriteLine($"action: took {ReadString(data, "item") ?? "item"}");
                break;
            case QuestToolIds.Use:
                Console.WriteLine($"action: used {ReadString(data, "item") ?? "item"} on {ReadString(data, "target") ?? "target"}");
                break;
            case QuestToolIds.Talk:
                Console.WriteLine($"action: talked to {ReadString(data, "npc") ?? "npc"}; flag={ReadString(data, "discoveredFlag") ?? "none"}");
                break;
            case QuestToolIds.CompleteObjective:
                Console.WriteLine("completion: host objective artifact requested");
                break;
            default:
                if (ReadString(data, "reason") is { } reason)
                {
                    Console.WriteLine($"blocker: {reason}");
                }

                break;
        }
    }

    private QuestToolTurn? FindTurn(string? receiptId) =>
        string.IsNullOrWhiteSpace(receiptId)
            ? _session.LastTurn
            : _session.Turns.LastOrDefault(turn =>
                string.Equals(turn.Result.Receipt.ReceiptId, receiptId, StringComparison.Ordinal));

    private static void PrintReason(ExecutionEvent executionEvent)
    {
        if (executionEvent.UserFacingReason is { } reason)
        {
            Console.WriteLine($"reason: {Compact(reason.Summary)}");
            if (!string.IsNullOrWhiteSpace(reason.Detail))
            {
                Console.WriteLine($"detail: {Compact(reason.Detail)}");
            }

            return;
        }

        PrintIntent(executionEvent.Intent);
    }

    private static void PrintIntent(ExecutionIntent? intent)
    {
        if (intent is null)
        {
            return;
        }

        Console.WriteLine($"intent: {Compact(intent.Action)}");
        Console.WriteLine($"why: {Compact(intent.Rationale)}");
        if (!string.IsNullOrWhiteSpace(intent.ExpectedOutcome))
        {
            Console.WriteLine($"expect: {Compact(intent.ExpectedOutcome)}");
        }
    }

    private static void PrintStepIntents(IReadOnlyList<StepIntentSummary> stepIntents)
    {
        var steps = stepIntents.Take(6).ToArray();
        if (steps.Length == 0)
        {
            return;
        }

        Console.WriteLine("planned flow:");
        foreach (var step in steps)
        {
            Console.WriteLine($"  - {step.StepId} -> {step.ToolId}: {Compact(step.Action ?? step.Reason ?? "no public intent")}");
        }
    }

    private static IEnumerable<StepIntentSummary> ReadStepIntents(IReadOnlyDictionary<string, object?> payload)
    {
        var value = payload.TryGetValue("stepIntents", out var stepIntents)
            ? stepIntents
            : payload.TryGetValue("nextStepIntents", out var nextStepIntents)
                ? nextStepIntents
                : null;

        if (value is not IEnumerable<object> items)
        {
            yield break;
        }

        foreach (var item in items)
        {
            if (item is not IReadOnlyDictionary<string, object?> dictionary)
            {
                continue;
            }

            yield return new StepIntentSummary(
                ReadString(dictionary, "stepId") ?? "step",
                ReadString(dictionary, "toolId") ?? "tool",
                ReadString(dictionary, "action"),
                ReadString(dictionary, "reason"));
        }
    }

    private static string SurfaceText(ExecutionEvent executionEvent)
    {
        var parts = new List<string>();
        if (executionEvent.Context?.ToolSurfaceId is { } surfaceId)
        {
            parts.Add($"surface={surfaceId}");
        }

        AddPayloadCount(parts, executionEvent, "visibleToolCount", "tools");
        AddPayloadCount(parts, executionEvent, "observationCount", "observations");
        AddPayloadCount(parts, executionEvent, "receiptCount", "receipts");
        return parts.Count == 0 ? "surface=unknown" : string.Join(" ", parts);
    }

    private static void AddPayloadCount(List<string> parts, ExecutionEvent executionEvent, string key, string label)
    {
        if (executionEvent.Payload.TryGetValue(key, out var value) && value is not null)
        {
            parts.Add($"{label}={value}");
        }
    }

    private static string Operation(ExecutionEvent executionEvent) =>
        ReadString(executionEvent.Payload, "operation") ?? executionEvent.Type;

    private static string? Data(ExecutionEvent executionEvent, string key) =>
        executionEvent.Data.TryGetValue(key, out var value) ? value : null;

    private static string? ReadString(IReadOnlyDictionary<string, object?> source, string key) =>
        source.TryGetValue(key, out var value) && value is not null ? Convert.ToString(value) : null;

    private static IReadOnlyList<string> ReadStringList(IReadOnlyDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        return value switch
        {
            IEnumerable<string> strings => strings.ToArray(),
            IEnumerable<object> objects => objects
                .Select(Convert.ToString)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray(),
            _ => []
        };
    }

    private static int CountItems(IReadOnlyDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        if (value is string)
        {
            return 1;
        }

        return value is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object>().Count()
            : 0;
    }

    private static string ListText(IEnumerable<string> values)
    {
        var items = values.Where(item => !string.IsNullOrWhiteSpace(item)).Take(8).ToArray();
        return items.Length == 0 ? "none" : string.Join(", ", items);
    }

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 220 ? compact : compact[..217] + "...";
    }

    private sealed record StepIntentSummary(
        string StepId,
        string ToolId,
        string? Action,
        string? Reason);
}
