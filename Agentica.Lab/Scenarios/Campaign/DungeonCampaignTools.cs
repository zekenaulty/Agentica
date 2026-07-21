using Agentica.Tools;

namespace Agentica.Lab.Scenarios.Campaign;

public static class DungeonCampaignTools
{
    public static ToolCatalog CreateCatalog(DungeonCampaignSession session)
    {
        var dispatcher = new DungeonCampaignToolDispatcher(session);
        return ToolCatalog.Create(
            Register(DungeonCampaignToolIds.GetState, "Dungeon Get State", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(
                DungeonCampaignToolIds.AcquireItem,
                "Dungeon Acquire Item",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField("item", Required: true, Example: "lantern"))),
            Register(
                DungeonCampaignToolIds.Explore,
                "Dungeon Explore",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField("area", Required: true, Example: "dark_archive"))),
            Register(
                DungeonCampaignToolIds.Unlock,
                "Dungeon Unlock",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField("gate", Required: true, Example: "bronze_vault"))),
            Register(DungeonCampaignToolIds.OpenFinalGate, "Dungeon Open Final Gate", ToolKind.Action, ToolEffect.WritesLocalState, dispatcher),
            Register(
                DungeonCampaignToolIds.CompleteMilestone,
                "Dungeon Complete Milestone",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField("milestoneId", Required: true, Example: "acquire_lantern"))));
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
                new ToolProvenance(ToolProvenanceKind.BuiltIn, "Agentica.Lab.Campaign", "1")));
    }
}

public sealed class DungeonCampaignToolDispatcher : ITool
{
    private readonly DungeonCampaignSession _session;

    public DungeonCampaignToolDispatcher(DungeonCampaignSession session)
    {
        _session = session;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_session.Execute(invocation));
    }
}
