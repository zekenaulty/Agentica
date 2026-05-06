using Agentica.Tools;

namespace Agentica.CLI.Scenarios.HexQuest;

public static class HexQuestTools
{
    public static ToolCatalog CreateCatalog(HexQuestSession session)
    {
        var dispatcher = new HexQuestToolDispatcher(session);
        return ToolCatalog.Create(
            Register(HexQuestToolIds.InspectEncoded, "HexQuest Inspect Encoded", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(HexQuestToolIds.InspectDecoded, "HexQuest Inspect Decoded", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(HexQuestToolIds.RequestExample, "HexQuest Request Example", ToolKind.Query, ToolEffect.ReadOnly, dispatcher),
            Register(
                HexQuestToolIds.SandboxSetDecoded,
                "HexQuest Sandbox Set Decoded",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                dispatcher,
                ToolInputSchema.Create(
                    new ToolInputField("field", Required: true, Description: "Decoded field to change in a sandbox copy.", AllowedValues: ["Strength", "Dexterity", "Gold"], Example: "Strength"),
                    new ToolInputField("entity", Required: false, Description: "Optional decoded entity id for multi-record scenarios.", Example: "B"),
                    new ToolInputField("value", ToolInputValueType.Integer, Required: true, Description: "Target decoded value for the sandbox copy.", Example: 18, Minimum: 0, Maximum: 65535))),
            Register(
                HexQuestToolIds.ValidatePatch,
                "HexQuest Validate Patch",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField(
                    "patch",
                    Required: true,
                    Description: "Comma-separated byte patch in offset:old>new hex format.",
                    Example: "0:A9>B7,4:E8>E6"))),
            Register(
                HexQuestToolIds.CommitPatch,
                "HexQuest Commit Patch",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                dispatcher,
                ToolInputSchema.Create(new ToolInputField(
                    "patch",
                    Required: true,
                    Description: "Comma-separated byte patch in offset:old>new hex format. This is the only winning mutation surface.",
                    Example: "0:A9>B7,4:E8>E6"))));
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
            HexQuestToolIds.InspectEncoded => "Returns the current authoritative encoded payload bytes. It does not reveal offsets or transforms.",
            HexQuestToolIds.InspectDecoded => "Returns the decoded projection and goal. It is read-only and cannot satisfy the objective.",
            HexQuestToolIds.RequestExample => "Returns one few-shot decoded/encoded pair from the same hidden transform. The agent may call this again for another example.",
            HexQuestToolIds.SandboxSetDecoded => "Applies a decoded-field edit to a sandbox copy and returns the encoded before/after diff. It teaches the transform but does not mutate the authoritative payload.",
            HexQuestToolIds.ValidatePatch => "Dry-runs an encoded payload patch against the authoritative payload and returns the decoded result, checksum status, and goal/protection status.",
            HexQuestToolIds.CommitPatch => "Applies an encoded payload patch to the authoritative payload. It emits the completion artifact only when the goal is satisfied and protected fields are unchanged.",
            _ => "HexQuest tool."
        };
}

public sealed class HexQuestToolDispatcher : ITool
{
    private readonly HexQuestSession _session;

    public HexQuestToolDispatcher(HexQuestSession session)
    {
        _session = session;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_session.Execute(invocation));
    }
}
