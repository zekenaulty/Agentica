using System.Collections.Concurrent;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class ActionCustodyDurableRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HttpPinsSurviveCountPruningWhileSocketUnavailableRemainsOrdinary(bool global)
    {
        var entries = global
            ? Enumerable.Range(0, ActionCustodyStore.MaximumEntries).Select(i => Entry(i, operation: false)).ToArray()
            : PerSessionEntries();
        entries[0] = HttpPinned(entries[0]);
        var socketResult = Unavailable(entries[1]);
        entries[1] = entries[1] with { Result = socketResult, ResultHash = ProtocolValidation.Digest(socketResult) };
        var store = Seed(entries);
        if (global) Reserve(store, Entry(9999, pending: true));
        else Resolve(store, entries[^1], Unavailable(entries[^1]));
        store = new(_root);
        Assert.Equal("http", Get(store, entries[0]).RecoveryRetention);
        Assert.Equal(entries[0].ResultHash, Get(store, entries[0]).ResultHash);
        Missing(store, entries[1]);
        Assert.Equal(1, store.PrunedCompletedCount);
        Assert.False(Recover(store, entries[0], entries[0].Result!));
    }

    [Fact]
    public void HttpPinsSurviveBytePruningAndCannotPruneThemselves()
    {
        var (_, entries) = SeedByteBoundary(includeOrdinary: true);
        entries[0] = HttpPinned(entries[0]);
        var store = Seed(entries);
        var ordinary = entries.Single(e => e.Request.ActionId == "action-9000");
        Reserve(store, Entry(9999, pending: true, padding: 8192));
        store = new(_root);
        Missing(store, ordinary);
        Assert.Equal(entries[0].ResultHash, Get(store, entries[0]).ResultHash);
        Assert.Equal("http", Get(store, entries[0]).RecoveryRetention);
        Assert.Equal(1, store.PrunedCompletedCount);
    }

    [Fact]
    public void FullHttpPinnedLedgerRefusesAnotherReservationWithoutEviction()
    {
        var entries = Enumerable.Range(0, ActionCustodyStore.MaximumEntries).Select(i => HttpPinned(Entry(i))).ToArray();
        var store = Seed(entries);
        var before = FileHash();
        Error("custody.storage_limit", () => Reserve(store, Entry(9999, pending: true)));
        Assert.Equal(before, FileHash());
        Assert.Equal(0, store.PrunedCompletedCount);
        Assert.False(Recover(new(_root), entries[0], entries[0].Result!));
    }

    [Fact]
    public async Task ExhaustedRecoveryCapacityRefusesBeforeAnyActionDelivery()
    {
        var entries = Enumerable.Range(0, ActionCustodyStore.MaximumEntries).Select(i => HttpPinned(Entry(i))).ToArray();
        var store = Seed(entries);
        var before = FileHash();
        var planners = new DurableStartFixture.CountingFactory();
        using var registry = new HostRunRegistry(new HostContextStore(Path.Combine(_root, "context")), planners, store);
        var request = DurableStartFixture.Request();
        var run = registry.Create(request with { Capabilities = [request.Capabilities[0] with { DurableHandoff = false }] });
        var connection = new NoDispatchConnection();
        run.Attach(connection);
        run.Start();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(run.Terminal);
        Assert.False(run.HasUnresolvedActions);
        Assert.DoesNotContain(connection.Messages, m => m.Type is "action.request" or "action.reconcile");
        Assert.Equal(before, FileHash());
        Assert.Equal(1, planners.Created);
    }

    [Fact]
    public void ExplicitRecoveryPinsExactLegacyResultAndChangedReplayStillFails()
    {
        var entry = Entry(1, operation: false);
        var store = Seed([entry]);
        Assert.DoesNotContain("recoveryRetention", File.ReadAllText(LedgerPath), StringComparison.Ordinal);
        var original = FileHash();
        _ = new ActionCustodyStore(_root);
        Assert.Equal(original, FileHash());
        Assert.False(Recover(store, entry, entry.Result!));
        store = new(_root);
        Assert.Equal("http", Get(store, entry).RecoveryRetention);
        var pinned = FileHash();
        Assert.False(Recover(store, entry, entry.Result!));
        Assert.False(Resolve(store, entry, entry.Result!));
        Assert.Equal(pinned, FileHash());
        Error("custody.result_changed", () => Recover(store, entry, entry.Result! with { Summary = "Changed" }));
        Assert.Equal(pinned, FileHash());
    }

    [Fact]
    public void SocketResolutionReleasesReservationWithoutPinningUnavailable()
    {
        var store = new ActionCustodyStore(_root);
        var entry = Reserve(store, Entry(1, pending: true));
        Assert.Equal("reserved", entry.RecoveryRetention);
        Assert.True(Resolve(store, entry, Unavailable(entry)));
        Assert.Null(Get(new(_root), entry).RecoveryRetention);
    }

    [Theory]
    [InlineData("unknown", true)]
    [InlineData("http", true)]
    [InlineData("reserved", false)]
    public void InvalidChecksummedRetentionMetadataFailsClosed(string retention, bool pending)
    {
        var entry = Entry(1, pending: pending) with { RecoveryRetention = retention };
        Assert.Throws<InvalidDataException>(() => Seed([entry]));
    }

    [Fact]
    public void RecoveryReservationsAreCheckedOnReload()
    {
        var (_, entries) = SeedByteBoundary(includeOrdinary: false);
        entries[^1] = entries[^1] with { RecoveryRetention = "reserved" };
        Assert.Throws<InvalidDataException>(() => Seed(entries));
    }

    [Fact(Skip = "Windows delete-sharing failure injection", SkipUnless = nameof(IsWindows))]
    public void FailedHttpPinReplacementPreservesReservationAndExactRetry()
    {
        var store = new ActionCustodyStore(_root);
        var entry = Reserve(store, Entry(1, pending: true));
        var result = Unavailable(entry);
        var before = FileHash();
        using (var held = new FileStream(LedgerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => Recover(store, entry, result)) is IOException or UnauthorizedAccessException);
        Assert.Equal(before, FileHash());
        store = new(_root);
        Assert.True(Get(store, entry).IsUnresolved);
        Assert.Equal("reserved", Get(store, entry).RecoveryRetention);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        Assert.True(Recover(store, entry, result));
        Assert.False(Recover(new(_root), entry, result));
    }

    private static ActionCustodyEntry HttpPinned(ActionCustodyEntry entry)
    {
        var result = Unavailable(entry);
        return entry with { Result = result, ResultHash = ProtocolValidation.Digest(result), RecoveryRetention = "http" };
    }

    private static HostActionResult Unavailable(ActionCustodyEntry entry) => new(entry.Request.ActionId, entry.SessionId,
        entry.SessionEpoch, "unavailable", 1, 1, "fence-" + entry.Request.ActionId, "Execution unavailable under the retained host fence.");

    private static bool Recover(ActionCustodyStore store, ActionCustodyEntry entry, HostActionResult result) =>
        store.ResolveRecovered(entry.HostId, entry.SessionId, entry.Request.ActionId, result);

    private sealed class NoDispatchConnection : IHostConnection
    {
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public ConcurrentQueue<ServiceMessage> Messages { get; } = new();
        public Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Enqueue(message);
            return Task.CompletedTask;
        }
    }
}
