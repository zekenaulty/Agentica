using Agentica.Observations;

namespace Agentica.Projection;

/// <summary>Question lenses suggested by AI.Forge. Hosts may use additional facet keys.</summary>
public static class SemanticFacets
{
    public const string What = "what";
    public const string When = "when";
    public const string Where = "where";
    public const string Why = "why";
    public const string Who = "who";
    public const string How = "how";
}

/// <summary>A status supplied by the host; the projector does not turn it into proof.</summary>
public enum SemanticClaimStatus
{
    Unknown = 0,
    Observed = 1,
    Inferred = 2,
    Proposed = 3
}

public sealed record SemanticFacet(
    string Text,
    SemanticClaimStatus Status,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

/// <summary>Host-owned domain lens. Parents allow a specific lens to inherit broader interpretations.</summary>
public sealed record DomainPerspective(
    string PerspectiveId,
    string DomainId,
    string? ParentPerspectiveId = null);

/// <summary>A bounded concept, workflow, plan, or other host-defined semantic node.</summary>
public sealed record SemanticConceptNode(
    string NodeId,
    string DomainId,
    string Kind,
    int Revision,
    IReadOnlyDictionary<string, SemanticFacet> BaseFacets,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, SemanticFacet>> PerspectiveFacets,
    IReadOnlyList<string> Keywords,
    SemanticVector? Vector = null);

/// <summary>Directed relation. Its type is host vocabulary, not an execution instruction.</summary>
public sealed record SemanticRelation(
    string RelationId,
    string FromNodeId,
    string ToNodeId,
    string Kind,
    SemanticClaimStatus Status,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

/// <summary>Read-only semantic link to a host workflow, task, step, or other opaque target.</summary>
public sealed record SemanticBinding(
    string BindingId,
    string NodeId,
    string TargetKind,
    string TargetId,
    string TargetDomainId,
    string? PerspectiveId,
    SemanticClaimStatus Status,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

/// <summary>Host-supplied embedding. No provider call or embedding generation occurs here.</summary>
public sealed record SemanticVector(string SpaceId, IReadOnlyList<float> Values);

public sealed record SemanticProjectionQuery(
    IReadOnlyList<string> AllowedDomainIds,
    string PerspectiveId,
    IReadOnlyList<string> SeedNodeIds,
    string? SearchText = null,
    SemanticVector? SearchVector = null,
    int MaxSeeds = 4,
    int MaxDepth = 2,
    int MaxNodes = 24,
    int MaxRelations = 48,
    IReadOnlyList<string>? FacetKeys = null);

public sealed record ProjectedConceptNode(
    string NodeId,
    string DomainId,
    string Kind,
    int Revision,
    int Depth,
    double MatchScore,
    double TextScore,
    double VectorScore,
    IReadOnlyDictionary<string, SemanticFacet> Facets,
    IReadOnlyDictionary<string, string> FacetSources);

public sealed record SemanticProjection(
    string ProjectionHash,
    string ScopedSourceHash,
    string PerspectiveId,
    IReadOnlyList<string> PerspectiveChain,
    IReadOnlyList<ProjectedConceptNode> Nodes,
    IReadOnlyList<SemanticRelation> Relations,
    IReadOnlyList<SemanticBinding> Bindings,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

/// <summary>Model output may propose this; only an explicit host authority can apply it.</summary>
public sealed record ConceptAdjustmentProposal(
    string ProposalId,
    string CatalogHash,
    string NodeId,
    int ExpectedRevision,
    string? PerspectiveId,
    IReadOnlyDictionary<string, SemanticFacet?> FacetChanges,
    string Reason,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record ConceptAdjustmentAuthorization(string DecisionId, string IssuerId);

public interface IConceptAdjustmentAuthority
{
    ConceptAdjustmentAuthorization? Authorize(
        ConceptAdjustmentProposal proposal,
        SemanticConceptNode currentNode);
}

public sealed record ConceptAdjustmentReceipt(
    string ProposalId,
    string DecisionId,
    string IssuerId,
    string NodeId,
    int PreviousRevision,
    int NewRevision,
    string PreviousCatalogHash,
    string NewCatalogHash,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record ConceptAdjustmentResult(
    SemanticProjectionCatalog Catalog,
    ConceptAdjustmentReceipt Receipt);
