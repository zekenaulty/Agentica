using System.Security.Cryptography;
using System.Text;
using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Tools;

namespace Agentica.Mcp;

/// <summary>Converts only locally approved MCP bindings into Agentica registrations.</summary>
public static class McpToolCatalogAdapter
{
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
            var discovered = await transport.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            var matches = discovered.Where(tool =>
                string.Equals(tool.Name, binding.RemoteName, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 ||
                !string.Equals(McpSchemaFingerprint.Sha256(matches[0].InputSchemaJson),
                    binding.ExpectedInputSchemaSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("MCP tool schema changed before invocation.");
            }
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
