using System.Net;
using System.Net.Http.Json;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HttpActionRecoveryTests
{
    [Fact]
    public async Task TerminalHttpRecoveryClearsMemoryAndReplaysAfterLostReplyAndRestart()
    {
        HostActionResult result;
        HostStartEntry entry;
        await using (var service = await Service.Open(_storage.FullName))
        {
            var dispatched = await service.Dispatch();
            var run = dispatched.Run;
            entry = dispatched.Entry;
            result = Fenced(dispatched.Action);
            await AssertError(await service.Recover(result), "recovery.live_run");
            Assert.Null((await service.Stop(entry)).Terminal);
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(run.HasUnresolvedActions);
            await AssertError(await service.Recover(result with { SessionEpoch = "wrong" }), "action.session");
            await AssertError(await service.Recover(result, hostId: "wrong"), "custody.not_found");
            await AssertError(await service.Recover(result with { BeforeRevision = -1 }), "action.revision");
            await AssertError(await service.Recover(result with { Disposition = "refused", BeforeRevision = 0, AfterRevision = 0 }), "action.observation");
            // Discard the response body: subsequent reconciliation must not depend on receiving its acknowledgement.
            using (var ignored = await service.Recover(result)) ignored.EnsureSuccessStatusCode();
            Assert.False(run.HasUnresolvedActions);
            var retained = service.Custody.Get("host", "session", result.ActionId);
            Assert.Equal("http", retained.RecoveryRetention);
            Assert.Equal(ProtocolValidation.Digest(result), retained.ResultHash);
            await AssertReply(await service.Recover(result), result.ActionId, duplicate: true);
            await AssertError(await service.Recover(result with { Summary = "changed" }), "action.duplicate_changed");
            Assert.NotNull((await service.Stop(entry)).Terminal);
            Assert.Equal(1, service.Planners.Created);
        }
        await using (var service = await Service.Open(_storage.FullName))
        {
            await AssertReply(await service.Recover(result), result.ActionId, duplicate: true);
            await AssertError(await service.Recover(result with { Summary = "changed" }), "custody.result_changed");
            Assert.Empty((await service.Http.GetFromJsonAsync<ActionCustodyEntry[]>("api/recovery?hostId=host&sessionId=session", HostProtocol.Json))!);
            Assert.NotNull((await service.Stop(entry)).Terminal);
            Assert.Equal(0, service.Planners.Created);
        }
    }

    [Fact]
    public async Task RestartBeforeRecoveryPinsTheOriginalWithoutReactivatingPlanner()
    {
        HostActionResult result;
        HostStartEntry entry;
        await using (var service = await Service.Open(_storage.FullName))
        {
            var dispatched = await service.Dispatch();
            entry = dispatched.Entry;
            result = Fenced(dispatched.Action);
            Assert.Null((await service.Stop(entry)).Terminal);
            await dispatched.Run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("reserved", service.Custody.Get("host", "session", result.ActionId).RecoveryRetention);
        }
        await using (var service = await Service.Open(_storage.FullName))
        {
            Assert.Null((await service.Stop(entry)).Terminal);
            await AssertReply(await service.Recover(result), result.ActionId, duplicate: false);
            await AssertReply(await service.Recover(result), result.ActionId, duplicate: true);
            Assert.Equal("http", service.Custody.Get("host", "session", result.ActionId).RecoveryRetention);
            Assert.NotNull((await service.Stop(entry)).Terminal);
            Assert.Equal(0, service.Planners.Created);
        }
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Windows delete-sharing failure injection", SkipUnless = nameof(IsWindows))]
    public async Task InMemoryDuplicateMustPersistHttpPinBeforeReportingSuccess()
    {
        await using var service = await Service.Open(_storage.FullName);
        var (run, entry, action) = await service.Dispatch();
        var result = Fenced(action);
        Assert.Null((await service.Stop(entry)).Terminal);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(run.AcceptResult(result));
        Assert.Null(service.Custody.Get("host", "session", action.ActionId).RecoveryRetention);
        var path = Path.Combine(_storage.FullName, "custody", ActionCustodyStore.LedgerFileName);
        var before = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using var response = await service.Recover(result);
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable });
        }
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Null(service.Custody.Get("host", "session", action.ActionId).RecoveryRetention);
        await AssertReply(await service.Recover(result), action.ActionId, duplicate: true);
        Assert.Equal("http", service.Custody.Get("host", "session", action.ActionId).RecoveryRetention);
        Assert.False(run.HasUnresolvedActions);
    }
}
