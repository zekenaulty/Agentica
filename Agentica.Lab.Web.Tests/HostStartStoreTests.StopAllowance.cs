using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HostStartStoreTests
{
    private const int StopAllowanceBytes = 512;

    [Fact]
    public void ExactlyFundedLedgerCanSettleBothStopShapesAndReloadWithoutEviction()
    {
        var store = new HostStartStore(_root);
        var template = Admit(store);
        var entries = WriteStopAllowanceBoundary(template);
        var first = entries[0];
        var second = entries[1];
        Assert.False(first.StopRequested);
        Assert.True(second.StopRequested);
        Assert.Equal(HostStartStore.MaximumFileBytes, new FileInfo(LedgerPath).Length + 2 * StopAllowanceBytes);
        store = new HostStartStore(_root);

        var stopped = store.RequestStop(Reference(first));
        Assert.True(stopped.StopRequested);
        Assert.Null(stopped.Terminal);
        Assert.Equal(HostStartStore.MaximumFileBytes - 1L, new FileInfo(LedgerPath).Length + 2 * StopAllowanceBytes);
        store = new HostStartStore(_root);
        Assert.True(store.HasOpen(first.OriginalRequest.HostId, first.OriginalRequest.SessionId));
        var settled = store.SettleStopped(Reference(first), "stopped", MaximumUsage(), false);
        Assert.True(new FileInfo(LedgerPath).Length + StopAllowanceBytes <= HostStartStore.MaximumFileBytes);
        store = new HostStartStore(_root);
        Same(settled, store.Find(new("host", first.OriginalRequest.SessionId, first.AttemptId))!);
        Assert.True(store.HasOpen(second.OriginalRequest.HostId, second.OriginalRequest.SessionId));

        var interrupted = store.SettleStopped(Reference(second), "interrupted", null, true);
        Assert.True(new FileInfo(LedgerPath).Length <= HostStartStore.MaximumFileBytes);
        store = new HostStartStore(_root);
        Same(interrupted, store.Find(new("host", second.OriginalRequest.SessionId, second.AttemptId))!);
        Same(entries[^1], store.Find(new("host", entries[^1].OriginalRequest.SessionId, entries[^1].AttemptId))!);
        Assert.False(store.HasOpen("host", first.OriginalRequest.SessionId));
        Assert.False(store.HasOpen("host", second.OriginalRequest.SessionId));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneUnfundedByteFencesReloadAndExistingStoreEvenAfterStopRequested(bool stopRequested)
    {
        var store = new HostStartStore(_root);
        var template = Admit(store);
        template = Rebind(template, template.OriginalRequest with { Objective = "Retain <original> evidence." });
        var entries = WriteStopAllowanceBoundary(template, excessBytes: 1, stopRequested);
        if (stopRequested)
        {
            // Equivalent, more compact JSON must not hide an unfunded normalized rewrite.
            File.WriteAllText(LedgerPath, File.ReadAllText(LedgerPath).Replace("\\u003C", "<", StringComparison.Ordinal));
            Assert.True(new FileInfo(LedgerPath).Length + 2 * StopAllowanceBytes < HostStartStore.MaximumFileBytes);
        }
        Assert.True(new FileInfo(LedgerPath).Length < HostStartStore.MaximumFileBytes);
        var before = FileHash();
        Assert.Throws<InvalidDataException>(() => new HostStartStore(_root));
        Assert.Throws<InvalidDataException>(() => store.Find(new("host", entries[0].OriginalRequest.SessionId, entries[0].AttemptId)));
        Assert.Throws<InvalidDataException>(() => store.RequestStop(Reference(entries[0])));
        Assert.Throws<InvalidDataException>(() => store.SettleStopped(Reference(entries[1]), "interrupted", null, true));
        Assert.Equal(before, FileHash());
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void AllowanceCoversMaximumSerializedTerminalGrowth()
    {
        var entry = Admit(new HostStartStore(_root));
        var before = JsonSerializer.SerializeToUtf8Bytes(entry, HostProtocol.Json).Length;
        foreach (var terminal in new[]
        {
            new HostStartTerminal("stopped", MaximumUsage(), false, DateTimeOffset.MaxValue),
            new HostStartTerminal("interrupted", null, true, DateTimeOffset.MaxValue)
        })
        {
            var after = JsonSerializer.SerializeToUtf8Bytes(entry with { StopRequested = true, Terminal = terminal }, HostProtocol.Json).Length;
            Assert.InRange(after - before, 1, StopAllowanceBytes);
        }
    }

    private static HostRunUsage MaximumUsage() => new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);

    private HostStartEntry[] WriteStopAllowanceBoundary(HostStartEntry template, int excessBytes = 0, bool stopRequested = false)
    {
        var request = RequestSized(template.OriginalRequest with { SessionId = "session-0000" }, HostStartStore.MaximumRequestBytes);
        var terminal = Rebind(template, request) with
        {
            StopRequested = true,
            Terminal = new("stopped", MaximumUsage(), false, template.AdmittedAt)
        };
        var entryBytes = JsonSerializer.SerializeToUtf8Bytes(EntryFor(terminal, 0), HostProtocol.Json).Length;
        var count = (HostStartStore.MaximumFileBytes - LedgerBytes([]).Length) / (entryBytes + 1) + 1;
        Assert.InRange(count, 3, HostStartStore.MaximumEntries);
        var entries = Enumerable.Range(0, count).Select(i => EntryFor(terminal, i)).ToArray();
        entries[0] = entries[0] with { StopRequested = stopRequested, Terminal = null };
        entries[1] = entries[1] with { Terminal = null };
        var targetBytes = HostStartStore.MaximumFileBytes - 2 * StopAllowanceBytes + excessBytes;
        var shrink = LedgerBytes(entries).Length - targetBytes;
        Assert.True(shrink >= 0);
        // Both requests shrink together; one ASCII run-id byte adjusts an odd remainder.
        entries[^1] = Rebind(entries[^1], RequestSized(entries[^1].OriginalRequest,
            HostStartStore.MaximumRequestBytes - (shrink + 1) / 2));
        if (shrink % 2 != 0) entries[^1] = entries[^1] with { RunId = entries[^1].RunId + "x" };
        var bytes = LedgerBytes(entries);
        Assert.Equal(targetBytes, bytes.Length);
        File.WriteAllBytes(LedgerPath, bytes);
        return entries;
    }
}
