using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Planning;

public sealed record PlanningRequest(
    RunRequest Request,
    IReadOnlyList<ToolDescriptor> ToolDescriptors,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Receipt> Receipts)
{
    public PlanningExecutionContext ExecutionContext { get; init; } = PlanningExecutionContext.Empty;

    public ToolSurfaceSnapshot? ToolSurface { get; init; }

    public IReadOnlyList<PlanningFrame> ContextFrames { get; init; } = [];
}
