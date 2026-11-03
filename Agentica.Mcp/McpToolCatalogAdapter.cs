using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;
using ModelContextProtocol;

namespace Agentica.Mcp;

/// <summary>Converts only locally approved MCP bindings into Agentica registrations.</summary>
public static class McpToolCatalogAdapter
{
    public const int MaximumInspectedBindings = 64;

    /// <summary>Projects configured bindings without discarding them when dependencies
    /// are unavailable. A null transport represents a host-observed connection failure.
    /// Applicability is supplied by the host; discovery never supplies a grant.</summary>
    public static async Task<McpBindingInspection> InspectAsync(
        IMcpToolTransport? transport,
        IReadOnlyList<McpToolBinding> bindings,
        IReadOnlySet<string>? applicableToolIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count > MaximumInspectedBindings)
            throw new ArgumentException("MCP inspection supports at most 64 trusted bindings.", nameof(bindings));
        cancellationToken.ThrowIfCancellationRequested();
        var configured = bindings.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in configured)
            if (binding is null || string.IsNullOrWhiteSpace(binding.ServerId) ||
                string.IsNullOrWhiteSpace(binding.RemoteName) || string.IsNullOrWhiteSpace(binding.ExpectedInputSchemaSha256) ||
                binding.Descriptor is null || binding.Security is null || binding.Security.Provenance is null ||
                string.IsNullOrWhiteSpace(binding.Descriptor.ToolId) || !ids.Add(binding.Descriptor.ToolId) ||
                binding.MaxResultCharacters is < 1 or > 262_144 || binding.Descriptor.Effect != binding.Security.Effect ||
                binding.Security.Provenance.Kind != ToolProvenanceKind.AdapterProvided)
                throw new ArgumentException("An MCP binding is incomplete or inconsistent.", nameof(bindings));

        var statuses = configured.Select(binding => new McpBindingStatus(binding,
            applicableToolIds?.Contains(binding.Descriptor.ToolId) ?? true, false,
            McpBindingIntegrity.Unknown, "mcp.not_applicable")).ToArray();
        var eligible = statuses.Select((status, index) => (status, index)).Where(item => item.status.Applicable).ToArray();
        foreach (var (status, index) in eligible)
            statuses[index] = transport is null ? status with { ReasonCode = "mcp.connection_unavailable" } :
                !string.Equals(transport.ServerId, status.Binding.ServerId, StringComparison.Ordinal)
                    ? status with { Integrity = McpBindingIntegrity.Mismatch, ReasonCode = "mcp.server_mismatch" }
                    : status with { ReasonCode = "mcp.discovery_unavailable" };
        if (transport is null || !statuses.Any(status => status.ReasonCode == "mcp.discovery_unavailable"))
            return new(Array.AsReadOnly(statuses), Array.Empty<ToolRegistration>());

        IReadOnlyList<McpDiscoveredTool> discovered;
        try { discovered = await transport.ListToolsAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (IsDependencyFailure(error, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(Array.AsReadOnly(statuses), Array.Empty<ToolRegistration>());
        }
        cancellationToken.ThrowIfCancellationRequested();
        var registrations = new List<ToolRegistration>();
        foreach (var (status, index) in eligible)
        {
            if (statuses[index].ReasonCode != "mcp.discovery_unavailable") continue;
            var binding = status.Binding;
            if (!string.Equals(transport.ServerId, binding.ServerId, StringComparison.Ordinal))
            {
                statuses[index] = status with { Integrity = McpBindingIntegrity.Mismatch, ReasonCode = "mcp.server_mismatch" };
                continue;
            }
            var matches = discovered.Where(tool => string.Equals(tool.Name, binding.RemoteName, StringComparison.Ordinal)).Take(2).ToArray();
            if (matches.Length != 1)
            {
                statuses[index] = status with { ReasonCode = matches.Length == 0 ? "mcp.tool_missing" : "mcp.tool_ambiguous" };
                continue;
            }
            string actualHash;
            try { actualHash = McpSchemaFingerprint.Sha256(matches[0].InputSchemaJson); }
            catch (Exception error) when (error is JsonException or ArgumentException)
            {
                statuses[index] = status with { Integrity = McpBindingIntegrity.Mismatch, ReasonCode = "mcp.schema_invalid" };
                continue;
            }
            if (!string.Equals(actualHash, binding.ExpectedInputSchemaSha256, StringComparison.OrdinalIgnoreCase))
            {
                statuses[index] = status with { Integrity = McpBindingIntegrity.Mismatch, ReasonCode = "mcp.schema_mismatch" };
                continue;
            }
            statuses[index] = status with { RuntimeReady = true, Integrity = McpBindingIntegrity.Matched, ReasonCode = "mcp.ready" };
            registrations.Add(new(binding.Descriptor, new BoundMcpTool(transport, binding), binding.Security));
        }
        return new(Array.AsReadOnly(statuses), registrations.AsReadOnly());
    }

    private static bool IsDependencyFailure(Exception error, CancellationToken cancellationToken) =>
        error is HttpRequestException or IOException or TimeoutException or ObjectDisposedException or McpException ||
        error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    public static async Task<IReadOnlyList<ToolRegistration>> BindAsync(
        IMcpToolTransport transport,
        IReadOnlyList<McpToolBinding> bindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(bindings);
        var discovered = await transport.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        var byName = discovered.GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var registrations = new List<ToolRegistration>(bindings.Count);
        var toolIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.ServerId) ||
                string.IsNullOrWhiteSpace(binding.RemoteName) ||
                string.IsNullOrWhiteSpace(binding.ExpectedInputSchemaSha256) ||
                !string.Equals(binding.ServerId, transport.ServerId, StringComparison.Ordinal) ||
                binding.MaxResultCharacters is < 1 or > 262_144 ||
                binding.Descriptor.Effect != binding.Security.Effect ||
                binding.Security.Provenance.Kind != ToolProvenanceKind.AdapterProvided ||
                !toolIds.Add(binding.Descriptor.ToolId))
            {
                throw new InvalidOperationException("An MCP binding is incomplete or inconsistent.");
            }
            if (!byName.TryGetValue(binding.RemoteName, out var matches) || matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"MCP tool '{binding.RemoteName}' was missing or duplicated.");
            }
            var actualHash = McpSchemaFingerprint.Sha256(matches[0].InputSchemaJson);
            if (!string.Equals(actualHash, binding.ExpectedInputSchemaSha256,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"MCP tool '{binding.RemoteName}' input schema changed.");
            }
            registrations.Add(new ToolRegistration(
                binding.Descriptor,
                new BoundMcpTool(transport, binding),
                binding.Security));
        }
        return registrations.AsReadOnly();
    }

    private sealed class BoundMcpTool(IMcpToolTransport transport, McpToolBinding binding) : ITool
    {
        public async Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(transport.ServerId, binding.ServerId, StringComparison.Ordinal))
                throw new InvalidOperationException("MCP tool server changed before invocation.");
            var discovered = await transport.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            var matches = discovered.Where(tool =>
                string.Equals(tool.Name, binding.RemoteName, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 ||
                !string.Equals(McpSchemaFingerprint.Sha256(matches[0].InputSchemaJson),
                    binding.ExpectedInputSchemaSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("MCP tool schema changed before invocation.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(transport.ServerId, binding.ServerId, StringComparison.Ordinal))
                throw new InvalidOperationException("MCP tool server changed before invocation.");
            var result = await transport.CallToolAsync(
                binding.RemoteName,
                invocation.Input,
                cancellationToken).ConfigureAwait(false);
            var content = string.Join("\n", result.TextBlocks);
            var structured = result.StructuredContentJson;
            if (content.Length > binding.MaxResultCharacters ||
                structured?.Length > binding.MaxResultCharacters)
            {
                throw new InvalidOperationException("MCP result exceeded its trusted binding limit.");
            }

            var receipt = new Receipt(
                AgenticaIds.New("receipt"),
                invocation.StepId,
                invocation.ToolId,
                result.IsError ? ReceiptStatus.Failed : ReceiptStatus.Succeeded,
                result.IsError ? "MCP tool reported an error." : "MCP tool completed.",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["mcp.serverId"] = binding.ServerId,
                    ["mcp.remoteName"] = binding.RemoteName,
                    ["mcp.schemaSha256"] = binding.ExpectedInputSchemaSha256,
                    ["mcp.contentSha256"] = Sha256(content, structured),
                    ["mcp.isError"] = result.IsError
                });
            var observation = new Observation(
                AgenticaIds.New("observation"),
                invocation.StepId,
                ObservationKind.ToolResult,
                result.IsError ? "MCP tool returned an error." : "MCP tool returned data.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["text"] = content,
                    ["structuredJson"] = structured
                },
                [new EvidenceRef("receipt", receipt.ReceiptId)]);
            return new ToolResult(receipt, observation);
        }

        private static string Sha256(string text, string? structured) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                text + "\n" + (structured ?? string.Empty))));
    }
}
