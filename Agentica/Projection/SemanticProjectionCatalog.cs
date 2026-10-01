using Agentica.Observations;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agentica.Projection;

/// <summary>
/// Immutable, bounded host snapshot. It owns semantic descriptions, not domain truth,
/// tool grants, or persistent memory. A new snapshot is produced for each approved edit.
/// </summary>
public sealed class SemanticProjectionCatalog
{
    private const int MaxNodes = 1024;
    private const int MaxRelations = 4096;
    private const int MaxBindings = 4096;
    private const int MaxPerspectives = 128;
    private readonly IReadOnlyDictionary<string, SemanticConceptNode> _nodesById;
    private readonly IReadOnlyDictionary<string, DomainPerspective> _perspectivesById;

    public SemanticProjectionCatalog(
        IReadOnlyList<SemanticConceptNode> nodes,
        IReadOnlyList<SemanticRelation> relations,
        IReadOnlyList<DomainPerspective> perspectives,
        IReadOnlyList<SemanticBinding>? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(perspectives);
        bindings ??= [];
        if (nodes.Count > MaxNodes || relations.Count > MaxRelations ||
            bindings.Count > MaxBindings || perspectives.Count > MaxPerspectives)
        {
            throw new ArgumentException("Semantic catalog exceeds its structural limits.");
        }

        var nodeCopies = nodes.Select(SnapshotNode)
            .OrderBy(node => node.NodeId, StringComparer.Ordinal).ToArray();
        var relationCopies = relations.Select(SnapshotRelation)
            .OrderBy(relation => relation.RelationId, StringComparer.Ordinal).ToArray();
        var bindingCopies = bindings.Select(SnapshotBinding)
            .OrderBy(binding => binding.BindingId, StringComparer.Ordinal).ToArray();
        var perspectiveCopies = perspectives.Select(SnapshotPerspective)
            .OrderBy(perspective => perspective.PerspectiveId, StringComparer.Ordinal).ToArray();

        _nodesById = Index(nodeCopies, node => node.NodeId, "node");
        _perspectivesById = Index(perspectiveCopies, perspective => perspective.PerspectiveId, "perspective");
        _ = Index(relationCopies, relation => relation.RelationId, "relation");
        _ = Index(bindingCopies, binding => binding.BindingId, "binding");

        foreach (var relation in relationCopies)
        {
            if (!_nodesById.ContainsKey(relation.FromNodeId) || !_nodesById.ContainsKey(relation.ToNodeId))
            {
                throw new ArgumentException($"Relation '{relation.RelationId}' refers to an unknown node.");
            }
        }

        foreach (var perspective in perspectiveCopies)
        {
            _ = PerspectiveChain(perspective.PerspectiveId);
        }

        foreach (var node in nodeCopies)
        {
            foreach (var perspectiveId in node.PerspectiveFacets.Keys)
            {
                if (!_perspectivesById.ContainsKey(perspectiveId))
                {
                    throw new ArgumentException($"Node '{node.NodeId}' has an unknown perspective overlay.");
                }
            }
        }

        foreach (var binding in bindingCopies)
        {
            if (!_nodesById.ContainsKey(binding.NodeId) ||
                (binding.PerspectiveId is not null && !_perspectivesById.ContainsKey(binding.PerspectiveId)))
            {
                throw new ArgumentException($"Binding '{binding.BindingId}' has an unknown node or perspective.");
            }
        }

        var dimensions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var vector in nodeCopies.Select(node => node.Vector).Where(vector => vector is not null))
        {
            if (dimensions.TryGetValue(vector!.SpaceId, out var dimension) && dimension != vector.Values.Count)
            {
                throw new ArgumentException($"Vector space '{vector.SpaceId}' has inconsistent dimensions.");
            }

            dimensions[vector!.SpaceId] = vector.Values.Count;
        }

