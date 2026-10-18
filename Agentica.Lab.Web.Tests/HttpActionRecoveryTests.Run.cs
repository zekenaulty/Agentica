using System.Net.Http.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HttpActionRecoveryTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(33)]
    public async Task RunDiscoveryPagesOrdinaryHttpAndPendingEvidenceWithoutEffects(int count)
    {
        await using var service = await Service.Open(_storage.FullName);
        var expected = new List<ActionCustodyEntry>();
        for (var index = count - 2; index >= 0; index--)
        {
            var id = (index % 2 == 0 ? "a-" : "Z-") + index;
            var entry = ReserveRunAction(service, id);
            if (index % 2 == 0) service.Custody.ResolveRecovered("host", "session", id, Fenced(entry.Request));
            else service.Custody.Resolve("host", "session", id, Fenced(entry.Request));
            expected.Add(service.Custody.Get("host", "session", id));
        }
        expected.Add(ReserveRunAction(service, "zz-pending"));
        expected = expected.OrderBy(entry => entry.Request.ActionId, StringComparer.Ordinal).ToList();
        var seen = new List<HostRunRecoveryItem>();
        string? cursor = null;
        do
        {
            var page = await ReadRunPage(service, afterActionId: cursor);
            Assert.Equal(Math.Min(16, count - seen.Count), page.Items.Count);
            seen.AddRange(page.Items);
            Assert.Equal(seen.Count < count ? seen[^1].ActionId : null, page.NextAfterActionId);
            cursor = page.NextAfterActionId;
        } while (cursor is not null);
        for (var index = 0; index < count; index++) AssertRunItem(expected[index], seen[index]);
        var exhausted = await ReadRunPage(service, afterActionId: "zz-pending");
        Assert.Empty(exhausted.Items);
        Assert.Null(exhausted.NextAfterActionId);
        Assert.True(service.Custody.HasUnresolved("host", "session"));
        Assert.Empty(service.Registry.List());
        Assert.Equal(0, service.Planners.Created);
    }

    [Fact]
    public async Task RunDiscoveryRediscoversCompletedHttpReceiptAfterLostReplyAndRestart()
    {
        ActionCustodyEntry retained;
        await using (var service = await Service.Open(_storage.FullName))
        {
            var (run, entry, action) = await service.Dispatch();
            var original = service.Custody.Get("host", "session", action.ActionId);
            AssertRunItem(original, Assert.Single((await ReadRunPage(service, runId: action.RunId)).Items));
            Assert.True(run.HasUnresolvedActions);
            Assert.Null((await service.Stop(entry)).Terminal);
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            // Discard the acknowledgement body; rediscovery must use only the persisted run scope.
            using (var ignored = await service.Recover(Result(action))) ignored.EnsureSuccessStatusCode();
            retained = service.Custody.Get("host", "session", action.ActionId);
            Assert.Equal("http", retained.RecoveryRetention);
            Assert.Equal("applied", retained.Result!.Disposition);
            Assert.Equal(original.RequestHash, retained.RequestHash);
            Assert.Equal(original.CapabilityHash, retained.CapabilityHash);
            AssertRunItem(retained, Assert.Single((await ReadRunPage(service, runId: action.RunId)).Items));
            Assert.False(run.HasUnresolvedActions);
            Assert.Equal(1, service.Planners.Created);
        }
        await using (var service = await Service.Open(_storage.FullName))
        {
            Assert.Empty((await service.Http.GetFromJsonAsync<ActionCustodyEntry[]>(
                "api/recovery?hostId=host&sessionId=session", HostProtocol.Json))!);
            var page = await ReadRunPage(service, runId: retained.RunId);
            AssertRunItem(retained, Assert.Single(page.Items));
            Assert.Null(page.NextAfterActionId);
            await AssertLookup(service, retained);
            Assert.Empty(service.Registry.List());
            Assert.Equal(0, service.Planners.Created);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunDiscoveryAfterRestartPreservesPendingOrUnresolvedFence(bool reportedUnresolved)
    {
        ActionCustodyEntry retained;
        await using (var service = await Service.Open(_storage.FullName))
        {
            retained = ReserveRunAction(service, "pending");
            if (reportedUnresolved)
            {
                using var response = await service.Recover(Fenced(retained.Request) with { Disposition = "unresolved" });
                response.EnsureSuccessStatusCode();
                retained = service.Custody.Get("host", "session", "pending");
            }
        }
        await using (var service = await Service.Open(_storage.FullName))
        {
            var item = Assert.Single((await ReadRunPage(service)).Items);
            AssertRunItem(retained, item);
            Assert.Equal(reportedUnresolved ? "unresolved" : null, item.Disposition);
            Assert.Equal("reserved", item.RecoveryRetention);
            Assert.True(service.Custody.HasUnresolved("host", "session"));
            Assert.Empty(service.Registry.List());
            Assert.Equal(0, service.Planners.Created);
        }
    }
}
