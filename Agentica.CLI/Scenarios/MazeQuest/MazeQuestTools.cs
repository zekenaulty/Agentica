using Agentica.Tools;

namespace Agentica.CLI.Scenarios.MazeQuest;

public static class MazeQuestTools
{
    public static ToolCatalog CreateCatalog(MazeQuestSession session)
    {
        var dispatcher = new MazeQuestToolDispatcher(session);
        return ToolCatalog.Create(
            Register(MazeQuestToolIds.GetState, "MazeQuest Get State", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(MazeQuestToolIds.RenderMap, "MazeQuest Render Map", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(MazeQuestToolIds.Scan, "MazeQuest Scan", ToolKind.Action, ToolEffect.WritesLocalState, dispatcher),
            Register(MazeQuestToolIds.SenseObjective, "MazeQuest Sense Objective", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(MazeQuestToolIds.EvaluateMoves, "MazeQuest Evaluate Moves", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(MazeQuestToolIds.AnalyzeProgress, "MazeQuest Analyze Progress", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(MazeQuestToolIds.EvaluateEscapeMoves, "MazeQuest Evaluate Escape Moves", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(
                MazeQuestToolIds.Move,
                "MazeQuest Move",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField(
                    "direction",
                    Required: true,
                    Description: "Exact lowercase cardinal direction copied from legalActions or moveEvaluations. Do not use diagonals, coordinates, compass compounds, or explanatory text.",
                    AllowedValues: ["north", "east", "south", "west"],
                    Example: "east"))),
            Register(
                MazeQuestToolIds.MoveTo,
                "MazeQuest Move To Known Cell",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(
                    new ToolInputField(
                        "x",
                        ToolInputValueType.Integer,
                        Required: true,
                        Description: "Destination x coordinate copied exactly from a legalActions maze.move_to entry or knownTravelOptions item. Destination must already be exposed.",
                        Example: 1),
                    new ToolInputField(
                        "y",
                        ToolInputValueType.Integer,
                        Required: true,
                        Description: "Destination y coordinate copied exactly from a legalActions maze.move_to entry or knownTravelOptions item. Every hop must be through exposed traversable cells.",
                        Example: 9))),
            Register(
                MazeQuestToolIds.Take,
                "MazeQuest Take",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField(
                    "objectId",
                    Required: true,
                    Description: "Exact objectId from a visible current-cell legalActions entry for maze.take. Do not guess hidden object ids.",
                    Example: "sun_key"))),
            Register(
                MazeQuestToolIds.Use,
                "MazeQuest Use",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(
                    new ToolInputField(
                        "targetId",
                        Required: true,
                        Description: "Exact targetId from a visible current-cell legalActions entry for maze.use. Use this for gates, activators, dropoffs, refuges, runes, and markers.",
                        Example: "sun_gate"),
                    new ToolInputField(
                        "item",
                        Required: false,
                        Description: "Inventory object id required by the target. Include only when legalActions or the target requiredItem provides it.",
                        Example: "sun_key"))),
            Register(MazeQuestToolIds.Rest, "MazeQuest Rest", ToolKind.Action, ToolEffect.WritesLocalState, dispatcher),
            Register(MazeQuestToolIds.CompleteObjective, "MazeQuest Complete Objective", ToolKind.Action, ToolEffect.WritesLocalState, dispatcher));
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
            MazeQuestToolIds.GetState => "Returns the public MazeQuest state, visible cells, objective progress, current objective, remaining objectives, and known legal actions. Batches well with objective, move, and progress query tools for a context refresh.",
            MazeQuestToolIds.RenderMap => "Returns the current fog-of-war ASCII map and legend. It never reveals hidden route data. Use with move evaluation when spatial layout matters.",
            MazeQuestToolIds.Scan => "Refreshes local fog-of-war discovery around the current position. Use when local state is stale or available moves are unclear.",
            MazeQuestToolIds.SenseObjective => "Returns hot/cold, rough bearing, distance band, and confidence for the active objective. This is guidance, not a path oracle. Pair with move evaluation to compare local choices against objective trend.",
            MazeQuestToolIds.EvaluateMoves => "Returns local cardinal move legality, visible risk, terrain cost, coarse objective affordance, frontier gain, and blocker reasons. It does not choose the best route. Pair with get_state and sense_objective before moving.",
            MazeQuestToolIds.AnalyzeProgress => "Returns a compact public trajectory frame: recent move log, repeated cells, objective trend, frontier trend, resource burn, and stagnation signals. It does not reveal hidden routes. Pair with evaluate_escape_moves when movement may be looping.",
            MazeQuestToolIds.EvaluateEscapeMoves => "Classifies current local moves by revisit count, loop break, visible risk, frontier gain, objective compatibility, and bounded-risk justification. It is not a pathfinding oracle. Pair with analyze_progress during stagnation recovery.",
            MazeQuestToolIds.Move => "Moves one cell in a legal cardinal direction. Input must be exactly one lowercase value from north, east, south, west copied from legalActions or moveEvaluations.",
            MazeQuestToolIds.MoveTo => "Moves across a fully exposed public route to an already discovered cell in one tool call. Use only when knownTravelOptions or legalActions provide the exact destination and no intermediate take/use interaction is needed.",
            MazeQuestToolIds.Take => "Takes a visible object at the current cell into inventory. Input objectId must be copied exactly from a current legalActions entry.",
            MazeQuestToolIds.Use => "Uses, activates, unlocks, or delivers to a visible current-cell target object. Input targetId and optional item must be copied from legalActions, currentObject, or requiredItem.",
            MazeQuestToolIds.Rest => "Recovers a small amount of health and energy. Use only when resource state makes continued movement risky.",
            MazeQuestToolIds.CompleteObjective => "Checks terminal completion and emits mazequest.objective_completed only when the generated objective chain is state-satisfied. Do not use as a guess.",
            _ => "MazeQuest tool."
        };

    private static ToolContextHint? ContextHintFor(string toolId) =>
        toolId switch
        {
            MazeQuestToolIds.GetState => new ToolContextHint(
                Produces: "current public state, visible cells, objective board, resources, legal actions, and active harness surface",
                Complements:
                [
                    MazeQuestToolIds.SenseObjective,
                    MazeQuestToolIds.EvaluateMoves,
                    MazeQuestToolIds.AnalyzeProgress
                ],
                CanBatchWith:
                [
                    MazeQuestToolIds.RenderMap,
                    MazeQuestToolIds.SenseObjective,
                    MazeQuestToolIds.EvaluateMoves,
                    MazeQuestToolIds.AnalyzeProgress,
                    MazeQuestToolIds.EvaluateEscapeMoves
                ],
                ShouldPrecede:
                [
                    MazeQuestToolIds.Move,
                    MazeQuestToolIds.Take,
                    MazeQuestToolIds.Use,
                    MazeQuestToolIds.Rest,
                    MazeQuestToolIds.MoveTo,
                    MazeQuestToolIds.CompleteObjective
                ])
            {
                UseWhen = "Current state, legal actions, resources, objective progress, or active surface may be stale.",
                NotEnoughWhen = "Spatial layout, objective bearing, local move risk, or trajectory-loop detail is needed."
            },
            MazeQuestToolIds.RenderMap => new ToolContextHint(
                Produces: "visual fog-of-war map and legend for currently discovered cells",
                Complements:
                [
                    MazeQuestToolIds.GetState,
                    MazeQuestToolIds.EvaluateMoves
                ],
                CanBatchWith:
                [
                    MazeQuestToolIds.GetState,
                    MazeQuestToolIds.SenseObjective,
                    MazeQuestToolIds.EvaluateMoves,
                    MazeQuestToolIds.AnalyzeProgress
                ],
                ShouldPrecede:
                [
                    MazeQuestToolIds.Move,
                    MazeQuestToolIds.MoveTo
                ])
            {
                UseWhen = "The planner needs spatial orientation or wants to compare local move options against visible layout.",
                NotEnoughWhen = "The planner needs exact local legality, resource impact, objective trend, or loop classification."
            },
            MazeQuestToolIds.SenseObjective => new ToolContextHint(
                Produces: "coarse bearing, distance band, warmth, and confidence for the active objective",
                Complements:
                [
                    MazeQuestToolIds.GetState,
                    MazeQuestToolIds.EvaluateMoves,
                    MazeQuestToolIds.AnalyzeProgress
                ],
                CanBatchWith:
                [
                    MazeQuestToolIds.GetState,
                    MazeQuestToolIds.RenderMap,
                    MazeQuestToolIds.EvaluateMoves,
                    MazeQuestToolIds.AnalyzeProgress,
                    MazeQuestToolIds.EvaluateEscapeMoves
                ],
                ShouldPrecede:
                [
                    MazeQuestToolIds.Move,
                    MazeQuestToolIds.MoveTo
                ])
            {
                UseWhen = "Objective direction or distance trend is needed before choosing a move.",
                NotEnoughWhen = "The planner needs legal move details, visible hazard risk, or repeated-path evidence."
            },
            MazeQuestToolIds.EvaluateMoves => new ToolContextHint(
                Produces: "legal cardinal moves, blockers, terrain cost, visible risk, frontier gain, and objective affordance per direction",
                Complements:
                [
                    MazeQuestToolIds.GetState,
                    MazeQuestToolIds.SenseObjective,
                    MazeQuestToolIds.EvaluateEscapeMoves
                ],
                CanBatchWith:
                [
                    MazeQuestToolIds.GetState,
                    MazeQuestToolIds.RenderMap,
                    MazeQuestToolIds.SenseObjective,
                    MazeQuestToolIds.AnalyzeProgress,
                    MazeQuestToolIds.EvaluateEscapeMoves
                ],
                ShouldPrecede:
                [
                    MazeQuestToolIds.Move,
                    MazeQuestToolIds.MoveTo,
                    MazeQuestToolIds.Rest
                ])
            {
                UseWhen = "Any move is being considered or current move legality/risk is uncertain.",
                NotEnoughWhen = "Recent movement history, loop state, or escape classification is the deciding factor."
            },
            MazeQuestToolIds.AnalyzeProgress => new ToolContextHint(
                Produces: "recent public trajectory, repeated cells, frontier/objective trends, resource burn, and stagnation signals",
                Complements:
                [
                    MazeQuestToolIds.EvaluateEscapeMoves,
                    MazeQuestToolIds.EvaluateMoves,
                    MazeQuestToolIds.SenseObjective
                ],
                CanBatchWith:
                [
                    MazeQuestToolIds.GetState,
                    MazeQuestToolIds.RenderMap,
                    MazeQuestToolIds.SenseObjective,
                    MazeQuestToolIds.EvaluateMoves,
                    MazeQuestToolIds.EvaluateEscapeMoves
                ],
                ShouldPrecede:
                [
                    MazeQuestToolIds.Move,
                    MazeQuestToolIds.MoveTo,
                    MazeQuestToolIds.Rest
                ])
            {
                UseWhen = "Recent moves may be cycling, frontier gain is repeatedly zero, or objective progress is unclear.",
                NotEnoughWhen = "The planner needs current per-direction escape classification or current legal move blockers."
            },
            MazeQuestToolIds.EvaluateEscapeMoves => new ToolContextHint(
                Produces: "per-direction loop escape classification, revisit count, loop-break flag, visible risk justification, and recommendation posture",
                Complements:
                [
                    MazeQuestToolIds.AnalyzeProgress,
                    MazeQuestToolIds.EvaluateMoves,
                    MazeQuestToolIds.GetState
                ],
                CanBatchWith:
                [
                    MazeQuestToolIds.GetState,
                    MazeQuestToolIds.RenderMap,
                    MazeQuestToolIds.SenseObjective,
                    MazeQuestToolIds.EvaluateMoves,
                    MazeQuestToolIds.AnalyzeProgress
                ],
                ShouldPrecede:
                [
                    MazeQuestToolIds.Move,
                    MazeQuestToolIds.MoveTo,
                    MazeQuestToolIds.Rest
                ])
            {
                UseWhen = "The planner suspects a loop or must compare safe looping moves against bounded-risk alternatives.",
                NotEnoughWhen = "The planner needs broader public objective board, inventory/object facts, or a rendered map."
            },
            _ => null
        };

    private static ToolCooldownPolicy? CooldownFor(string toolId) =>
        toolId switch
        {
            MazeQuestToolIds.GetState or
            MazeQuestToolIds.RenderMap or
            MazeQuestToolIds.SenseObjective or
            MazeQuestToolIds.EvaluateMoves or
            MazeQuestToolIds.AnalyzeProgress or
            MazeQuestToolIds.EvaluateEscapeMoves => new ToolCooldownPolicy(
                PlanStepCount: 3,
                Reason: "MazeQuest public context queries normally do not produce new data until another tool advances host state.",
                ResetOnMutation: true),
            _ => null
        };
}

public sealed class MazeQuestToolDispatcher : ITool
{
    private readonly MazeQuestSession _session;

    public MazeQuestToolDispatcher(MazeQuestSession session)
    {
        _session = session;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_session.Execute(invocation));
    }
}
