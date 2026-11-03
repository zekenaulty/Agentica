using System.Text.Json;
using Agentica.Lab.Web.Contracts;

namespace Agentica.Lab.Web.Tests;

public sealed partial class HostOperationSocketTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_wake_binding_precedes_action_and_exact_terminal_retry_replays_without_planning(bool adopt)
    {
        await using var fixture = await WakeSocketFixture.Open();
        var original = await fixture.Park();
        var decision = Event(original, "decision", 1, 2);
        var wake = WakeMessage(decision, adopt);
        var socket = await fixture.Connect();
        await SendAsync(socket, wake.Type, wake.Payload, "new-wake", null, fixture.Token);
        var started = await ReceiveWakeBinding(socket, "new-wake", fixture.Token);
        var run = fixture.Registry.Get(started.GetProperty("runId").GetString()!);
        AssertWakeBinding(started, run, decision, adopt);
        Assert.NotEqual(original.RunId, run.RunId);
        var action = (await ReceiveUntilAsync(socket, "action.request", fixture.Token)).GetProperty("payload")
            .Deserialize<HostActionRequest>(HostProtocol.Json)!;
        Assert.Equal(run.RunId, action.RunId);
        Assert.Equal(adopt ? 3 : 2, action.ExpectedRevision);
        Assert.Equal(run.Request.SessionEpoch, action.SessionEpoch);
        await SendAsync(socket, "action.result", Admission(action), "wake-admission", run.RunId, fixture.Token);
        var parked = await ReceiveUntilAsync(socket, "operation.parked", fixture.Token);
        var terminal = await ReceiveUntilAsync(socket, "outcome", fixture.Token);
        await run.Completion.WaitAsync(fixture.Token);

        await SendAsync(socket, wake.Type, wake.Payload, "terminal-retry", null, fixture.Token);
        AssertWakeBinding(await ReceiveWakeBinding(socket, "terminal-retry", fixture.Token), run, decision, adopt);
        using var replayDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var parkedReplay = await ReceiveUntilAsync(socket, "operation.parked", replayDeadline.Token);
        var terminalReplay = await ReceiveUntilAsync(socket, "outcome", replayDeadline.Token);
        Assert.Equal(ProtocolValidation.Digest(parked), ProtocolValidation.Digest(parkedReplay));
        Assert.Equal(ProtocolValidation.Digest(terminal), ProtocolValidation.Digest(terminalReplay));
        Assert.Equal(2, fixture.Planners.Created);
        Assert.Equal(2, fixture.Planners.Planned);
        Assert.Equal(0, fixture.Planners.Refined);
        Assert.Equal(2, fixture.Registry.List().Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_wake_reply_retry_reconciles_original_pending_action_after_correlated_binding(bool adopt)
    {
        await using var fixture = await WakeSocketFixture.Open();
        var original = await fixture.Park();
        var run = await LoseWakeReply(fixture, original, adopt);
        var pending = Assert.Single(HostProtocol.Element(run.Snapshot(false)).GetProperty("pendingActions").EnumerateArray());
        Assert.True(pending.GetProperty("deliveryAttempted").GetBoolean());
        var retainedAction = pending.GetProperty("request").Deserialize<HostActionRequest>(HostProtocol.Json)!;
        var decision = Event(original, "decision", 1, 2);
        var wake = WakeMessage(decision, adopt);
        var retry = await fixture.Connect();
        await SendAsync(retry, wake.Type, wake.Payload, "lost-reply-retry", null, fixture.Token);
        AssertWakeBinding(await ReceiveWakeBinding(retry, "lost-reply-retry", fixture.Token), run, decision, adopt);
        using var replayDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var replay = await ReceiveUntilAsync(retry, "action.reconcile", replayDeadline.Token);
        Assert.Equal(run.RunId, replay.GetProperty("runId").GetString());
        Assert.Equal(ProtocolValidation.Digest(retainedAction), ProtocolValidation.Digest(replay.GetProperty("payload")));
        Assert.Equal(2, fixture.Planners.Created);
        Assert.Equal(2, fixture.Planners.Planned);
        Assert.Equal(0, fixture.Planners.Refined);
        Assert.Equal(2, fixture.Registry.List().Length);

        await SendAsync(retry, "action.result", Admission(retainedAction), "recovered-admission", run.RunId, fixture.Token);
        await ReceiveUntilAsync(retry, "operation.parked", fixture.Token);
        await ReceiveUntilAsync(retry, "outcome", fixture.Token);
        await run.Completion.WaitAsync(fixture.Token);
        Assert.False(run.HasUnresolvedActions);
        var retained = fixture.Operations.Get("socket-host", original.SessionId, original.ActionId);
        Assert.Equal(ProtocolValidation.Digest(decision), ProtocolValidation.Digest(retained.LatestEvent!));
        Assert.Equal(original.SessionEpoch, retained.Request.SessionEpoch);
        Assert.Equal(run.RunId, retained.WakeRunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_wake_reply_after_service_restart_is_interrupted_without_reexecution(bool adopt)
    {
        var storage = Directory.CreateTempSubdirectory("agentica-wake-restart-");
        try
        {
            HostActionRequest original;
            string successorId;
            await using (var first = await WakeSocketFixture.Open(storage))
            {
                original = await first.Park();
                successorId = (await LoseWakeReply(first, original, adopt)).RunId;
                Assert.Equal(2, first.Planners.Created);
            }
            await using var restarted = await WakeSocketFixture.Open(storage);
            var before = restarted.Operations.Get("socket-host", original.SessionId, original.ActionId);
            var socket = await restarted.Connect();
            var wake = WakeMessage(Event(original, "decision", 1, 2), adopt);
            await SendAsync(socket, wake.Type, wake.Payload, "restart-retry", null, restarted.Token);
            var error = await ReceiveMessage(socket, restarted.Token);
            Assert.Equal("error", error.GetProperty("type").GetString());
            Assert.Equal("restart-retry", error.GetProperty("requestId").GetString());
            Assert.Equal("operation.wake_interrupted", error.GetProperty("payload").GetProperty("code").GetString());
            Assert.Empty(restarted.Registry.List());
            Assert.Equal(0, restarted.Planners.Created);
            Assert.Equal(0, restarted.Planners.Planned);
            var after = restarted.Operations.Get("socket-host", original.SessionId, original.ActionId);
            Assert.Equal(successorId, after.WakeRunId);
            Assert.Equal(ProtocolValidation.Digest(before), ProtocolValidation.Digest(after));
        }
        finally { storage.Delete(recursive: true); }
    }
}
