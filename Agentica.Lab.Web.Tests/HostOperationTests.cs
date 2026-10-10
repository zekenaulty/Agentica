using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Agentica.Artifacts;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

public sealed class HostOperationTests
{
    [Fact]
    public async Task Admission_parks_before_another_planning_call_and_preserves_original_receipt()
    {
        await using var harness = new OperationHarness();
        var (run, action, admission) = await harness.ParkAsync();

        Assert.Equal("parked", Snapshot(run).GetProperty("status").GetString());
        Assert.True(run.Terminal);
        Assert.False(run.HasUnresolvedActions);
        Assert.Equal(1, harness.Planners.Created);
        Assert.Equal(1, harness.Planners.Planned);
        Assert.Equal(0, harness.Planners.Refined);
        var outcome = Outcome(run);
        Assert.Equal(ReceiptStatus.Accepted, Assert.Single(outcome.Receipts.Items).Status);
        Assert.NotEqual(RunOutcomeStatus.Succeeded, outcome.Outcome.Status);
        Assert.Empty(outcome.Outcome.CompletionEvidence);
        Assert.Equal(ProtocolValidation.Digest(admission), harness.Custody.Get("operation-host", "operation-session", action.ActionId).ResultHash);

        var progress = Event(action, "progress", 1, 2);
        harness.Registry.ApplyOperationEvent(progress);
        harness.Registry.ApplyOperationEvent(progress);
        Assert.Equal(1, harness.Planners.Planned);
        Assert.Equal(0, harness.Planners.Refined);
        Assert.Equal(ProtocolValidation.Digest(admission), harness.Custody.Get("operation-host", "operation-session", action.ActionId).ResultHash);
        Assert.Single(harness.Registry.ListOperations("operation-host", "operation-session"));
        Assert.Equal("session.parked", Assert.Throws<HostProtocolException>(() =>
            harness.Registry.Create(Request() with { SessionEpoch = "another-epoch" })).Code);
    }

