using Agentica.Tools;

namespace Agentica.Planning;

public sealed record PlanStep(
    string StepId,
    string ToolId,
    ToolKind Kind,
    ToolEffect Effect,
    IReadOnlyDictionary<string, object?> Input);
