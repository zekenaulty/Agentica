using System.Net;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Runtime;
using static Agentica.Lab.Web.Tests.DurableStartFixture;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HttpActionRecoveryTests
{
    [Fact]
    public async Task CancelledStatusStillRejectsHttpRecoveryUntilTaskCompletes()
    {
        await using var service = await Service.Open(_storage.FullName);
        var (run, entry) = service.Registry.CreateDurable(Submission());
        var connection = new TerminalBarrier();
        run.Attach(connection);
        run.Start();
        try
        {
            var action = await connection.Action.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null((await service.Stop(entry)).Terminal);
            await connection.Terminating.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("cancelled", HostProtocol.Element(run.Snapshot(false)).GetProperty("status").GetString());
            Assert.False(run.Terminal);
            await AssertError(await service.Recover(Fenced(action)), "recovery.live_run");
            Assert.True(run.HasUnresolvedActions);
            connection.Release.TrySetResult();
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            await AssertReply(await service.Recover(Fenced(action)), action.ActionId, duplicate: false);
            Assert.False(run.HasUnresolvedActions);
            Assert.Equal(1, service.Planners.Created);
        }
        finally { connection.Release.TrySetResult(); }
    }

    [Fact(Skip = "Windows delete-sharing failure injection", SkipUnless = nameof(IsWindows))]
    public async Task FailedCustodyCommitAfterContextWriteRemainsUnresolvedAcrossRestart()
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
            var path = Path.Combine(_storage.FullName, "custody", ActionCustodyStore.LedgerFileName);
            var before = File.ReadAllBytes(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using var response = await service.Recover(result);
                Assert.Contains(response.StatusCode, new[] { HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable });
            }
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.True(dispatched.Run.HasUnresolvedActions);
            Assert.True(service.Custody.Get("host", "session", result.ActionId).IsUnresolved);
            Assert.Null((await service.Stop(entry)).Terminal);
        }
        await using (var service = await Service.Open(_storage.FullName))
        {
            Assert.Null((await service.Stop(entry)).Terminal);
            await AssertReply(await service.Recover(result), result.ActionId, duplicate: false);
            await AssertReply(await service.Recover(result), result.ActionId, duplicate: true);
            Assert.NotNull((await service.Stop(entry)).Terminal);
            Assert.Equal(0, service.Planners.Created);
        }
    }

    private sealed class TerminalBarrier : IHostConnection
    {
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public TaskCompletionSource<HostActionRequest> Action { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Terminating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default)
        {
            if (message.Type == "action.request") Action.TrySetResult((HostActionRequest)message.Payload);
            if (message.Type is "outcome" or "run.terminated")
            {
                Terminating.TrySetResult();
                return Release.Task;
            }
            return Task.CompletedTask;
        }
    }
}
