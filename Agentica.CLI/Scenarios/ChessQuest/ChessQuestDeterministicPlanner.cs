using System.Text.Json;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed class ChessQuestDeterministicPlanner : IWorkflowPlanner
{
    private readonly ChessQuestSession _session;
    private readonly HashSet<string> _projectedLegalMoveObservationIds = new(StringComparer.Ordinal);
    private string? _latestLegalMoveObservationId;
    private string? _latestLegalMoveObservationFen;
    private int? _latestLegalMoveObservationPly;
    private string? _latestLegalMoveObservationSideToMove;
    private string[] _latestLegalMoves = [];
    private int _nextStepNumber = 1;

    public ChessQuestDeterministicPlanner(ChessQuestSession session)
    {
        _session = session;
    }

    public Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        CaptureLatestLegalMoveObservation(request.Observations);
        return Task.FromResult(NextPlan());
    }

    public Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default)
    {
        CaptureLatestLegalMoveObservation(request.Observations);
        CaptureLatestLegalMoveObservation([observation]);
        return Task.FromResult(NextPlan());
    }

    private WorkflowPlan NextPlan()
    {
        var stepNumber = _nextStepNumber++;
        var step = NextStep(stepNumber);
        return new WorkflowPlan(
            PlanId: $"chessquest_plan_{stepNumber:000}",
            Version: stepNumber,
            Steps: [step],
            Description: "Deterministic ChessQuest plan slice.")
        {
            PlanningReason = step.ToolId switch
            {
                ChessQuestToolIds.GetState or ChessQuestToolIds.RenderBoard or ChessQuestToolIds.ListLegalMoves =>
                    "establish_public_chess_state",
                ChessQuestToolIds.ProjectLine => "project_agent_authored_line",
                ChessQuestToolIds.CompleteObjective => "verify_terminal_win",
                ChessQuestToolIds.PlayMove => "commit_public_chess_move",
                _ => "continue_deterministic_chessquest_slice"
            }
        };
    }

    private PlanStep NextStep(int stepNumber)
    {
        if (_session.CurrentState.IsTerminal)
        {
            return Step(stepNumber, ChessQuestToolIds.CompleteObjective, ToolKind.Action, ToolEffect.WritesLocalState);
        }

        return stepNumber switch
        {
            1 => Step(stepNumber, ChessQuestToolIds.GetState, ToolKind.Query, ToolEffect.ReadOnly),
            2 => Step(stepNumber, ChessQuestToolIds.RenderBoard, ToolKind.Query, ToolEffect.ReadOnly),
            _ when !HasFreshLegalMoveObservation() =>
                Step(stepNumber, ChessQuestToolIds.ListLegalMoves, ToolKind.Query, ToolEffect.ReadOnly),
            _ when ShouldProjectCurrentLegalMoveObservation() =>
                Step(
                    stepNumber,
                    ChessQuestToolIds.ProjectLine,
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    ("line", CandidateLine()),
                    ("maxPlies", Math.Max(1, Math.Min(4, CandidateLine().Length)))),
            _ => PlayMoveStep(stepNumber, CandidateLine().FirstOrDefault() ?? PickFallbackLegalMove())
        };
    }

    private PlanStep PlayMoveStep(int stepNumber, string move)
    {
        var agentColor = _session.SessionContext.AgentColor.ToString().ToLowerInvariant();
        var input = new List<(string Key, object? Value)>
        {
            ("move", move)
        };
        if (!string.IsNullOrWhiteSpace(_latestLegalMoveObservationId) &&
            string.Equals(_latestLegalMoveObservationFen, _session.CurrentState.Fen, StringComparison.Ordinal))
        {
            input.Add(("legalMoveObservationId", _latestLegalMoveObservationId));
        }

        input.Add((
            "turnIntent",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agentColor"] = agentColor,
                ["selectedMove"] = move,
                ["legalBasis"] = "selected_from_current_legal_move_list",
                ["goal"] = "Commit one legal move while preserving the win objective.",
                ["evidence"] = new[] { $"{move} is the deterministic candidate from the current legal move surface." },
                ["hypothesis"] = "The move advances the deterministic harness slice without asserting move quality.",
                ["riskCheck"] = "Opponent replies are not fully modeled, so safety is unverified.",
                ["claimLevel"] = "hypothesis",
                ["publicReason"] = "Use a legal move from the strict referee surface and keep playing for a win.",
                ["completionClaim"] = false
            }));

        return Step(
            stepNumber,
            ChessQuestToolIds.PlayMove,
            ToolKind.Action,
            ToolEffect.WritesLocalState,
            input.ToArray());
    }

    private void CaptureLatestLegalMoveObservation(IEnumerable<Observation> observations)
    {
        foreach (var observation in observations)
        {
            if (!observation.Data.TryGetValue("operation", out var operation) ||
                !string.Equals(Convert.ToString(operation), "list_legal_moves", StringComparison.Ordinal))
            {
                continue;
            }

            if (!observation.Data.TryGetValue("fen", out var fenValue))
            {
                continue;
            }

            var fen = Convert.ToString(fenValue);
            if (string.IsNullOrWhiteSpace(fen))
            {
                continue;
            }

            _latestLegalMoveObservationId = observation.ObservationId;
            _latestLegalMoveObservationFen = fen;
            _latestLegalMoveObservationPly = ReadInt(observation.Data, "ply");
            _latestLegalMoveObservationSideToMove = ReadString(observation.Data, "sideToMove");
            _latestLegalMoves = ReadStringArray(observation.Data, "legalMoves");
        }
    }

    private string[] CandidateLine()
    {
        if (_session.Scenario.HiddenSolutionLine is { Count: > 0 } line &&
            line.FirstOrDefault() is { } hiddenMove &&
            _latestLegalMoves.Contains(hiddenMove, StringComparer.Ordinal))
        {
            return [hiddenMove];
        }

        return [PickBoundLegalMove()];
    }

    private bool HasFreshLegalMoveObservation()
    {
        if (string.IsNullOrWhiteSpace(_latestLegalMoveObservationId) ||
            string.IsNullOrWhiteSpace(_latestLegalMoveObservationFen))
        {
            return false;
        }

        var state = _session.CurrentState;
        return string.Equals(_latestLegalMoveObservationFen, state.Fen, StringComparison.Ordinal) &&
            _latestLegalMoveObservationPly == state.Ply &&
            string.Equals(_latestLegalMoveObservationSideToMove, state.SideToMove.ToString().ToLowerInvariant(), StringComparison.Ordinal) &&
            _latestLegalMoves.Length > 0;
    }

    private bool ShouldProjectCurrentLegalMoveObservation()
    {
        if (!_session.Scenario.DisclosurePolicy.AllowLineProjection ||
            string.IsNullOrWhiteSpace(_latestLegalMoveObservationId) ||
            _projectedLegalMoveObservationIds.Contains(_latestLegalMoveObservationId))
        {
            return false;
        }

        _projectedLegalMoveObservationIds.Add(_latestLegalMoveObservationId);
        return true;
    }

    private string PickBoundLegalMove() =>
        _latestLegalMoves.FirstOrDefault() ?? PickFallbackLegalMove();

    private string PickFallbackLegalMove()
    {
        var legalMoves = new GeraChessRulesEngine(_session.CurrentState.Fen).ListLegalMoves();
        return legalMoves.FirstOrDefault()?.Uci ?? "a2a3";
    }

    private static int? ReadInt(IReadOnlyDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            JsonElement { ValueKind: JsonValueKind.Number } json when json.TryGetInt32(out var intValue) => intValue,
            _ when int.TryParse(Convert.ToString(value), out var parsed) => parsed,
            _ => null
        };
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> source, string key) =>
        source.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value)
            : null;

    private static string[] ReadStringArray(IReadOnlyDictionary<string, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is string text)
        {
            return string.IsNullOrWhiteSpace(text) ? [] : [text];
        }

        if (value is JsonElement json)
        {
            return json.ValueKind == JsonValueKind.Array
                ? json.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!.Trim().ToLowerInvariant())
                    .ToArray()
                : [];
        }

        if (value is IEnumerable<string> strings)
        {
            return strings
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim().ToLowerInvariant())
                .ToArray();
        }

        if (value is System.Collections.IEnumerable items)
        {
            return items
                .Cast<object?>()
                .Select(item => Convert.ToString(item))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim().ToLowerInvariant())
                .ToArray();
        }

        return [];
    }

    private static PlanStep Step(
        int number,
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        params (string Key, object? Value)[] input) =>
        new(
            StepId: $"chessquest_step_{number:000}",
            ToolId: toolId,
            Kind: kind,
            Effect: effect,
            Input: input
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))
        {
            Reason = ReasonFor(toolId),
            Intent = IntentFor(toolId, input)
        };

    private static string ReasonFor(string toolId) =>
        toolId switch
        {
            ChessQuestToolIds.GetState => "Inspect public role, turn, goal, and FEN before choosing an action.",
            ChessQuestToolIds.RenderBoard => "Render the public board for spatial inspection.",
            ChessQuestToolIds.ListLegalMoves => "List exact UCI legal moves before selecting a move.",
            ChessQuestToolIds.ProjectLine => "Project only a self-authored UCI line without mutating the session.",
            ChessQuestToolIds.PlayMove => "Commit one legal agent move with a public turn intent.",
            ChessQuestToolIds.CompleteObjective => "Verify the terminal board state through the host objective gate.",
            _ => "Continue the bounded ChessQuest plan slice."
        };

    private static ExecutionIntent IntentFor(
        string toolId,
        IReadOnlyList<(string Key, object? Value)> input)
    {
        var move = input.FirstOrDefault(pair => pair.Key == "move").Value?.ToString();
        return toolId switch
        {
            ChessQuestToolIds.GetState => new ExecutionIntent(
                "Inspect the current ChessQuest session.",
                "The agent needs public role, turn, goal, and FEN before acting.",
                "Receive the current public chess session context."),
            ChessQuestToolIds.RenderBoard => new ExecutionIntent(
                "Render the current chess board.",
                "A board-shaped view supports public state inspection without strategic annotations.",
                "Receive a plain ASCII board render."),
            ChessQuestToolIds.ListLegalMoves => new ExecutionIntent(
                "List legal UCI moves.",
                "The strict referee surface exposes legal action affordances without ranking or tactical labels.",
                "Receive the current legal UCI move set."),
            ChessQuestToolIds.ProjectLine => new ExecutionIntent(
                "Project a self-authored UCI line.",
                "The projection is read-only and checks public-rule consequences for submitted moves only.",
                "Receive the resulting board state for the submitted line."),
            ChessQuestToolIds.PlayMove => new ExecutionIntent(
                $"Play {move ?? "the selected move"}.",
                "The selected UCI move is being committed with public turn intent.",
                "Commit the move and receive the host-controlled opponent reply when applicable."),
            ChessQuestToolIds.CompleteObjective => new ExecutionIntent(
                "Verify ChessQuest completion.",
                "Only the host objective verifier can emit the completion artifact.",
                "Receive a verified completion artifact if the agent has won."),
            _ => new ExecutionIntent(
                $"Invoke {toolId}.",
                "Continue the deterministic ChessQuest run.",
                "Receive the next receipt-backed result.")
        };
    }
}
