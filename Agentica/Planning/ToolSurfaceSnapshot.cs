using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.Planning;

public sealed record ToolSurfaceSnapshot(
    string SurfaceId,
    string ManifestHash,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ToolDescriptor> ToolDescriptors,
    PlanningExecutionContext ExecutionContext,
    IReadOnlyList<EvidenceRef> ObservationRefs,
    IReadOnlyList<EvidenceRef> ReceiptRefs,
    IReadOnlyDictionary<string, object?> PolicySummary);
