using Agentica.Tools;

namespace Agentica.Mcp;

/// <summary>Trusted host policy for one remote MCP tool. Discovery never supplies these grants.</summary>
public sealed record McpToolBinding(
    string ServerId,
    string RemoteName,
    string ExpectedInputSchemaSha256,
    ToolDescriptor Descriptor,
    ToolSecurityDeclaration Security,
    int MaxResultCharacters = 65_536);

public enum McpBindingIntegrity
{
    Unknown,
    Matched,
    Mismatch
}

/// <summary>An as-of dependency observation. Binding remains host-owned standing authority;
/// readiness neither grants authority nor replaces dispatch-time validation.</summary>
public sealed record McpBindingStatus(
    McpToolBinding Binding,
    bool Applicable,
    bool RuntimeReady,
    McpBindingIntegrity Integrity,
    string ReasonCode)
{
    public bool Bound => true;
}

public sealed record McpBindingInspection(
    IReadOnlyList<McpBindingStatus> Statuses,
    IReadOnlyList<ToolRegistration> ReadyRegistrations);

public sealed record McpDiscoveredTool(
    string Name,
    string InputSchemaJson);

public sealed record McpCallResult(
    bool IsError,
    IReadOnlyList<string> TextBlocks,
    string? StructuredContentJson = null);

public interface IMcpToolTransport
{
    string ServerId { get; }

    Task<IReadOnlyList<McpDiscoveredTool>> ListToolsAsync(CancellationToken cancellationToken);

    Task<McpCallResult> CallToolAsync(
        string remoteName,
        IReadOnlyDictionary<string, object?> input,
        CancellationToken cancellationToken);
}
