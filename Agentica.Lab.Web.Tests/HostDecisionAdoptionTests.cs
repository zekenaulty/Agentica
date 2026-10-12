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

public sealed class HostDecisionAdoptionTests
{
    [Fact]
    public void Adoption_wire_envelope_retains_two_separate_sources()
    {
        var decision = Event(Action(Request(), "wire-action", "wire-run"), "decision", 1, 2);
        var adoption = Adoption(3);
        var envelope = new HostDecisionWake(decision, adoption);
        var wire = HostProtocol.Element(envelope);
        Assert.Equal(new[] { "decision", "adoption" }, wire.EnumerateObject().Select(item => item.Name).ToArray());
        var restored = wire.Deserialize<HostDecisionWake>(HostProtocol.Json)!;
        AssertSame(decision, restored.Decision);
        AssertSame(adoption, restored.Adoption);
        Assert.Equal("adoption-epoch", restored.Decision.SessionEpoch);
        Assert.Equal("new-owner-epoch", restored.Adoption.NextSessionEpoch);
    }

    [Fact]
    public async Task Adoption_preserves_original_receipt_event_hashes_and_history_across_reload_and_exact_retry()
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        harness.Operations.Apply(Event(action, "progress", 1, 2));
        harness.Operations.Apply(Event(action, "transfer", 2, 3) with { NextSessionEpoch = "intermediate-epoch" });
        var sources = OriginalSources(harness.LedgerPath);
        var history = Deliveries(harness.LedgerPath);
        var decision = Event(action, "decision", 3, 4) with { SessionEpoch = "intermediate-epoch" };
        var adoption = Adoption(5);
        var decisionJson = HostProtocol.Element(decision).GetRawText();
        var custody = File.ReadAllBytes(harness.CustodyPath);

        var adopted = harness.Operations.Apply(decision, "adopted-run", adoption);

