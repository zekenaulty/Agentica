using Agentica.Events;

namespace Agentica.Lab.Scenarios.HexQuest;

public sealed class HexQuestTraceEventSink : IEventSink
{
    private readonly HexQuestSession _session;
    private readonly Dictionary<string, ExecutionIntent> _stepIntents = new(StringComparer.Ordinal);
    private int _stepNumber;

    public HexQuestTraceEventSink(HexQuestSession session)
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
        Console.WriteLine($"=== HexQuest Context Slice | {Operation(executionEvent)} ===");
        Console.WriteLine($"surface: {SurfaceText(executionEvent)}");
        Console.WriteLine($"goal: set {_session.Scenario.Goal.Field}={_session.Scenario.Goal.TargetValue} preserve={ListText(_session.Scenario.Goal.ProtectedFields)}");
        Console.WriteLine($"patch discipline: {PatchBudgetText()} contrastiveRequired={_session.Scenario.Goal.RequiredContrastiveProbes}");
        Console.WriteLine($"probe state: examples={_session.State.Examples.Count} sandbox={_session.State.SandboxProbes.Count} validations={_session.State.Validations.Count} commits={_session.State.Commits.Count}");
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
        Console.WriteLine($"--- HexQuest Plan Slice | {Data(executionEvent, "plan") ?? "unknown"} ---");
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
        Console.WriteLine($"--- HexQuest Step {++_stepNumber:000} | {executionEvent.Context?.ToolId ?? Data(executionEvent, "tool") ?? "tool"} ---");
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

        Console.WriteLine($"state: encoded={Preview(HexQuestCodec.ToHex(_session.State.Encoded), 96)} completed={_session.State.Completed} checksum={HexQuestCodec.HasValidChecksum(_session.Scenario, _session.State.Encoded)}");
    }

    private void PrintToolSummary(HexQuestToolTurn turn)
    {
        var data = turn.Result.Receipt.Data;
        switch (turn.Invocation.ToolId)
        {
            case HexQuestToolIds.InspectEncoded:
                Console.WriteLine($"context: encoded bytes={ReadString(data, "byteCount") ?? "?"} payload={Preview(ReadString(data, "encoded"), 96)}");
                break;
            case HexQuestToolIds.InspectDecoded:
                Console.WriteLine($"context: decoded {DecodedText(HexQuestCodec.Decode(_session.Scenario, _session.State.Encoded))}");
                break;
            case HexQuestToolIds.RequestExample:
                Console.WriteLine($"example: #{ReadString(data, "exampleNumber") ?? "?"} encoded={Preview(ReadString(data, "encoded"), 96)}");
                break;
            case HexQuestToolIds.SandboxSetDecoded:
                Console.WriteLine($"sandbox: field={ReadString(data, "field") ?? "?"} entity={ReadString(data, "entity") ?? "default"} value={ReadString(data, "value") ?? "?"} contrastive={ReadString(data, "contrastiveProbe") ?? "false"}");
                Console.WriteLine($"sandbox diff: changes={CountItems(data, "diff")} authoritative payload unchanged");
                break;
            case HexQuestToolIds.ValidatePatch:
                Console.WriteLine($"validation: #{ReadString(data, "validationNumber") ?? "?"} patch={Preview(ReadString(data, "patch"), 140)} accepted={ReadString(data, "accepted") ?? ReadString(data, "valid") ?? "unknown"}");
                PrintEvaluationDetail(data);
                break;
            case HexQuestToolIds.CommitPatch:
                Console.WriteLine($"commit: #{ReadString(data, "commitNumber") ?? "?"} patch={Preview(ReadString(data, "patch"), 140)}");
                PrintEvaluationDetail(data);
                break;
        }

        if (ReadString(data, "reason") is { } reason)
        {
            Console.WriteLine($"blocker: {reason}");
        }
    }

    private static void PrintEvaluationDetail(IReadOnlyDictionary<string, object?> data)
    {
        var details = new[]
        {
            Pair("goalSatisfied", ReadString(data, "goalSatisfied")),
            Pair("checksumValid", ReadString(data, "checksumValid")),
            Pair("protectedFieldsUnchanged", ReadString(data, "protectedFieldsUnchanged")),
            Pair("patchBytes", ReadString(data, "patchByteCount")),
            Pair("message", ReadString(data, "message"))
        }.Where(item => !string.IsNullOrWhiteSpace(item));
        var text = string.Join(" ", details);
        if (!string.IsNullOrWhiteSpace(text))
        {
            Console.WriteLine($"evaluation: {Compact(text)}");
        }
    }

    private static string? Pair(string label, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : $"{label}={value}";

    private string PatchBudgetText() =>
        _session.Scenario.Goal.ExposePatchConstraints
            ? _session.Scenario.Goal.MaxPatchBytes is null
                ? "patchBudget=unbounded"
                : $"patchBudget={_session.Scenario.Goal.MaxPatchBytes} byte edit(s)"
            : "patchBudget=discover through validation";

    private HexQuestToolTurn? FindTurn(string? receiptId) =>
        string.IsNullOrWhiteSpace(receiptId)
            ? _session.LastTurn
            : _session.Turns.LastOrDefault(turn =>
                string.Equals(turn.Result.Receipt.ReceiptId, receiptId, StringComparison.Ordinal));

    private static string DecodedText(HexQuestDecodedState state)
    {
        if (state.Characters is { Count: > 0 } characters)
        {
            return string.Join(
                "; ",
                characters.Select(character =>
                    $"{character.EntityId}:str={character.Strength} dex={character.Dexterity} gold={character.Gold} displayStr={character.DisplayStrength}"));
        }

        return $"strength={state.Strength} dexterity={state.Dexterity} gold={state.Gold}";
    }

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
