using Agentica.Tools;

namespace Agentica.CLI.Scenarios.ChessQuest;

public static class ChessQuestTools
{
    public static ToolCatalog CreateCatalog(ChessQuestSession session)
    {
        var dispatcher = new ChessQuestToolDispatcher(session);
        var registrations = new List<ToolRegistration>
        {
            Register(ChessQuestToolIds.GetState, "ChessQuest Get State", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(ChessQuestToolIds.RenderBoard, "ChessQuest Render Board", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(ChessQuestToolIds.ListLegalMoves, "ChessQuest List Legal Moves", ToolKind.Query, ToolEffect.ReadOnly, dispatcher)
        };

        if (session.Scenario.DisclosurePolicy.AllowLineProjection)
        {
            registrations.Add(Register(
                ChessQuestToolIds.ProjectLine,
                "ChessQuest Project Line",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                dispatcher,
                ToolInputSchema.Create(
                    new ToolInputField(
                        "line",
                        ToolInputValueType.Array,
                        Required: true,
                        Description: "Agent-authored UCI line to project from the current public position. The host does not choose moves or generate replies.",
                        Example: new[] { "g1f3", "g8f6" }),
                    new ToolInputField(
                        "maxPlies",
                        ToolInputValueType.Integer,
                        Required: false,
                        Description: "Maximum submitted plies to project, capped by the active surface policy.",
                        Example: 4,
                        Minimum: 1,
                        Maximum: session.Scenario.DisclosurePolicy.MaxProjectedPliesPerLine),
                    new ToolInputField(
                        "claims",
                        ToolInputValueType.Array,
                        Required: false,
                        Description: "Optional rule claims to verify for the submitted line. Supported values: check, checkmate. The host verifies only the agent-authored line and never suggests alternatives.",
                        Example: new[] { "check", "checkmate" }))));
        }

        if (session.Scenario.DisclosurePolicy.AllowAttackInspection)
        {
            registrations.Add(Register(
                ChessQuestToolIds.InspectAttacks,
                "ChessQuest Inspect Attacks",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                dispatcher));
        }

        registrations.Add(Register(
            ChessQuestToolIds.PlayMove,
            "ChessQuest Play Move",
            ToolKind.Action,
            ToolEffect.WritesLocalState,
            dispatcher,
            ToolInputSchema.Create(
                new ToolInputField(
                    "move",
                    Required: true,
                    Description: "Exact UCI move selected by the agent from current legal moves or its public line projection.",
                    Example: "g1f3"),
                new ToolInputField(
                    "legalMoveObservationId",
                    Required: false,
                    Description: "Observation id from the current chess.list_legal_moves result that contained the selected move. Required in strict gameplay; omitted only by actor probes.",
                    Example: "observation_abc123"),
                new ToolInputField(
                    "turnIntent",
                    ToolInputValueType.Object,
                    Required: true,
                    Description: "Short public decision declaration. Must include selectedMove matching move and should separate goal, evidence, hypothesis, riskCheck, and claimLevel. Do not include hidden chain-of-thought or unverified checkmate/completion claims.",
                    Example: new Dictionary<string, object?>
                    {
                        ["agentColor"] = "white",
                        ["selectedMove"] = "g1f3",
                        ["legalBasis"] = "selected_from_current_legal_move_list",
                        ["goal"] = "Improve development while preserving king safety.",
                        ["evidence"] = new[] { "g1f3 appeared in the current legal move list" },
                        ["hypothesis"] = "The knight move may improve piece activity.",
                        ["riskCheck"] = "Opponent replies are not fully modeled, so safety is unverified.",
                        ["claimLevel"] = "hypothesis",
                        ["publicReason"] = "Develop a knight without claiming it is fully safe.",
                        ["completionClaim"] = false
                    }))));
        registrations.Add(Register(ChessQuestToolIds.CompleteObjective, "ChessQuest Complete Objective", ToolKind.Action, ToolEffect.WritesLocalState, dispatcher));

        return ToolCatalog.Create(registrations.ToArray());
    }

    private static ToolRegistration Register(
        string toolId,
        string name,
        ToolKind kind,
        ToolEffect effect,
        ITool tool,
        ToolInputSchema? inputSchema = null) =>
        new(new ToolDescriptor(
            ToolId: toolId,
            Name: name,
            Kind: kind,
            Effect: effect,
            InputSchema: inputSchema,
            Description: DescriptionFor(toolId),
            ContextHint: ContextHintFor(toolId),
            Cooldown: CooldownFor(toolId)), tool);

    private static string DescriptionFor(string toolId) =>
        toolId switch
        {
            ChessQuestToolIds.GetState => "Returns current public ChessQuest session state, role, goal, FEN, turn, terminal state, and strict surface contract.",
            ChessQuestToolIds.RenderBoard => "Returns a plain ASCII board render for spatial inspection of the current public position.",
            ChessQuestToolIds.ListLegalMoves => "Returns current legal UCI moves plus legalMoveObservationId. Legal means playable under chess rules only; the order is not a ranking and legality does not imply safety, quality, or recommendation.",
            ChessQuestToolIds.ProjectLine => "Projects an agent-authored UCI line under public chess rules. It validates legality and resulting board state only; it does not rank moves, prove safety, evaluate quality, or generate opponent replies.",
            ChessQuestToolIds.InspectAttacks => "Returns neutral public attack facts: opponent legal captures from the current placement and agent pieces currently capturable. It does not score, choose, or attach quality labels.",
            ChessQuestToolIds.PlayMove => "Commits one selected legal agent UCI move with public intent, then applies one host-controlled opponent move when non-terminal. Public intent should separate evidence, hypothesis, risk, and verified facts.",
            ChessQuestToolIds.CompleteObjective => "Checks whether the current terminal board state satisfies the ChessQuest objective and emits the completion artifact only when verified.",
            _ => "ChessQuest tool."
        };

    private static ToolContextHint? ContextHintFor(string toolId) =>
        toolId switch
        {
            ChessQuestToolIds.GetState => new ToolContextHint(
                Produces: "current public chess session state, agent color, side to move, goal, FEN, and active strict surface",
                Complements:
                [
                    ChessQuestToolIds.RenderBoard,
                    ChessQuestToolIds.ListLegalMoves
                ],
                CanBatchWith:
                [
                    ChessQuestToolIds.RenderBoard,
                    ChessQuestToolIds.ListLegalMoves
                ],
                ShouldPrecede:
                [
                    ChessQuestToolIds.ProjectLine,
                    ChessQuestToolIds.PlayMove,
                    ChessQuestToolIds.CompleteObjective
                ])
            {
                UseWhen = "The planner needs current role, turn, goal, terminal status, or board FEN.",
                NotEnoughWhen = "The planner needs legal UCI moves or a board-shaped view."
            },
            ChessQuestToolIds.RenderBoard => new ToolContextHint(
                Produces: "plain current board layout",
                Complements:
                [
                    ChessQuestToolIds.GetState,
                    ChessQuestToolIds.ListLegalMoves
                ],
                CanBatchWith:
                [
                    ChessQuestToolIds.GetState,
                    ChessQuestToolIds.ListLegalMoves
                ],
                ShouldPrecede:
                [
                    ChessQuestToolIds.ProjectLine,
                    ChessQuestToolIds.PlayMove
                ])
            {
                UseWhen = "The planner needs spatial board inspection.",
                NotEnoughWhen = "The planner needs the exact legal UCI move set."
            },
            ChessQuestToolIds.ListLegalMoves => new ToolContextHint(
                Produces: "legal UCI moves for the current side and a legalMoveObservationId for the exact observed board state; legal affordances are not recommendations",
                Complements:
                [
                    ChessQuestToolIds.GetState,
                    ChessQuestToolIds.RenderBoard,
                    ChessQuestToolIds.ProjectLine
                ],
                CanBatchWith:
                [
                    ChessQuestToolIds.GetState,
                    ChessQuestToolIds.RenderBoard
                ],
                ShouldPrecede:
                [
                    ChessQuestToolIds.ProjectLine,
                    ChessQuestToolIds.PlayMove
                ])
            {
                UseWhen = "The planner needs exact legal action affordances before selecting a move, or must refresh after a stale/illegal move refusal.",
                NotEnoughWhen = "The planner needs to know whether its own hypothesis survives a submitted line; a legal move is not evidence of safety."
            },
            ChessQuestToolIds.ProjectLine => new ToolContextHint(
                Produces: "read-only public-rule projection of submitted UCI moves, including check/checkmate status for the submitted line; one-ply projection is not safety or quality evaluation",
                Complements:
                [
                    ChessQuestToolIds.ListLegalMoves,
                    ChessQuestToolIds.RenderBoard
                ],
                CanBatchWith: [],
                ShouldPrecede:
                [
                    ChessQuestToolIds.PlayMove
                ])
            {
                UseWhen = "The planner has self-authored candidate moves or a line and needs deterministic rule projection.",
                NotEnoughWhen = "The planner wants the host to select moves, rank choices, prove safety, or evaluate move quality; this surface never does that."
            },
            ChessQuestToolIds.InspectAttacks => new ToolContextHint(
                Produces: "read-only opponent legal captures and capturable agent pieces from the current placement, without scoring or move selection",
                Complements:
                [
                    ChessQuestToolIds.RenderBoard,
                    ChessQuestToolIds.ListLegalMoves,
                    ChessQuestToolIds.ProjectLine
                ],
                CanBatchWith:
                [
                    ChessQuestToolIds.GetState,
                    ChessQuestToolIds.RenderBoard,
                    ChessQuestToolIds.ListLegalMoves
                ],
                ShouldPrecede:
                [
                    ChessQuestToolIds.ProjectLine,
                    ChessQuestToolIds.PlayMove
                ])
            {
                UseWhen = "The planner needs public attack/capture facts before making safety or material claims.",
                NotEnoughWhen = "The planner wants scoring, ordering by quality, a chosen move, or a proof that a move is safe."
            },
            ChessQuestToolIds.PlayMove => new ToolContextHint(
                Produces: "committed agent move, committed opponent reply when applicable, updated public board state, receipt evidence, and the agent's public decision declaration",
                Complements: [],
                CanBatchWith: [],
                ShouldPrecede:
                [
                    ChessQuestToolIds.CompleteObjective
                ])
            {
                UseWhen = "It is the agent's turn and a legal UCI move has been selected with public turn intent that distinguishes evidence, hypothesis, and risk.",
                NotEnoughWhen = "The planner has not established legal UCI moves, turn ownership, or evidence/risk discipline for strong safety or material claims."
            },
            ChessQuestToolIds.CompleteObjective => new ToolContextHint(
                Produces: "verified ChessQuest completion artifact when the agent has won",
                Complements: [], CanBatchWith: [], ShouldPrecede: [])
            {
                UseWhen = "The current board state is terminal and the agent appears to have won.",
                NotEnoughWhen = "The game is not terminal or a draw/loss occurred."
            },
            _ => null
        };

    private static ToolCooldownPolicy? CooldownFor(string toolId) =>
        toolId is ChessQuestToolIds.GetState or ChessQuestToolIds.RenderBoard
            ? new ToolCooldownPolicy(
                PlanStepCount: 2,
                Reason: "ChessQuest public query data normally remains stable until a move changes the board.",
                ResetOnMutation: true)
            : null;
}

public sealed class ChessQuestToolDispatcher : ITool
{
    private readonly ChessQuestSession _session;

    public ChessQuestToolDispatcher(ChessQuestSession session)
    {
        _session = session;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
        _session.ExecuteAsync(invocation, cancellationToken);
}