        Assert.Equal("waking", adopted.State);
        Assert.Equal(adoption.NextSessionEpoch, adopted.ActiveEpoch);
        Assert.Equal("adopted-run", adopted.WakeRunId);
        Assert.Equal(decisionJson, HostProtocol.Element(adopted.LatestEvent!).GetRawText());
        AssertSame(adoption, adopted.WakeAdoption);
        Assert.Equal(action.SessionEpoch, adopted.Request.SessionEpoch);
        Assert.Equal(action.SessionEpoch, adopted.Admission!.SessionEpoch);
        Assert.Equal(sources, OriginalSources(harness.LedgerPath));
        Assert.Equal(history, Deliveries(harness.LedgerPath).Take(history.Length).ToArray());
        var delivery = JsonDocument.Parse(Deliveries(harness.LedgerPath).Last());
        using (delivery)
        {
            Assert.Equal(decisionJson, delivery.RootElement.GetProperty("event").GetRawText());
            Assert.Equal(ProtocolValidation.Digest(decision), delivery.RootElement.GetProperty("eventHash").GetString());
        }
        Assert.Equal(3, Deliveries(harness.LedgerPath).Length);
        var saved = File.ReadAllBytes(harness.LedgerPath);
        for (var reload = 0; reload < 2; reload++)
        {
            harness.Restart();
            AssertSame(adopted, harness.Get(action));
            AssertSame(adopted, harness.Operations.Apply(Clone(decision), "adopted-run", Clone(adoption)));
            Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
            Assert.Equal(custody, File.ReadAllBytes(harness.CustodyPath));
        }
        Assert.Equal(decisionJson, HostProtocol.Element(decision).GetRawText());
        Assert.Equal(0, harness.Planners.Created);
    }

    [Fact]
    public async Task Claimed_decision_refuses_changed_or_missing_adoption_changed_event_and_changed_successor()
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        var adoption = Adoption(3);
        var original = harness.Operations.Apply(decision, "original-successor", adoption);
        HostDecisionAdoption?[] changedAdoptions =
        [
            null,
            adoption with { AdoptionId = "different-adoption" },
            adoption with { NextSessionEpoch = "different-owner" },
            adoption with { Observation = adoption.Observation with { ObservationId = "different-observation" } },
            adoption with { Observation = adoption.Observation with { Revision = 4 } },
            adoption with { Observation = adoption.Observation with { ObservedAt = adoption.Observation.ObservedAt.AddSeconds(1) } },
            adoption with { Observation = adoption.Observation with { Data = HostProtocol.Element(new { changed = true }) } },
            adoption with { Observation = adoption.Observation with { Facts = [new KnowledgeFact("changed", "Changed fact.", HostProtocol.Element(true))] } }
        ];
        HostOperationEvent[] changedDecisions =
        [
            decision with { SessionEpoch = adoption.NextSessionEpoch },
            decision with { OperationId = "different-operation" },
            decision with { Summary = "Changed original decision." },
            decision with { Usage = HostProtocol.Element(new { ticks = 999 }) },
            decision with { Observation = adoption.Observation }
        ];
        for (var reload = 0; reload < 2; reload++)
        {
            var saved = File.ReadAllBytes(harness.LedgerPath);
            foreach (var changed in changedAdoptions)
                RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(decision, "original-successor", changed));
            foreach (var changed in changedDecisions)
                RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(changed, "original-successor", adoption));
            RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(decision, "different-successor", adoption));
            AssertSame(original, harness.Operations.Apply(decision, "original-successor", adoption));
            Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
            harness.Restart();
        }
    }

    [Fact]
    public async Task Ordinary_wake_cannot_be_retroactively_adopted_and_keeps_legacy_serialization()
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        var original = harness.Operations.Apply(decision, "ordinary-successor");
        var saved = File.ReadAllBytes(harness.LedgerPath);
        Assert.Null(original.WakeAdoption);
        Assert.False(HostProtocol.Element(original).TryGetProperty("wakeAdoption", out _));
        using (var file = JsonDocument.Parse(saved))
        {
            var stored = file.RootElement.GetProperty("payload").GetProperty("entries")[0];
            var delivered = stored.GetProperty("events")[0];
            Assert.Equal(new[] { "event", "eventHash", "successorRunId" }, delivered.EnumerateObject().Select(p => p.Name).ToArray());
            Assert.Equal(ProtocolValidation.Digest(decision), delivered.GetProperty("eventHash").GetString());
            Assert.Equal(ProtocolValidation.Digest(stored.GetProperty("entry").GetProperty("request")), stored.GetProperty("requestHash").GetString());
            Assert.Equal(ProtocolValidation.Digest(file.RootElement.GetProperty("payload")), file.RootElement.GetProperty("contentHash").GetString());
        }
        harness.Restart();
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(decision, "ordinary-successor", Adoption(3)));
        AssertSame(original, harness.Operations.Apply(decision, "ordinary-successor"));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
    }

    [Theory]
    [InlineData("adoption-epoch")]
    [InlineData("intermediate-epoch")]
    [InlineData("current-epoch")]
    [InlineData("")]
    [InlineData("invalid\nowner")]
    public async Task Adoption_refuses_current_retired_or_invalid_owner_epochs_without_claiming(string nextEpoch)
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        harness.Operations.Apply(Event(action, "transfer", 1, 2) with { NextSessionEpoch = "intermediate-epoch" });
        harness.Operations.Apply(Event(action, "transfer", 2, 3) with { SessionEpoch = "intermediate-epoch", NextSessionEpoch = "current-epoch" });
        harness.Restart();
        var decision = Event(action, "decision", 3, 4) with { SessionEpoch = "current-epoch" };
        var saved = File.ReadAllBytes(harness.LedgerPath);
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(decision, "successor", Adoption(5) with { NextSessionEpoch = nextEpoch }));
        Assert.Equal("parked", harness.Get(action).State);
        Assert.Null(harness.Get(action).WakeRunId);
        Assert.Equal("new-owner-epoch", harness.Operations.Apply(decision, "successor", Adoption(5)).ActiveEpoch);
    }

    [Fact]
    public async Task Adoption_still_requires_the_original_decision_to_bind_the_current_old_owner()
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        harness.Operations.Apply(Event(action, "transfer", 1, 2) with { NextSessionEpoch = "current-epoch" });
        var decision = Event(action, "decision", 2, 3) with { SessionEpoch = "current-epoch" };
        var adoption = Adoption(4);
        var saved = File.ReadAllBytes(harness.LedgerPath);
        foreach (var epoch in new[] { action.SessionEpoch, adoption.NextSessionEpoch, "foreign-owner" })
            RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(decision with { SessionEpoch = epoch }, "successor", adoption));
        AssertSame(decision, harness.Operations.Apply(decision, "successor", adoption).LatestEvent);
    }

    [Fact]
    public async Task Invalid_adoption_evidence_cannot_rewind_or_replace_the_decision_observation()
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 4);
        var adoption = Adoption(5);
        HostDecisionAdoption[] invalid =
        [
            adoption with { AdoptionId = "" },
            adoption with { Observation = Observation("rewound", 3) },
            adoption with { Observation = decision.Observation with { Data = HostProtocol.Element(new { changed = true }) } },
            adoption with { Observation = decision.Observation with { ObservedAt = decision.Observation.ObservedAt.AddSeconds(1) } },
            adoption with { Observation = decision.Observation with { Revision = 5 } },
            adoption with { Observation = Observation("", 5) },
            adoption with { Observation = adoption.Observation with { Data = HostProtocol.Element(new[] { 1 }) } }
        ];
        var saved = File.ReadAllBytes(harness.LedgerPath);
        foreach (var item in invalid)
            RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(decision, "successor", item));
        harness.Restart();
        Assert.Null(harness.Get(action).LatestEvent);
        Assert.Null(harness.Get(action).WakeAdoption);
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adoption_allows_equal_revision_with_unchanged_identity_or_a_fresh_observation(bool freshIdentity)
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        var observation = freshIdentity ? Observation("fresh-same-revision", 2) : Clone(decision.Observation);
        var adopted = harness.Operations.Apply(decision, "successor", Adoption(2) with { Observation = observation });
        harness.Restart();
        AssertSame(adopted, harness.Get(action));
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("transfer")]
    [InlineData("completed")]
    [InlineData("blocked")]
    [InlineData("cancelled")]
    public async Task Only_a_decision_can_adopt_an_epoch(string kind)
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var item = Event(action, kind, 1, 2) with
        {
            NextSessionEpoch = kind == "transfer" ? "transfer-owner" : null,
            Completion = kind == "completed" ? new HostCompletion(Request().ObjectiveId, "completion-proof", "Host verified completion.") : null
        };
        var saved = File.ReadAllBytes(harness.LedgerPath);
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(item, adoption: Adoption(3)));
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(item, "successor", Adoption(3)));
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(Event(action, "decision", 1, 2), adoption: Adoption(3)));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("blocked")]
    [InlineData("cancelled")]
    [InlineData("stopped")]
    public async Task Adoption_cannot_reopen_terminal_or_stopped_operations(string state)
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        if (state == "stopped") harness.Operations.StopDecision(decision);
        else harness.Operations.Apply(decision with
        {
            Kind = state,
            Completion = state == "completed" ? new HostCompletion(Request().ObjectiveId, "completion-proof", "Host verified completion.") : null
        });
        harness.Restart();
        var saved = File.ReadAllBytes(harness.LedgerPath);
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(decision, "successor", Adoption(3)));
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(Event(action, "decision", 2, 3), "successor", Adoption(4)));
        RefusesWithoutWrite(harness, saved, () => harness.Registry.Wake(decision, Adoption(3)));
        Assert.Equal(0, harness.Planners.Created);
    }

    [Fact]
    public async Task A_claimed_adoption_cannot_be_transferred_or_replaced_by_another_decision_while_waking()
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var adoption = Adoption(3);
        var claimed = harness.Operations.Apply(Event(action, "decision", 1, 2), "successor", adoption);
        harness.Restart();
        var saved = File.ReadAllBytes(harness.LedgerPath);
        var transfer = Event(action, "transfer", 2, 4) with { SessionEpoch = adoption.NextSessionEpoch, NextSessionEpoch = "third-owner" };
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(transfer));
        var nextDecision = transfer with { Kind = "decision", NextSessionEpoch = null };
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(nextDecision, "second-successor", Adoption(5) with { NextSessionEpoch = "third-owner" }));
        AssertSame(claimed, harness.Get(action));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("blocked")]
    [InlineData("cancelled")]
    public async Task Stop_uses_original_old_epoch_decision_after_adoption_and_optional_settlement(string? settlement)
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        var adoption = Adoption(3);
        harness.Operations.Apply(decision, "successor", adoption);
        if (settlement is not null)
            harness.Operations.FinishWake(decision.HostId, decision.SessionId, action.ActionId, "successor", settlement,
                new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2));
        harness.Restart();
        var before = harness.Get(action);
        var sources = OriginalSources(harness.LedgerPath);
        var deliveries = Deliveries(harness.LedgerPath);
        var saved = File.ReadAllBytes(harness.LedgerPath);
        RefusesWithoutWrite(harness, saved, () => harness.Registry.StopDecision(decision with { SessionEpoch = adoption.NextSessionEpoch }));

        var stopped = harness.Registry.StopDecision(decision);

        Assert.Equal("cancelled", stopped.State);
        AssertSame(decision, stopped.StopDecision);
        AssertSame(decision, stopped.LatestEvent);
        AssertSame(adoption, stopped.WakeAdoption);
        Assert.Equal(adoption.NextSessionEpoch, stopped.ActiveEpoch);
        Assert.Equal(before.WakeRunId, stopped.WakeRunId);
        Assert.Equal(before.SettledUsage, stopped.SettledUsage);
        Assert.Equal(sources, OriginalSources(harness.LedgerPath));
        Assert.Equal(deliveries, Deliveries(harness.LedgerPath));
        saved = File.ReadAllBytes(harness.LedgerPath);
        harness.Restart();
        AssertSame(stopped, harness.Registry.StopDecision(Clone(decision)));
        RefusesWithoutWrite(harness, saved, () => harness.Registry.Wake(decision, adoption));
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Apply(decision, "successor", adoption));
        var childRequest = Request() with { SessionEpoch = adoption.NextSessionEpoch, Observation = adoption.Observation };
        RefusesWithoutWrite(harness, saved, () => harness.Operations.Reserve(childRequest, Action(childRequest, "late-child", "successor"),
            new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2)));
        Assert.False(harness.Operations.HasOpen(decision.HostId, decision.SessionId));
        Assert.Equal(0, harness.Planners.Created);
    }

    [Fact]
    public async Task New_epoch_child_admission_atomically_settles_parent_and_survives_restart_without_FinishWake()
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        var adoption = Adoption(5);
        harness.Operations.Apply(decision, "successor", adoption);
        var sources = OriginalSources(harness.LedgerPath);
        var deliveries = Deliveries(harness.LedgerPath);
        var request = Request() with { SessionEpoch = adoption.NextSessionEpoch, Observation = adoption.Observation };
        var child = Action(request, "child-action", "successor");
        var usage = new HostRunUsage(Steps: 2, Refinements: 1, Continuations: 1, ProviderCalls: 3);
        harness.Operations.Reserve(request, child, usage);
        var beforeAdmission = File.ReadAllBytes(harness.LedgerPath);
        RefusesWithoutWrite(harness, beforeAdmission, () => harness.Operations.Admit(request.HostId, request.SessionId,
            Admission(child) with { SessionEpoch = decision.SessionEpoch }));

        harness.Operations.Admit(request.HostId, request.SessionId, Admission(child));
        // A fresh reader sees both sides without a registry, execution-finally, or repair write.
        var reloaded = new HostOperationStore(harness.OperationRoot);
        var entries = reloaded.List(request.HostId, request.SessionId);
        var parent = Assert.Single(entries, entry => entry.Action.ActionId == action.ActionId);
        var admitted = Assert.Single(entries, entry => entry.Action.ActionId == child.ActionId);
        Assert.Equal("continued", parent.State);
        Assert.Equal(usage, parent.SettledUsage);
        Assert.Equal(new HostRunUsage(Steps: 1, ProviderCalls: 1), parent.Usage);
        AssertSame(decision, parent.LatestEvent);
        AssertSame(adoption, parent.WakeAdoption);
        Assert.Equal("parked", admitted.State);
        Assert.Equal(parent.WakeRunId, admitted.Action.RunId);
        Assert.Equal(parent.ActiveEpoch, admitted.Request.SessionEpoch);
        Assert.Equal(adoption.NextSessionEpoch, admitted.Admission!.SessionEpoch);
        Assert.Equal(usage, admitted.Usage);
        Assert.Equal(sources, OriginalSources(harness.LedgerPath));
        Assert.Equal(deliveries, Deliveries(harness.LedgerPath));
        var saved = File.ReadAllBytes(harness.LedgerPath);
        harness.Restart();
        AssertSame(admitted, harness.Operations.Admit(request.HostId, request.SessionId, Admission(child)));
        RefusesWithoutWrite(harness, saved, () => harness.Operations.StopDecision(decision));
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
    }

    [Fact]
    public async Task Completed_adopted_successor_refuses_stop_without_rewriting_settlement()
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        var adoption = Adoption(3);
        harness.Operations.Apply(decision, "successor", adoption);
        var completed = harness.Operations.FinishWake(decision.HostId, decision.SessionId, action.ActionId, "successor", "completed",
            new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2));
        var saved = File.ReadAllBytes(harness.LedgerPath);
        for (var reload = 0; reload < 2; reload++)
        {
            RefusesWithoutWrite(harness, saved, () => harness.Registry.StopDecision(decision));
            AssertSame(completed, harness.Get(action));
            harness.Restart();
        }
        Assert.Equal(0, harness.Planners.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_process_claim_cannot_replan_with_missing_changed_or_fresh_adoption(bool originallyAdopted)
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        var adoption = Adoption(3);
        var original = harness.Operations.Apply(decision, "lost-process-run", originallyAdopted ? adoption : null);
        harness.Restart();
        var saved = File.ReadAllBytes(harness.LedgerPath);
        var exact = Assert.Throws<HostProtocolException>(() => harness.Registry.Wake(Clone(decision), originallyAdopted ? Clone(adoption) : null));
        Assert.Equal("operation.wake_interrupted", exact.Code);
        RefusesWithoutWrite(harness, saved, () => harness.Registry.Wake(decision, originallyAdopted ? null : adoption));
        RefusesWithoutWrite(harness, saved, () => harness.Registry.Wake(decision, adoption with
        {
            AdoptionId = "replacement-adoption", NextSessionEpoch = "replacement-owner", Observation = Observation("replacement-observation", 4)
        }));
        AssertSame(original, harness.Get(action));
        Assert.Empty(harness.Registry.List());
        Assert.Equal(0, harness.Planners.Created);
        Assert.Equal(0, harness.Planners.Planned);
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
    }

    [Fact]
    public async Task Registry_binds_adopted_observation_and_retains_objective_provider_limits_and_cumulative_usage()
    {
        await using var harness = new AdoptionHarness();
        var initialUsage = new HostRunUsage(Steps: 2, Refinements: 1, Continuations: 1, ProviderCalls: 3);
        var action = harness.Seed(initialUsage);
        var original = harness.Get(action);
        var decision = Event(action, "decision", 1, 2);
        var adoption = Adoption(5);
        var successor = harness.Track(harness.Registry.Wake(decision, adoption));
        AssertSame(original.Request with { SessionEpoch = adoption.NextSessionEpoch, Observation = adoption.Observation }, successor.Request);
        Assert.Equal(initialUsage with { Continuations = 2 }, Usage(successor));
        Assert.Same(successor, harness.Registry.Wake(Clone(decision), Clone(adoption)));
        var saved = File.ReadAllBytes(harness.LedgerPath);
        RefusesWithoutWrite(harness, saved, () => harness.Registry.Wake(decision));
        RefusesWithoutWrite(harness, saved, () => harness.Registry.Wake(decision, adoption with { NextSessionEpoch = "other-owner" }));
        Assert.Equal(0, harness.Planners.Created);

        var connection = new CaptureConnection();
        successor.Attach(connection);
        successor.Start();
        var child = await connection.NextActionAsync();
        Assert.Equal(adoption.NextSessionEpoch, child.SessionEpoch);
        Assert.Equal(adoption.Observation.Revision, child.ExpectedRevision);
        Assert.True(successor.AcceptResult(Admission(child)));
        await successor.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var expected = new HostRunUsage(Steps: 3, Refinements: 1, Continuations: 2, ProviderCalls: 4);
        Assert.Equal(expected, Usage(successor));
        harness.Restart();
        Assert.Equal(expected, harness.Get(action).SettledUsage);
        Assert.Equal(expected, harness.Get(child).Usage);
        AssertSame(successor.Request, harness.Get(child).Request);
        AssertSame(original.Request.Provider, harness.Planners.Settings);
        Assert.Equal(1, harness.Planners.Created);
        Assert.Equal(1, harness.Planners.Planned);
    }

    [Fact]
    public async Task Invalid_new_epoch_context_is_refused_before_claiming_or_creating_a_planner()
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        var adoption = Adoption(3);
        harness.Contexts.Open(Request() with { SessionEpoch = adoption.NextSessionEpoch, Observation = Observation("newer-context", 10) });
        var saved = File.ReadAllBytes(harness.LedgerPath);
        RefusesWithoutWrite(harness, saved, () => harness.Registry.Wake(decision, adoption));
        Assert.Null(harness.Get(action).WakeRunId);
        Assert.Null(harness.Get(action).WakeAdoption);
        Assert.Empty(harness.Registry.List());
        Assert.Equal(0, harness.Planners.Created);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("missing-adoption")]
    [InlineData("observation")]
    [InlineData("rewritten-decision")]
    public async Task Replay_rejects_forged_adopted_projection_even_with_a_recomputed_outer_checksum(string corruption)
    {
        await using var harness = new AdoptionHarness();
        var action = harness.Seed();
        var decision = Event(action, "decision", 1, 2);
        var adoption = Adoption(3);
        harness.Operations.Apply(decision, "successor", adoption);
        harness.Operations.FinishWake(decision.HostId, decision.SessionId, action.ActionId, "successor", "blocked",
            new HostRunUsage(Steps: 2, Continuations: 1, ProviderCalls: 2));
        var file = JsonNode.Parse(File.ReadAllText(harness.LedgerPath))!;
        var entry = file["payload"]!["entries"]![0]!["entry"]!;
        switch (corruption)
        {
            case "owner": entry["activeEpoch"] = decision.SessionEpoch; break;
            case "missing-adoption": entry.AsObject().Remove("wakeAdoption"); break;
            case "observation": entry["wakeAdoption"]!["observation"]!["data"] = new JsonObject { ["forged"] = true }; break;
            case "rewritten-decision": entry["latestEvent"]!["sessionEpoch"] = adoption.NextSessionEpoch; break;
        }
        file["contentHash"] = ProtocolValidation.Digest(JsonSerializer.SerializeToElement(file["payload"], HostProtocol.Json));
        File.WriteAllText(harness.LedgerPath, file.ToJsonString(HostProtocol.Json));
        var forged = File.ReadAllBytes(harness.LedgerPath);
        Assert.Throws<InvalidDataException>(() => new HostOperationStore(harness.OperationRoot));
        Assert.Equal(forged, File.ReadAllBytes(harness.LedgerPath));
    }

    private static void RefusesWithoutWrite(AdoptionHarness harness, byte[] saved, Action operation)
    {
        Assert.Throws<HostProtocolException>(operation);
        Assert.Equal(saved, File.ReadAllBytes(harness.LedgerPath));
    }

    private static void AssertSame<T>(T expected, T actual) => Assert.Equal(ProtocolValidation.Digest(expected!), ProtocolValidation.Digest(actual!));
    private static T Clone<T>(T value) => HostProtocol.Element(value!).Deserialize<T>(HostProtocol.Json)!;
    private static HostRunUsage Usage(HostRun run) => HostProtocol.Element(run.Snapshot()).GetProperty("usage").Deserialize<HostRunUsage>(HostProtocol.Json)!;

    // Keep receipt/hash and history comparisons separate from the changing entry projection.
    private static string[] OriginalSources(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var stored = json.RootElement.GetProperty("payload").GetProperty("entries")[0];
        var entry = stored.GetProperty("entry");
        return [.. new[] { "request", "action", "admission", "usage" }.Select(name => entry.GetProperty(name).GetRawText()),
            .. new[] { "requestHash", "actionHash", "admissionHash" }.Select(name => stored.GetProperty(name).GetRawText())];
    }

    private static string[] Deliveries(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("payload").GetProperty("entries")[0].GetProperty("events")
            .EnumerateArray().Select(item => item.GetRawText()).ToArray();
    }

    private static HostRunRequest Request() => new(1, "adoption-host", "adoption-session", "adoption-epoch", "adoption-scope", "adoption-perspective",
        "adoption-objective", "Complete durable host work with authoritative evidence.", Observation("initial", 0),
        [new HostCapability("host.operate", "Operate", "Admit durable host work.", ToolKind.Action,
            ToolEffect.WritesLocalState, ToolInputSchema.Create(), DurableHandoff: true)],
        new ProviderSettings("fixture", "offline-model", "high"),
        new HostRunLimits(MaxSteps: 6, MaxRefinements: 4, MaxPlanContinuations: 4, TimeoutSeconds: 20, ActionTimeoutSeconds: 10));

    private static HostObservation Observation(string id, long revision) => new(id, revision,
        new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero).AddSeconds(revision), HostProtocol.Element(new { revision, decisionRequired = true }));
    private static HostDecisionAdoption Adoption(long revision) => new("original-adoption", "new-owner-epoch", Observation("adoption-observation", revision));
    private static HostActionRequest Action(HostRunRequest request, string actionId, string runId) => new(actionId, runId,
        "runner-" + actionId, "step-" + actionId, request.SessionId, request.SessionEpoch, request.Capabilities[0].Id,
        ProtocolValidation.Digest(request.Capabilities), HostProtocol.Element(new { }), request.Observation.Revision, DateTimeOffset.UtcNow.AddMinutes(1));
    private static HostActionResult Admission(HostActionRequest action) => new(action.ActionId, action.SessionId, action.SessionEpoch,
        "applied", action.ExpectedRevision, action.ExpectedRevision + 1, "admission-" + action.ActionId, "Host admitted work, not completion.",
        Observation("admitted-" + action.ActionId, action.ExpectedRevision + 1),
        Operation: new HostOperationAdmission("operation-" + action.ActionId, "Host owns execution.", HostProtocol.Element(new { ticks = 0, limit = (int?)null })));
    private static HostOperationEvent Event(HostActionRequest action, string kind, long sequence, long revision) => new(
        "adoption-host", action.SessionId, action.SessionEpoch, action.ActionId, "operation-" + action.ActionId,
        sequence, "event-" + action.ActionId + "-" + sequence, kind, Observation("event-observation-" + action.ActionId + "-" + sequence, revision),
        "The host reported " + kind + ".", HostProtocol.Element(new { ticks = sequence * 100, limit = (int?)null }));

    private sealed class CountingPlannerFactory : ILabPlannerFactory
    {
        public int Created;
        public int Planned;
        public ProviderSettings? Settings;
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
        {
            Interlocked.Increment(ref Created);
            Settings = settings;
            return new CountingPlanner(this, onStreamEvent);
        }
        public IReadOnlyList<ProviderMetadata> GetProviders() => [];
    }

    private sealed class CountingPlanner(CountingPlannerFactory owner, Action<LlmStreamEvent> onStreamEvent) : IWorkflowPlanner
    {
        public Task<WorkflowPlan> CreatePlanAsync(PlanningRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref owner.Planned);
            onStreamEvent(new LlmStreamEvent(LlmStreamEventKind.Started));
            onStreamEvent(new LlmStreamEvent(LlmStreamEventKind.Completed, Response: new LlmResponse("fixture", "offline-model", "Local fixture only.")));
            return Task.FromResult(new WorkflowPlan("adoption-plan", 1,
                [new PlanStep("adoption-step", "host.operate", ToolKind.Action, ToolEffect.WritesLocalState, new Dictionary<string, object?>())], "Admit one bounded operation."));
        }
        public Task<WorkflowPlan> RefinePlanAsync(PlanningRequest request, Observation observation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Admission must park without replanning.");
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

    private sealed class AdoptionHarness : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentica-decision-adoption-tests", Guid.NewGuid().ToString("N"));
        private readonly List<HostRun> _runs = [];
        public CountingPlannerFactory Planners { get; } = new();
        public HostOperationStore Operations { get; private set; } = null!;
        public ActionCustodyStore Custody { get; private set; } = null!;
        public HostContextStore Contexts { get; private set; } = null!;
        public HostRunRegistry Registry { get; private set; } = null!;
        public string OperationRoot => Path.Combine(_root, "operations");
        public string LedgerPath => Path.Combine(OperationRoot, HostOperationStore.LedgerFileName);
        public string CustodyPath => Path.Combine(_root, "custody", ActionCustodyStore.LedgerFileName);
        public AdoptionHarness() => Open();
        private void Open()
        {
            Operations = new HostOperationStore(OperationRoot);
            Custody = new ActionCustodyStore(Path.Combine(_root, "custody"));
            Contexts = new HostContextStore(_root);
            Registry = new HostRunRegistry(Contexts, Planners, Custody, Operations);
        }
        public HostActionRequest Seed(HostRunUsage? usage = null)
        {
            var request = Request();
            var action = Action(request, "original-action", "original-run");
            var context = Contexts.Open(request);
            context.RecordActionDispatch(action);
            Operations.Reserve(request, action, usage ?? new HostRunUsage(Steps: 1, ProviderCalls: 1));
            Custody.Reserve(request.HostId, request.SessionId, request.SessionEpoch, action.RunId, action, request.Capabilities[0], request.ObjectiveId);
            var admission = Admission(action);
            Operations.Admit(request.HostId, request.SessionId, admission);
            context.RecordActionResult(action, admission);
            Custody.Resolve(request.HostId, request.SessionId, action.ActionId, admission);
            return action;
        }
        public HostOperationEntry Get(HostActionRequest action) => Operations.Get("adoption-host", action.SessionId, action.ActionId);
        public void Restart()
        {
            Assert.All(_runs, run => Assert.True(run.Terminal || run.Completion.IsCompleted));
            Registry.Dispose();
            Open();
        }
        public HostRun Track(HostRun run) { _runs.Add(run); return run; }
        public async ValueTask DisposeAsync()
        {
            Registry.Dispose();
            await Task.WhenAll(_runs.Select(run => run.Completion)).WaitAsync(TimeSpan.FromSeconds(10));
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
