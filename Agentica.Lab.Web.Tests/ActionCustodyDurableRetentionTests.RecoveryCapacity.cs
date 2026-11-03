using System.Text.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class ActionCustodyDurableRetentionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservedRecoveryFitsNearByteCapIncludingLargeIntermediateResult(bool intermediate)
    {
        var (store, entries) = FundedBoundary();
        var pending = entries[^1];
        var before = FileHash();
        Error("custody.storage_limit", () => Reserve(store, Entry(9999, pending: true)));
        Assert.Equal(before, FileHash());
        var final = LargeResult(pending);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(final, HostProtocol.Json).Length > 100_000);
        if (intermediate)
        {
            var unknown = final with { Disposition = "unresolved", Completion = null };
            Assert.True(Recover(store, pending, unknown));
            store = new(_root);
            Assert.Equal("reserved", Get(store, pending).RecoveryRetention);
            Assert.True(Get(store, pending).IsUnresolved);
        }
        Assert.True(Recover(store, pending, final));
        store = new(_root);
        Assert.Equal("http", Get(store, pending).RecoveryRetention);
        Assert.False(Get(store, pending).IsUnresolved);
        Assert.False(Recover(store, pending, final));
        Assert.Equal(0, store.PrunedCompletedCount);
        Assert.True(new FileInfo(LedgerPath).Length <= ActionCustodyStore.MaximumFileBytes);
        AssertAdmissions(entries, ReadEntries());
    }

    [Fact]
    public void LegacyUnfundedRecoveryFailsAtomicallyInsteadOfEvictingItsReceipt()
    {
        var (store, entries) = SeedByteBoundary(includeOrdinary: false);
        var pending = entries[^1];
        var before = FileHash();
        Error("custody.storage_limit", () => Recover(store, pending, LargeResult(pending)));
        Assert.Equal(before, FileHash());
        Assert.True(Get(new(_root), pending).IsUnresolved);
        Assert.Null(Get(store, pending).RecoveryRetention);
    }

    private (ActionCustodyStore Store, ActionCustodyEntry[] Entries) FundedBoundary()
    {
        var (_, entries) = SeedByteBoundary(includeOrdinary: false);
        entries[^1] = entries[^1] with { RecoveryRetention = "reserved" };
        var pending = entries[^1];
        var allowance = HostProtocol.MaxMessageBytes + 512
            - JsonSerializer.SerializeToUtf8Bytes(pending.Result, HostProtocol.Json).Length
            - JsonSerializer.SerializeToUtf8Bytes(pending.ResultHash, HostProtocol.Json).Length
            - JsonSerializer.SerializeToUtf8Bytes(pending.UpdatedAt, HostProtocol.Json).Length;
        var payload = new FixturePayload(File.ReadAllText(Path.Combine(_root, "action-custody-v1.initialized")), entries, 0);
        var envelopeBytes = LedgerBytes(entries).Length - JsonSerializer.SerializeToUtf8Bytes(payload, HostProtocol.Json).Length;
        // Exactly fill the conservative admission charge, not just the physical file limit.
        var target = ActionCustodyStore.MaximumFileBytes - allowance - (256 - envelopeBytes);
        var shrink = LedgerBytes(entries).Length - target;
        for (var i = 0; shrink > 0; i++)
        {
            var padding = entries[i].Request.Arguments.GetProperty("padding").GetString()!.Length;
            var removed = Math.Min(shrink, padding);
            var request = entries[i].Request with { Arguments = HostProtocol.Element(new { padding = new string('p', padding - removed) }) };
            entries[i] = entries[i] with { Request = request, RequestHash = ProtocolValidation.Digest(request) };
            shrink -= removed;
        }
        Assert.Equal(target, LedgerBytes(entries).Length);
        return (Seed(entries), entries);
    }

    private static HostActionResult LargeResult(ActionCustodyEntry entry)
    {
        var observation = new HostObservation("large-observation", 1, Time, HostProtocol.Element(new { padding = "" }));
        var padding = 65_536 - JsonSerializer.SerializeToUtf8Bytes(observation, HostProtocol.Json).Length;
        observation = observation with { Data = HostProtocol.Element(new { padding = new string('r', padding) }) };
        return new(entry.Request.ActionId, entry.SessionId, entry.SessionEpoch, "applied", 0, 1,
            "result-" + entry.Request.ActionId, new string('\u4e00', 4000), observation,
            new(entry.ObjectiveId, "complete-" + entry.Request.ActionId, new string('\u4e00', 4000)));
    }
}
