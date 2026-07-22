using Agentica.Tools;

namespace Agentica.Lab.Scenarios.Quest;

public static class QuestTools
{
    public static ToolCatalog CreateCatalog(QuestSession session)
    {
        var dispatcher = new QuestToolDispatcher(session);
        return ToolCatalog.Create(
            Register(QuestToolIds.GetState, "Quest Get State", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(QuestToolIds.ListLegalActions, "Quest List Legal Actions", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(
                QuestToolIds.Inspect,
                "Quest Inspect",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField("target", Required: false, Example: "room"))),
            Register(
                QuestToolIds.Move,
                "Quest Move",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField(
                    "direction",
                    Required: true,
                    AllowedValues: ["north", "south", "east", "west"],
                    Example: "east"))),
            Register(
                QuestToolIds.Take,
                "Quest Take",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField("item", Required: true, Example: "sun_key"))),
            Register(
                QuestToolIds.Use,
                "Quest Use",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(
                    new ToolInputField("item", Required: true, Example: "sun_key"),
                    new ToolInputField("target", Required: true, Example: "sun_gate"))),
            Register(
                QuestToolIds.Talk,
                "Quest Talk",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField("npc", Required: true, Example: "guard"))),
            Register(QuestToolIds.CompleteObjective, "Quest Complete Objective", ToolKind.Action, ToolEffect.WritesLocalState, dispatcher));
    }

    private static ToolRegistration Register(
        string toolId,
        string name,
        ToolKind kind,
        ToolEffect effect,
        ITool tool,
        ToolInputSchema? inputSchema = null)
    {
        var retrySafety = effect == ToolEffect.ReadOnly
            ? ToolRetrySafety.Idempotent
            : ToolRetrySafety.MutationUnsafe;
        return new ToolRegistration(
            new ToolDescriptor(
                toolId,
                name,
                kind,
                effect,
                InputSchema: inputSchema,
                RetrySafety: retrySafety),
            tool,
            new ToolSecurityDeclaration(
                effect,
                [ToolDataBoundary.HostState],
                [ToolDataBoundary.HostState],
                ToolExternalOutputClassification.None,
                ToolApprovalRequirement.None,
                retrySafety,
                new ToolProvenance(ToolProvenanceKind.BuiltIn, "Agentica.Lab.Quest", "1")));
    }
}

public sealed class QuestToolDispatcher : ITool
{
    private readonly QuestSession _session;

    public QuestToolDispatcher(QuestSession session)
    {
        _session = session;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_session.Execute(invocation));
    }
}
