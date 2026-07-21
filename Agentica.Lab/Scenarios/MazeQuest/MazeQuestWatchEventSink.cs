using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Events;

namespace Agentica.Lab.Scenarios.MazeQuest;

public sealed class MazeQuestWatchEventSink : IEventSink
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private readonly MazeQuestSession _session;
    private readonly IMazeQuestTurnNarrator _narrator;
    private readonly MazeQuestWatchControl _control;
    private readonly CancellationToken _cancellationToken;
    private readonly bool _turnJson;
    private readonly int _delayMilliseconds;
    private readonly Action<MazeQuestTurnEnvelope>? _turnRecorder;
    private int _turnNumber;

    static MazeQuestWatchEventSink()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public MazeQuestWatchEventSink(
        MazeQuestSession session,
        IMazeQuestTurnNarrator narrator,
        MazeQuestWatchControl control,
        CancellationToken cancellationToken,
        bool turnJson,
        int delayMilliseconds,
        Action<MazeQuestTurnEnvelope>? turnRecorder = null)
    {
        _session = session;
        _narrator = narrator;
        _control = control;
        _cancellationToken = cancellationToken;
        _turnJson = turnJson;
        _delayMilliseconds = Math.Max(0, delayMilliseconds);
        _turnRecorder = turnRecorder;
    }

    public void PrintOpening(string runObjective)
    {
        Console.WriteLine($"Agent has accepted MazeQuest: \"{_session.Stage.Quest.Title}\"");
        Console.WriteLine($"Objective: {_session.Stage.Quest.Objective}");
        Console.WriteLine($"Quest Type: {_session.Stage.Quest.QuestType}");
        Console.WriteLine($"Coverage: {string.Join(", ", _session.Stage.Quest.CoverageTags)}");
        Console.WriteLine($"Seed: {_session.Stage.Seed}");
        Console.WriteLine();
        Console.WriteLine("Host self-prompted RunRequest:");
        Console.WriteLine(Indent(runObjective.Trim(), "  "));
        Console.WriteLine();
        _control.PrintControls();
        Console.WriteLine();
        Console.WriteLine("Initial visible stage:");
        Console.WriteLine(MazeQuestRenderer.RenderFog(_session.Stage, _session.CurrentRunState));
    }

    public void Emit(ExecutionEvent executionEvent)
    {
        switch (executionEvent.Type)
        {
            case "run.created":
                PrintEvent("Run", executionEvent);
                break;
            case "request.accepted":
                PrintEvent("Request", executionEvent);
                break;
            case "plan.created":
                PrintEvent("Plan", executionEvent);
                break;
            case "plan.refined":
                PrintEvent("Plan", executionEvent);
                break;
            case "step.started":
                _control.WaitIfPaused(_cancellationToken);
                PrintStep(executionEvent);
                break;
            case "receipt.emitted":
                PrintReceiptTurn();
                Delay();
                break;
            case "outcome.reported":
                PrintEvent("Outcome", executionEvent);
                break;
            case "run.succeeded":
            case "run.blocked":
            case "run.failed":
            case "run.stopped":
                PrintEvent("Run", executionEvent);
                break;
            default:
                PrintEvent("Event", executionEvent);
                break;
        }
    }

    private void PrintStep(ExecutionEvent executionEvent)
    {
        var step = Data(executionEvent, "step") ?? "unknown";
        var tool = Data(executionEvent, "tool") ?? "unknown";
        Console.WriteLine();
        Console.WriteLine($"[step] {step} -> {tool}");
        PrintReason(executionEvent.UserFacingReason, executionEvent.Intent);
    }

    private void PrintReceiptTurn()
    {
        if (_session.LastTurn is null)
        {
            return;
        }

        var baseEnvelope = BuildEnvelope(_session.LastTurn);
        var narration = _narrator.Narrate(baseEnvelope, _cancellationToken);
        var envelope = baseEnvelope with { Narration = narration };
        _turnRecorder?.Invoke(envelope);

        Console.WriteLine($"Turn {envelope.TurnNumber:000} | {envelope.ToolId} | {envelope.ReceiptStatus}");
        Console.WriteLine($"Receipt: {envelope.ReceiptMessage}");
        Console.WriteLine($"State: pos=({envelope.Position.X},{envelope.Position.Y}) health={envelope.Health} energy={envelope.Energy} objective={envelope.ActiveObjectiveId} inventory={InventoryText(envelope)}");
        if (!string.IsNullOrWhiteSpace(envelope.Narration))
        {
            Console.WriteLine($"Narration: {envelope.Narration}");
        }

        if (!string.IsNullOrWhiteSpace(envelope.VisibleMapAscii))
        {
            Console.WriteLine();
            Console.WriteLine(envelope.VisibleMapAscii);
        }

        if (envelope.ObjectiveSignal is { } signal)
        {
            Console.WriteLine($"Signal: objective={signal.ObjectiveId} bearing={signal.Bearing} distance={signal.DistanceBand} warmth={signal.Warmth:0.00}");
        }

        var legalMoves = envelope.MoveEvaluations.Where(move => move.Legal).ToArray();
        if (legalMoves.Length > 0)
        {
            Console.WriteLine("Legal Moves:");
            foreach (var move in legalMoves)
            {
                Console.WriteLine($"  {move.Direction,-5} {move.ObjectiveDelta,-6} cost={move.TerrainCost} risk={move.VisibleRisk:0.00} frontier={move.FrontierGain}");
            }
        }

        var knownRoutes = envelope.KnownTravelOptions.Take(5).ToArray();
        if (knownRoutes.Length > 0)
        {
            Console.WriteLine("Known Routes:");
            foreach (var route in knownRoutes)
            {
                Console.WriteLine($"  to=({route.Destination.X},{route.Destination.Y}) hops={route.HopCount} cost={route.TotalTerrainCost} risk={route.MaxVisibleRisk:0.00} frontier={route.FrontierGain} {route.ObjectiveDelta}");
            }
        }

        if (_turnJson)
        {
            Console.WriteLine();
            Console.WriteLine("--- MazeQuest TurnEnvelope ---");
            Console.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
        }
    }

    private MazeQuestTurnEnvelope BuildEnvelope(MazeQuestToolTurn turn)
    {
        var receipt = turn.Result.Receipt;
        var runState = turn.RunState;
        return new MazeQuestTurnEnvelope(
            TurnNumber: ++_turnNumber,
            StepId: turn.Invocation.StepId,
            ToolId: turn.Invocation.ToolId,
            ReceiptId: receipt.ReceiptId,
            ReceiptStatus: receipt.Status.ToString(),
            ReceiptMessage: receipt.Message,
            ObservationId: turn.Result.Observation?.ObservationId,
            ArtifactId: turn.Result.Artifact?.ArtifactId,
            ArtifactKind: turn.Result.Artifact?.Kind,
            ActiveObjectiveId: runState.ActiveObjectiveId,
            StepCount: runState.StepCount,
            Position: runState.Position,
            Health: runState.Health,
            Energy: runState.Energy,
            Inventory: runState.Inventory,
            VisibleMapAscii: MazeQuestRenderer.RenderFog(_session.Stage, runState),
            ObjectiveSignal: MazeQuestAnalyzer.SenseObjective(_session.Stage, runState),
            MoveEvaluations: MazeQuestAnalyzer.EvaluateMoves(_session.Stage, runState),
            KnownTravelOptions: MazeQuestAnalyzer.KnownTravelOptions(_session.Stage, runState),
            Narration: string.Empty);
    }

    private void PrintEvent(string label, ExecutionEvent executionEvent)
    {
        var data = executionEvent.Data.Count == 0
            ? string.Empty
            : " " + string.Join(" ", executionEvent.Data.Select(pair => $"{pair.Key}={pair.Value}"));

        Console.WriteLine($"[{label.ToLowerInvariant()}] {executionEvent.Type}{data}");
        PrintReason(executionEvent.UserFacingReason, executionEvent.Intent);
    }

    private static void PrintReason(UserFacingReason? reason, ExecutionIntent? fallbackIntent)
    {
        if (reason is null)
        {
            PrintIntent(fallbackIntent);
            return;
        }

        Console.WriteLine($"  reason: {Compact(reason.Summary)}");
        if (!string.IsNullOrWhiteSpace(reason.Detail))
        {
            Console.WriteLine($"  detail: {Compact(reason.Detail)}");
        }
    }

    private static void PrintIntent(ExecutionIntent? intent)
    {
        if (intent is null)
        {
            return;
        }

        Console.WriteLine($"  action: {Compact(intent.Action)}");
        Console.WriteLine($"  why:    {Compact(intent.Rationale)}");
        if (!string.IsNullOrWhiteSpace(intent.ExpectedOutcome))
        {
            Console.WriteLine($"  expect: {Compact(intent.ExpectedOutcome)}");
        }
    }

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 220 ? compact : compact[..217] + "...";
    }

    private void Delay()
    {
        if (_delayMilliseconds <= 0)
        {
            return;
        }

        Thread.Sleep(_delayMilliseconds);
    }

    private static string? Data(ExecutionEvent executionEvent, string key) =>
        executionEvent.Data.TryGetValue(key, out var value) ? value : null;

    private static string InventoryText(MazeQuestTurnEnvelope envelope) =>
        envelope.Inventory.Count == 0 ? "empty" : string.Join(", ", envelope.Inventory);

    private static string Indent(string value, string prefix) =>
        string.Join(Environment.NewLine, value.Split(Environment.NewLine).Select(line => prefix + line));
}
