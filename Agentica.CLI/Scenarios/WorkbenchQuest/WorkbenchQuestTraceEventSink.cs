using Agentica.Events;

namespace Agentica.CLI.Scenarios.WorkbenchQuest;

public sealed class WorkbenchQuestTraceEventSink : IEventSink
{
    private readonly WorkbenchQuestSession _session;
    private readonly Dictionary<string, ExecutionIntent> _stepIntents = new(StringComparer.Ordinal);
    private int _stepNumber;

    public WorkbenchQuestTraceEventSink(WorkbenchQuestSession session)
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
        Console.WriteLine($"=== WorkbenchQuest Context Slice | {Operation(executionEvent)} ===");
        Console.WriteLine($"surface: {SurfaceText(executionEvent)}");
        Console.WriteLine($"scenario: {_session.Scenario.Descriptor.ScenarioId}");
        Console.WriteLine($"evidence gates: {EvidenceGateText()}");
        Console.WriteLine($"files: {ListText(_session.State.Files.Keys.Order(StringComparer.Ordinal))}");
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
        Console.WriteLine($"--- WorkbenchQuest Plan Slice | {Data(executionEvent, "plan") ?? "unknown"} ---");
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
        Console.WriteLine($"--- WorkbenchQuest Step {++_stepNumber:000} | {executionEvent.Context?.ToolId ?? Data(executionEvent, "tool") ?? "tool"} ---");
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

        Console.WriteLine($"evidence gates: {EvidenceGateText()}");
    }

    private void PrintToolSummary(WorkbenchQuestToolTurn turn)
    {
        var data = turn.Result.Receipt.Data;
        switch (turn.Invocation.ToolId)
        {
            case WorkbenchQuestToolIds.ListFiles:
                Console.WriteLine($"context: files={CountItems(data, "files")}");
                break;
            case WorkbenchQuestToolIds.ReadFile:
                Console.WriteLine($"context: read {ReadString(data, "path") ?? "file"} bytes={ReadString(data, "content")?.Length ?? 0}");
                break;
            case WorkbenchQuestToolIds.Search:
                Console.WriteLine($"context: query=\"{ReadString(data, "query") ?? string.Empty}\" matches={CountItems(data, "matches")} truncated={ReadString(data, "truncated") ?? "false"}");
                break;
            case WorkbenchQuestToolIds.RunCheck:
                Console.WriteLine($"check: #{ReadString(data, "checkNumber") ?? "?"} status={ReadString(data, "status") ?? "unknown"} output={Preview(ReadString(data, "output"), 180)}");
                break;
            case WorkbenchQuestToolIds.Diff:
                Console.WriteLine($"context: changedFiles={CountItems(data, "changedFiles")}");
                break;
            case WorkbenchQuestToolIds.ApplyPatch:
                Console.WriteLine($"patch: path={ReadString(data, "path") ?? "unknown"} patchNumber={ReadString(data, "patchNumber") ?? "?"}");
                if (ReadString(data, "rationale") is { } rationale)
                {
                    Console.WriteLine($"patch rationale: {Compact(rationale)}");
                }

                break;
            case WorkbenchQuestToolIds.WriteNote:
                Console.WriteLine($"note: #{ReadString(data, "noteNumber") ?? "?"} {Preview(ReadString(data, "note"), 160)}");
                break;
            case WorkbenchQuestToolIds.Complete:
                Console.WriteLine("completion: host completion artifact requested");
                break;
        }

        if (ReadString(data, "reason") is { } reason)
        {
            Console.WriteLine($"blocker: {reason}");
        }
    }

    private string EvidenceGateText()
    {
        var failedCheck = _session.State.CheckHistory.Any(check => !check.Passed);
        var passingAfterPatch = _session.State.PatchHistory.Any(patch => patch.Applied) &&
            _session.State.CheckHistory.Any(check => check.Passed);
        return $"failedCheck={failedCheck} readRelevantFile={ReadRelevantFile()} patchApplied={_session.State.PatchHistory.Any(patch => patch.Applied)} passingCheck={passingAfterPatch}";
    }

    private bool ReadRelevantFile() =>
        _session.State.ReadPaths.Any(path => _session.Scenario.RelevantPaths.Contains(path, StringComparer.Ordinal));

    private WorkbenchQuestToolTurn? FindTurn(string? receiptId) =>
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

    private static string Preview(string? value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var compact = value.ReplaceLineEndings(" ").Trim();
        return compact.Length <= maxCharacters ? compact : compact[..maxCharacters] + "...";
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
