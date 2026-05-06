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
            Description: DescriptionFor(toolId)), tool);

    private static string DescriptionFor(string toolId) =>
        toolId switch
        {
            MazeQuestToolIds.GetState => "Returns the public MazeQuest state, visible cells, objective progress, current objective, remaining objectives, and known legal actions.",
            MazeQuestToolIds.RenderMap => "Returns the current fog-of-war ASCII map and legend. It never reveals hidden route data.",
            MazeQuestToolIds.Scan => "Refreshes local fog-of-war discovery around the current position. Use when local state is stale or available moves are unclear.",
            MazeQuestToolIds.SenseObjective => "Returns hot/cold, rough bearing, distance band, and confidence for the active objective. This is guidance, not a path oracle.",
            MazeQuestToolIds.EvaluateMoves => "Returns local cardinal move legality, visible risk, terrain cost, objective delta, frontier gain, and blocker reasons. Use before choosing maze.move.",
            MazeQuestToolIds.Move => "Moves one cell in a legal cardinal direction. Input must be exactly one lowercase value from north, east, south, west copied from legalActions or moveEvaluations.",
            MazeQuestToolIds.Take => "Takes a visible object at the current cell into inventory. Input objectId must be copied exactly from a current legalActions entry.",
            MazeQuestToolIds.Use => "Uses, activates, unlocks, or delivers to a visible current-cell target object. Input targetId and optional item must be copied from legalActions, currentObject, or requiredItem.",
            MazeQuestToolIds.Rest => "Recovers a small amount of health and energy. Use only when resource state makes continued movement risky.",
            MazeQuestToolIds.CompleteObjective => "Checks terminal completion and emits mazequest.objective_completed only when the generated objective chain is state-satisfied. Do not use as a guess.",
            _ => "MazeQuest tool."
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
