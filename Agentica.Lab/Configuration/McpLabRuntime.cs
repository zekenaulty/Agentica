using Agentica.Mcp;
using Agentica.Tools;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        var bindingsFile = Environment.GetEnvironmentVariable("AGENTICA_MCP_BINDINGS_FILE");
        if (!string.IsNullOrWhiteSpace(bindingsFile))
        {
            if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("AGENTICA_MCP_ENDPOINT")))
            {
                throw new InvalidOperationException(
                    "Choose either AGENTICA_MCP_BINDINGS_FILE or the single-tool MCP settings.");
            }
            return await OpenManifestAsync(bindingsFile, cancellationToken).ConfigureAwait(false);
        }
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
            serverId, endpoint, cancellationToken,
            Environment.GetEnvironmentVariable("AGENTICA_MCP_BEARER_TOKEN"))
            .ConfigureAwait(false);
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

    private static async Task<McpLabRuntime> OpenManifestAsync(
        string path, CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > 1_048_576)
            throw new InvalidOperationException("MCP bindings file is missing or too large.");
        var json = await File.ReadAllTextAsync(file.FullName, cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<McpBindingsManifest>(json,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            }) ?? throw new InvalidOperationException("MCP bindings file is empty.");
        if (string.IsNullOrWhiteSpace(manifest.ServerId) ||
            !Uri.TryCreate(manifest.Endpoint, UriKind.Absolute, out var endpoint) ||
            manifest.Tools is not { Count: > 0 and <= 64 })
        {
            throw new InvalidOperationException("MCP bindings manifest is incomplete.");
        }

        var transport = await SdkMcpToolTransport.ConnectHttpAsync(
            manifest.ServerId, endpoint, cancellationToken,
            Environment.GetEnvironmentVariable("AGENTICA_MCP_BEARER_TOKEN"))
            .ConfigureAwait(false);
        try
        {
            var discovered = await transport.ListToolsAsync(cancellationToken)
                .ConfigureAwait(false);
            var bindings = manifest.Tools.Select(entry =>
            {
                if (string.IsNullOrWhiteSpace(entry.RemoteName) ||
                    string.IsNullOrWhiteSpace(entry.SchemaSha256) ||
                    string.IsNullOrWhiteSpace(entry.ToolId) ||
                    string.IsNullOrWhiteSpace(entry.DisplayName) ||
                    string.IsNullOrWhiteSpace(entry.Description) ||
                    entry.Reads is null || entry.ExposesToPlanner is null)
                {
                    throw new InvalidOperationException("MCP tool binding is incomplete.");
                }
                var matches = discovered.Where(tool =>
                    string.Equals(tool.Name, entry.RemoteName, StringComparison.Ordinal))
                    .ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException("Configured MCP tool is missing or duplicated.");
                return McpTrustedBindingFactory.Create(manifest.ServerId,
                    entry.RemoteName, entry.SchemaSha256, entry.ToolId,
                    entry.DisplayName, entry.Description, matches[0].InputSchemaJson,
                    Parse<ToolKind>(entry.Kind), Parse<ToolEffect>(entry.Effect),
                    entry.Reads.Select(Parse<ToolDataBoundary>).ToArray(),
                    entry.ExposesToPlanner.Select(Parse<ToolDataBoundary>).ToArray(),
                    Parse<ToolExternalOutputClassification>(entry.ExternalOutput),
                    Parse<ToolApprovalRequirement>(entry.ApprovalRequirement),
                    Parse<ToolRetrySafety>(entry.RetrySafety),
                    entry.MaxResultCharacters ?? 65_536);
            }).ToArray();
            var registrations = await McpToolCatalogAdapter.BindAsync(transport,
                bindings, cancellationToken).ConfigureAwait(false);
            return new McpLabRuntime(transport, registrations);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static T Parse<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var result) &&
        Enum.IsDefined(result)
            ? result
            : throw new InvalidOperationException(
                $"MCP binding has an invalid {typeof(T).Name} value.");

    private sealed record McpBindingsManifest(
        string? ServerId,
        string? Endpoint,
        IReadOnlyList<McpToolEntry>? Tools);

    private sealed record McpToolEntry(
        string? RemoteName,
        string? SchemaSha256,
        string? ToolId,
        string? DisplayName,
        string? Description,
        string? Kind,
        string? Effect,
        IReadOnlyList<string>? Reads,
        IReadOnlyList<string>? ExposesToPlanner,
        string? ExternalOutput,
        string? ApprovalRequirement,
        string? RetrySafety,
        int? MaxResultCharacters);

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{name} is required when MCP is enabled.");
}
