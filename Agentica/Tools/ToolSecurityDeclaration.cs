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

        if (!Enum.IsDefined(Effect))
        {
            throw new ArgumentOutOfRangeException(nameof(Effect), Effect, "Tool effect is undefined.");
        }

        if (!Enum.IsDefined(ExternalOutput))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExternalOutput),
                ExternalOutput,
                "External-output classification is undefined.");
        }

        if (!Enum.IsDefined(ApprovalRequirement))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ApprovalRequirement),
                ApprovalRequirement,
                "Approval requirement is undefined.");
        }

        if (!Enum.IsDefined(RetrySafety))
        {
            throw new ArgumentOutOfRangeException(nameof(RetrySafety), RetrySafety, "Retry safety is undefined.");
        }

        if (!Enum.IsDefined(Provenance.Kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Provenance),
                Provenance.Kind,
                "Provenance kind is undefined.");
        }

        var reads = SnapshotBoundaries(Reads, nameof(Reads));
        var exposesToPlanner = SnapshotBoundaries(
            ExposesToPlanner,
            nameof(ExposesToPlanner));
        if (reads.Any(boundary => !Enum.IsDefined(boundary)))
        {
            throw new ArgumentException("Read boundaries cannot contain undefined values.", nameof(Reads));
        }

        if (exposesToPlanner.Any(boundary => !Enum.IsDefined(boundary)))
        {
            throw new ArgumentException(
                "Planner-exposure boundaries cannot contain undefined values.",
                nameof(ExposesToPlanner));
        }

        this.Effect = Effect;
        this.Reads = reads.ToFrozenSet();
        this.ExposesToPlanner = exposesToPlanner.ToFrozenSet();
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

    private static ToolDataBoundary[] SnapshotBoundaries(
        IEnumerable<ToolDataBoundary> source,
        string parameterName)
    {
        const int maximumItems = 32;
        var snapshot = new List<ToolDataBoundary>();
        foreach (var boundary in source)
        {
            if (snapshot.Count >= maximumItems)
            {
                throw new ArgumentException(
                    $"{parameterName} cannot contain more than {maximumItems} items.",
                    parameterName);
            }

            snapshot.Add(boundary);
        }

        return snapshot.ToArray();
    }
}
