using Agentica.Events;
using Agentica.Tools;

namespace Agentica.Planning;

public sealed record PlanStep(
    string StepId,
    string ToolId,
    ToolKind Kind,
    ToolEffect Effect,
    IReadOnlyDictionary<string, object?> Input)
{
    public string? Reason { get; init; }

    public ExecutionIntent? Intent { get; init; }

    public IReadOnlyList<string> DependsOn { get; init; } = [];

    public string? BatchId { get; init; }
}
