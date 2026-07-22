namespace Agentica.Tools;

public sealed record ToolRegistration(
    ToolDescriptor Descriptor,
    ITool Tool,
    ToolSecurityDeclaration Security);
