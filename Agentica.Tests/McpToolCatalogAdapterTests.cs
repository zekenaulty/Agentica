using Agentica.Artifacts;
using Agentica.Mcp;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class McpToolCatalogAdapterTests
{
    private const string Schema = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}";

    [Fact]
    public async Task Approved_read_only_tool_produces_receipted_untrusted_result()
    {
        var transport = new FakeTransport();
        var registrations = await McpToolCatalogAdapter.BindAsync(transport, [Binding()]);
        var registration = Assert.Single(registrations);
        Assert.Equal(ToolEffect.ReadOnly, registration.Descriptor.Effect);
        Assert.Equal(ToolProvenanceKind.AdapterProvided, registration.Security.Provenance.Kind);

        var result = await registration.Tool.ExecuteAsync(new ToolInvocation(
            "run-1", "step-1", "remote-search", new Dictionary<string, object?>
            {
                ["query"] = "Agentica"
            }), CancellationToken.None);

        Assert.Equal(1, transport.CallCount);
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status);
        Assert.Equal("server-1", result.Receipt.Data["mcp.serverId"]);
        Assert.Equal("search", result.Receipt.Data["mcp.remoteName"]);
        Assert.NotNull(result.Receipt.Data["mcp.contentSha256"]);
        Assert.Equal("remote data", result.Observation!.Data["text"]);
    }

    [Fact]
    public async Task Unapproved_remote_tool_is_not_registered()
    {
        var transport = new FakeTransport();
        transport.Tools.Add(new McpDiscoveredTool("delete", Schema));
        var registrations = await McpToolCatalogAdapter.BindAsync(transport, [Binding()]);
        Assert.Single(registrations);
        Assert.DoesNotContain(registrations, registration =>
            registration.Descriptor.ToolId == "delete");
    }

    [Fact]
    public async Task Wrong_server_or_changed_schema_fails_before_call()
    {
        var transport = new FakeTransport();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            McpToolCatalogAdapter.BindAsync(transport, [Binding() with { ServerId = "other" }]));

        var registration = Assert.Single(await McpToolCatalogAdapter.BindAsync(
            transport, [Binding()]));
        transport.Tools[0] = new McpDiscoveredTool("search", "{\"type\":\"object\"}");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registration.Tool.ExecuteAsync(new ToolInvocation(
                "run-1", "step-1", "remote-search", new Dictionary<string, object?>()),
                CancellationToken.None));
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Remote_error_is_a_failed_receipt()
    {
        var transport = new FakeTransport { ReturnError = true };
        var registration = Assert.Single(await McpToolCatalogAdapter.BindAsync(
            transport, [Binding()]));
        var result = await registration.Tool.ExecuteAsync(new ToolInvocation(
            "run-1", "step-1", "remote-search", new Dictionary<string, object?>()),
            CancellationToken.None);
        Assert.Equal(ReceiptStatus.Failed, result.Receipt.Status);
    }

    [Fact]
    public void Read_only_binding_projects_trusted_schema_without_remote_description()
    {
        var binding = McpReadOnlyBindingFactory.Create(
            "server-1", "search", McpSchemaFingerprint.Sha256(Schema),
            "remote-search", "Approved search", "Host-approved description", Schema);

        Assert.Equal("Host-approved description", binding.Descriptor.Description);
        var field = Assert.Single(binding.Descriptor.InputSchema!.Fields);
        Assert.Equal("query", field.Name);
        Assert.Equal(ToolInputValueType.String, field.Type);
        Assert.Equal(ToolEffect.ReadOnly, binding.Security.Effect);
        Assert.Equal(ToolDataBoundary.ExternalUntrusted,
            Assert.Single(binding.Security.ExposesToPlanner));
    }

    [Fact]
    public void Read_only_binding_rejects_schema_drift()
    {
        Assert.Throws<InvalidOperationException>(() =>
            McpReadOnlyBindingFactory.Create("server-1", "search",
                McpSchemaFingerprint.Sha256(Schema), "remote-search", "Search",
                "Description", "{\"type\":\"object\"}"));
    }

    private static McpToolBinding Binding() => new(
        "server-1", "search", McpSchemaFingerprint.Sha256(Schema),
        new ToolDescriptor("remote-search", "Remote search", ToolKind.Query,
            ToolEffect.ReadOnly, RetrySafety: ToolRetrySafety.Idempotent),
        new ToolSecurityDeclaration(ToolEffect.ReadOnly,
            [ToolDataBoundary.Public], [ToolDataBoundary.ExternalUntrusted],
            ToolExternalOutputClassification.UntrustedText,
            ToolApprovalRequirement.None, ToolRetrySafety.Idempotent,
            new ToolProvenance(ToolProvenanceKind.AdapterProvided, "server-1", "1")));

    private sealed class FakeTransport : IMcpToolTransport
    {
        public string ServerId => "server-1";
        public List<McpDiscoveredTool> Tools { get; } = [new("search", Schema)];
        public int CallCount { get; private set; }
        public bool ReturnError { get; init; }

        public Task<IReadOnlyList<McpDiscoveredTool>> ListToolsAsync(
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<McpDiscoveredTool>>(Tools);

        public Task<McpCallResult> CallToolAsync(string remoteName,
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new McpCallResult(ReturnError, ["remote data"]));
        }
    }
}
