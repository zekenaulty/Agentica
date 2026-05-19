using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Planning;

public sealed record PlanningFrame(
    string FrameId,
    string Kind,
    string Version,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, object?> Payload,
    IReadOnlyList<EvidenceRef> EvidenceRefs)
{
    public string? ToolSurfaceId { get; init; }
}

public sealed record PlanningFrameProjectionRequest(
    string RunId,
    int AttemptNumber,
    RunRequest Request,
    PlanningExecutionContext ExecutionContext,
    IReadOnlyList<ToolDescriptor> ToolDescriptors,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Receipt> Receipts,
    ToolSurfaceSnapshot? ToolSurface);

public interface IPlanningFrameProjector
{
    IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request);
}
