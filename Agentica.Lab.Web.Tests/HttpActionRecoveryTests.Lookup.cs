using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HttpActionRecoveryTests
{
    [Fact]
    public async Task LookupReturnsExactOriginalAndResultAfterLostReplyAndRestart()
    {
        ActionCustodyEntry retained;
        await using (var service = await Service.Open(_storage.FullName))
        {
            var (run, entry, action) = await service.Dispatch();
            var original = service.Custody.Get("host", "session", action.ActionId);
            await AssertLookup(service, original);
            Assert.True(run.HasUnresolvedActions);
            Assert.Null((await service.Stop(entry)).Terminal);
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            var result = Result(action);
            using (var ignored = await service.Recover(result)) ignored.EnsureSuccessStatusCode();
            retained = service.Custody.Get("host", "session", action.ActionId);
            Assert.Equal("http", retained.RecoveryRetention);
            Assert.Equal(ProtocolValidation.Digest(result), retained.ResultHash);
            Assert.Equal(original.RequestHash, retained.RequestHash);
            Assert.Equal(original.CapabilityHash, retained.CapabilityHash);
            await AssertLookup(service, retained);
            Assert.False(run.HasUnresolvedActions);
            Assert.Equal(1, service.Planners.Created);
        }
        await using (var service = await Service.Open(_storage.FullName))
        {
            await AssertLookup(service, retained);
            Assert.Empty(service.Registry.List());
            Assert.Equal(0, service.Planners.Created);
            Assert.Empty((await service.Http.GetFromJsonAsync<ActionCustodyEntry[]>(
                "api/recovery?hostId=host&sessionId=session", HostProtocol.Json))!);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LookupOfPendingOriginalAfterRestartDoesNotActivateOrResolve(bool reportedUnresolved)
    {
        ActionCustodyEntry retained;
        await using (var service = await Service.Open(_storage.FullName))
        {
            retained = ReserveLookup(service);
            if (reportedUnresolved)
            {
                var result = Fenced(retained.Request) with { Disposition = "unresolved" };
                using var response = await service.Recover(result);
                response.EnsureSuccessStatusCode();
                retained = service.Custody.Get(retained.HostId, retained.SessionId, retained.Request.ActionId);
            }
        }
        await using (var service = await Service.Open(_storage.FullName))
        {
            await AssertLookup(service, retained);
            Assert.True(service.Custody.HasUnresolved(retained.HostId, retained.SessionId));
            Assert.Equal("reserved", retained.RecoveryRetention);
            Assert.Empty(service.Registry.List());
            Assert.Equal(0, service.Planners.Created);
        }
    }

    [Fact]
    public async Task LookupScopesTheSameActionIdToExactHostAndSession()
    {
        await using var service = await Service.Open(_storage.FullName);
        var original = ReserveLookup(service);
        var otherHost = ReserveLookup(service, hostId: "other-host");
        var otherSession = ReserveLookup(service, sessionId: "other-session");
        foreach (var entry in new[] { original, otherHost, otherSession }) await AssertLookup(service, entry);
        var before = StorageSnapshot();
        foreach (var query in new[]
        {
            "hostId=unknown&sessionId=session&actionId=original-action",
            "hostId=host&sessionId=unknown&actionId=original-action",
            "hostId=host&sessionId=session&actionId=unknown",
            "hostId=other-host&sessionId=other-session&actionId=original-action"
        })
        {
            var response = await service.Http.GetAsync("api/recovery/action?" + query);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            await AssertError(response, "custody.not_found");
        }
        Assert.Equal(before, StorageSnapshot());
        Assert.Empty(service.Registry.List());
        Assert.Equal(0, service.Planners.Created);
    }

    [Theory]
    [InlineData("hostId")]
    [InlineData("sessionId")]
    [InlineData("actionId")]
    public async Task LookupRequiresValidIdentifiersBeforeReadingStorage(string field)
    {
        await using var service = await Service.Open(_storage.FullName);
        ReserveLookup(service);
        // A corrupt ledger proves validation happens before the durable read, not just before lookup.
        File.WriteAllText(Path.Combine(_storage.FullName, "custody", ActionCustodyStore.LedgerFileName), "invalid");
        var before = StorageSnapshot();
        foreach (var invalid in new string?[] { null, "", " ", "bad\nidentifier", new('x', 129) })
        {
            var fields = new Dictionary<string, string> { ["hostId"] = "host", ["sessionId"] = "session", ["actionId"] = "original-action" };
            if (invalid is null) fields.Remove(field);
            else fields[field] = invalid;
            var query = string.Join("&", fields.Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value)));
            using var response = await service.Http.GetAsync("api/recovery/action?" + query);
            Assert.Equal(invalid is null ? HttpStatusCode.BadRequest : HttpStatusCode.Conflict, response.StatusCode);
            if (invalid is not null) await AssertError(response, "input.invalid");
        }
        await AssertError(await service.Http.GetAsync(
            "api/recovery/action?hostId=host&sessionId=session&actionId=original-action"), "storage.unavailable");
        Assert.Equal(before, StorageSnapshot());
        Assert.Empty(service.Registry.List());
        Assert.Equal(0, service.Planners.Created);
    }

    private async Task AssertLookup(Service service, ActionCustodyEntry expected)
    {
        var before = StorageSnapshot();
        var planners = service.Planners.Created;
        var runs = service.Registry.List().Length;
        var query = $"hostId={Uri.EscapeDataString(expected.HostId)}&sessionId={Uri.EscapeDataString(expected.SessionId)}&actionId={Uri.EscapeDataString(expected.Request.ActionId)}";
        using var response = await service.Http.GetAsync("api/recovery/action?" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var actual = await response.Content.ReadFromJsonAsync<ActionCustodyEntry>(HostProtocol.Json);
        Assert.NotNull(actual);
        Assert.Equal(ProtocolValidation.Digest(expected), ProtocolValidation.Digest(actual));
        Assert.Equal(before, StorageSnapshot());
        Assert.Equal(planners, service.Planners.Created);
        Assert.Equal(runs, service.Registry.List().Length);
    }

    private static ActionCustodyEntry ReserveLookup(Service service, string hostId = "host", string sessionId = "session")
    {
        var request = Request() with { HostId = hostId, SessionId = sessionId };
        var action = new HostActionRequest("original-action", "original-run", "original-runner", "original-step",
            sessionId, request.SessionEpoch, request.Capabilities[0].Id, ProtocolValidation.Digest(request.Capabilities),
            HostProtocol.Element(new { value = "original arguments" }), request.Observation.Revision, DateTimeOffset.UtcNow.AddMinutes(1));
        return service.Custody.Reserve(hostId, sessionId, request.SessionEpoch, action.RunId, action, request.Capabilities[0], request.ObjectiveId);
    }

    private (string Path, string Hash, DateTime Written)[] StorageSnapshot() =>
        Directory.GetFiles(_storage.FullName, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => (path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), File.GetLastWriteTimeUtc(path))).ToArray();
}
