using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HostStartStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentica-start-store-tests", Guid.NewGuid().ToString("N"));
    private string LedgerPath => Path.Combine(_root, HostStartStore.LedgerFileName);
    private string MarkerPath => Path.ChangeExtension(LedgerPath, ".initialized");

    [Fact]
    public void AdmissionAndStopFencesSurviveReloadWithoutExecutionOrRewritingReplay()
    {
        var store = new HostStartStore(_root);
        Assert.Null(store.Find(Key()));
        Assert.False(store.HasOpen("host", "session"));
        Assert.False(store.HasRetainedObjective("host", "session", "objective"));
        var request = Request();
        var entry = store.Admit("attempt", request, request, "run");
        Assert.Equal(1, entry.Schema);
        Assert.Equal(ProtocolValidation.Digest(request), entry.OriginalRequestHash);
        Assert.Equal(entry.OriginalRequestHash, entry.AcceptedRequestHash);
        Assert.Equal(ProtocolValidation.Digest(request.Capabilities), entry.ManifestHash);
        Assert.True(entry.AdmittedAt > DateTimeOffset.UnixEpoch);
        Assert.Null(entry.Terminal);
        Assert.False(entry.StopRequested);
        var before = Bytes();
        store = new HostStartStore(_root);
        Same(entry, store.Find(Key())!);
        Same(entry, store.Admit("attempt", request, request, "run"));
        Assert.Equal(before, Bytes());
        Assert.True(store.HasOpen("host", "session"));
        Assert.True(store.HasRetainedObjective("host", "session", "objective"));
        Assert.False(store.IsStopRequested("host", "session", "objective"));

        var stopped = store.RequestStop(Reference(entry));
        Assert.True(stopped.StopRequested);
        Assert.Null(stopped.Terminal);
        before = Bytes();
        store = new HostStartStore(_root);
        Same(stopped, store.RequestStop(Reference(entry)));
        Assert.Equal(before, Bytes());
        Assert.True(store.HasOpen("host", "session"));
        Assert.True(store.IsStopRequested("host", "session", "objective"));

        var settled = store.SettleStopped(Reference(entry), "stopped", new HostRunUsage(2, 1, 0, 1), false);
        Assert.Equal("stopped", settled.Terminal!.Reason);
        Assert.True(settled.Terminal.SettledAt >= settled.AdmittedAt);
        before = Bytes();
        store = new HostStartStore(_root);
        Same(settled, store.SettleStopped(Reference(entry), "stopped", new HostRunUsage(2, 1, 0, 1), false));
        Same(settled, store.RequestStop(Reference(entry)));
        Same(settled, store.Admit("attempt", request, request, "run"));
        Assert.Equal(before, Bytes());
        Assert.False(store.HasOpen("host", "session"));
        Assert.True(store.HasRetainedObjective("host", "session", "objective"));
        Assert.True(store.IsStopRequested("host", "session", "objective"));
        Assert.False(store.HasOpen("other", "session"));
        Assert.False(store.HasRetainedObjective("host", "other", "objective"));
        Assert.False(store.IsStopRequested("host", "session", "other"));
    }

    [Fact]
    public void RestartDoesNotSettleOrInferZeroUsageAndInterruptedSettlementIsImmutable()
    {
        var store = new HostStartStore(_root);
        var entry = Admit(store);
        var before = Bytes();
        store = new HostStartStore(_root);
        Assert.True(store.HasOpen("host", "session"));
        Assert.Null(store.Find(Key())!.Terminal);
        Error("start.stop_required", () => store.SettleStopped(Reference(entry), "interrupted", null, true));
        Assert.Equal(before, Bytes());
        store.RequestStop(Reference(entry));
        var settled = store.SettleStopped(Reference(entry), "interrupted", null, true);
        Assert.Null(settled.Terminal!.Usage);
        Assert.True(settled.Terminal.UsageUnknown);
        before = Bytes();
        store = new HostStartStore(_root);
        Same(settled, store.SettleStopped(Reference(entry), "interrupted", null, true));
        Error("start.terminal_changed", () => store.SettleStopped(Reference(entry), "stopped", new HostRunUsage(), false));
        Assert.Equal(before, Bytes());
    }

    [Fact]
    public void ReturnedAndCallerOwnedMutableValuesCannotRewriteRetainedInputs()
    {
        var store = new HostStartStore(_root);
        using var document = JsonDocument.Parse("{\"position\":\"north\"}");
        var request = Request() with { Observation = Request().Observation with { Data = document.RootElement } };
        var originalHash = ProtocolValidation.Digest(request);
        var admitted = store.Admit("attempt", request, request, "run");
        Mutate(request);
        document.Dispose();
        Mutate(admitted.OriginalRequest);
        Mutate(admitted.AcceptedRequest);
        var found = store.Find(Key())!;
        Assert.Equal(originalHash, ProtocolValidation.Digest(found.OriginalRequest));
        Assert.Equal(originalHash, ProtocolValidation.Digest(found.AcceptedRequest));
        Mutate(found.OriginalRequest);
        var stopped = store.RequestStop(Reference(found));
        Mutate(stopped.AcceptedRequest);
        var settled = store.SettleStopped(Reference(found), "interrupted", null, true);
        Mutate(settled.OriginalRequest);
        var retained = new HostStartStore(_root).Find(Key())!;
        Assert.Equal(originalHash, ProtocolValidation.Digest(retained.OriginalRequest));
        Assert.Equal(originalHash, ProtocolValidation.Digest(retained.AcceptedRequest));
    }

    [Fact]
    public async Task ConcurrentExactAdmissionsAndStopsHaveOneDurableIdentity()
    {
        var store = new HostStartStore(_root);
        var entries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Admit(store))));
        Assert.All(entries, entry => Same(entries[0], entry));
        var stopped = await Task.WhenAll(entries.Select(entry => Task.Run(() => store.RequestStop(Reference(entry)))));
        Assert.All(stopped, entry => Same(stopped[0], entry));
        Assert.Single(JsonNode.Parse(File.ReadAllText(LedgerPath))!["payload"]!["entries"]!.AsArray());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private static HostRunRequest Request() => new(1, "host", "session", "epoch", "scope", "party", "objective", "Inspect the host state",
        new HostObservation("observation", 7, DateTimeOffset.UnixEpoch.AddSeconds(7), HostProtocol.Element(new { position = "north" }),
            new List<KnowledgeFact> { new("position", "Observed position", HostProtocol.Element("north"), EvidenceObservationIds: new List<string> { "observation" }) }),
        new List<HostCapability> { new("host.inspect", "Inspect", "Inspect bounded host state", ToolKind.Query, ToolEffect.ReadOnly,
            ToolInputSchema.Create(new ToolInputField("direction", Required: true, AllowedValues: new List<string> { "north", "south" },
                Example: new Dictionary<string, object?> { ["direction"] = "north" }))) },
        new ProviderSettings("openai", "explicit-model", "low", 1024, 4096), new HostRunLimits());
    private static HostStartKey Key() => new("host", "session", "attempt");
    private static HostStartReference Reference(HostStartEntry entry) => new(entry.OriginalRequest.HostId,
        entry.OriginalRequest.SessionId, entry.AttemptId, entry.RunId, entry.AcceptedRequestHash);
    private static HostStartEntry Admit(HostStartStore store)
    {
        var request = Request();
        return store.Admit("attempt", request, request, "run");
    }
    private static void Same(HostStartEntry expected, HostStartEntry actual) => Assert.Equal(ProtocolValidation.Digest(expected), ProtocolValidation.Digest(actual));
    private static void Error(string code, Action action) => Assert.Equal(code, Assert.Throws<HostProtocolException>(action).Code);
    private byte[] Bytes() => File.ReadAllBytes(LedgerPath);
    private string FileHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(LedgerPath)));
    private static void Mutate(HostRunRequest request)
    {
        var capability = request.Capabilities[0];
        var schema = capability.InputSchema.Fields;
        ((IList<string>)schema[0].AllowedValues!)[0] = "changed";
        ((IList<ToolInputField>)schema)[0] = schema[0] with { Description = "changed" };
        ((IList<HostCapability>)request.Capabilities)[0] = capability with { Name = "changed" };
        ((IList<string>)request.Observation.Facts![0].EvidenceObservationIds!)[0] = "changed";
        ((IList<KnowledgeFact>)request.Observation.Facts)[0] = request.Observation.Facts[0] with { Summary = "changed" };
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
