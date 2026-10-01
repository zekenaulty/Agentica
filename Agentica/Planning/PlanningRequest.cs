using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Requests;
using Agentica.Tools;
using System.Text.Json.Serialization;

namespace Agentica.Planning;

public sealed record PlanningRequest(
    RunRequest Request,
    IReadOnlyList<ToolDescriptor> ToolDescriptors,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Receipt> Receipts)
{
    [JsonIgnore]
    public PlanningSessionContext? SessionContext { get; init; }

    public PlanningExecutionContext ExecutionContext { get; init; } = PlanningExecutionContext.Empty;

    public ToolSurfaceSnapshot? ToolSurface { get; init; }

    public IReadOnlyList<PlanningFrame> ContextFrames { get; init; } = [];
}
