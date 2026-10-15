using System.Text.Json;
using System.Text.Json.Nodes;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HostStartStoreTests
{
    [Theory]
    [InlineData("missing-ledger")]
    [InlineData("invalid-json")]
    [InlineData("checksum")]
    [InlineData("version")]
    [InlineData("missing-marker")]
    [InlineData("wrong-marker")]
    [InlineData("oversize-marker")]
    [InlineData("oversize-ledger")]
    public void MissingOrCorruptStorageFencesEveryQueryAndMutation(string change)
    {
        var store = new HostStartStore(_root);
        var entry = Admit(store);
        switch (change)
        {
            case "missing-ledger": File.Delete(LedgerPath); break;
            case "invalid-json": File.WriteAllText(LedgerPath, "{"); break;
            case "checksum": File.WriteAllText(LedgerPath, File.ReadAllText(LedgerPath).Replace("north", "south", StringComparison.Ordinal)); break;
            case "version":
                var node = JsonNode.Parse(File.ReadAllText(LedgerPath))!;
                node["version"] = 2;
                File.WriteAllText(LedgerPath, node.ToJsonString(HostProtocol.Json));
                break;
            case "missing-marker": File.Delete(MarkerPath); break;
            case "wrong-marker": File.WriteAllText(MarkerPath, Guid.NewGuid().ToString("N")); break;
            case "oversize-marker": File.WriteAllText(MarkerPath, new string('a', 129)); break;
            default:
                using (var file = new FileStream(LedgerPath, FileMode.Open, FileAccess.Write)) file.SetLength(HostStartStore.MaximumFileBytes + 1L);
                break;
        }
        var before = File.Exists(LedgerPath) ? FileHash() : null;
        Assert.Throws<InvalidDataException>(() => new HostStartStore(_root));
        Assert.Throws<InvalidDataException>(() => store.Find(Key()));
        Assert.Throws<InvalidDataException>(() => store.HasOpen("host", "session"));
        Assert.Throws<InvalidDataException>(() => store.HasRetainedObjective("host", "session", "objective"));
        Assert.Throws<InvalidDataException>(() => store.IsStopRequested("host", "session", "objective"));
        Assert.Throws<InvalidDataException>(() => Admit(store));
        Assert.Throws<InvalidDataException>(() => store.RequestStop(Reference(entry)));
        Assert.Throws<InvalidDataException>(() => store.SettleStopped(Reference(entry), "interrupted", null, true));
        Assert.Equal(before, File.Exists(LedgerPath) ? FileHash() : null);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void EmptyLedgerWithMissingMarkerIsNotTreatedAsFreshInitialization()
    {
        var store = new HostStartStore(_root);
        var before = Bytes();
        File.Delete(MarkerPath);
        Assert.Throws<InvalidDataException>(() => new HostStartStore(_root));
        Assert.Throws<InvalidDataException>(() => store.Find(Key()));
        Assert.False(File.Exists(MarkerPath));
        Assert.Equal(before, Bytes());
    }

    [Theory]
    [InlineData("entry-schema")]
    [InlineData("original-hash")]
    [InlineData("accepted-hash")]
    [InlineData("manifest-hash")]
    [InlineData("accepted-observation")]
    [InlineData("admitted-at")]
    [InlineData("terminal-without-stop")]
    [InlineData("terminal-reason")]
    [InlineData("terminal-usage")]
    [InlineData("terminal-time")]
    [InlineData("null-entry")]
    [InlineData("duplicate-key")]
    [InlineData("duplicate-run")]
    [InlineData("duplicate-objective")]
    [InlineData("duplicate-open-session")]
    [InlineData("too-many-entries")]
    [InlineData("unknown-property")]
    public void StructuralCorruptionStillFailsWithARecomputedEnvelopeChecksum(string change)
    {
        var store = new HostStartStore(_root);
        var retained = Admit(store);
        store.RequestStop(Reference(retained));
        store.SettleStopped(Reference(retained), "stopped", new HostRunUsage(1, 0, 0, 1), false);
        var node = JsonNode.Parse(File.ReadAllText(LedgerPath))!;
        var entries = node["payload"]!["entries"]!.AsArray();
        var entry = entries[0]!;
        switch (change)
        {
            case "entry-schema": entry["schema"] = 2; break;
            case "original-hash": entry["originalRequestHash"] = "changed"; break;
            case "accepted-hash": entry["acceptedRequestHash"] = "changed"; break;
            case "manifest-hash": entry["manifestHash"] = "changed"; break;
            case "accepted-observation":
                entry["acceptedRequest"]!["observation"]!["data"]!["position"] = "south";
                entry["acceptedRequestHash"] = ProtocolValidation.Digest(JsonSerializer.SerializeToElement(entry["acceptedRequest"], HostProtocol.Json));
                break;
            case "admitted-at": entry["admittedAt"] = "0001-01-01T00:00:00+00:00"; break;
            case "terminal-without-stop": entry["stopRequested"] = false; break;
            case "terminal-reason": entry["terminal"]!["reason"] = "completed"; break;
            case "terminal-usage": entry["terminal"]!["usage"]!["providerCalls"] = -1; break;
            case "terminal-time": entry["terminal"]!["settledAt"] = DateTimeOffset.UnixEpoch; break;
            case "null-entry": entries[0] = null; break;
            case "unknown-property": entry["unexpected"] = true; break;
            case "duplicate-key": entries.Add(entry.DeepClone()); break;
            case "duplicate-run":
                entries.Add(JsonSerializer.SerializeToNode(EntryFor(retained, 1) with { RunId = retained.RunId }, HostProtocol.Json));
                break;
            case "duplicate-objective":
                entries.Add(JsonSerializer.SerializeToNode(retained with { AttemptId = "other-attempt", RunId = "other-run" }, HostProtocol.Json));
                break;
            case "duplicate-open-session":
                entry["terminal"] = null;
                var otherRequest = retained.OriginalRequest with { ObjectiveId = "other" };
                var other = Rebind(retained with { AttemptId = "other-attempt", RunId = "other-run" }, otherRequest);
                entries.Add(JsonSerializer.SerializeToNode(other, HostProtocol.Json));
                break;
            default:
                entries.Clear();
                for (var i = 0; i <= HostStartStore.MaximumEntries; i++) entries.Add(JsonSerializer.SerializeToNode(EntryFor(retained, i), HostProtocol.Json));
                break;
        }
        node["contentHash"] = ProtocolValidation.Digest(JsonSerializer.SerializeToElement(node["payload"], HostProtocol.Json));
        File.WriteAllText(LedgerPath, node.ToJsonString(HostProtocol.Json));
        var before = Bytes();
        Assert.Throws<InvalidDataException>(() => new HostStartStore(_root));
        Assert.Throws<InvalidDataException>(() => store.Find(Key()));
        Assert.Equal(before, Bytes());
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Windows delete-sharing failure injection", SkipUnless = nameof(IsWindows))]
    public void FailedAtomicReplaceLeavesOriginalBytesAndNoTemporaryOrCachedStop()
    {
        var store = new HostStartStore(_root);
        var entry = Admit(store);
        var before = Bytes();
        using (var held = new FileStream(LedgerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => store.RequestStop(Reference(entry))) is IOException or UnauthorizedAccessException);
        Assert.Equal(before, Bytes());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.False(store.Find(Key())!.StopRequested);
        Assert.True(store.RequestStop(Reference(entry)).StopRequested);
        before = Bytes();
        using (var held = new FileStream(LedgerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => store.SettleStopped(Reference(entry), "interrupted", null, true)) is IOException or UnauthorizedAccessException);
        Assert.Equal(before, Bytes());
        Assert.Null(store.Find(Key())!.Terminal);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        store.SettleStopped(Reference(entry), "interrupted", null, true);
        Assert.False(store.HasOpen("host", "session"));
    }

    private static HostStartEntry Rebind(HostStartEntry template, HostRunRequest request) => template with
    {
        OriginalRequest = request, AcceptedRequest = request,
        OriginalRequestHash = ProtocolValidation.Digest(request), AcceptedRequestHash = ProtocolValidation.Digest(request),
        ManifestHash = ProtocolValidation.Digest(request.Capabilities)
    };
    private static HostStartEntry EntryFor(HostStartEntry template, int index) => Rebind(template with
    {
        AttemptId = "attempt-" + index.ToString("D4"), RunId = "run-" + index.ToString("D4")
    }, template.OriginalRequest with { SessionId = "session-" + index.ToString("D4") });
    private byte[] LedgerBytes(IReadOnlyList<HostStartEntry> entries)
    {
        var payload = new { StoreId = File.ReadAllText(MarkerPath), Entries = entries };
        return JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Payload = payload, ContentHash = ProtocolValidation.Digest(payload) }, HostProtocol.Json);
    }
}
