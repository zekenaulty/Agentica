using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class ActionCustodyStoreTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(32)]
    [InlineData(33)]
    public void RunRecoveryPagesRetainedActionsInOrdinalOrderWithoutMutation(int count)
    {
        var store = new ActionCustodyStore(_root);
        var ids = Enumerable.Range(0, count).Select(index => (index % 2 == 0 ? "a-" : "Z-") + index).ToArray();
        foreach (var id in ids.Reverse())
        {
            var request = Request(id);
            Reserve(store, request);
            store.ResolveRecovered("host", "session", id, Result(request));
        }
        var path = Path.Combine(_root, ActionCustodyStore.LedgerFileName);
        var before = File.ReadAllBytes(path);
        var written = File.GetLastWriteTimeUtc(path);
        var pruned = store.PrunedCompletedCount;
        var expected = ids.Order(StringComparer.Ordinal).ToArray();
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = store.ListRetainedForRun("host", "session", "epoch", "run", cursor);
            Assert.Equal(Math.Min(16, count - seen.Count), page.Items.Count);
            seen.AddRange(page.Items.Select(item => item.ActionId));
            Assert.All(page.Items, item => { Assert.Equal("applied", item.Disposition); Assert.Equal("http", item.RecoveryRetention); });
            Assert.Equal(seen.Count < count ? seen[^1] : null, page.NextAfterActionId);
            cursor = page.NextAfterActionId;
        } while (cursor is not null);
        Assert.Equal(expected, seen);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(pruned, store.PrunedCompletedCount);
        Assert.Empty(store.ListRetainedForRun("host", "session", "epoch", "run", "zzzz").Items);
    }

    [Fact]
    public void RunRecoveryCursorIsAnExclusiveKeyNotAnOffsetOrRequiredStoredAction()
    {
        var store = new ActionCustodyStore(_root);
        foreach (var id in new[] { "a", "c", "e" })
        {
            var request = Request(id);
            Reserve(store, request);
            store.Resolve("host", "session", id, Result(request));
        }
        var first = store.ListRetainedForRun("host", "session", "epoch", "run");
        var added = Request("b");
        Reserve(store, added);
        store.Resolve("host", "session", added.ActionId, Result(added));
        Assert.Equal(new[] { "e" }, store.ListRetainedForRun("host", "session", "epoch", "run", "c").Items.Select(item => item.ActionId));
        Assert.Equal(new[] { "e" }, store.ListRetainedForRun("host", "session", "epoch", "run", "d").Items.Select(item => item.ActionId));
        Assert.Equal(new[] { "a", "c", "e" }, first.Items.Select(item => item.ActionId));
        Assert.All(first.Items, item => Assert.Null(item.RecoveryRetention));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RunRecoveryValidatesEveryKeyBeforeReadingTheLedger(int field)
    {
        var store = new ActionCustodyStore(_root);
        var path = Path.Combine(_root, ActionCustodyStore.LedgerFileName);
        File.WriteAllText(path, "invalid");
        foreach (var invalid in new string?[] { null, "", " ", "bad\nkey", "bad\tkey", new('x', 129) })
        {
            if (field == 4 && invalid is null) continue;
            string?[] keys = ["host", "session", "epoch", "run", "cursor"];
            keys[field] = invalid;
            var error = Assert.Throws<HostProtocolException>(() => store.ListRetainedForRun(keys[0]!, keys[1]!, keys[2]!, keys[3]!, keys[4]));
            Assert.Equal("input.invalid", error.Code);
        }
        Assert.Throws<InvalidDataException>(() => store.ListRetainedForRun("host", "session", "epoch", "run"));
        Assert.Equal("invalid", File.ReadAllText(path));
    }
}
