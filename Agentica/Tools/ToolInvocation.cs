namespace Agentica.Tools;

/// <summary>The host scope identifier is a lookup reference for the bound tool.
/// The tool must resolve and validate its authority context before effects.</summary>
public sealed record ToolInvocation(
    string RunId,
    string StepId,
    string ToolId,
    IReadOnlyDictionary<string, object?> Input,
    string? AuthorizationScopeId = null);