        Nodes = Array.AsReadOnly(nodeCopies);
        Relations = Array.AsReadOnly(relationCopies);
        Bindings = Array.AsReadOnly(bindingCopies);
        Perspectives = Array.AsReadOnly(perspectiveCopies);
        CatalogHash = Hash(new
        {
            nodes = Nodes.Select(node => new
            {
                node.NodeId,
                node.DomainId,
                node.Kind,
                node.Revision,
                facets = CanonicalFacets(node.BaseFacets),
                overlays = node.PerspectiveFacets.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { perspectiveId = pair.Key, facets = CanonicalFacets(pair.Value) }),
                keywords = node.Keywords,
                vector = node.Vector
            }),
            relations = Relations,
            bindings = Bindings,
            perspectives = Perspectives
        });
    }

    public IReadOnlyList<SemanticConceptNode> Nodes { get; }
    public IReadOnlyList<SemanticRelation> Relations { get; }
    public IReadOnlyList<SemanticBinding> Bindings { get; }
    public IReadOnlyList<DomainPerspective> Perspectives { get; }
    public string CatalogHash { get; }

    public bool TryGetNode(string nodeId, out SemanticConceptNode node) =>
        _nodesById.TryGetValue(nodeId, out node!);

    public IReadOnlyList<DomainPerspective> PerspectiveChain(string perspectiveId)
    {
        var result = new List<DomainPerspective>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = perspectiveId;
        while (current is not null)
        {
            if (!seen.Add(current))
            {
                throw new ArgumentException($"Perspective inheritance has a cycle at '{current}'.");
            }

            if (!_perspectivesById.TryGetValue(current, out var perspective))
            {
                throw new ArgumentException($"Perspective '{current}' is unknown.");
            }

            result.Add(perspective);
            current = perspective.ParentPerspectiveId;
        }

        result.Reverse();
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>
    /// Applies an expected-version concept edit only after a host-owned authority approves it.
    /// The proposal itself and any semantic similarity score grant no authority.
    /// </summary>
    public ConceptAdjustmentResult ApplyAdjustment(
        ConceptAdjustmentProposal proposal,
        IConceptAdjustmentAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(authority);
        Identifier(proposal.ProposalId, "proposal id");
        if (!string.Equals(proposal.CatalogHash, CatalogHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Concept adjustment targets a stale catalog.");
        }

        if (!TryGetNode(proposal.NodeId, out var current) || current.Revision != proposal.ExpectedRevision)
        {
            throw new InvalidOperationException("Concept adjustment targets a missing or stale node.");
        }

        if (proposal.PerspectiveId is not null && !_perspectivesById.ContainsKey(proposal.PerspectiveId))
        {
            throw new ArgumentException("Concept adjustment names an unknown perspective.", nameof(proposal));
        }

        Text(proposal.Reason, "adjustment reason", 1024);
        if (proposal.FacetChanges is null || proposal.FacetChanges.Count is 0 or > 16)
        {
            throw new ArgumentException("Concept adjustment must contain 1 to 16 facet changes.", nameof(proposal));
        }

        if (proposal.EvidenceRefs is null || proposal.EvidenceRefs.Count == 0)
        {
            throw new ArgumentException("Concept adjustment requires a source reference.", nameof(proposal));
        }

        var frozenEvidence = SnapshotEvidence(proposal.EvidenceRefs);
        var frozenChanges = new Dictionary<string, SemanticFacet?>(StringComparer.Ordinal);
        foreach (var change in proposal.FacetChanges)
        {
            Identifier(change.Key, "facet key");
            frozenChanges.Add(change.Key, change.Value is null ? null : SnapshotFacet(change.Value));
        }

        var frozenProposal = proposal with
        {
            FacetChanges = new ReadOnlyDictionary<string, SemanticFacet?>(frozenChanges),
            EvidenceRefs = frozenEvidence
        };

        var authorization = authority.Authorize(frozenProposal, current);
        if (authorization is null)
        {
            throw new UnauthorizedAccessException("The host denied the concept adjustment.");
        }

        Identifier(authorization.DecisionId, "decision id");
        Identifier(authorization.IssuerId, "issuer id");
        var baseFacets = new Dictionary<string, SemanticFacet>(current.BaseFacets, StringComparer.Ordinal);
        var overlays = current.PerspectiveFacets.ToDictionary(
            pair => pair.Key,
            pair => new Dictionary<string, SemanticFacet>(pair.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var target = frozenProposal.PerspectiveId is null
            ? baseFacets
            : overlays.GetValueOrDefault(frozenProposal.PerspectiveId) ??
              (overlays[frozenProposal.PerspectiveId] = new Dictionary<string, SemanticFacet>(StringComparer.Ordinal));

        var changed = false;
        foreach (var change in frozenProposal.FacetChanges)
        {
            if (change.Value is null)
            {
                changed |= target.Remove(change.Key);
            }
            else
            {
                var nextFacet = SnapshotFacet(change.Value);
                if (!target.TryGetValue(change.Key, out var previousFacet) ||
                    !SameFacet(previousFacet, nextFacet))
                {
                    target[change.Key] = nextFacet;
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            throw new InvalidOperationException("Concept adjustment did not change any facet.");
        }

        var updated = current with
        {
            Revision = checked(current.Revision + 1),
            BaseFacets = baseFacets,
            PerspectiveFacets = overlays.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyDictionary<string, SemanticFacet>)pair.Value,
                StringComparer.Ordinal)
        };
        var next = new SemanticProjectionCatalog(
            Nodes.Select(node => node.NodeId == current.NodeId ? updated : node).ToArray(),
            Relations,
            Perspectives,
            Bindings);
        var receipt = new ConceptAdjustmentReceipt(
            frozenProposal.ProposalId,
            authorization.DecisionId,
            authorization.IssuerId,
            current.NodeId,
            current.Revision,
            updated.Revision,
            CatalogHash,
            next.CatalogHash,
            frozenEvidence);
        return new ConceptAdjustmentResult(next, receipt);
    }

    internal static string Hash(object value) =>
        "sha256-v1:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    private static bool SameFacet(SemanticFacet left, SemanticFacet right) =>
        left.Text == right.Text && left.Status == right.Status &&
        left.EvidenceRefs.SequenceEqual(right.EvidenceRefs);

    private static object CanonicalFacets(IReadOnlyDictionary<string, SemanticFacet> facets) =>
        facets.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new { key = pair.Key, pair.Value.Text, pair.Value.Status, pair.Value.EvidenceRefs });

    private static SemanticConceptNode SnapshotNode(SemanticConceptNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Identifier(node.NodeId, "node id");
        Identifier(node.DomainId, "domain id");
        Identifier(node.Kind, "node kind");
        if (node.Revision < 1 || node.BaseFacets is null || node.PerspectiveFacets is null || node.Keywords is null ||
            node.BaseFacets.Count > 16 || node.PerspectiveFacets.Count > 16 || node.Keywords.Count > 32)
        {
            throw new ArgumentException($"Node '{node.NodeId}' has invalid revision or exceeds its limits.");
        }

        var overlays = node.PerspectiveFacets.ToDictionary(
            pair =>
            {
                Identifier(pair.Key, "perspective id");
                return pair.Key;
            },
            pair => SnapshotFacets(pair.Value),
            StringComparer.Ordinal);
        var keywords = node.Keywords.Select(keyword => Text(keyword, "keyword", 128))
            .Distinct(StringComparer.Ordinal).OrderBy(keyword => keyword, StringComparer.Ordinal).ToArray();
        return node with
        {
            BaseFacets = SnapshotFacets(node.BaseFacets),
            PerspectiveFacets = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, SemanticFacet>>(overlays),
            Keywords = Array.AsReadOnly(keywords),
            Vector = node.Vector is null ? null : SnapshotVector(node.Vector)
        };
    }

    internal static SemanticVector SnapshotVector(SemanticVector vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        Identifier(vector.SpaceId, "vector space id");
        if (vector.Values is null || vector.Values.Count is 0 or > 2048 ||
            vector.Values.Any(value => !float.IsFinite(value)))
        {
            throw new ArgumentException("Semantic vector must contain 1 to 2048 finite values.");
        }

        return new SemanticVector(vector.SpaceId, Array.AsReadOnly(vector.Values.ToArray()));
    }

    private static IReadOnlyDictionary<string, SemanticFacet> SnapshotFacets(
        IReadOnlyDictionary<string, SemanticFacet> facets)
    {
        if (facets is null || facets.Count > 16)
        {
            throw new ArgumentException("A concept interpretation exceeds 16 facets.");
        }

        var copy = new Dictionary<string, SemanticFacet>(StringComparer.Ordinal);
        foreach (var pair in facets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Identifier(pair.Key, "facet key");
            copy.Add(pair.Key, SnapshotFacet(pair.Value));
        }

        return new ReadOnlyDictionary<string, SemanticFacet>(copy);
    }

    private static SemanticFacet SnapshotFacet(SemanticFacet facet)
    {
        ArgumentNullException.ThrowIfNull(facet);
        Text(facet.Text, "facet text", 2048);
        if (!Enum.IsDefined(facet.Status))
        {
            throw new ArgumentException("Facet has an invalid claim status.");
        }

        if (facet.EvidenceRefs is null ||
            (facet.Status == SemanticClaimStatus.Observed && facet.EvidenceRefs.Count == 0))
        {
            throw new ArgumentException("Observed facet requires a source reference.");
        }

        return facet with { EvidenceRefs = SnapshotEvidence(facet.EvidenceRefs) };
    }

    private static SemanticRelation SnapshotRelation(SemanticRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        Identifier(relation.RelationId, "relation id");
        Identifier(relation.FromNodeId, "relation source");
        Identifier(relation.ToNodeId, "relation target");
        Identifier(relation.Kind, "relation kind");
        if (!Enum.IsDefined(relation.Status))
        {
            throw new ArgumentException("Relation has an invalid claim status.");
        }

        if (relation.EvidenceRefs is null ||
            (relation.Status == SemanticClaimStatus.Observed && relation.EvidenceRefs.Count == 0))
        {
            throw new ArgumentException("Observed relation requires a source reference.");
        }

        return relation with { EvidenceRefs = SnapshotEvidence(relation.EvidenceRefs) };
    }

    private static SemanticBinding SnapshotBinding(SemanticBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Identifier(binding.BindingId, "binding id");
        Identifier(binding.NodeId, "binding node id");
        Identifier(binding.TargetKind, "binding target kind");
        Identifier(binding.TargetId, "binding target id");
        Identifier(binding.TargetDomainId, "binding target domain id");
        if (binding.PerspectiveId is not null)
        {
            Identifier(binding.PerspectiveId, "binding perspective id");
        }

        if (!Enum.IsDefined(binding.Status))
        {
            throw new ArgumentException("Binding has an invalid claim status.");
        }

        if (binding.EvidenceRefs is null ||
            (binding.Status == SemanticClaimStatus.Observed && binding.EvidenceRefs.Count == 0))
        {
            throw new ArgumentException("Observed binding requires a source reference.");
        }

        return binding with { EvidenceRefs = SnapshotEvidence(binding.EvidenceRefs) };
    }

    private static DomainPerspective SnapshotPerspective(DomainPerspective perspective)
    {
        ArgumentNullException.ThrowIfNull(perspective);
        Identifier(perspective.PerspectiveId, "perspective id");
        Identifier(perspective.DomainId, "perspective domain id");
        if (perspective.ParentPerspectiveId is not null)
        {
            Identifier(perspective.ParentPerspectiveId, "parent perspective id");
        }

        return perspective;
    }

    private static IReadOnlyList<EvidenceRef> SnapshotEvidence(IReadOnlyList<EvidenceRef> evidence)
    {
        if (evidence is null || evidence.Count > 16)
        {
            throw new ArgumentException("An evidence list exceeds 16 references.");
        }

        var copy = evidence.Select(item =>
        {
            ArgumentNullException.ThrowIfNull(item);
            Identifier(item.Kind, "evidence kind");
            Identifier(item.RefId, "evidence id");
            return item with { };
        }).Distinct().OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.RefId, StringComparer.Ordinal).ToArray();
        return Array.AsReadOnly(copy);
    }

    private static IReadOnlyDictionary<string, T> Index<T>(
        IReadOnlyList<T> values,
        Func<T, string> key,
        string kind)
    {
        var copy = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!copy.TryAdd(key(value), value))
            {
                throw new ArgumentException($"Duplicate {kind} id '{key(value)}'.");
            }
        }

        return new ReadOnlyDictionary<string, T>(copy);
    }

    internal static string Identifier(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException($"Invalid {description}.");
        }

        return value;
    }

    private static string Text(string value, string description, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentException($"Invalid {description}.");
        }

        return value;
    }
}
