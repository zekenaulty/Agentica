using Agentica.Tools;

namespace Agentica.Tests;

internal static class TestToolRegistration
{
    public static ToolRegistration Create(ToolDescriptor descriptor, ITool tool)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(tool);

        var retrySafety = descriptor.RetrySafety != ToolRetrySafety.Unknown
            ? descriptor.RetrySafety
            : descriptor.Effect switch
            {
                ToolEffect.ReadOnly => ToolRetrySafety.Idempotent,
                ToolEffect.ExternalSideEffect => ToolRetrySafety.Additive,
                _ => ToolRetrySafety.MutationUnsafe
            };
        var normalizedDescriptor = descriptor with { RetrySafety = retrySafety };
        return new ToolRegistration(
            normalizedDescriptor,
            tool,
            new ToolSecurityDeclaration(
                normalizedDescriptor.Effect,
                [ToolDataBoundary.HostState],
                [ToolDataBoundary.HostState],
                normalizedDescriptor.Effect == ToolEffect.ExternalSideEffect
                    ? ToolExternalOutputClassification.Mixed
                    : ToolExternalOutputClassification.None,
                normalizedDescriptor.RequiresApproval
                    ? ToolApprovalRequirement.ExplicitGrant
                    : ToolApprovalRequirement.None,
                retrySafety,
                new ToolProvenance(ToolProvenanceKind.HostAuthored, "Agentica.Tests", "1")));
    }
}
