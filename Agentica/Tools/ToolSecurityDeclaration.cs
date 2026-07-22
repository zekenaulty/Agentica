using System.Collections.Frozen;

namespace Agentica.Tools;

/// <summary>
/// Classifies data that can be present in a run or cross a tool/planner boundary.
/// Empty boundary sets mean that no classified data is read or exposed.
/// </summary>
public enum ToolDataBoundary
{
    Unknown,
    Public,
    HostState,
    UserContent,
    ConversationContent,
    WorkspaceContent,
    ExternalUntrusted
}

/// <summary>
/// Classifies untrusted output returned by an external tool or provider.
/// This is an inbound trust classification, not an effect/egress classification.
/// </summary>
public enum ToolExternalOutputClassification
{
    Unknown,
    None,
    UntrustedText,
    UntrustedStructuredData,
    UntrustedBinary,
    Mixed
}

public enum ToolApprovalRequirement
{
    Unknown,
    None,
    ExplicitGrant
}

public enum ToolProvenanceKind
{
    Unknown,
    BuiltIn,
    HostAuthored,
    AdapterProvided
}

public sealed record ToolProvenance(
    ToolProvenanceKind Kind,
    string Source,
    string? Version = null);

/// <summary>
/// Authoritative security and provenance declaration for one tool registration.
/// All supplied boundary collections are copied into immutable sets.
/// </summary>
public sealed class ToolSecurityDeclaration
{
    public ToolSecurityDeclaration(
        ToolEffect Effect,
        IEnumerable<ToolDataBoundary> Reads,
        IEnumerable<ToolDataBoundary> ExposesToPlanner,
        ToolExternalOutputClassification ExternalOutput,
        ToolApprovalRequirement ApprovalRequirement,
        ToolRetrySafety RetrySafety,
        ToolProvenance Provenance)
    {
        ArgumentNullException.ThrowIfNull(Reads);
        ArgumentNullException.ThrowIfNull(ExposesToPlanner);
        ArgumentNullException.ThrowIfNull(Provenance);

        this.Effect = Effect;
        this.Reads = Reads.ToFrozenSet();
        this.ExposesToPlanner = ExposesToPlanner.ToFrozenSet();
        this.ExternalOutput = ExternalOutput;
        this.ApprovalRequirement = ApprovalRequirement;
        this.RetrySafety = RetrySafety;
        this.Provenance = Provenance with { };
    }

    public ToolEffect Effect { get; }

    public IReadOnlySet<ToolDataBoundary> Reads { get; }

    public IReadOnlySet<ToolDataBoundary> ExposesToPlanner { get; }

    public ToolExternalOutputClassification ExternalOutput { get; }

    public ToolApprovalRequirement ApprovalRequirement { get; }

    public ToolRetrySafety RetrySafety { get; }

    public ToolProvenance Provenance { get; }
}
