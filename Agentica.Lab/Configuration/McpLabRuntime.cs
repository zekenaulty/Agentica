using Agentica.Mcp;
using Agentica.Tools;

namespace Agentica.Lab.Configuration;

internal sealed class McpLabRuntime : IAsyncDisposable
{
    private readonly SdkMcpToolTransport _transport;

    private McpLabRuntime(SdkMcpToolTransport transport,
        IReadOnlyList<ToolRegistration> registrations)
    {
        _transport = transport;
        Registrations = registrations;
    }

    public IReadOnlyList<ToolRegistration> Registrations { get; }

    public static async Task<McpLabRuntime?> OpenFromEnvironmentAsync(
        CancellationToken cancellationToken)
    {
        var endpointText = Environment.GetEnvironmentVariable("AGENTICA_MCP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(endpointText))
        {
            return null;
        }
        var serverId = Required("AGENTICA_MCP_SERVER_ID");
        var remoteName = Required("AGENTICA_MCP_TOOL_NAME");
        var expectedHash = Required("AGENTICA_MCP_TOOL_SHA256");
        var toolId = Required("AGENTICA_MCP_TOOL_ID");
        var displayName = Required("AGENTICA_MCP_TOOL_DISPLAY_NAME");
        var description = Required("AGENTICA_MCP_TOOL_DESCRIPTION");
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
        {
            throw new InvalidOperationException("AGENTICA_MCP_ENDPOINT must be an absolute URI.");
        }

        var transport = await SdkMcpToolTransport.ConnectHttpAsync(
            serverId, endpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            var discovered = await transport.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            var matches = discovered.Where(tool =>
                string.Equals(tool.Name, remoteName, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException("Configured MCP tool is missing or duplicated.");
            }
            var binding = McpReadOnlyBindingFactory.Create(serverId, remoteName,
                expectedHash, toolId, displayName, description, matches[0].InputSchemaJson);
            var registrations = await McpToolCatalogAdapter.BindAsync(transport,
                [binding], cancellationToken).ConfigureAwait(false);
            return new McpLabRuntime(transport, registrations);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{name} is required when MCP is enabled.");
}
