using Agentica.Planning;
using System.Text.Json;

namespace Agentica.Projection;

/// <summary>
/// Opt-in bridge from a host-owned semantic catalog into Agentica's planning context.
/// The host query factory must derive admitted domains from authoritative policy.
/// </summary>
public sealed class SemanticPlanningFrameProjector : IPlanningFrameProjector
{
    private readonly SemanticProjectionCompiler _compiler;
    private readonly Func<PlanningFrameProjectionRequest, SemanticProjectionQuery> _hostQueryFactory;
    private readonly int _maxPayloadBytes;

    public SemanticPlanningFrameProjector(
        SemanticProjectionCatalog catalog,
        Func<PlanningFrameProjectionRequest, SemanticProjectionQuery> hostQueryFactory,
        int maxPayloadBytes = 256 * 1024)
    {
        _compiler = new SemanticProjectionCompiler(catalog);
        _hostQueryFactory = hostQueryFactory ?? throw new ArgumentNullException(nameof(hostQueryFactory));
        if (maxPayloadBytes is < 1024 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        }

        _maxPayloadBytes = maxPayloadBytes;
    }

    public IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = _hostQueryFactory(request) ??
                    throw new InvalidOperationException("The host did not provide a semantic projection query.");
        var projection = _compiler.Compile(query);
        var payload = Payload(projection);
        if (JsonSerializer.SerializeToUtf8Bytes(payload).Length > _maxPayloadBytes)
        {
            throw new InvalidOperationException("Semantic planning frame exceeds the host payload budget.");
        }

        return
        [
            new PlanningFrame(
                projection.ProjectionHash,
                "semantic-projection",
                "1",
                DateTimeOffset.UtcNow,
                payload,
                projection.EvidenceRefs)
            {
                ToolSurfaceId = request.ToolSurface?.SurfaceId
            }
        ];
    }

    private static IReadOnlyDictionary<string, object?> Payload(SemanticProjection projection) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["projectionHash"] = projection.ProjectionHash,
            ["scopedSourceHash"] = projection.ScopedSourceHash,
            ["perspectiveId"] = projection.PerspectiveId,
            ["perspectiveChain"] = projection.PerspectiveChain,
            ["nodes"] = projection.Nodes.Select(node =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["nodeId"] = node.NodeId,
                    ["domainId"] = node.DomainId,
                    ["kind"] = node.Kind,
                    ["revision"] = node.Revision,
                    ["depth"] = node.Depth,
                    ["matchScore"] = node.MatchScore,
                    ["textScore"] = node.TextScore,
                    ["vectorScore"] = node.VectorScore,
                    ["facets"] = node.Facets.ToDictionary(
                        pair => pair.Key,
                        pair => (object?)new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["text"] = pair.Value.Text,
                            ["status"] = pair.Value.Status.ToString(),
                            ["evidence"] = pair.Value.EvidenceRefs.Select(reference =>
                                new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["kind"] = reference.Kind,
                                    ["refId"] = reference.RefId
                                }).ToArray(),
                            ["sourcePerspective"] = node.FacetSources[pair.Key]
                        },
                        StringComparer.Ordinal)
                }).ToArray(),
            ["relations"] = projection.Relations.Select(relation =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["relationId"] = relation.RelationId,
                    ["fromNodeId"] = relation.FromNodeId,
                    ["toNodeId"] = relation.ToNodeId,
                    ["kind"] = relation.Kind,
                    ["status"] = relation.Status.ToString(),
                    ["evidence"] = relation.EvidenceRefs.Select(reference =>
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["kind"] = reference.Kind,
                            ["refId"] = reference.RefId
                        }).ToArray()
                }).ToArray(),
            ["bindings"] = projection.Bindings.Select(binding =>
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["bindingId"] = binding.BindingId,
                    ["nodeId"] = binding.NodeId,
                    ["targetKind"] = binding.TargetKind,
                    ["targetId"] = binding.TargetId,
                    ["targetDomainId"] = binding.TargetDomainId,
                    ["perspectiveId"] = binding.PerspectiveId,
                    ["status"] = binding.Status.ToString(),
                    ["evidence"] = binding.EvidenceRefs.Select(reference =>
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["kind"] = reference.Kind,
                            ["refId"] = reference.RefId
                        }).ToArray()
                }).ToArray()
        };
}
