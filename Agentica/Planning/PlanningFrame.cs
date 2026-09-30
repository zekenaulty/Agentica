using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Requests;
using Agentica.Tools;
using System.Text.Json.Serialization;

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

    /// <summary>Host-authored reduced projection for a tight provider input budget.
    /// It is kept outside ordinary frame serialization until selected.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, object?>? CompactPayload { get; init; }
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
