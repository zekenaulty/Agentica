using System.Security.Cryptography;
using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

public sealed partial class ActionCustodyDurableRetentionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentica-custody-retention", Guid.NewGuid().ToString("N"));
    private string LedgerPath => Path.Combine(_root, ActionCustodyStore.LedgerFileName);
    private static readonly DateTimeOffset Time = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PerSessionPruningRetainsAdmissionButPrunesOrdinaryResultsFromDurableCapabilities()
    {
        var entries = PerSessionEntries();
        var store = Seed(entries);
        var pending = entries[^1];
        Assert.True(Resolve(store, pending, Result(pending, operation: false)));
        store = new ActionCustodyStore(_root);
        AssertAdmissions(entries, ReadEntries());
        Missing(store, entries[1]);
        Assert.NotNull(Get(store, pending).Result);
        Assert.Equal(1, store.PrunedCompletedCount);
        Assert.False(store.HasUnresolved("host", "session"));
        Assert.False(Resolve(store, entries[0], entries[0].Result!));
        Error("custody.result_changed", () => Resolve(store, entries[0], entries[0].Result! with { Summary = "Changed" }));
    }

    [Fact]
    public void MoreThan256AdmissionsInOneSessionDoNotPreventOriginalResolution()
    {
        var entries = Enumerable.Range(0, ActionCustodyStore.MaximumCompletedPerSession + 1)
            .Select(i => Entry(i, "session")).Append(Entry(9999, "session", pending: true)).ToArray();
        var store = Seed(entries);
        var result = Result(entries[^1]);
        Assert.True(Resolve(store, entries[^1], result));
        store = new ActionCustodyStore(_root);
        Assert.Equal(entries.Length, ReadEntries().Length);
        AssertAdmissions(entries, ReadEntries());
        Assert.Equal(ProtocolValidation.Digest(result), Get(store, entries[^1]).ResultHash);
        Assert.Equal(0, store.PrunedCompletedCount);
    }

    [Fact]
    public void GlobalPruningSkipsOldAdmissionAndUnresolvedOriginal()
    {
        var entries = Enumerable.Range(0, ActionCustodyStore.MaximumEntries)
            .Select(i => Entry(i, operation: i == 0, pending: i == ActionCustodyStore.MaximumEntries - 1)).ToArray();
        var store = Seed(entries);
        var next = Entry(9999, pending: true);
        Reserve(store, next);
        store = new ActionCustodyStore(_root);
        AssertAdmissions(entries, ReadEntries());
        Missing(store, entries[1]);
        Assert.True(Get(store, entries[^1]).IsUnresolved);
        Assert.True(Get(store, next).IsUnresolved);
        Assert.Equal(ActionCustodyStore.MaximumEntries, ReadEntries().Length);
        Assert.Equal(1, store.PrunedCompletedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullProtectedCountRejectsNewReservationButAllowsOrdinaryResolution(bool reportedUnresolved)
    {
        var entries = Enumerable.Range(0, ActionCustodyStore.MaximumEntries - 1).Select(i => Entry(i))
            .Append(Entry(9998, pending: true)).ToArray();
        var pending = entries[^1];
        if (reportedUnresolved)
        {
            var unknown = Result(pending, operation: false) with { Disposition = "unresolved", AfterRevision = 0, Observation = null };
            entries[^1] = pending = pending with { Result = unknown, ResultHash = ProtocolValidation.Digest(unknown) };
        }
        var store = Seed(entries);
        var next = Entry(9999, pending: true);
        var before = FileHash();
        Error("custody.storage_limit", () => Reserve(store, next));
        Assert.Equal(before, FileHash());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        store = new ActionCustodyStore(_root);
        Assert.True(Get(store, pending).IsUnresolved);
        Assert.Equal(pending.ResultHash, Get(store, pending).ResultHash);
        Missing(store, next);
        Assert.Equal(0, store.PrunedCompletedCount);

        Assert.True(Resolve(store, pending, Result(pending, operation: false)));
        Reserve(store, next); // The newly resolved ordinary row is now the only eligible eviction.
        store = new ActionCustodyStore(_root);
        AssertAdmissions(entries, ReadEntries());
        Missing(store, pending);
        Assert.False(store.HasUnresolved("host", pending.SessionId));
        Assert.True(Get(store, next).IsUnresolved);
        Assert.Equal(1, store.PrunedCompletedCount);
    }

    private static ActionCustodyEntry[] PerSessionEntries() =>
        Enumerable.Range(0, ActionCustodyStore.MaximumCompletedPerSession + 1)
            .Select(i => Entry(i, "session", operation: i == 0)).Append(Entry(9999, "session", pending: true)).ToArray();

    private static ActionCustodyEntry Entry(int index, string? session = null, bool operation = true, bool pending = false, int padding = 0)
    {
        var id = index.ToString("D4");
        session ??= "session-" + id;
        var capability = new HostCapability("host.work", "Work", "Bounded durable host work", ToolKind.Action,
            ToolEffect.WritesLocalState, ToolInputSchema.Create(), DurableHandoff: true);
        var request = new HostActionRequest("action-" + id, "run-" + id, "runner-" + id, "step-" + id, session, "epoch",
            capability.Id, ProtocolValidation.Digest(new[] { capability }), HostProtocol.Element(new { padding = new string('p', padding) }), 0, Time.AddMinutes(1));
        var entry = new ActionCustodyEntry("host", session, "epoch", request.RunId, "objective-" + id, request, capability,
            ProtocolValidation.Digest(request), ProtocolValidation.Digest(capability), Time, Time);
        if (pending) return entry;
        var result = Result(entry, operation);
        return entry with { Result = result, ResultHash = ProtocolValidation.Digest(result) };
    }

    private static HostActionResult Result(ActionCustodyEntry entry, bool operation = true, int padding = 0) =>
        new(entry.Request.ActionId, entry.SessionId, entry.SessionEpoch, "applied", 0, 1, "receipt-" + entry.Request.ActionId,
            "Host verified the original action.", new("observation-" + entry.Request.ActionId, 1, Time.AddSeconds(1),
                HostProtocol.Element(new { padding = new string('r', padding) })),
            Operation: operation ? new("operation-" + entry.Request.ActionId, "Host work admitted.") : null);

    private static ActionCustodyEntry Reserve(ActionCustodyStore store, ActionCustodyEntry entry) =>
        store.Reserve(entry.HostId, entry.SessionId, entry.SessionEpoch, entry.RunId, entry.Request, entry.Capability, entry.ObjectiveId);
    private static bool Resolve(ActionCustodyStore store, ActionCustodyEntry entry, HostActionResult result) =>
        store.Resolve(entry.HostId, entry.SessionId, entry.Request.ActionId, result);
    private static ActionCustodyEntry Get(ActionCustodyStore store, ActionCustodyEntry entry) =>
        store.Get(entry.HostId, entry.SessionId, entry.Request.ActionId);
    private static void Missing(ActionCustodyStore store, ActionCustodyEntry entry) => Error("custody.not_found", () => Get(store, entry));
    private static void Error(string code, Action action) => Assert.Equal(code, Assert.Throws<HostProtocolException>(action).Code);

    private ActionCustodyStore Seed(ActionCustodyEntry[] entries)
    {
        _ = new ActionCustodyStore(_root);
        File.WriteAllBytes(LedgerPath, LedgerBytes(entries));
        return new ActionCustodyStore(_root); // Validate the synthetic fixture through the actual ledger loader.
    }
    private byte[] LedgerBytes(ActionCustodyEntry[] entries)
    {
        var payload = new FixturePayload(File.ReadAllText(Path.Combine(_root, "action-custody-v1.initialized")), entries, 0);
        return JsonSerializer.SerializeToUtf8Bytes(new FixtureEnvelope(1, payload, ProtocolValidation.Digest(payload)), HostProtocol.Json);
    }
    private ActionCustodyEntry[] ReadEntries() =>
        JsonSerializer.Deserialize<FixtureEnvelope>(File.ReadAllBytes(LedgerPath), HostProtocol.Json)!.Payload.Entries;
    private string FileHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(LedgerPath)));
    private static void AssertAdmissions(ActionCustodyEntry[] before, ActionCustodyEntry[] after)
    {
        var retained = after.ToDictionary(entry => entry.Request.ActionId, StringComparer.Ordinal);
        foreach (var admission in before.Where(entry => entry.Result?.Operation is not null))
        {
            Assert.True(retained.TryGetValue(admission.Request.ActionId, out var actual));
            Assert.Equal(admission.RequestHash, actual.RequestHash);
            Assert.Equal(admission.CapabilityHash, actual.CapabilityHash);
            Assert.Equal(admission.ResultHash, actual.ResultHash);
        }
    }
    private sealed record FixturePayload(string StoreId, ActionCustodyEntry[] Entries, long PrunedCompletedCount);
    private sealed record FixtureEnvelope(int Version, FixturePayload Payload, string ContentHash);
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
