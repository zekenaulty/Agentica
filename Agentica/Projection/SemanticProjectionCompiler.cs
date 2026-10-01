using Agentica.Observations;
using System.Collections.ObjectModel;
using System.Text;

namespace Agentica.Projection;

/// <summary>
/// Deterministic, recursive read projection over host-admitted semantic data.
/// Search and perspective selection never authorize data or actions.
/// </summary>
public sealed class SemanticProjectionCompiler
{
    private readonly SemanticProjectionCatalog _catalog;

    public SemanticProjectionCompiler(SemanticProjectionCatalog catalog) =>
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public SemanticProjection Compile(SemanticProjectionQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.AllowedDomainIds is null || query.AllowedDomainIds.Count is 0 or > 32 ||
            query.SeedNodeIds is null || query.SeedNodeIds.Count > 64 ||
            query.FacetKeys?.Count > 16 ||
            query.MaxSeeds is < 1 or > 16 || query.MaxDepth is < 0 or > 8 ||
            query.MaxNodes is < 1 or > 64 || query.MaxRelations is < 0 or > 128 ||
            query.SearchText?.Length > 512)
        {
            throw new ArgumentException("Semantic projection query exceeds its limits.", nameof(query));
        }

        var allowed = query.AllowedDomainIds
            .Select(domain => SemanticProjectionCatalog.Identifier(domain, "allowed domain id"))
            .ToHashSet(StringComparer.Ordinal);
        var facetKeys = query.FacetKeys?.Select(key =>
                SemanticProjectionCatalog.Identifier(key, "facet key"))
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<DomainPerspective> chain;
        try
        {
            chain = _catalog.PerspectiveChain(query.PerspectiveId);
        }
        catch (ArgumentException)
        {
            throw new UnauthorizedAccessException(
                "The requested perspective is unavailable in the admitted scope.");
        }

        if (chain.Any(perspective => !allowed.Contains(perspective.DomainId)))
        {
            throw new UnauthorizedAccessException(
                "The requested perspective is unavailable in the admitted scope.");
        }

        var scopedNodes = _catalog.Nodes.Where(node => allowed.Contains(node.DomainId)).ToArray();
        var searchVector = query.SearchVector is null
            ? null
            : SemanticProjectionCatalog.SnapshotVector(query.SearchVector);
        if (searchVector is not null &&
            !scopedNodes.Any(node => node.Vector is { } vector &&
                                     string.Equals(vector.SpaceId, searchVector.SpaceId, StringComparison.Ordinal) &&
                                     vector.Values.Count == searchVector.Values.Count))
        {
            throw new ArgumentException("Search vector space is unavailable in the admitted scope.", nameof(query));
        }

        var terms = Terms(query.SearchText);
        if (query.SeedNodeIds.Count == 0 && terms.Count == 0 && searchVector is null)
        {
            throw new ArgumentException("A semantic query needs seed nodes, search text, or a search vector.", nameof(query));
        }

        var scopedIndex = scopedNodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var scopedRelations = _catalog.Relations
            .Where(relation => scopedIndex.ContainsKey(relation.FromNodeId) &&
                               scopedIndex.ContainsKey(relation.ToNodeId))
            .ToArray();
        var chainIds = chain.Select(item => item.PerspectiveId).ToHashSet(StringComparer.Ordinal);
        var scopedBindings = _catalog.Bindings
            .Where(binding => scopedIndex.ContainsKey(binding.NodeId) &&
                              allowed.Contains(binding.TargetDomainId) &&
                              (binding.PerspectiveId is null || chainIds.Contains(binding.PerspectiveId)))
            .ToArray();
        var scored = scopedNodes.Select(node => Score(node, chain, facetKeys, terms, searchVector))
            .ToDictionary(item => item.Node.NodeId, StringComparer.Ordinal);

        var seedIds = query.SeedNodeIds.Distinct(StringComparer.Ordinal).ToList();
        foreach (var seedId in seedIds)
        {
            if (!scopedIndex.ContainsKey(seedId))
            {
                throw new ArgumentException("A seed node is unavailable in the admitted scope.", nameof(query));
            }
        }

