namespace Agentica.Tools;

public sealed record ToolDescriptor(
    string ToolId,
    string Name,
    ToolKind Kind,
    ToolEffect Effect,
    bool RequiresApproval = false,
    ToolInputSchema? InputSchema = null,
    string? Description = null);
