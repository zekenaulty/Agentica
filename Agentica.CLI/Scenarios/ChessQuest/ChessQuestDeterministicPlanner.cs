using Agentica.Events;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.ChessQuest;

public sealed class ChessQuestDeterministicPlanner : IWorkflowPlanner
{
    private readonly ChessQuestSession _session;
    private int _nextStepNumber = 1;

    public ChessQuestDeterministicPlanner(ChessQuestSession session)
    {
        _session = session;
    }

    public Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextPlan());

    public Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(NextPlan());

    private WorkflowPlan NextPlan()
    {
        var stepNumber = _nextStepNumber++;
        return new WorkflowPlan(
            PlanId: $"chessquest_plan_{stepNumber:000}",
            Version: stepNumber,
            Steps: [NextStep(stepNumber)],
            Description: "Deterministic ChessQuest plan slice.")
        {
            PlanningReason = stepNumber switch
            {
                <= 3 => "establish_public_chess_state",
                4 => "project_agent_authored_line",
                _ when _session.CurrentState.IsTerminal => "verify_terminal_win",
                _ => "commit_public_chess_move"
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
            3 => Step(stepNumber, ChessQuestToolIds.ListLegalMoves, ToolKind.Query, ToolEffect.ReadOnly),
            4 when _session.Scenario.DisclosurePolicy.AllowLineProjection =>
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
        return Step(
            stepNumber,
            ChessQuestToolIds.PlayMove,
            ToolKind.Action,
            ToolEffect.WritesLocalState,
            ("move", move),
            ("turnIntent", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["agentColor"] = agentColor,
                ["selectedMove"] = move,
                ["legalBasis"] = "selected_from_current_legal_move_list",
                ["publicReason"] = "Use a legal move from the strict referee surface and keep playing for a win.",
                ["completionClaim"] = false
            }));
    }

    private string[] CandidateLine()
    {
        if (_session.Scenario.HiddenSolutionLine is { Count: > 0 } line)
        {
            return line.ToArray();
        }

        return [PickFallbackLegalMove()];
    }

    private string PickFallbackLegalMove()
    {
        var legalMoves = new GeraChessRulesEngine(_session.CurrentState.Fen).ListLegalMoves();
        return legalMoves.FirstOrDefault()?.Uci ?? "a2a3";
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
