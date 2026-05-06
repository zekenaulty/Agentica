namespace Agentica.Tools;

public sealed record ToolInvocation(
    string RunId,
    string StepId,
    string ToolId,
    IReadOnlyDictionary<string, object?> Input);
