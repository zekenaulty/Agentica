using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

public sealed class HostDecisionStopTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_after_reload_retains_exact_decision_without_appending_history_or_claiming_a_successor(bool delivered)
    {
        await using var harness = new StopHarness();
        var action = harness.Seed();
        harness.Operations.Apply(Event(action, "progress", 1, 2));
        harness.Operations.Apply(Event(action, "transfer", 2, 3) with { NextSessionEpoch = "transferred-epoch" });
        var decision = Event(action, "decision", 3, 4) with { SessionEpoch = "transferred-epoch" };
        if (delivered) harness.Operations.Apply(decision, "original-wake");
        harness.Restart();
        var before = harness.Get(action);
        var sources = RetainedSources(harness.LedgerPath);

        var stopped = harness.Operations.StopDecision(decision);

        AssertStopped(before, decision, stopped);
        Assert.Equal(delivered ? "original-wake" : null, stopped.WakeRunId);
        Assert.Equal("transferred-epoch", stopped.ActiveEpoch);
        Assert.False(harness.Operations.HasOpen(decision.HostId, decision.SessionId));
        Assert.Equal(sources, RetainedSources(harness.LedgerPath));
        var saved = File.ReadAllBytes(harness.LedgerPath);
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(harness.Operations.StopDecision(decision)));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));

        harness.Restart();
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(harness.Get(action)));
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(harness.Operations.StopDecision(decision)));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        Assert.Throws<HostProtocolException>(() => harness.Operations.Apply(Event(action, "progress", 4, 5) with
        {
            SessionEpoch = decision.SessionEpoch
        }));
        Assert.Empty(harness.Registry.List());
        Assert.Equal(0, harness.Planners.Created);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Changed_claimed_or_stopped_decision_is_refused_without_touching_original_bytes(bool delivered, bool alreadyStopped)
    {
        await using var harness = new StopHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        if (delivered) harness.Operations.Apply(decision, "original-wake");
        if (alreadyStopped) harness.Operations.StopDecision(decision);
        harness.Restart();
        var saved = File.ReadAllBytes(harness.LedgerPath);

        foreach (var changed in ChangedDecisions(decision))
        {
            Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(changed));
            Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        }

        AssertStopped(harness.Get(action), decision, harness.Operations.StopDecision(decision));
    }

    [Fact]
    public async Task A_suppressed_decision_identity_cannot_be_reused_by_another_operation()
    {
        await using var harness = new StopHarness();
        var first = harness.Seed();
        var stopped = Event(first, "decision", 1, 2);
        harness.Operations.StopDecision(stopped);
        var request = Request();
        var second = Action(request, "second-action", "second-run");
        harness.Operations.Reserve(request, second, new HostRunUsage(Steps: 1, ProviderCalls: 1));
        harness.Operations.Admit(request.HostId, request.SessionId, Admission(second));
        harness.Restart();
        var saved = File.ReadAllBytes(harness.LedgerPath);
        var reused = Event(second, "decision", 1, 2) with { EventId = stopped.EventId };

        Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(reused));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        Assert.Throws<HostProtocolException>(() => harness.Operations.Apply(reused, "different-wake"));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
    }

    [Fact]
    public async Task Undelivered_stop_must_bind_the_next_exact_sequence_and_valid_decision_evidence()
    {
        await using var harness = new StopHarness();
        var action = harness.Seed();
        var progress = Event(action, "progress", 1, 2);
        harness.Operations.Apply(progress);
        var decision = Event(action, "decision", 2, 3);
        var saved = File.ReadAllBytes(harness.LedgerPath);
        HostOperationEvent[] invalid =
        [
            decision with { HostId = "foreign-host" },
            decision with { SessionId = "foreign-session" },
            decision with { ActionId = "foreign-action" },
            decision with { SessionEpoch = "foreign-epoch" },
            decision with { OperationId = "foreign-operation" },
            decision with { Sequence = 0 },
            decision with { Sequence = 1 },
            decision with { Sequence = 3 },
            decision with { EventId = progress.EventId },
            decision with { Kind = "progress" },
            decision with { Kind = "cancelled" },
            decision with { Kind = "transfer", NextSessionEpoch = "next-epoch" },
            decision with { NextSessionEpoch = "next-epoch" },
            decision with { Completion = new HostCompletion("stop-objective", "false-proof", "Not completion.") },
            decision with { Observation = Observation("rewound", 1) },
            decision with { Observation = progress.Observation with { Data = HostProtocol.Element(new { changed = true }) } },
            decision with { Usage = HostProtocol.Element(new[] { 1 }) },
            decision with { Usage = HostProtocol.Element(new { oversized = new string('x', 8192) }) }
        ];
        foreach (var item in invalid)
        {
            Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(item));
            Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        }
        AssertStopped(harness.Get(action), decision, harness.Operations.StopDecision(decision));
    }

    [Fact]
    public async Task Stop_cannot_cancel_an_unadmitted_reservation()
    {
        await using var harness = new StopHarness();
        var action = Action(Request(), "reserved-action", "reserved-run");
        harness.Operations.Reserve(Request(), action, new HostRunUsage());
        var saved = File.ReadAllBytes(harness.LedgerPath);
        Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(Event(action, "decision", 1, 1)));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("blocked")]
    [InlineData("cancelled")]
    public async Task A_host_terminal_event_cannot_be_replaced_by_a_decision_stop(string state)
    {
        await using var harness = new StopHarness();
        var action = harness.Seed();
        var terminal = Event(action, state, 1, 2) with
        {
            Completion = state == "completed" ? new HostCompletion("stop-objective", "terminal-proof", "Host verified completion.") : null
        };
        harness.Operations.Apply(terminal);
        harness.Restart();
        var saved = File.ReadAllBytes(harness.LedgerPath);
        Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(terminal with { Kind = "decision", Completion = null }));
        Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(Event(action, "decision", 2, 3)));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        Assert.Equal(state, harness.Get(action).State);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("cancelled")]
    public async Task Settled_noncompleted_wake_can_be_stopped_without_rewriting_its_usage_or_history(string state)
    {
        await using var harness = new StopHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        harness.Operations.Apply(decision, "settled-wake");
        var usage = new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2);
        harness.Operations.FinishWake(decision.HostId, decision.SessionId, action.ActionId, "settled-wake", state, usage);
        harness.Restart();
        var before = harness.Get(action);
        var sources = RetainedSources(harness.LedgerPath);

        var stopped = harness.Operations.StopDecision(decision);

        AssertStopped(before, decision, stopped);
        Assert.Equal(usage, stopped.SettledUsage);
        Assert.Equal(sources, RetainedSources(harness.LedgerPath));
        harness.Restart();
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(harness.Get(action)));
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(harness.Operations.StopDecision(decision)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Completed_wake_or_parent_with_an_admitted_child_refuses_stop_before_and_after_restart(bool continued)
    {
        await using var harness = new StopHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        harness.Operations.Apply(decision, "successor-run");
        var usage = new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2);
        if (continued)
        {
            var request = Request() with { Observation = decision.Observation };
            var child = Action(request, "child-action", "successor-run");
            harness.Operations.Reserve(request, child, usage);
            harness.Operations.Admit(request.HostId, request.SessionId, Admission(child));
        }
        else harness.Operations.FinishWake(decision.HostId, decision.SessionId, action.ActionId, "successor-run", "completed", usage);

        var saved = File.ReadAllBytes(harness.LedgerPath);
        Assert.Equal(continued ? "continued" : "completed", harness.Get(action).State);
        Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(decision));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        harness.Restart();
        Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(decision));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
    }

    [Fact]
    public async Task Legacy_waking_parent_with_an_admitted_child_refuses_stop_without_rewriting_evidence()
    {
        await using var harness = new StopHarness();
        var (parent, child, decision) = SeedLegacyWakingParentWithAdmittedChild(harness);
        var saved = File.ReadAllBytes(harness.LedgerPath);
        Assert.Equal("waking", harness.Get(parent).State);
        Assert.Null(harness.Get(parent).SettledUsage);
        Assert.Null(harness.Get(parent).StopDecision);
        Assert.Equal(child.RunId, harness.Get(parent).WakeRunId);
        Assert.Equal("parked", harness.Get(child).State);
        Assert.NotNull(harness.Get(child).Admission);

        Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(decision));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));

        harness.Restart();
        Assert.Throws<HostProtocolException>(() => harness.Operations.StopDecision(decision));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        Assert.Equal("waking", harness.Get(parent).State);
        Assert.Equal("parked", harness.Get(child).State);
    }

    [Fact]
    public async Task Reload_rejects_a_forged_stopped_legacy_parent_with_an_admitted_child()
    {
        await using var harness = new StopHarness();
        var (parent, child, decision) = SeedLegacyWakingParentWithAdmittedChild(harness);
        harness.Restart();
        Assert.Equal("waking", harness.Get(parent).State);
        Assert.Equal("parked", harness.Get(child).State);
        var sources = RetainedSources(harness.LedgerPath);
        var file = JsonNode.Parse(File.ReadAllText(harness.LedgerPath))!;
        var parentEntry = file["payload"]!["entries"]![0]!["entry"]!;
        Assert.Equal(parent.ActionId, parentEntry["action"]!["actionId"]!.GetValue<string>());
        parentEntry["state"] = "cancelled";
        parentEntry["stopDecision"] = JsonSerializer.SerializeToNode(decision, HostProtocol.Json);
        WriteRehashed(harness.LedgerPath, file);
        var forged = File.ReadAllBytes(harness.LedgerPath);
        Assert.Equal(sources, RetainedSources(harness.LedgerPath));

        Assert.Throws<InvalidDataException>(() => new HostOperationStore(harness.OperationRoot));
        Assert.Equal(forged, File.ReadAllBytes(harness.LedgerPath));
        Assert.Throws<InvalidDataException>(() => harness.Get(parent));
        Assert.Equal(forged, File.ReadAllBytes(harness.LedgerPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pre_stop_saves_without_the_optional_field_remain_readable_and_stoppable(bool delivered)
    {
        await using var harness = new StopHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        if (delivered) harness.Operations.Apply(decision, "legacy-wake");
        var original = harness.Get(action);
        Assert.False(HostProtocol.Element(original).TryGetProperty("stopDecision", out _));
        var file = JsonNode.Parse(File.ReadAllText(harness.LedgerPath))!;
        file["payload"]!["entries"]![0]!["entry"]!.AsObject().Remove("stopDecision");
        WriteRehashed(harness.LedgerPath, file);
        var saved = File.ReadAllBytes(harness.LedgerPath);

        harness.Restart();

        Assert.Null(harness.Get(action).StopDecision);
        Assert.Equal(ProtocolValidation.Digest(original), ProtocolValidation.Digest(harness.Get(action)));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        AssertStopped(original, decision, harness.Operations.StopDecision(decision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_validates_the_cancellation_source_even_when_the_outer_checksum_is_recomputed(bool delivered)
    {
        await using var harness = new StopHarness();
        var action = harness.Seed();
        harness.Operations.Apply(Event(action, "progress", 1, 2));
        var decision = Event(action, "decision", 2, 3);
        if (delivered) harness.Operations.Apply(decision, "original-wake");
        harness.Operations.StopDecision(decision);
        var saved = File.ReadAllText(harness.LedgerPath);
        Action<JsonNode>[] corruptions =
        [
            stored => stored["entry"]!.AsObject().Remove("stopDecision"),
            stored => stored["entry"]!["stopDecision"]!["summary"] = "Changed cancellation source.",
            stored => stored["entry"]!["latestEvent"]!["summary"] = "Changed latest decision.",
            stored => stored["entry"]!["wakeRunId"] = "invented-successor",
            stored => stored["entry"]!["state"] = "parked",
            stored => stored["entry"]!["admission"]!["evidenceId"] = "replaced-receipt",
            stored => stored["events"]![0]!["event"]!["summary"] = "Rewritten progress history.",
            stored => ChangeSourceAndLatest(stored, "kind", JsonValue.Create("cancelled")!),
            stored => ChangeSourceAndLatest(stored, "sessionEpoch", JsonValue.Create("wrong-epoch")!),
            stored => ChangeSourceAndLatest(stored, "operationId", JsonValue.Create("wrong-operation")!),
            stored => ChangeSourceAndLatest(stored, "sequence", JsonValue.Create(99)!)
        ];
        foreach (var corrupt in corruptions)
        {
            var file = JsonNode.Parse(saved)!;
            corrupt(file["payload"]!["entries"]![0]!);
            WriteRehashed(harness.LedgerPath, file);
            Assert.Throws<InvalidDataException>(() => new HostOperationStore(harness.OperationRoot));
        }
        File.WriteAllText(harness.LedgerPath, saved);
        harness.Restart();
        Assert.Equal(ProtocolValidation.Digest(decision), ProtocolValidation.Digest(harness.Get(action).StopDecision!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_stop_after_restart_never_creates_or_replays_cognition_and_preserves_admission_custody(bool delivered)
    {
        await using var harness = new StopHarness();
        var (_, action, admission) = await harness.ParkAsync();
        var decision = Event(action, "decision", 1, 2);
        var wakeId = delivered ? harness.Track(harness.Registry.Wake(decision)).RunId : null;
        harness.Restart();
        var before = harness.Get(action);
        var custodyHash = ProtocolValidation.Digest(harness.Custody.Get(decision.HostId, decision.SessionId, action.ActionId));

        var stopped = harness.Registry.StopDecision(decision);

        AssertStopped(before, decision, stopped);
        Assert.Equal(wakeId, stopped.WakeRunId);
        Assert.Equal(ProtocolValidation.Digest(admission), harness.Custody.Get(decision.HostId, decision.SessionId, action.ActionId).ResultHash);
        Assert.Equal(custodyHash, ProtocolValidation.Digest(harness.Custody.Get(decision.HostId, decision.SessionId, action.ActionId)));
        var saved = File.ReadAllBytes(harness.LedgerPath);
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(harness.Registry.StopDecision(decision)));
        Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(decision));
        harness.Restart();
        Assert.Equal(ProtocolValidation.Digest(stopped), ProtocolValidation.Digest(harness.Registry.StopDecision(decision)));
        Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(decision));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        Assert.Empty(harness.Registry.List());
        Assert.Equal(1, harness.Planners.Created);
        Assert.Equal(1, harness.Planners.Planned);
        Assert.Equal(0, harness.Planners.Refined);
    }

    [Fact]
    public async Task Registry_refuses_a_live_claim_before_dispatch_without_marking_it_cancelled()
    {
        await using var harness = new StopHarness();
        var (_, action, _) = await harness.ParkAsync();
        var decision = Event(action, "decision", 1, 2);
        var successor = harness.Track(harness.Registry.Wake(decision));
        Assert.False(successor.Terminal);
        Assert.False(harness.Custody.HasUnresolved(decision.HostId, decision.SessionId));
        var saved = File.ReadAllBytes(harness.LedgerPath);

        Assert.Throws<HostProtocolException>(() => harness.Registry.StopDecision(decision));

        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        Assert.Equal("waking", harness.Get(action).State);
        Assert.Null(harness.Get(action).StopDecision);
        Assert.Equal(1, harness.Planners.Created);
        Assert.Equal(1, harness.Planners.Planned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Registry_refuses_unresolved_custody_even_after_a_claimed_run_has_disappeared(bool delivered)
    {
        await using var harness = new StopHarness();
        var (_, action, _) = await harness.ParkAsync();
        var decision = Event(action, "decision", 1, 2);
        var successorId = delivered ? harness.Track(harness.Registry.Wake(decision)).RunId : "unrelated-run";
        var unresolved = action with { ActionId = "unresolved-action", RunId = successorId, StepId = "unresolved-step", ExpectedRevision = 2 };
        harness.Custody.Reserve(decision.HostId, decision.SessionId, decision.SessionEpoch, successorId,
            unresolved, Request().Capabilities[0], Request().ObjectiveId);
        harness.Restart();
        var saved = File.ReadAllBytes(harness.LedgerPath);
        var custodyHash = ProtocolValidation.Digest(harness.Custody.Get(decision.HostId, decision.SessionId, unresolved.ActionId));

        Assert.Equal("session.unresolved", Assert.Throws<HostProtocolException>(() => harness.Registry.StopDecision(decision)).Code);

        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        Assert.True(harness.Custody.HasUnresolved(decision.HostId, decision.SessionId));
        Assert.Equal(custodyHash, ProtocolValidation.Digest(harness.Custody.Get(decision.HostId, decision.SessionId, unresolved.ActionId)));
        Assert.Null(harness.Get(action).StopDecision);
        Assert.Empty(harness.Registry.List());
        Assert.Equal(1, harness.Planners.Planned);
    }

    [Fact]
    public async Task Registry_refuses_changed_original_admission_custody()
    {
        await using var harness = new StopHarness();
        var action = harness.Seed();
        var request = Request();
        harness.Custody.Reserve(request.HostId, request.SessionId, request.SessionEpoch, action.RunId,
            action, request.Capabilities[0], request.ObjectiveId);
        harness.Custody.Resolve(request.HostId, request.SessionId, action.ActionId, Admission(action) with { EvidenceId = "changed-receipt" });
        harness.Restart();
        var saved = File.ReadAllBytes(harness.LedgerPath);

        Assert.Equal("operation.admission_unsettled", Assert.Throws<HostProtocolException>(() =>
            harness.Registry.StopDecision(Event(action, "decision", 1, 2))).Code);

        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
        Assert.Equal(0, harness.Planners.Created);
    }

    [Fact]
    public async Task Registry_can_stop_a_settled_execution_window_without_an_additional_planner_call()
    {
        await using var harness = new StopHarness();
        var request = Request() with { Limits = Request().Limits! with { MaxRefinements = 0 } };
        var (_, action, _) = await harness.ParkAsync(request);
        var decision = Event(action, "decision", 1, 2);
        var successor = harness.Track(harness.Registry.Wake(decision));
        var connection = new CaptureConnection();
        successor.Attach(connection);
        successor.Start();
        var dispatched = await connection.NextActionAsync();
        var result = Admission(dispatched) with { Operation = null };
        Assert.True(successor.AcceptResult(result));
        await successor.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(successor.Terminal);
        Assert.False(successor.HasUnresolvedActions);
        var before = harness.Get(action);
        Assert.Equal("blocked", before.State);
        Assert.NotNull(before.SettledUsage);
        var sources = RetainedSources(harness.LedgerPath);

        var stopped = harness.Registry.StopDecision(decision);

        AssertStopped(before, decision, stopped);
        Assert.Equal(sources, RetainedSources(harness.LedgerPath));
        Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(decision));
        Assert.Equal(2, harness.Planners.Created);
        Assert.Equal(2, harness.Planners.Planned);
        Assert.Equal(0, harness.Planners.Refined);
    }

    private static void AssertStopped(HostOperationEntry before, HostOperationEvent decision, HostOperationEntry stopped)
    {
        Assert.Equal("cancelled", stopped.State);
        Assert.NotNull(stopped.StopDecision);
        Assert.NotNull(stopped.LatestEvent);
        Assert.Equal(JsonSerializer.Serialize(decision, HostProtocol.Json), JsonSerializer.Serialize(stopped.StopDecision, HostProtocol.Json));
        Assert.Equal(JsonSerializer.Serialize(decision, HostProtocol.Json), JsonSerializer.Serialize(stopped.LatestEvent, HostProtocol.Json));
        Assert.Equal("decision", stopped.LatestEvent.Kind);
        Assert.Equal(before.WakeRunId, stopped.WakeRunId);
        Assert.Equal(before.ActiveEpoch, stopped.ActiveEpoch);
        Assert.Equal(before.CreatedAt, stopped.CreatedAt);
        Assert.Equal(before.Usage, stopped.Usage);
        Assert.Equal(before.SettledUsage, stopped.SettledUsage);
        Assert.Equal(ProtocolValidation.Digest(before.Request), ProtocolValidation.Digest(stopped.Request));
        Assert.Equal(ProtocolValidation.Digest(before.Action), ProtocolValidation.Digest(stopped.Action));
        Assert.Equal(ProtocolValidation.Digest(before.Admission!), ProtocolValidation.Digest(stopped.Admission!));
    }

    private static IEnumerable<HostOperationEvent> ChangedDecisions(HostOperationEvent decision)
    {
        yield return decision with { HostId = "foreign-host" };
        yield return decision with { SessionId = "foreign-session" };
        yield return decision with { SessionEpoch = "foreign-epoch" };
        yield return decision with { ActionId = "foreign-action" };
        yield return decision with { OperationId = "foreign-operation" };
        yield return decision with { EventId = "replacement-event" };
        yield return decision with { Sequence = decision.Sequence - 1 };
        yield return decision with { Sequence = decision.Sequence + 1 };
        yield return decision with { Kind = "cancelled" };
        yield return decision with { Summary = "Changed original decision." };
        yield return decision with { Observation = decision.Observation with { ObservationId = "replacement-observation" } };
        yield return decision with { Observation = decision.Observation with { Revision = decision.Observation.Revision + 1 } };
        yield return decision with { Observation = decision.Observation with { ObservedAt = decision.Observation.ObservedAt.AddTicks(1) } };
        yield return decision with { Observation = decision.Observation with { Data = HostProtocol.Element(new { changed = true }) } };
        yield return decision with { Usage = HostProtocol.Element(new { ticks = 999 }) };
        yield return decision with { Completion = new HostCompletion("stop-objective", "false-proof", "Not completion.") };
        yield return decision with { NextSessionEpoch = "replacement-epoch" };
    }

    private static (HostActionRequest Parent, HostActionRequest Child, HostOperationEvent Decision)
        SeedLegacyWakingParentWithAdmittedChild(StopHarness harness)
    {
        var parent = harness.Seed();
        var decision = Event(parent, "decision", 1, 2);
        harness.Operations.Apply(decision, "legacy-successor");
        using var claimed = JsonDocument.Parse(File.ReadAllText(harness.LedgerPath));
        var originalParent = claimed.RootElement.GetProperty("payload").GetProperty("entries")[0].GetRawText();
        var request = Request() with { Observation = decision.Observation };
        var child = Action(request, "legacy-child-action", "legacy-successor");
        harness.Operations.Reserve(request, child, new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2));
        harness.Operations.Admit(request.HostId, request.SessionId, Admission(child));
        Assert.Equal("continued", harness.Get(parent).State);
        using var admitted = JsonDocument.Parse(File.ReadAllText(harness.LedgerPath));
        var originalChild = admitted.RootElement.GetProperty("payload").GetProperty("entries")[1].GetRawText();

        // Recreate the pre-atomic crash window without changing either original source record.
        var legacy = JsonNode.Parse(File.ReadAllText(harness.LedgerPath))!;
        legacy["payload"]!["entries"]![0] = JsonNode.Parse(originalParent);
        WriteRehashed(harness.LedgerPath, legacy);
        using var restored = JsonDocument.Parse(File.ReadAllText(harness.LedgerPath));
        var entries = restored.RootElement.GetProperty("payload").GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal(originalParent, entries[0].GetRawText());
        Assert.Equal(originalChild, entries[1].GetRawText());
        return (parent, child, decision);
    }

    // Compare the original serialized source records, not just the projected latest state.
    private static string[] RetainedSources(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("payload").GetProperty("entries").EnumerateArray().SelectMany(stored =>
        {
            var entry = stored.GetProperty("entry");
            return new[] { entry.GetProperty("request").GetRawText(), entry.GetProperty("action").GetRawText(),
                entry.GetProperty("admission").GetRawText(), stored.GetProperty("admissionHash").GetRawText(),
                stored.GetProperty("events").GetRawText(), stored.GetProperty("finishState").GetRawText(),
                stored.TryGetProperty("finishUsage", out var usage) ? usage.GetRawText() : "absent" };
        }).ToArray();
    }

    private static void WriteRehashed(string path, JsonNode file)
    {
        file["contentHash"] = ProtocolValidation.Digest(JsonSerializer.SerializeToElement(file["payload"], HostProtocol.Json));
        File.WriteAllText(path, file.ToJsonString(HostProtocol.Json));
    }

    private static void ChangeSourceAndLatest(JsonNode stored, string property, JsonNode value)
    {
        stored["entry"]!["stopDecision"]![property] = value.DeepClone();
        stored["entry"]!["latestEvent"]![property] = value.DeepClone();
    }

    private static HostRunRequest Request() => new(1, "stop-host", "stop-session", "stop-epoch", "stop-scope", "stop-perspective",
        "stop-objective", "Complete durable host work with authoritative evidence.", Observation("initial", 0),
        [new HostCapability("host.operate", "Operate", "Admit durable host work.", ToolKind.Action,
            ToolEffect.WritesLocalState, ToolInputSchema.Create(), DurableHandoff: true)],
        new ProviderSettings("fixture", "offline-model"),
        new HostRunLimits(MaxSteps: 4, MaxRefinements: 4, MaxPlanContinuations: 4, TimeoutSeconds: 20, ActionTimeoutSeconds: 10));

    private static HostObservation Observation(string id, long revision) => new(id, revision,
        new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero).AddSeconds(revision),
        HostProtocol.Element(new { revision, decisionRequired = true }));

    private static HostActionRequest Action(HostRunRequest request, string actionId, string runId) => new(actionId, runId,
        "runner-" + actionId, "step-" + actionId, request.SessionId, request.SessionEpoch, request.Capabilities[0].Id,
        ProtocolValidation.Digest(request.Capabilities), HostProtocol.Element(new { }), request.Observation.Revision, DateTimeOffset.UtcNow.AddMinutes(1));

    private static HostActionResult Admission(HostActionRequest action) => new(action.ActionId, action.SessionId, action.SessionEpoch,
        "applied", action.ExpectedRevision, action.ExpectedRevision + 1, "admission-" + action.ActionId, "Host work was admitted, not completed.",
        Observation("admitted-" + action.ActionId, action.ExpectedRevision + 1),
        Operation: new HostOperationAdmission("operation-" + action.ActionId, "Host execution owns the operation.",
            HostProtocol.Element(new { ticks = 0, limit = (int?)null })));

    private static HostOperationEvent Event(HostActionRequest action, string kind, long sequence, long revision) => new(
        "stop-host", action.SessionId, action.SessionEpoch, action.ActionId, "operation-" + action.ActionId,
        sequence, "event-" + action.ActionId + "-" + sequence, kind, Observation("event-observation-" + action.ActionId + "-" + sequence, revision),
        "The host reported " + kind + ".", HostProtocol.Element(new { ticks = sequence * 100, limit = (int?)null }));

    private sealed class CountingPlannerFactory : ILabPlannerFactory
    {
        public int Created;
        public int Planned;
        public int Refined;
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
        {
            Interlocked.Increment(ref Created);
            return new CountingPlanner(this);
        }
        public IReadOnlyList<ProviderMetadata> GetProviders() => [];
    }

    private sealed class CountingPlanner(CountingPlannerFactory owner) : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref owner.Planned);
            return Task.FromResult(new WorkflowPlan("stop-plan-" + call, call,
                [new PlanStep("stop-step-" + call, "host.operate", ToolKind.Action, ToolEffect.WritesLocalState,
                    new Dictionary<string, object?>())], "Admit one bounded operation."));
        }
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.Refined);
            throw new InvalidOperationException("Stopping a decision must not refine or reactivate cognition.");
        }
    }

    private sealed class CaptureConnection : IHostConnection
    {
        private readonly Channel<HostActionRequest> _actions = Channel.CreateUnbounded<HostActionRequest>();
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default)
        {
            if (message.Type is "action.request" or "action.reconcile") _actions.Writer.TryWrite((HostActionRequest)message.Payload);
            return Task.CompletedTask;
        }
        public async Task<HostActionRequest> NextActionAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _actions.Reader.ReadAsync(deadline.Token);
        }
    }

    private sealed class StopHarness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentica-decision-stop-tests", Guid.NewGuid().ToString("N"));
        private readonly List<HostRun> _runs = [];
        public CountingPlannerFactory Planners { get; } = new();
        public HostOperationStore Operations { get; private set; } = null!;
        public ActionCustodyStore Custody { get; private set; } = null!;
        public HostRunRegistry Registry { get; private set; } = null!;
        public string OperationRoot => Path.Combine(_root, "operations");
        public string LedgerPath => Path.Combine(OperationRoot, HostOperationStore.LedgerFileName);
        public StopHarness() => Open();
        private void Open()
        {
            Operations = new HostOperationStore(OperationRoot);
            Custody = new ActionCustodyStore(Path.Combine(_root, "custody"));
            Registry = new HostRunRegistry(new HostContextStore(_root), Planners, Custody, Operations);
        }
        public HostActionRequest Seed()
        {
            var request = Request();
            var action = Action(request, "original-action", "original-run");
            Operations.Reserve(request, action, new HostRunUsage(Steps: 1, ProviderCalls: 1));
            Operations.Admit(request.HostId, request.SessionId, Admission(action));
            return action;
        }
        public HostOperationEntry Get(HostActionRequest action) => Operations.Get("stop-host", action.SessionId, action.ActionId);
        public void Restart()
        {
            Assert.All(_runs, run => Assert.True(run.Terminal || run.Completion.IsCompleted));
            Registry.Dispose();
            Open();
        }
        public HostRun Track(HostRun run)
        {
            _runs.Add(run);
            return run;
        }
        public async Task<(HostRun Run, HostActionRequest Action, HostActionResult Admission)> ParkAsync(HostRunRequest? request = null)
        {
            var connection = new CaptureConnection();
            var run = Track(Registry.Create(request ?? Request()));
            run.Attach(connection);
            run.Start();
            var action = await connection.NextActionAsync();
            var admission = Admission(action);
            Assert.True(run.AcceptResult(admission));
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            return (run, action, admission);
        }
        public async ValueTask DisposeAsync()
        {
            Registry.Dispose();
            await Task.WhenAll(_runs.Select(run => run.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
