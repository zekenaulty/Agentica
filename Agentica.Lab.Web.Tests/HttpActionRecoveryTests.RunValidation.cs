using System.Net;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HttpActionRecoveryTests
{
    [Fact]
    public async Task RunDiscoveryFiltersEveryScopeFieldOrdinallyWithoutEffects()
    {
        await using var service = await Service.Open(_storage.FullName);
        var foreign = new[]
        {
            ReserveRunAction(service, "same-action", hostId: "Host"),
            ReserveRunAction(service, "same-action", sessionId: "Session"),
            ReserveRunAction(service, "foreign-epoch", sessionEpoch: "Epoch")
        };
        service.Custody.ResolveRecovered("host", "session", "foreign-epoch", Fenced(foreign[2].Request));
        var foreignRun = ReserveRunAction(service, "foreign-run", runId: "Run");
        service.Custody.Resolve("host", "session", "foreign-run", Fenced(foreignRun.Request));
        var original = ReserveRunAction(service, "same-action");
        AssertRunItem(original, Assert.Single((await ReadRunPage(service)).Items));
        foreach (var entry in foreign.Append(foreignRun))
        {
            var stored = service.Custody.Get(entry.HostId, entry.SessionId, entry.Request.ActionId);
            var page = await ReadRunPage(service, entry.HostId, entry.SessionId, entry.SessionEpoch, entry.RunId);
            AssertRunItem(stored, Assert.Single(page.Items));
        }
        foreach (var scope in new[]
        {
            ("unknown", "session", "epoch", "run"), ("host", "unknown", "epoch", "run"),
            ("host", "session", "unknown", "run"), ("host", "session", "epoch", "unknown"),
            ("Host", "Session", "epoch", "run")
        })
        {
            var page = await ReadRunPage(service, scope.Item1, scope.Item2, scope.Item3, scope.Item4);
            Assert.Empty(page.Items);
            Assert.Null(page.NextAfterActionId);
        }
        Assert.True(service.Custody.HasUnresolved("host", "session"));
        Assert.Empty(service.Registry.List());
        Assert.Equal(0, service.Planners.Created);
    }

    [Theory]
    [InlineData("hostId")]
    [InlineData("sessionId")]
    [InlineData("sessionEpoch")]
    [InlineData("runId")]
    [InlineData("afterActionId")]
    public async Task RunDiscoveryRejectsInvalidKeysBeforeReadingStorage(string field)
    {
        await using var service = await Service.Open(_storage.FullName);
        ReserveRunAction(service, "original");
        File.WriteAllText(Path.Combine(_storage.FullName, "custody", ActionCustodyStore.LedgerFileName), "invalid");
        var before = StorageSnapshot();
        foreach (var invalid in new string?[] { null, "", " ", "bad\nidentifier", "bad\tidentifier", new('x', 129) })
        {
            if (field == "afterActionId" && invalid is null) continue;
            var fields = new Dictionary<string, string> { ["hostId"] = "host", ["sessionId"] = "session",
                ["sessionEpoch"] = "epoch", ["runId"] = "run", ["afterActionId"] = "cursor" };
            if (invalid is null) fields.Remove(field);
            else fields[field] = invalid;
            var query = string.Join("&", fields.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
            using var response = await service.Http.GetAsync("api/recovery/run?" + query);
            Assert.Equal(invalid is null ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, response.StatusCode);
            if (invalid is not null) await AssertError(response, "input.invalid");
        }
        using var corrupt = await service.Http.GetAsync("api/recovery/run?" + RunQuery("host", "session", "epoch", "run"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupt.StatusCode);
        await AssertError(corrupt, "storage.unavailable");
        Assert.Equal(before, StorageSnapshot());
        Assert.Empty(service.Registry.List());
        Assert.Equal(0, service.Planners.Created);
    }
}
