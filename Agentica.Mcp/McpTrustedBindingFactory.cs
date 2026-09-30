using Agentica.Tools;

namespace Agentica.Mcp;

/// <summary>Creates a tool registration only from host-owned declarations and a pinned
/// discovered input schema. Server annotations never determine effect or authority.</summary>
public static class McpTrustedBindingFactory
{
    public static McpToolBinding Create(
        string serverId,
        string remoteName,
        string expectedSchemaSha256,
        string toolId,
        string displayName,
        string description,
        string discoveredSchemaJson,
        ToolKind kind,
        ToolEffect effect,
        IReadOnlyCollection<ToolDataBoundary> reads,
        IReadOnlyCollection<ToolDataBoundary> exposesToPlanner,
        ToolExternalOutputClassification externalOutput,
        ToolApprovalRequirement approvalRequirement,
        ToolRetrySafety retrySafety,
        int maxResultCharacters = 65_536)
    {
        if (kind is not (ToolKind.Query or ToolKind.Action) || !Enum.IsDefined(effect) ||
            effect == ToolEffect.Unknown ||
            !Enum.IsDefined(externalOutput) || externalOutput == ToolExternalOutputClassification.Unknown ||
            !Enum.IsDefined(approvalRequirement) || approvalRequirement == ToolApprovalRequirement.Unknown ||
            !Enum.IsDefined(retrySafety) || retrySafety == ToolRetrySafety.Unknown ||
            kind == ToolKind.Query && effect != ToolEffect.ReadOnly ||
            kind == ToolKind.Action && effect == ToolEffect.ReadOnly)
        {
            throw new ArgumentException("MCP tool authority classification is invalid.");
        }
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(exposesToPlanner);
        var projected = McpReadOnlyBindingFactory.Create(serverId, remoteName,
            expectedSchemaSha256, toolId, displayName, description,
            discoveredSchemaJson);
        var descriptor = projected.Descriptor with
        {
            Kind = kind,
            Effect = effect,
            RequiresApproval = approvalRequirement == ToolApprovalRequirement.ExplicitGrant,
            RetrySafety = retrySafety
        };
        var security = new ToolSecurityDeclaration(effect, reads, exposesToPlanner,
            externalOutput, approvalRequirement, retrySafety,
            new ToolProvenance(ToolProvenanceKind.AdapterProvided, serverId));
        return projected with
        {
            Descriptor = descriptor,
            Security = security,
            MaxResultCharacters = maxResultCharacters
        };
    }
}
