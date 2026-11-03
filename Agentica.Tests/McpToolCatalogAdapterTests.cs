using Agentica.Artifacts;
using Agentica.Mcp;
using Agentica.Tools;
using System.Text.Json;

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

    [Fact]
    public async Task Host_bound_external_action_is_standing_scoped_authority()
    {
        var binding = McpTrustedBindingFactory.Create(
            "server-1", "search", McpSchemaFingerprint.Sha256(Schema),
            "remote-publish", "Publish", "Host installed publication capability", Schema,
            ToolKind.Action, ToolEffect.ExternalSideEffect,
            [ToolDataBoundary.UserContent], [ToolDataBoundary.ExternalUntrusted],
            ToolExternalOutputClassification.Mixed,
            ToolApprovalRequirement.None, ToolRetrySafety.Additive);
        Assert.Equal(ToolEffect.ExternalSideEffect, binding.Descriptor.Effect);
        Assert.False(binding.Descriptor.RequiresApproval);
        Assert.Equal(ToolApprovalRequirement.None, binding.Security.ApprovalRequirement);
        Assert.Equal(ToolRetrySafety.Additive, binding.Security.RetrySafety);
        var registration = Assert.Single(await McpToolCatalogAdapter.BindAsync(
            new FakeTransport(), [binding]));
        Assert.Equal("remote-publish", registration.Descriptor.ToolId);
    }

    [Fact]
    public void Host_bound_external_action_rejects_ambiguous_classification()
    {
        Assert.Throws<ArgumentException>(() => McpTrustedBindingFactory.Create(
            "server-1", "search", McpSchemaFingerprint.Sha256(Schema),
            "remote-publish", "Publish", "Host installed publication capability", Schema,
            ToolKind.Action, ToolEffect.Unknown,
            [ToolDataBoundary.UserContent], [ToolDataBoundary.ExternalUntrusted],
            ToolExternalOutputClassification.Mixed,
            ToolApprovalRequirement.None, ToolRetrySafety.Additive));
    }

    [Fact]
    public async Task Inspection_keeps_bound_configuration_during_connection_and_discovery_outages()
    {
        var binding = Binding();
        var disconnected = await McpToolCatalogAdapter.InspectAsync(null, [binding]);
        var status = Assert.Single(disconnected.Statuses);
        Assert.Same(binding, status.Binding); Assert.True(status.Bound); Assert.True(status.Applicable);
        Assert.False(status.RuntimeReady); Assert.Equal(McpBindingIntegrity.Unknown, status.Integrity);
        Assert.Equal("mcp.connection_unavailable", status.ReasonCode); Assert.Empty(disconnected.ReadyRegistrations);
        var transport = new FakeTransport();
        var ready = await McpToolCatalogAdapter.InspectAsync(transport, [binding]);
        var registration = Assert.Single(ready.ReadyRegistrations);
        transport.ListError = new IOException("fixture-secret-diagnostic");
        var unavailable = await McpToolCatalogAdapter.InspectAsync(transport, [binding]);
        Assert.Same(binding, Assert.Single(unavailable.Statuses).Binding);
        Assert.Equal("mcp.discovery_unavailable", unavailable.Statuses[0].ReasonCode);
        Assert.False(unavailable.Statuses[0].RuntimeReady); Assert.Empty(unavailable.ReadyRegistrations);
        Assert.DoesNotContain("fixture-secret-diagnostic", JsonSerializer.Serialize(unavailable), StringComparison.Ordinal);
        await Assert.ThrowsAsync<IOException>(() => registration.Tool.ExecuteAsync(Invocation(), CancellationToken.None));
        Assert.Equal(0, transport.CallCount);
        await Assert.ThrowsAsync<IOException>(() => McpToolCatalogAdapter.BindAsync(transport, [binding]));
    }

    [Fact]
    public async Task Inspection_recovers_only_the_original_pin_and_does_not_widen_standing_authority()
    {
        var binding = Binding(); var transport = new FakeTransport();
        var original = await McpToolCatalogAdapter.InspectAsync(transport, [binding]);
        var registration = Assert.Single(original.ReadyRegistrations);
        transport.Tools[0] = new("search", "{\"type\":\"object\"}");
        var drifted = await McpToolCatalogAdapter.InspectAsync(transport, [binding]);
        var status = Assert.Single(drifted.Statuses);
        Assert.Same(binding, status.Binding); Assert.True(status.Bound); Assert.False(status.RuntimeReady);
        Assert.Equal(McpBindingIntegrity.Mismatch, status.Integrity); Assert.Equal("mcp.schema_mismatch", status.ReasonCode);
        Assert.Empty(drifted.ReadyRegistrations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Tool.ExecuteAsync(Invocation(), CancellationToken.None));
        Assert.Equal(0, transport.CallCount);
        transport.Tools[0] = new("search", Schema);
        var recovered = await McpToolCatalogAdapter.InspectAsync(transport, [binding]);
        Assert.True(Assert.Single(recovered.Statuses).RuntimeReady);
        Assert.Equal(binding.ExpectedInputSchemaSha256, recovered.Statuses[0].Binding.ExpectedInputSchemaSha256);
        Assert.Equal(ToolApprovalRequirement.None, Assert.Single(recovered.ReadyRegistrations).Security.ApprovalRequirement);
        var result = await registration.Tool.ExecuteAsync(Invocation(), CancellationToken.None);
        Assert.Equal(ReceiptStatus.Succeeded, result.Receipt.Status); Assert.Equal(1, transport.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_change_before_or_during_discovery_stops_an_existing_registration(bool duringDiscovery)
    {
        var transport = new FakeTransport(); var binding = Binding();
        var registration = Assert.Single(await McpToolCatalogAdapter.BindAsync(transport, [binding]));
        if (duringDiscovery) transport.OnList = () => transport.ServerId = "other-server";
        else transport.ServerId = "other-server";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registration.Tool.ExecuteAsync(Invocation(), CancellationToken.None));
        Assert.Contains("server changed", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, transport.CallCount);
        var projected = await McpToolCatalogAdapter.InspectAsync(transport, [binding]);
        Assert.Equal("mcp.server_mismatch", Assert.Single(projected.Statuses).ReasonCode);
        Assert.Empty(projected.ReadyRegistrations);
    }

    [Fact]
    public async Task Host_applicability_and_binding_membership_remain_separate_from_remote_discovery()
    {
        var binding = Binding(); var transport = new FakeTransport();
        transport.Tools.Add(new("unbound", Schema));
        var inactive = await McpToolCatalogAdapter.InspectAsync(transport, [binding], new HashSet<string>(["unbound"], StringComparer.Ordinal));
        var status = Assert.Single(inactive.Statuses);
        Assert.True(status.Bound); Assert.False(status.Applicable); Assert.False(status.RuntimeReady);
        Assert.Equal("mcp.not_applicable", status.ReasonCode); Assert.Equal(McpBindingIntegrity.Unknown, status.Integrity);
        Assert.Empty(inactive.ReadyRegistrations); Assert.Equal(0, transport.ListCount);
        var active = await McpToolCatalogAdapter.InspectAsync(transport, [binding], new HashSet<string>(["remote-search"], StringComparer.Ordinal));
        Assert.Equal("remote-search", Assert.Single(active.ReadyRegistrations).Descriptor.ToolId);
        Assert.Equal("mcp.ready", Assert.Single(active.Statuses).ReasonCode); Assert.Equal(0, transport.CallCount);
    }

    [Theory]
    [InlineData("missing", "mcp.tool_missing", McpBindingIntegrity.Unknown)]
    [InlineData("duplicate", "mcp.tool_ambiguous", McpBindingIntegrity.Unknown)]
    [InlineData("invalid", "mcp.schema_invalid", McpBindingIntegrity.Mismatch)]
    public async Task Missing_ambiguous_and_malformed_dependencies_have_explicit_nonexecutable_status(
        string condition, string reason, McpBindingIntegrity integrity)
    {
        var transport = new FakeTransport();
        if (condition == "missing") transport.Tools.Clear();
        if (condition == "duplicate") transport.Tools.Add(new("search", Schema));
        if (condition == "invalid") transport.Tools[0] = new("search", "{");
        var result = await McpToolCatalogAdapter.InspectAsync(transport, [Binding()]);
        var status = Assert.Single(result.Statuses);
        Assert.Equal(reason, status.ReasonCode); Assert.Equal(integrity, status.Integrity);
        Assert.True(status.Bound); Assert.False(status.RuntimeReady); Assert.Empty(result.ReadyRegistrations);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Inspection_is_bounded_and_caller_cancellation_is_not_relabelled_as_unavailability()
    {
        var transport = new FakeTransport();
        var bindings = Enumerable.Range(0, 65).Select(index => Binding() with
        { Descriptor = Binding().Descriptor with { ToolId = "tool-" + index } }).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() => McpToolCatalogAdapter.InspectAsync(transport, bindings));
        Assert.Equal(0, transport.ListCount);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => McpToolCatalogAdapter.InspectAsync(transport, [Binding()], cancellationToken: stop.Token));
        Assert.Equal(0, transport.ListCount);
        Assert.Empty((await McpToolCatalogAdapter.InspectAsync(transport, [])).Statuses);
        Assert.Equal(0, transport.ListCount);
    }

    private static ToolInvocation Invocation() => new("run", "step", "remote-search", new Dictionary<string, object?>());

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
        public string ServerId { get; set; } = "server-1";
        public List<McpDiscoveredTool> Tools { get; } = [new("search", Schema)];
        public int CallCount { get; private set; }
        public bool ReturnError { get; init; }
        public int ListCount { get; private set; }
        public Exception? ListError { get; set; }
        public Action? OnList { get; set; }

        public Task<IReadOnlyList<McpDiscoveredTool>> ListToolsAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); ListCount++;
            if (ListError is not null) throw ListError;
            OnList?.Invoke();
            return Task.FromResult<IReadOnlyList<McpDiscoveredTool>>(Tools);
        }

        public Task<McpCallResult> CallToolAsync(string remoteName,
            IReadOnlyDictionary<string, object?> input,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new McpCallResult(ReturnError, ["remote data"]));
        }
    }
}