    [Fact]
    public async Task Full_store_restart_wakes_once_and_reuses_bound_objective_limits_and_provider()
    {
        await using var harness = new OperationHarness();
        var (original, action, _) = await harness.ParkAsync();
        var originalId = original.RunId;
        var request = original.Request;
        harness.Restart();
        harness.Registry.ApplyOperationEvent(Event(action, "transfer", 1, 2) with { NextSessionEpoch = "wake-epoch" });
        var wake = Event(action, "decision", 2, 3) with { SessionEpoch = "wake-epoch" };
        var resumed = harness.Track(harness.Registry.Wake(wake));
        var duplicate = harness.Registry.Wake(wake);
        Assert.Same(resumed, duplicate);
        Assert.NotEqual(originalId, resumed.RunId);
        Assert.Equal(request.ObjectiveId, resumed.Request.ObjectiveId);
        Assert.Equal(request.Objective, resumed.Request.Objective);
        Assert.Equal(request.ScopeId, resumed.Request.ScopeId);
        Assert.Equal(request.PerspectiveId, resumed.Request.PerspectiveId);
        Assert.Equal(request.Provider, resumed.Request.Provider);
        Assert.Equal(request.Limits, resumed.Request.Limits);
        Assert.Equal("wake-epoch", resumed.Request.SessionEpoch);
        Assert.Equal(ProtocolValidation.Digest(wake.Observation), ProtocolValidation.Digest(resumed.Request.Observation));

        var connection = new CaptureConnection();
        resumed.Attach(connection);
        resumed.Start();
        var resumedAction = await connection.NextActionAsync();
        Assert.Equal(3, resumedAction.ExpectedRevision);
        resumed.AcceptResult(Completed(resumedAction));
        await resumed.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, harness.Planners.Created);
        Assert.Equal(2, harness.Planners.Planned);
        Assert.Equal(0, harness.Planners.Refined);
        Assert.Single(harness.Registry.List());
        Assert.Equal(RunOutcomeStatus.Succeeded, Outcome(resumed).Outcome.Status);
    }

    [Fact]
    public async Task Restart_after_wake_claim_does_not_recreate_or_replay_the_missing_execution()
    {
        await using var harness = new OperationHarness();
        var (_, action, _) = await harness.ParkAsync();
        var wake = Event(action, "decision", 1, 2);
        harness.Track(harness.Registry.Wake(wake));
        harness.Restart();

        var error = Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(wake));
        Assert.Equal("operation.wake_interrupted", error.Code);
        Assert.Empty(harness.Registry.List());
        Assert.Equal(1, harness.Planners.Created);
        Assert.Equal(1, harness.Planners.Planned);
    }

    [Fact]
    public async Task Completion_event_needs_no_planner_and_cannot_replace_admission_evidence()
    {
        await using var harness = new OperationHarness();
        var (run, action, admission) = await harness.ParkAsync();
        var completion = Event(action, "completed", 1, 2) with
        {
            Completion = new HostCompletion("operation-objective", "host-objective-proof", "Authoritative operation completed the objective.")
        };
        var entry = harness.Registry.ApplyOperationEvent(completion);
        Assert.Equal(ProtocolValidation.Digest(entry), ProtocolValidation.Digest(harness.Registry.ApplyOperationEvent(completion)));
        Assert.Equal(1, harness.Planners.Created);
        Assert.Equal(1, harness.Planners.Planned);
        Assert.Equal(ReceiptStatus.Accepted, Assert.Single(Outcome(run).Receipts.Items).Status);
        Assert.Equal(ProtocolValidation.Digest(admission), harness.Custody.Get("operation-host", "operation-session", action.ActionId).ResultHash);
        Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(Event(action, "decision", 2, 3)));
    }

    [Fact]
    public async Task Stale_epoch_skipped_sequence_and_changed_event_replay_cannot_change_parked_work()
    {
        await using var harness = new OperationHarness();
        var (_, action, _) = await harness.ParkAsync();
        var first = Event(action, "progress", 1, 2);
        Assert.Throws<HostProtocolException>(() => harness.Registry.ApplyOperationEvent(first with { SessionEpoch = "stale-epoch" }));
        Assert.Throws<HostProtocolException>(() => harness.Registry.ApplyOperationEvent(first with { Sequence = 2 }));
        var accepted = harness.Registry.ApplyOperationEvent(first);
        Assert.Throws<HostProtocolException>(() => harness.Registry.ApplyOperationEvent(first with { Summary = "Changed event under same identity." }));
        Assert.Throws<HostProtocolException>(() => harness.Registry.ApplyOperationEvent(first with { EventId = "different-event" }));
        Assert.Equal(ProtocolValidation.Digest(accepted), ProtocolValidation.Digest(harness.Registry.ApplyOperationEvent(first)));
        Assert.Equal(1, harness.Planners.Planned);
    }

    [Fact]
    public async Task Semantic_cancellation_of_parked_objective_prevents_later_wake()
    {
        await using var harness = new OperationHarness();
        var (_, action, _) = await harness.ParkAsync();
        harness.Registry.ApplyOperationEvent(Event(action, "cancelled", 1, 2));
        Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(Event(action, "decision", 2, 3)));
        Assert.Equal(1, harness.Planners.Created);
        Assert.Equal(1, harness.Planners.Planned);
    }

    [Fact]
    public async Task Crash_after_admission_and_context_commit_recovers_original_custody_without_rewinding_observation()
    {
        await using var harness = new OperationHarness();
        var request = Request();
        var action = new HostActionRequest("crash-action", "crash-run", "crash-runner", "crash-step",
            request.SessionId, request.SessionEpoch, request.Capabilities[0].Id,
            ProtocolValidation.Digest(request.Capabilities), HostProtocol.Element(new { }), 0, DateTimeOffset.UtcNow.AddMinutes(1));
        var context = harness.Contexts.Open(request);
        context.RecordActionDispatch(action);
        harness.Operations.Reserve(request, action, new HostRunUsage(Steps: 1, ProviderCalls: 1));
        harness.Custody.Reserve(request.HostId, request.SessionId, request.SessionEpoch, action.RunId,
            action, request.Capabilities[0], request.ObjectiveId);
        var admission = Admission(action);
        harness.Operations.Admit(request.HostId, request.SessionId, admission);
        context.RecordActionResult(action, admission);
        // The process stops after durable admission/context writes but before custody settlement.
        harness.Restart();
        Assert.True(harness.Custody.HasUnresolved(request.HostId, request.SessionId));
        Assert.Equal("session.unresolved", Assert.Throws<HostProtocolException>(() =>
            harness.Registry.Wake(Event(action, "decision", 1, 2))).Code);

        Assert.True(harness.Registry.Recover(request.HostId, request.SessionId, admission));
        Assert.False(harness.Registry.Recover(request.HostId, request.SessionId, admission));
        Assert.False(harness.Custody.HasUnresolved(request.HostId, request.SessionId));
        Assert.Equal(ProtocolValidation.Digest(admission), harness.Custody.Get(request.HostId, request.SessionId, action.ActionId).ResultHash);
        Assert.Equal(ProtocolValidation.Digest(admission.Observation!), ProtocolValidation.Digest(
            harness.Contexts.Open(request with { Observation = admission.Observation! }).CurrentObservation));
        Assert.Single(harness.Registry.ListOperations(request.HostId, request.SessionId));
        Assert.NotNull(harness.Track(harness.Registry.Wake(Event(action, "decision", 1, 2))));
        Assert.Equal(0, harness.Planners.Created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_or_corrupt_initialized_operation_ledger_fences_even_plain_capability_admission(bool missing)
    {
        await using var harness = new OperationHarness();
        if (missing) File.Delete(harness.OperationLedgerPath);
        else await File.WriteAllTextAsync(harness.OperationLedgerPath, "invalid fixture ledger", TestContext.Current.CancellationToken);
        var request = Request();
        request = request with { Capabilities = [request.Capabilities[0] with { DurableHandoff = false }] };
        Assert.Throws<InvalidDataException>(() => harness.Registry.Create(request));
        Assert.Empty(harness.Registry.List());
        Assert.Equal(0, harness.Planners.Created);
    }

    [Fact]
    public async Task Usage_accounting_preserves_one_native_planner_session_across_create_and_refine()
    {
        var planners = new SessionPlannerFactory();
        await using var harness = new OperationHarness(planners);
        var connection = new CaptureConnection();
        var run = harness.Track(harness.Registry.Create(Request()));
        run.Attach(connection);
        run.Start();
        var first = await connection.NextActionAsync();
        run.AcceptResult(Completed(first) with { Completion = null });
        var second = await connection.NextActionAsync();
        run.AcceptResult(Completed(second));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, planners.Factory.BeginCount);
        Assert.Equal(1, planners.Factory.Session.Created);
        Assert.Equal(1, planners.Factory.Session.Refined);
        Assert.Equal(1, planners.Factory.Session.DisposeCount);
        Assert.Equal(first.RunnerRunId, second.RunnerRunId);
        Assert.Equal(first.RunnerRunId, planners.Factory.Context!.RunId);
        Assert.Equal(RunOutcomeStatus.Succeeded, Outcome(run).Outcome.Status);
        Assert.Equal(1, Snapshot(run).GetProperty("usage").GetProperty("refinements").GetInt32());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Invalid_context_fact_cannot_claim_wake_or_poison_its_event_identity(bool invalidState)
    {
        await using var harness = new OperationHarness();
        var (_, action, _) = await harness.ParkAsync();
        var valid = Event(action, "decision", 1, 2);
        var fact = new KnowledgeFact("candidate", "Host fact.", HostProtocol.Element(true),
            State: invalidState ? "unrecognized" : "observed", Supersedes: invalidState ? null : "missing-fact");
        var invalid = valid with { Observation = valid.Observation with { Facts = [fact] } };
        Assert.Equal("operation.observation", Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(invalid)).Code);

        var retained = Assert.Single(harness.Registry.ListOperations("operation-host", "operation-session"));
        Assert.Equal("parked", retained.State);
        Assert.Null(retained.WakeRunId);
        Assert.Null(retained.LatestEvent);
        Assert.Equal(1, harness.Planners.Created);
        Assert.Equal(1, harness.Planners.Planned);
        Assert.NotNull(harness.Track(harness.Registry.Wake(valid)));
    }

    [Fact]
    public async Task Ownership_transfer_cannot_cycle_back_to_a_retired_epoch()
    {
        await using var harness = new OperationHarness();
        var (_, action, _) = await harness.ParkAsync();
        harness.Registry.ApplyOperationEvent(Event(action, "transfer", 1, 2) with { NextSessionEpoch = "second-epoch" });
        var cycle = Event(action, "transfer", 2, 3) with { SessionEpoch = "second-epoch", NextSessionEpoch = action.SessionEpoch };
        Assert.Throws<HostProtocolException>(() => harness.Registry.ApplyOperationEvent(cycle));
        harness.Restart();
        var retained = Assert.Single(harness.Registry.ListOperations("operation-host", "operation-session"));
        Assert.Equal("second-epoch", retained.ActiveEpoch);
        Assert.Equal(1, retained.LatestEvent!.Sequence);
        Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(Event(action, "decision", 2, 3)));
        Assert.NotNull(harness.Track(harness.Registry.Wake(Event(action, "decision", 2, 3) with { SessionEpoch = "second-epoch" })));
        Assert.Equal(1, harness.Planners.Created);
    }

    [Fact]
    public async Task Child_admission_atomically_settles_parent_before_process_crash()
    {
        await using var harness = new OperationHarness();
        var (_, parentAction, _) = await harness.ParkAsync();
        var successor = harness.Track(harness.Registry.Wake(Event(parentAction, "decision", 1, 2)));
        var child = new HostActionRequest("crash-child-action", successor.RunId, "child-runner", "child-step",
            successor.Request.SessionId, successor.Request.SessionEpoch, successor.Request.Capabilities[0].Id,
            ProtocolValidation.Digest(successor.Request.Capabilities), HostProtocol.Element(new { }), 2, DateTimeOffset.UtcNow.AddMinutes(1));
        harness.Contexts.Open(successor.Request).RecordActionDispatch(child);
        harness.Operations.Reserve(successor.Request, child, new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2));
        harness.Custody.Reserve(successor.Request.HostId, child.SessionId, child.SessionEpoch, child.RunId,
            child, successor.Request.Capabilities[0], successor.Request.ObjectiveId);
        var admission = Admission(child);
        harness.Operations.Admit(successor.Request.HostId, child.SessionId, admission);
        // No explicit FinishWake call or execution-finally block runs before this restart.
        harness.Restart();
        var retained = harness.Registry.ListOperations("operation-host", "operation-session");
        var parent = Assert.Single(retained, entry => entry.Action.ActionId == parentAction.ActionId);
        Assert.Equal("continued", parent.State);
        Assert.Equal(new HostRunUsage(Steps: 1, ProviderCalls: 1), parent.Usage);
        Assert.Equal(new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2), parent.SettledUsage);
        Assert.Equal("parked", Assert.Single(retained, entry => entry.Action.ActionId == child.ActionId).State);
        Assert.Equal("session.unresolved", Assert.Throws<HostProtocolException>(() =>
            harness.Registry.Wake(Event(child, "decision", 1, 4))).Code);
        Assert.True(harness.Registry.Recover("operation-host", "operation-session", admission));
        Assert.NotNull(harness.Track(harness.Registry.Wake(Event(child, "decision", 1, 4))));
        Assert.Equal(1, harness.Planners.Created);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Completed_or_blocked_successor_retains_final_cumulative_usage_after_restart(bool complete)
    {
        await using var harness = new OperationHarness();
        var request = Request() with { Limits = Request().Limits! with { MaxRefinements = 0 } };
        var (_, action, _) = await harness.ParkAsync(request);
        var wake = Event(action, "decision", 1, 2);
        var successor = harness.Track(harness.Registry.Wake(wake));
        var connection = new CaptureConnection();
        successor.Attach(connection);
        successor.Start();
        var secondAction = await connection.NextActionAsync();
        var result = Completed(secondAction);
        successor.AcceptResult(complete ? result : result with { Completion = null });
        await successor.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(complete ? RunOutcomeStatus.Succeeded : RunOutcomeStatus.Blocked, Outcome(successor).Outcome.Status);
        var expected = new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2);
        Assert.Equal(expected, Snapshot(successor).GetProperty("usage").Deserialize<HostRunUsage>(HostProtocol.Json));

        harness.Restart();
        var parent = Assert.Single(harness.Registry.ListOperations(request.HostId, request.SessionId),
            entry => entry.Action.ActionId == action.ActionId);
        Assert.Equal(complete ? "completed" : "blocked", parent.State);
        Assert.Equal(new HostRunUsage(Steps: 1, ProviderCalls: 1), parent.Usage);
        Assert.Equal(expected, parent.SettledUsage);
        Assert.Equal(expected, harness.Operations.FinishWake(request.HostId, request.SessionId,
            action.ActionId, successor.RunId, parent.State, expected).SettledUsage);
        Assert.Equal("operation.wake_changed", Assert.Throws<HostProtocolException>(() =>
            harness.Operations.FinishWake(request.HostId, request.SessionId, action.ActionId,
                successor.RunId, parent.State, expected with { ProviderCalls = 3 })).Code);
        Assert.Equal(2, harness.Planners.Created);
        Assert.Equal(0, harness.Planners.Refined);
    }

    [Fact]
    public async Task Cumulative_usage_and_limits_survive_multiple_execution_windows_without_reset()
    {
        await using var harness = new OperationHarness();
        var request = Request() with { Limits = Request().Limits! with { MaxSteps = 2 } };
        var (_, firstAction, _) = await harness.ParkAsync(request);
        var first = Assert.Single(harness.Registry.ListOperations(request.HostId, request.SessionId));
        Assert.Equal(new HostRunUsage(Steps: 1, ProviderCalls: 1), first.Usage);
        harness.Restart();

        var continuation = harness.Track(harness.Registry.Wake(Event(firstAction, "decision", 1, 2)));
        var connection = new CaptureConnection();
        continuation.Attach(connection);
        continuation.Start();
        var secondAction = await connection.NextActionAsync();
        continuation.AcceptResult(Admission(secondAction));
        await continuation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Assert.Single(harness.Registry.ListOperations(request.HostId, request.SessionId),
            entry => entry.Action.ActionId == secondAction.ActionId);
        Assert.Equal(new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2), second.Usage);
        Assert.Equal(request.Limits, second.Request.Limits);
        Assert.Equal("continued", Assert.Single(harness.Registry.ListOperations(request.HostId, request.SessionId),
            entry => entry.Action.ActionId == firstAction.ActionId).State);
        Assert.Equal("parked", second.State);
        harness.Restart();

        var error = Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(Event(secondAction, "decision", 1, 4)));
        Assert.Equal("operation.budget", error.Code);
        Assert.Equal(2, harness.Planners.Created);
        Assert.Equal(2, harness.Planners.Planned);
        Assert.Empty(harness.Registry.List());
    }

    [Fact]
    public async Task Exhausted_continuation_allowance_does_not_start_a_fresh_run()
    {
        await using var harness = new OperationHarness();
        var request = Request() with { Limits = Request().Limits! with { MaxPlanContinuations = 0 } };
        var (_, action, _) = await harness.ParkAsync(request);
        Assert.Equal("operation.budget", Assert.Throws<HostProtocolException>(() =>
            harness.Registry.Wake(Event(action, "decision", 1, 2))).Code);
        Assert.Equal(1, harness.Planners.Created);
    }

    [Fact]
    public async Task Unresolved_original_action_custody_prevents_wake_even_with_a_durable_admission()
    {
        await using var harness = new OperationHarness();
        var (_, action, _) = await harness.ParkAsync();
        // Model an independent possible dispatch whose result has not been reconciled.
        var unresolved = action with { ActionId = "unresolved-action", RunId = "unresolved-run", StepId = "unresolved-step", ExpectedRevision = 1 };
        harness.Custody.Reserve("operation-host", action.SessionId, action.SessionEpoch, unresolved.RunId,
            unresolved, Request().Capabilities[0], "operation-objective");
        Assert.Equal("session.unresolved", Assert.Throws<HostProtocolException>(() =>
            harness.Registry.Wake(Event(action, "decision", 1, 2))).Code);
        Assert.Throws<HostProtocolException>(() => harness.Registry.Create(Request()));
        Assert.Equal(1, harness.Planners.Created);
        Assert.Equal("parked", Assert.Single(harness.Registry.ListOperations("operation-host", "operation-session")).State);
    }

    [Fact]
    public void Legacy_capability_and_result_hashes_omit_new_default_fields()
    {
        var capability = Request().Capabilities[0] with { DurableHandoff = false };
        var legacyCapability = new
        {
            capability.Id,
            capability.Name,
            capability.Description,
            capability.Kind,
            capability.Effect,
            capability.InputSchema
        };
        Assert.Equal(ProtocolValidation.Digest(legacyCapability), ProtocolValidation.Digest(capability));
        Assert.False(HostProtocol.Element(capability).TryGetProperty("durableHandoff", out _));

        var result = new HostActionResult("legacy-action", "legacy-session", "legacy-epoch", "applied", 0, 1,
            "legacy-evidence", "The host applied the original action.", Observation("legacy-observation", 1));
        var legacyResult = new
        {
            result.ActionId,
            result.SessionId,
            result.SessionEpoch,
            result.Disposition,
            result.BeforeRevision,
            result.AfterRevision,
            result.EvidenceId,
            result.Summary,
            result.Observation,
            result.Completion
        };
        Assert.Equal(ProtocolValidation.Digest(legacyResult), ProtocolValidation.Digest(result));
        Assert.False(HostProtocol.Element(result).TryGetProperty("operation", out _));
    }

    private static HostRunRequest Request() => new(
        1, "operation-host", "operation-session", "operation-epoch", "operation-scope", "operation-perspective",
        "operation-objective", "Complete the host-owned durable operation and verify its result.", Observation("initial", 0),
        [new HostCapability("host.operate", "Operate", "Admit bounded work for host execution.", ToolKind.Action,
            ToolEffect.WritesLocalState, ToolInputSchema.Create(), DurableHandoff: true)],
        new ProviderSettings("fixture", "retained-fixture-model", "high"),
        new HostRunLimits(MaxSteps: 4, MaxRefinements: 4, MaxPlanContinuations: 4, TimeoutSeconds: 20, ActionTimeoutSeconds: 10));

    private static HostObservation Observation(string id, long revision) => new(id, revision,
        new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero).AddSeconds(revision),
        HostProtocol.Element(new { revision, completed = revision > 2 }));

    private static HostActionResult Admission(HostActionRequest action) => new(action.ActionId,
        action.SessionId, action.SessionEpoch, "applied", action.ExpectedRevision, action.ExpectedRevision + 1,
        "admission-" + action.ActionId, "The host admitted the operation without claiming objective completion.",
        Observation("admitted-" + action.ActionId, action.ExpectedRevision + 1),
        Operation: new HostOperationAdmission("operation-" + action.ActionId, "Durable host execution is active.",
            HostProtocol.Element(new { executionTicks = 0 })));

    private static HostActionResult Completed(HostActionRequest action) => new(action.ActionId,
        action.SessionId, action.SessionEpoch, "applied", action.ExpectedRevision, action.ExpectedRevision + 1,
        "completed-" + action.ActionId, "The host verified the result.",
        Observation("complete-" + action.ActionId, action.ExpectedRevision + 1),
        new HostCompletion("operation-objective", "proof-" + action.ActionId, "Objective fulfilled in authoritative host state."));

    private static HostOperationEvent Event(HostActionRequest action, string kind, long sequence, long revision) => new(
        "operation-host", action.SessionId, action.SessionEpoch, action.ActionId, "operation-" + action.ActionId,
        sequence, "event-" + action.ActionId + "-" + sequence, kind, Observation("event-observation-" + action.ActionId + "-" + sequence, revision),
        "The host reported " + kind + ".", HostProtocol.Element(new { executionTicks = sequence * 100 }));

    private static JsonElement Snapshot(HostRun run) => HostProtocol.Element(run.Snapshot());
    private static OutcomeEnvelope Outcome(HostRun run) => Snapshot(run).GetProperty("outcome").Deserialize<OutcomeEnvelope>(HostProtocol.Json)!;

    private sealed class CountingPlannerFactory : ILabPlannerFactory
    {
        public int Created;
        public int Planned;
        public int Refined;
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
        {
            Interlocked.Increment(ref Created);
            return new CountingPlanner(this, onStreamEvent);
        }
        public IReadOnlyList<ProviderMetadata> GetProviders() => [];
    }

    private sealed class CountingPlanner(CountingPlannerFactory owner, Action<LlmStreamEvent> onStreamEvent) : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref owner.Planned);
            onStreamEvent(new LlmStreamEvent(LlmStreamEventKind.Started));
            onStreamEvent(new LlmStreamEvent(LlmStreamEventKind.Completed,
                Response: new LlmResponse("fixture", "retained-fixture-model", "Provider-free deterministic fixture.")));
            return Task.FromResult(new WorkflowPlan("operation-plan-" + call, call,
                [new PlanStep("operation-step-" + call, "host.operate", ToolKind.Action, ToolEffect.WritesLocalState,
                    new Dictionary<string, object?>())], "Admit one durable host operation."));
        }
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.Refined);
            throw new InvalidOperationException("Admission must park before refining or making another planning call.");
        }
    }

    private sealed class CaptureConnection : IHostConnection
    {
        private readonly Channel<HostActionRequest> _actions = Channel.CreateUnbounded<HostActionRequest>();
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public ConcurrentQueue<ServiceMessage> Messages { get; } = new();
        public Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default)
        {
            Messages.Enqueue(message);
            if (message.Type is "action.request" or "action.reconcile") _actions.Writer.TryWrite((HostActionRequest)message.Payload);
            return Task.CompletedTask;
        }
        public async Task<HostActionRequest> NextActionAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await _actions.Reader.ReadAsync(deadline.Token);
        }
    }

    private sealed class SessionPlannerFactory : ILabPlannerFactory
    {
        public NativeSessionFactory Factory { get; } = new();
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent) => Factory;
        public IReadOnlyList<ProviderMetadata> GetProviders() => [];
    }

    private sealed class NativeSessionFactory : IWorkflowPlanner, IWorkflowPlannerSessionFactory, IExternalWorkflowPlanner
    {
        public int BeginCount;
        public PlanningSessionContext? Context;
        public NativeSession Session { get; } = new();
        public IWorkflowPlannerSession BeginSession(PlanningSessionContext context)
        {
            BeginCount++;
            Context = context;
            return Session;
        }
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Accounting must preserve the native session factory.");
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Accounting must preserve the native session factory.");
    }

    private sealed class NativeSession : IWorkflowPlannerSession, IExternalWorkflowPlanner
    {
        public int Created;
        public int Refined;
        public int DisposeCount;
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
        {
            Created++;
            return Task.FromResult(Plan(1));
        }
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default)
        {
            Refined++;
            return Task.FromResult(Plan(2));
        }
        public void Dispose() => DisposeCount++;
        private static WorkflowPlan Plan(int version) => new("session-plan-" + version, version,
            [new PlanStep("session-step-" + version, "host.operate", ToolKind.Action, ToolEffect.WritesLocalState,
                new Dictionary<string, object?>())], "Keep provider continuation in the same bounded session.");
    }

    private sealed class OperationHarness : IAsyncDisposable
    {
        private readonly string _storage = Path.Combine(Path.GetTempPath(), "agentica-operation-tests", Guid.NewGuid().ToString("N"));
        private readonly List<HostRun> _runs = [];
        private readonly ILabPlannerFactory? _planners;
        public CountingPlannerFactory Planners { get; } = new();
        public ActionCustodyStore Custody { get; private set; } = null!;
        public HostContextStore Contexts { get; private set; } = null!;
        public HostOperationStore Operations { get; private set; } = null!;
        public string OperationLedgerPath => Path.Combine(_storage, "operations", HostOperationStore.LedgerFileName);
        public HostRunRegistry Registry { get; private set; } = null!;
        public OperationHarness(ILabPlannerFactory? planners = null)
        {
            _planners = planners;
            Open();
        }
        private void Open()
        {
            Custody = new ActionCustodyStore(Path.Combine(_storage, "custody"));
            Contexts = new HostContextStore(_storage);
            Operations = new HostOperationStore(Path.Combine(_storage, "operations"));
            Registry = new HostRunRegistry(Contexts, _planners ?? Planners, Custody, Operations);
        }
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
            if (Directory.Exists(_storage)) Directory.Delete(_storage, recursive: true);
        }
    }
}
