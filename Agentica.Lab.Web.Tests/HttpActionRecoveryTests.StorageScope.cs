namespace Agentica.Lab.Web.Tests;

public sealed partial class HttpActionRecoveryTests
{
    [Fact]
    public void StorageSnapshotExcludesChatLeaseButRetainsEveryHostStoreFile()
    {
        var hostFiles = new[] { "custody", "operations", "starts", "context" }
            .SelectMany(directory =>
            {
                var root = Directory.CreateDirectory(Path.Combine(_storage.FullName, directory)).FullName;
                var nested = Directory.CreateDirectory(Path.Combine(root, "pending")).FullName;
                var paths = new[] { Path.Combine(root, "ledger.json"), Path.Combine(root, "store.initialized"),
                    Path.Combine(nested, "write.tmp") };
                foreach (var path in paths) File.WriteAllText(path, "original host state");
                return paths;
            }).Order(StringComparer.Ordinal).ToArray();
        var chat = Directory.CreateDirectory(Path.Combine(_storage.FullName, "chat")).FullName;
        File.WriteAllText(Path.Combine(chat, "experiments.db"), "unrelated chat state");
        using var lease = new FileStream(Path.Combine(chat, "service.lock"), FileMode.Create,
            FileAccess.ReadWrite, FileShare.None);

        var before = StorageSnapshot();
        Assert.Equal(hostFiles, before.Select(entry => entry.Path));
        File.WriteAllText(Path.Combine(chat, "experiments.db"), "changed chat state");
        Assert.Equal(before, StorageSnapshot());

        foreach (var path in hostFiles)
        {
            File.WriteAllText(path, "changed host state");
            var changed = StorageSnapshot();
            Assert.NotEqual(before.Single(entry => entry.Path == path).Hash,
                changed.Single(entry => entry.Path == path).Hash);
            File.WriteAllText(path, "original host state");
        }

        var added = Path.Combine(_storage.FullName, "custody", "new.tmp");
        File.WriteAllText(added, "unexpected host write");
        Assert.Contains(StorageSnapshot(), entry => entry.Path == added);
        File.Delete(hostFiles[0]);
        Assert.DoesNotContain(StorageSnapshot(), entry => entry.Path == hostFiles[0]);
    }
}
