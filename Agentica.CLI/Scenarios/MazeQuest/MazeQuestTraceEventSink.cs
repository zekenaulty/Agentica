using Agentica.Events;

namespace Agentica.CLI.Scenarios.MazeQuest;

public sealed class MazeQuestTraceEventSink : IEventSink
{
    private readonly MazeQuestSession _session;
    private readonly string _runObjective;
    private readonly Dictionary<string, ExecutionIntent> _stepIntents = new(StringComparer.Ordinal);
    private int _stepNumber;

    public MazeQuestTraceEventSink(MazeQuestSession session, string runObjective)
    {
        _session = session;
        _runObjective = runObjective;
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
                PrintContextFrame(executionEvent);
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

    private void PrintContextFrame(ExecutionEvent executionEvent)
    {
        var state = _session.CurrentRunState;
        var frame = MazeQuestCockpitFrameCompiler.BuildFrame(_session.Stage, state, _session.Turns);
        var harness = MazeQuestCapabilitySurfaceCompiler.BuildHarnessContext(
            _session.Stage,
            state,
            _runObjective,
            _session.Turns);

        Console.WriteLine();
        Console.WriteLine($"=== MazeQuest ContextFrame mazequest.cockpit | {Operation(executionEvent)} ===");
        Console.WriteLine($"surface: {executionEvent.Context?.ToolSurfaceId ?? "unknown"} tools={ReadString(executionEvent.Payload, "visibleToolCount") ?? "?"} observations={ReadString(executionEvent.Payload, "observationCount") ?? "?"} receipts={ReadString(executionEvent.Payload, "receiptCount") ?? "?"}");
        Console.WriteLine($"state: pos=({state.Position.X},{state.Position.Y}) health={state.Health} energy={state.Energy} objective={state.ActiveObjectiveId} inventory={ListText(state.Inventory)}");
        Console.WriteLine($"trajectory: pattern={frame.RecentTrajectory.DetectedPattern} repeated={frame.RecentTrajectory.RepeatedMoveCount} frontierGain={frame.ProgressSignals.RecentFrontierGain} turnsSinceProgress={frame.ProgressSignals.TurnsSinceProductiveStep}");
        Console.WriteLine($"posture: {frame.RecommendedPlannerPosture}; loop={frame.LoopSignals.StagnationSuspected}; boundedRisk={frame.ResourceRisk.BoundedRiskAllowed}");
        Console.WriteLine($"capabilities: preferred={ListText(PreferredCapabilities(harness))}");
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
        Console.WriteLine($"--- MazeQuest Plan Slice | {Data(executionEvent, "plan") ?? "unknown"} ---");
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
        Console.WriteLine($"--- MazeQuest Step {++_stepNumber:000} | {executionEvent.Context?.ToolId ?? Data(executionEvent, "tool") ?? "tool"} ---");
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

        var state = _session.CurrentRunState;
        Console.WriteLine($"state: pos=({state.Position.X},{state.Position.Y}) health={state.Health} energy={state.Energy} objective={state.ActiveObjectiveId} completed={state.CompletedObjectives.Contains("complete", StringComparer.Ordinal)}");
    }

    private static void PrintToolSummary(MazeQuestToolTurn turn)
    {
        var data = turn.Result.Receipt.Data;
        switch (turn.Invocation.ToolId)
        {
            case MazeQuestToolIds.Scan:
                Console.WriteLine($"context: newCellsDiscovered={ReadString(data, "newCellsDiscovered") ?? "0"}");
                break;
            case MazeQuestToolIds.SenseObjective:
                if (data.TryGetValue("objectiveSignal", out var signalValue) && signalValue is MazeObjectiveSignal signal)
                {
                    Console.WriteLine($"signal: objective={signal.ObjectiveId} bearing={signal.Bearing} distance={signal.DistanceBand} warmth={signal.Warmth:0.00}");
                }

                break;
            case MazeQuestToolIds.EvaluateMoves:
                Console.WriteLine($"context: moveEvaluations={CountItems(data, "moveEvaluations")} legalActions={CountItems(data, "legalActions")}");
                break;
            case MazeQuestToolIds.AnalyzeProgress:
                if (data.TryGetValue("loopSignals", out var loopValue) && loopValue is MazeQuestLoopSignals loop)
                {
                    Console.WriteLine($"progress: loop={loop.StagnationSuspected} cycle={loop.CycleType} reason={Compact(loop.Reason)}");
                }

                break;
            case MazeQuestToolIds.EvaluateEscapeMoves:
                Console.WriteLine($"escape: candidates={CountItems(data, "escapeCandidateMoves")} posture={ReadString(data, "recommendedPlannerPosture") ?? "unknown"}");
                break;
            case MazeQuestToolIds.Move:
                Console.WriteLine($"move: direction={ReadString(data, "direction") ?? "unknown"} frontierGain={ReadString(data, "frontierGain") ?? "0"} objectiveDelta={ReadString(data, "objectiveDelta") ?? "unknown"} risk={ReadString(data, "visibleRisk") ?? "0"}");
                break;
            case MazeQuestToolIds.MoveTo:
                Console.WriteLine($"move_to: hops={ReadString(data, "hopCount") ?? "0"} frontierGain={ReadString(data, "frontierGain") ?? "0"} maxRisk={ReadString(data, "maxVisibleRisk") ?? "0"}");
                break;
            case MazeQuestToolIds.Take:
                Console.WriteLine($"take: object={ReadString(data, "objectId") ?? "unknown"} name={ReadString(data, "displayName") ?? "unknown"}");
                break;
            case MazeQuestToolIds.Use:
                Console.WriteLine($"use: target={ReadString(data, "targetId") ?? "unknown"} item={ReadString(data, "item") ?? "none"} name={ReadString(data, "displayName") ?? "unknown"}");
                break;
            case MazeQuestToolIds.Rest:
                Console.WriteLine($"rest: energy {ReadString(data, "previousEnergy") ?? "?"}->{ReadString(data, "newEnergy") ?? "?"}; health {ReadString(data, "previousHealth") ?? "?"}->{ReadString(data, "newHealth") ?? "?"}");
                break;
            case MazeQuestToolIds.CompleteObjective:
                Console.WriteLine("completion: host objective artifact requested");
                break;
        }

        if (ReadString(data, "reason") is { } reason)
        {
            Console.WriteLine($"blocker: {reason}");
        }
    }

    private MazeQuestToolTurn? FindTurn(string? receiptId) =>
        string.IsNullOrWhiteSpace(receiptId)
            ? _session.LastTurn
            : _session.Turns.LastOrDefault(turn =>
                string.Equals(turn.Result.Receipt.ReceiptId, receiptId, StringComparison.Ordinal));

    private static IEnumerable<string> PreferredCapabilities(MazeQuestHarnessContext harness) =>
        harness.ActiveCapabilitySurface.Bindings
            .Where(binding => binding.State == MazeQuestCapabilityBindingState.Preferred)
            .OrderByDescending(binding => binding.Priority)
            .Select(binding => binding.ToolId ?? binding.CapabilityId)
            .Distinct(StringComparer.Ordinal)
            .Take(8);

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
