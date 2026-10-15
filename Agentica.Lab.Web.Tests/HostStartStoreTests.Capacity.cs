using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HostStartStoreTests
{
    [Fact]
    public void Full512EntryStoreRetainsOpenAndTerminalHistoryAndAllowsOnlyExactReplay()
    {
        var store = new HostStartStore(_root);
        var original = Admit(store);
        var terminal = original with { StopRequested = true, Terminal = new("interrupted", null, true, original.AdmittedAt) };
        var entries = Enumerable.Range(0, HostStartStore.MaximumEntries).Select(i => EntryFor(terminal, i)).ToArray();
        entries[0] = original;
        File.WriteAllBytes(LedgerPath, LedgerBytes(entries));
        store = new HostStartStore(_root);
        var before = Bytes();
        var extra = Request() with { SessionId = "new-session" };
        Error("start.capacity", () => store.Admit("new-attempt", extra, extra, "new-run"));
        Assert.Equal(before, Bytes());
        Same(original, Admit(store));
        Assert.Equal(before, Bytes());
        Assert.True(store.HasOpen("host", "session"));
        Assert.True(store.IsStopRequested("host", "session-0511", "objective"));
        Assert.True(store.HasRetainedObjective("host", "session-0511", "objective"));
        Same(entries[511], store.Find(new HostStartKey("host", "session-0511", "attempt-0511"))!);
        store.RequestStop(Reference(original));
        store.SettleStopped(Reference(original), "interrupted", null, true);
        Error("start.capacity", () => store.Admit("new-attempt", extra, extra, "new-run"));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Exceeding64MiBRejectsAdmissionWithoutEvictionOrPartialWrite()
    {
        var store = new HostStartStore(_root);
        var request = Request();
        request = request with { Provider = request.Provider! with { Model = new string('m', 120000) } };
        var template = store.Admit("attempt", request, request, "run");
        var entryBytes = JsonSerializer.SerializeToUtf8Bytes(EntryFor(template, 0), HostProtocol.Json).Length;
        var emptyBytes = LedgerBytes([]).Length;
        var count = (HostStartStore.MaximumFileBytes - emptyBytes + 1) / (entryBytes + 1);
        Assert.InRange(count, 1, HostStartStore.MaximumEntries - 1);
        var entries = Enumerable.Range(0, count).Select(i => EntryFor(template, i)).ToArray();
        var bytes = LedgerBytes(entries);
        Assert.InRange(bytes.Length, HostStartStore.MaximumFileBytes - entryBytes, HostStartStore.MaximumFileBytes);
        File.WriteAllBytes(LedgerPath, bytes);
        store = new HostStartStore(_root);
        var before = FileHash();
        var oversizedAddition = request with { SessionId = "fresh", Provider = request.Provider! with { Model = new string('m', 200000) } };
        Error("start.storage_limit", () => store.Admit("fresh-attempt", oversizedAddition, oversizedAddition, "fresh-run"));
        Assert.Equal(before, FileHash());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        var found = store.Find(new HostStartKey("host", "session-0000", "attempt-0000"));
        Same(entries[0], found!);
    }
}
