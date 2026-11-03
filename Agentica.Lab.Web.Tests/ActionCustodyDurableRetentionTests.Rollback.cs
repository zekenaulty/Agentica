using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class ActionCustodyDurableRetentionTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Windows delete-sharing failure injection", SkipUnless = nameof(IsWindows))]
    public void FailedReplacementDoesNotCommitPruningOrClearOriginalCustody()
    {
        var entries = PerSessionEntries();
        var store = Seed(entries);
        var pending = entries[^1];
        var result = Result(pending, operation: false);
        var before = FileHash();
        using (var held = new FileStream(LedgerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => Resolve(store, pending, result)) is IOException or UnauthorizedAccessException);
        Assert.Equal(before, FileHash());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        store = new ActionCustodyStore(_root);
        AssertAdmissions(entries, ReadEntries());
        Assert.NotNull(Get(store, entries[1]).Result);
        Assert.True(Get(store, pending).IsUnresolved);
        Assert.Equal(0, store.PrunedCompletedCount);

        Assert.True(Resolve(store, pending, result));
        store = new ActionCustodyStore(_root);
        AssertAdmissions(entries, ReadEntries());
        Missing(store, entries[1]);
        Assert.False(Get(store, pending).IsUnresolved);
        Assert.Equal(1, store.PrunedCompletedCount);
    }
}