        if (seedIds.Count > query.MaxSeeds || seedIds.Count > query.MaxNodes)
        {
            throw new ArgumentException("Seed count exceeds the query's node budget.", nameof(query));
        }

        if (terms.Count > 0 || searchVector is not null)
        {
            foreach (var candidate in scored.Values
                         .Where(candidate => candidate.MatchScore > 0)
                         .OrderByDescending(candidate => candidate.MatchScore)
                         .ThenBy(candidate => candidate.Node.NodeId, StringComparer.Ordinal))
            {
                if (seedIds.Count >= query.MaxSeeds || seedIds.Count >= query.MaxNodes)
                {
                    break;
                }

                if (!seedIds.Contains(candidate.Node.NodeId, StringComparer.Ordinal))
                {
                    seedIds.Add(candidate.Node.NodeId);
                }
            }
        }

        var selected = new Dictionary<string, int>(StringComparer.Ordinal);
        var selectedRelations = new Dictionary<string, SemanticRelation>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var seedId in seedIds)
        {
            selected.Add(seedId, 0);
            queue.Enqueue(seedId);
        }

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            var depth = selected[currentId];
            if (depth >= query.MaxDepth)
            {
                continue;
            }

            var neighbors = scopedRelations
                .Where(relation => relation.FromNodeId == currentId || relation.ToNodeId == currentId)
                .Select(relation => new
                {
                    Relation = relation,
                    NeighborId = relation.FromNodeId == currentId
                        ? relation.ToNodeId
                        : relation.FromNodeId
                })
                .OrderByDescending(item => scored[item.NeighborId].MatchScore)
                .ThenBy(item => item.Relation.RelationId, StringComparer.Ordinal)
                .ThenBy(item => item.NeighborId, StringComparer.Ordinal);

            foreach (var neighbor in neighbors)
            {
                var alreadySelected = selected.ContainsKey(neighbor.NeighborId);
                if (!alreadySelected && selected.Count >= query.MaxNodes)
                {
                    continue;
                }

                if (!selectedRelations.ContainsKey(neighbor.Relation.RelationId) &&
                    selectedRelations.Count >= query.MaxRelations)
                {
                    continue;
                }

                selectedRelations.TryAdd(neighbor.Relation.RelationId, neighbor.Relation);
                if (!alreadySelected)
                {
                    selected.Add(neighbor.NeighborId, depth + 1);
                    queue.Enqueue(neighbor.NeighborId);
                }
            }
        }

        var nodes = selected
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var item = scored[pair.Key];
                return new ProjectedConceptNode(
                    item.Node.NodeId,
                    item.Node.DomainId,
                    item.Node.Kind,
                    item.Node.Revision,
                    pair.Value,
                    seedIds.Contains(pair.Key, StringComparer.Ordinal) &&
                    terms.Count == 0 && searchVector is null ? 1 : item.MatchScore,
                    item.TextScore,
                    item.VectorScore,
                    item.Facets,
                    item.FacetSources);
            }).ToArray();
        var relations = selectedRelations.Values.OrderBy(item => item.RelationId, StringComparer.Ordinal).ToArray();
        var bindings = scopedBindings.Where(binding => selected.ContainsKey(binding.NodeId)).ToArray();
        var evidence = nodes.SelectMany(node => node.Facets.Values.SelectMany(facet => facet.EvidenceRefs))
            .Concat(relations.SelectMany(relation => relation.EvidenceRefs))
            .Concat(bindings.SelectMany(binding => binding.EvidenceRefs))
            .Distinct().OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.RefId, StringComparer.Ordinal)
            .ToArray();
        var scopedSourceHash = SemanticProjectionCatalog.Hash(new
        {
            nodes = scored.Values.OrderBy(item => item.Node.NodeId, StringComparer.Ordinal)
                .Select(item => new
                {
                    item.Node.NodeId,
                    item.Node.DomainId,
                    item.Node.Kind,
                    item.Node.Revision,
                    item.Node.Keywords,
                    item.Node.Vector,
                    item.Facets,
                    item.FacetSources
                }),
            relations = scopedRelations,
            bindings = scopedBindings,
            perspectives = chain
        });
        var projectionHash = SemanticProjectionCatalog.Hash(new
        {
            scopedSourceHash,
            perspectiveId = query.PerspectiveId,
            chain = chain.Select(item => item.PerspectiveId),
            nodes,
            relations,
            bindings,
            evidence
        });
        return new SemanticProjection(
            projectionHash,
            scopedSourceHash,
            query.PerspectiveId,
            Array.AsReadOnly(chain.Select(item => item.PerspectiveId).ToArray()),
            Array.AsReadOnly(nodes),
            Array.AsReadOnly(relations),
            Array.AsReadOnly(bindings),
            Array.AsReadOnly(evidence));
    }

    private static ScoredNode Score(
        SemanticConceptNode node,
        IReadOnlyList<DomainPerspective> chain,
        IReadOnlySet<string>? facetKeys,
        IReadOnlySet<string> terms,
        SemanticVector? searchVector)
    {
        var facets = new Dictionary<string, SemanticFacet>(node.BaseFacets, StringComparer.Ordinal);
        var sources = node.BaseFacets.Keys.ToDictionary(key => key, _ => "base", StringComparer.Ordinal);
        foreach (var perspective in chain)
        {
            if (!node.PerspectiveFacets.TryGetValue(perspective.PerspectiveId, out var overlay))
            {
                continue;
            }

            foreach (var pair in overlay)
            {
                facets[pair.Key] = pair.Value;
                sources[pair.Key] = perspective.PerspectiveId;
            }
        }

        if (facetKeys is not null)
        {
            foreach (var key in facets.Keys.Where(key => !facetKeys.Contains(key)).ToArray())
            {
                facets.Remove(key);
                sources.Remove(key);
            }
        }

        var searchable = string.Join(' ', node.NodeId, node.Kind,
            string.Join(' ', node.Keywords),
            string.Join(' ', facets.Values.Select(facet => facet.Text)));
        var nodeTerms = Terms(searchable);
        var textScore = terms.Count == 0 ? 0 :
            Math.Round((double)terms.Count(nodeTerms.Contains) / terms.Count, 6);
        var vectorScore = searchVector is not null && node.Vector is not null &&
            string.Equals(node.Vector.SpaceId, searchVector.SpaceId, StringComparison.Ordinal)
            ? Math.Round(Math.Max(0, Cosine(node.Vector.Values, searchVector.Values)), 6)
            : 0;
        var matchScore = terms.Count > 0 && searchVector is not null
            ? Math.Round((textScore + vectorScore) / 2, 6)
            : terms.Count > 0 ? textScore : vectorScore;
        return new ScoredNode(
            node,
            new ReadOnlyDictionary<string, SemanticFacet>(facets),
            new ReadOnlyDictionary<string, string>(sources),
            textScore,
            vectorScore,
            matchScore);
    }

    private static HashSet<string> Terms(string? value)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var token = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (token.Length < 64)
                {
                    token.Append(char.ToLowerInvariant(character));
                }
            }
            else if (token.Length > 0)
            {
                result.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            result.Add(token.ToString());
        }

        return result;
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var dot = 0d;
        var leftMagnitude = 0d;
        var rightMagnitude = 0d;
        for (var index = 0; index < left.Count; index++)
        {
            dot += (double)left[index] * right[index];
            leftMagnitude += (double)left[index] * left[index];
            rightMagnitude += (double)right[index] * right[index];
        }

        return leftMagnitude == 0 || rightMagnitude == 0
            ? 0
            : dot / Math.Sqrt(leftMagnitude * rightMagnitude);
    }

    private sealed record ScoredNode(
        SemanticConceptNode Node,
        IReadOnlyDictionary<string, SemanticFacet> Facets,
        IReadOnlyDictionary<string, string> FacetSources,
        double TextScore,
        double VectorScore,
        double MatchScore);
}
