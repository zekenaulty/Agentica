using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Planning;

public sealed record PlanningRequest(
    RunRequest Request,
    IReadOnlyList<ToolDescriptor> ToolDescriptors,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Receipt> Receipts);
