using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class ActionCustodyDurableRetentionTests
{
    [Fact]
    public void BytePruningRetainsEveryAdmissionAndPendingOriginalWhileEvictingOrdinaryResult()
    {
        var (store, entries) = SeedByteBoundary(includeOrdinary: true);
        Assert.True(entries.Length < ActionCustodyStore.MaximumEntries);
        Assert.Equal(ActionCustodyStore.MaximumFileBytes - 1024L, new FileInfo(LedgerPath).Length);
        var ordinary = entries.Single(entry => !entry.IsUnresolved && entry.Result!.Operation is null);
        var before = FileHash();
        Error("custody.storage_limit", () => Reserve(store, Entry(9999, pending: true, padding: 32_768)));
        Assert.Equal(before, FileHash());
        Assert.NotNull(Get(store, ordinary).Result);
        Assert.Equal(0, store.PrunedCompletedCount);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));

        var next = Entry(9999, pending: true, padding: 8192);
        Reserve(store, next);
        store = new ActionCustodyStore(_root);
        AssertAdmissions(entries, ReadEntries());
        Missing(store, ordinary);
        Assert.True(Get(store, entries[^1]).IsUnresolved);
        Assert.True(Get(store, next).IsUnresolved);
        Assert.Equal(1, store.PrunedCompletedCount);
        Assert.True(new FileInfo(LedgerPath).Length <= ActionCustodyStore.MaximumFileBytes);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void ByteExhaustionRollsBackAdmissionResolutionButOrdinaryResolutionCanPruneItself()
    {
        var (store, entries) = SeedByteBoundary(includeOrdinary: false);
        var pending = entries[^1];
        var next = Entry(9999, pending: true, padding: 8192);
        var before = FileHash();
        Error("custody.storage_limit", () => Reserve(store, next));
        Assert.Equal(before, FileHash());
        Missing(store, next);

        var admission = Result(pending, padding: 8192);
        store.PrevalidateResolution(pending.HostId, pending.SessionId, pending.Request.ActionId, admission);
        Error("custody.storage_limit", () => Resolve(store, pending, admission));
        Assert.Equal(before, FileHash());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        store = new ActionCustodyStore(_root);
        Assert.True(Get(store, pending).IsUnresolved);
        Assert.Null(Get(store, pending).Result);
        Assert.True(store.HasUnresolved(pending.HostId, pending.SessionId));
        Assert.Equal(0, store.PrunedCompletedCount);

        // This is an alternative ordinary outcome after a failed write, not a rewrite of a committed admission.
        var ordinary = Result(pending, operation: false, padding: 8192);
        Assert.True(Resolve(store, pending, ordinary));
        store = new ActionCustodyStore(_root);
        AssertAdmissions(entries, ReadEntries());
        Missing(store, pending);
        Assert.False(store.HasUnresolved(pending.HostId, pending.SessionId));
        Assert.Equal(1, store.PrunedCompletedCount);
        Assert.True(new FileInfo(LedgerPath).Length <= ActionCustodyStore.MaximumFileBytes);
    }

    private (ActionCustodyStore Store, ActionCustodyEntry[] Entries) SeedByteBoundary(bool includeOrdinary)
    {
        _ = new ActionCustodyStore(_root);
        const int padding = 250_000;
        var extras = new List<ActionCustodyEntry>();
        if (includeOrdinary) extras.Add(Entry(9000, operation: false, padding: 16_384));
        extras.Add(Entry(9998, pending: true));
        var targetBytes = ActionCustodyStore.MaximumFileBytes - 1024;
        var largeEntryBytes = JsonSerializer.SerializeToUtf8Bytes(Entry(0, padding: padding), HostProtocol.Json).Length;
        var count = (targetBytes - LedgerBytes(extras.ToArray()).Length) / (largeEntryBytes + 1) + 1;
        var entries = Enumerable.Range(0, count).Select(i => Entry(i, padding: padding)).Concat(extras).ToArray();
        var shrink = LedgerBytes(entries).Length - targetBytes;
        Assert.InRange(shrink, 0, 2 * padding);
        // At most two in-memory request adjustments; no sequential reservation/write loop.
        for (var i = count - 1; shrink > 0; i--)
        {
            var removed = Math.Min(shrink, padding);
            var request = entries[i].Request with { Arguments = HostProtocol.Element(new { padding = new string('p', padding - removed) }) };
            entries[i] = entries[i] with { Request = request, RequestHash = ProtocolValidation.Digest(request) };
            shrink -= removed;
        }
        var bytes = LedgerBytes(entries);
        Assert.Equal(targetBytes, bytes.Length);
        File.WriteAllBytes(LedgerPath, bytes);
        return (new ActionCustodyStore(_root), entries);
    }
}
