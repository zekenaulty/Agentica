using Agentica;
using Agentica.Artifacts;
using Agentica.Lab.Scenarios.Campaign;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class CampaignHarnessTests
{
    [Fact]
    public void Campaign_graph_computes_available_branched_milestones()
    {
        var definition = DungeonCampaignBoard.Create();
        var state = new CampaignState(definition);

        var initial = CampaignGraph.AvailableMilestones(definition, state);

        Assert.Equal(["acquire_lantern", "acquire_bronze_key"], initial.Select(item => item.MilestoneId));

        state.CompletedMilestones.Add("acquire_lantern");
        var afterLantern = CampaignGraph.AvailableMilestones(definition, state);

        Assert.Contains(afterLantern, milestone => milestone.MilestoneId == "explore_dark_archive");
        Assert.Contains(afterLantern, milestone => milestone.MilestoneId == "optional_cache");
        Assert.DoesNotContain(afterLantern, milestone => milestone.MilestoneId == "open_final_gate");
    }

    [Fact]
    public void Campaign_graph_handles_multiple_dependencies_and_optional_skip()
    {
        var definition = DungeonCampaignBoard.Create();
        var state = new CampaignState(definition);
        state.CompletedMilestones.AddRange(
        [
            "acquire_lantern",
            "acquire_bronze_key",
            "explore_dark_archive",
            "unlock_bronze_vault",
            "recover_moon_sigil",
            "recover_sun_sigil"
        ]);

        var available = CampaignGraph.AvailableMilestones(definition, state);

        Assert.Contains(available, milestone => milestone.MilestoneId == "open_final_gate");
        Assert.Contains(available, milestone => milestone.MilestoneId == "optional_cache");
        Assert.False(CampaignGraph.RequiredMilestonesComplete(definition, state));

        state.CompletedMilestones.Add("open_final_gate");

        Assert.True(CampaignGraph.RequiredMilestonesComplete(definition, state));
    }

    [Fact]
    public void Campaign_graph_excludes_blocked_prerequisites()
    {
        var definition = DungeonCampaignBoard.Create();
        var state = new CampaignState(definition);
        state.BlockedMilestones.Add("acquire_lantern");

        var available = CampaignGraph.AvailableMilestones(definition, state);

        Assert.DoesNotContain(available, milestone => milestone.MilestoneId == "acquire_lantern");
        Assert.DoesNotContain(available, milestone => milestone.MilestoneId == "explore_dark_archive");
        Assert.Contains(available, milestone => milestone.MilestoneId == "acquire_bronze_key");
    }

    [Fact]
    public void Campaign_acceptance_uses_receipts_artifacts_and_host_state_not_report_prose()
    {
        var milestone = new CampaignMilestone(
            "accepted",
            "Accept only with proof.",
            [],
            Optional: false,
            Priority: 1,
            RunOutcomeStatus.Succeeded,
            [
                new CampaignRequiredEvidence(CampaignEvidenceKind.Artifact, ArtifactKind: "proof.artifact"),
                new CampaignRequiredEvidence(CampaignEvidenceKind.Receipt, ToolId: "proof.tool", ReceiptStatus: ReceiptStatus.Succeeded),
                new CampaignRequiredEvidence(CampaignEvidenceKind.HostState, HostStateKey: "hostReady", HostStateValue: true)
            ],
            new Dictionary<string, object?>());
        var envelope = Envelope(
            RunOutcomeStatus.Succeeded,
            [Receipt("proof.tool", ReceiptStatus.Succeeded)],
            [Artifact("proof.artifact")],
            reportSummary: "This report could claim anything.");

        var accepted = CampaignAcceptance.Evaluate(milestone, envelope, new Dictionary<string, object?>
        {
            ["hostReady"] = true
        });
        var missingArtifact = CampaignAcceptance.Evaluate(milestone, envelope with
        {
            Details = envelope.Details with { Artifacts = [] }
        }, new Dictionary<string, object?> { ["hostReady"] = true });
        var missingReceipt = CampaignAcceptance.Evaluate(milestone, envelope with
        {
            Receipts = new ReceiptEnvelope([])
        }, new Dictionary<string, object?> { ["hostReady"] = true });
        var missingHostState = CampaignAcceptance.Evaluate(milestone, envelope, new Dictionary<string, object?>
        {
            ["hostReady"] = false
        });

        Assert.True(accepted.Accepted);
        Assert.False(missingArtifact.Accepted);
        Assert.False(missingReceipt.Accepted);
        Assert.False(missingHostState.Accepted);
    }

    [Fact]
    public void Campaign_snapshot_carries_evidence_refs_without_full_envelope_bulk()
    {
        var definition = DungeonCampaignBoard.Create();
        var state = new CampaignState(definition);
        state.CompletedMilestones.Add("acquire_lantern");
        var receipt = Receipt(DungeonCampaignToolIds.CompleteMilestone, ReceiptStatus.Succeeded);
        var artifact = Artifact("campaign.milestone_completed");
        state.PriorRunRefs.Add(new CampaignPriorRunRef(
            "acquire_lantern",
            "run_lantern",
            RunOutcomeStatus.Succeeded,
            [new EvidenceRef("artifact", artifact.ArtifactId)]));
        var envelope = Envelope(RunOutcomeStatus.Succeeded, [receipt], [artifact]);

        var snapshot = CampaignProgressSnapshotCompiler.Compile(
            definition,
            state,
            definition.Milestones[0],
            envelope,
            new Dictionary<string, object?>
            {
                ["hasLantern"] = true,
                ["largeIrrelevantText"] = new string('x', 512)
            });

        Assert.Contains("acquire_lantern", snapshot.CompletedMilestones);
        Assert.Contains(snapshot.ReceiptRefs, evidence => evidence.RefId == receipt.ReceiptId);
        Assert.Contains(snapshot.ArtifactRefs, evidence => evidence.RefId == artifact.ArtifactId);
        Assert.Contains(snapshot.ProvenFacts, fact =>
            fact.FactId == "milestone.acquire_lantern.completed" &&
            fact.Evidence.Any(evidence => evidence.RefId == artifact.ArtifactId));
        Assert.Contains(snapshot.ProvenFacts, fact => fact.FactId == "host.hasLantern");
        Assert.False(snapshot.HostStateProjection.ContainsKey("largeIrrelevantText"));
        Assert.DoesNotContain(snapshot.ProvenFacts, fact => fact.Summary.Contains("largeIrrelevantText", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Campaign_runner_passes_progress_snapshot_without_prior_envelopes()
    {
        var definition = new CampaignDefinition(
            "context_campaign",
            "Context Campaign",
            "Prove context carry-forward.",
            [
                new CampaignMilestone(
                    "first",
                    "First milestone.",
                    [],
                    Optional: false,
                    Priority: 1,
                    RunOutcomeStatus.Succeeded,
                    [new CampaignRequiredEvidence(CampaignEvidenceKind.Receipt, ToolId: "context.complete", ReceiptStatus: ReceiptStatus.Succeeded)],
                    new Dictionary<string, object?>()),
                new CampaignMilestone(
                    "second",
                    "Second milestone.",
                    ["first"],
                    Optional: false,
                    Priority: 2,
                    RunOutcomeStatus.Succeeded,
                    [new CampaignRequiredEvidence(CampaignEvidenceKind.Receipt, ToolId: "context.complete", ReceiptStatus: ReceiptStatus.Succeeded)],
                    new Dictionary<string, object?>())
            ],
            []);
        var state = new CampaignState(definition);
        var planner = new ContextRecordingPlanner();
        var runner = new CampaignRunner(
            definition,
            state,
            _ => planner,
            () => ToolCatalog.Create(TestToolRegistration.Create(
                new ToolDescriptor("context.complete", "Context Complete", ToolKind.Action, ToolEffect.WritesLocalState),
                new ContextCompleteTool())),
            new DeterministicOutcomeReporter(),
            new InMemoryEventSink(),
            () => new Dictionary<string, object?>());

        var result = await runner.RunAsync();

        Assert.Equal(CampaignRunStatus.Succeeded, result.State.Status);
        Assert.Equal(2, planner.RequestContexts.Count);
        var secondContext = planner.RequestContexts[1];
        Assert.True(secondContext.ContainsKey("campaign.progress"));
        Assert.True(secondContext.ContainsKey("campaign.workingContext"));
        Assert.False(secondContext.ContainsKey("priorEnvelopes"));
        var snapshot = Assert.IsType<CampaignProgressSnapshot>(secondContext["campaign.progress"]);
        Assert.Contains("first", snapshot.CompletedMilestones);
        var workingContext = Assert.IsType<CampaignWorkingContextSnapshot>(secondContext["campaign.workingContext"]);
        Assert.Contains(workingContext.ProvenFacts, fact => fact.Summary.Contains("first", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Campaign_working_context_is_bounded_and_keeps_unsupported_claims_out_of_proven_facts()
    {
        var definition = DungeonCampaignBoard.Create();
        var state = new CampaignState(definition);
        state.CompletedMilestones.Add("acquire_lantern");
        var progress = new CampaignProgressSnapshot(
            definition.CampaignId,
            definition.Goal,
            "acquire_lantern",
            ["acquire_lantern"],
            Enumerable.Range(0, 20)
                .Select(index => new CampaignFact(
                    $"unsupported.{index}",
                    $"Unsupported claim {index}",
                    []))
                .Concat(
                [
                    new CampaignFact(
                        "milestone.acquire_lantern.completed",
                        "Milestone acquire_lantern completed.",
                        [new EvidenceRef("artifact", "artifact_lantern")])
                ])
                .ToArray(),
            Enumerable.Range(0, 20).Select(index => $"Outstanding fact {index}").ToArray(),
            [new EvidenceRef("artifact", "artifact_lantern")],
            [new EvidenceRef("receipt", "receipt_lantern")],
            Enumerable.Range(0, 20).Select(index => $"Blocker {index}").ToArray(),
            new Dictionary<string, object?>());

        var context = CampaignWorkingContextCompiler.Compile(
            definition,
            definition.Milestones[0],
            progress,
            null,
            progress.Blockers);

        Assert.DoesNotContain(context.ProvenFacts, fact => fact.FactId.StartsWith("unsupported.", StringComparison.Ordinal));
        Assert.Contains(context.ProvenFacts, fact => fact.FactId == "milestone.acquire_lantern.completed");
        Assert.True(context.OpenQuestions.Count <= 8);
        Assert.True(context.KnownBlockers.Count <= 8);
        Assert.True(context.NextConsiderations.Count <= 8);
        Assert.Contains(context.EvidenceRefs, evidence => evidence.RefId == "artifact_lantern");
    }

    [Fact]
    public async Task Campaign_runner_completes_dungeon_campaign_across_separate_runs()
    {
        var definition = DungeonCampaignBoard.Create();
        var state = new CampaignState(definition);
        var session = new DungeonCampaignSession(definition);
        var runner = new CampaignRunner(
            definition,
            state,
            milestone => new DungeonCampaignDeterministicPlanner(milestone),
            () => DungeonCampaignTools.CreateCatalog(session),
            new DeterministicOutcomeReporter(),
            new InMemoryEventSink(),
            session.PublicSnapshot);

        var result = await runner.RunAsync();

        Assert.Equal(CampaignRunStatus.Succeeded, result.State.Status);
        Assert.Equal(7, result.Envelopes.Count);
        Assert.Contains("open_final_gate", result.State.CompletedMilestones);
        Assert.DoesNotContain("optional_cache", result.State.CompletedMilestones);
        Assert.Equal(result.Envelopes.Count, result.State.PriorRunRefs.Count);
        Assert.True((bool)result.State.ProgressSnapshot.HostStateProjection["finalGateOpen"]!);
    }

    [Fact]
    public async Task Campaign_runner_stops_on_unmet_milestone_acceptance()
    {
        var definition = DungeonCampaignBoard.Create();
        var state = new CampaignState(definition);
        var session = new DungeonCampaignSession(definition);
        var runner = new CampaignRunner(
            definition,
            state,
            milestone => new BadMilestonePlanner(milestone),
            () => DungeonCampaignTools.CreateCatalog(session),
            new DeterministicOutcomeReporter(),
            new InMemoryEventSink(),
            session.PublicSnapshot);

        var result = await runner.RunAsync();

        Assert.Equal(CampaignRunStatus.Blocked, result.State.Status);
        Assert.Contains("acquire_lantern", result.State.BlockedMilestones);
        Assert.Contains(result.State.ProgressSnapshot.Blockers, blocker => blocker.Contains("Host state", StringComparison.Ordinal));
    }

    private static OutcomeEnvelope Envelope(
        RunOutcomeStatus status,
        IReadOnlyList<Receipt> receipts,
        IReadOnlyList<Artifact> artifacts,
        string reportSummary = "Report summary.") =>
        new(
            new RunOutcome(
                "run_test",
                status,
                status == RunOutcomeStatus.Succeeded ? StopReason.Complete : StopReason.ToolFailure,
                [],
                [],
                artifacts.Select(artifact => new EvidenceRef("artifact", artifact.ArtifactId)).ToArray()),
            new OutcomeReport("report_test", reportSummary, []),
            new ReceiptEnvelope(receipts),
            new DetailEnvelope(
                new RunRequest("test"),
                [],
                [],
                [],
                artifacts,
                [],
                [],
                []));

    private static Receipt Receipt(string toolId, ReceiptStatus status) =>
        new(
            AgenticaIds.New("receipt"),
            "step_test",
            toolId,
            status,
            "Receipt.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());

    private static Artifact Artifact(string kind) =>
        new(
            AgenticaIds.New("artifact"),
            kind,
            new Dictionary<string, object?>(),
            []);

    private sealed class BadMilestonePlanner : IWorkflowPlanner
    {
        private readonly CampaignMilestone _milestone;

        public BadMilestonePlanner(CampaignMilestone milestone)
        {
            _milestone = milestone;
        }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkflowPlan(
                "bad_campaign_plan",
                1,
                [
                    new PlanStep(
                        "bad_complete",
                        DungeonCampaignToolIds.CompleteMilestone,
                        ToolKind.Action,
                        ToolEffect.WritesLocalState,
                        new Dictionary<string, object?>
                        {
                            ["milestoneId"] = _milestone.MilestoneId
                        })
                ],
                "Bad planner skips required host action."));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            CreatePlanAsync(request, cancellationToken);
    }

    private sealed class ContextRecordingPlanner : IWorkflowPlanner
    {
        public List<IReadOnlyDictionary<string, object?>> RequestContexts { get; } = [];

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            RequestContexts.Add(request.Request.Context ?? new Dictionary<string, object?>());
            return Task.FromResult(new WorkflowPlan(
                $"context_plan_{RequestContexts.Count}",
                1,
                [
                    new PlanStep(
                        $"context_step_{RequestContexts.Count}",
                        "context.complete",
                        ToolKind.Action,
                        ToolEffect.WritesLocalState,
                        new Dictionary<string, object?>())
                ],
                "Context recording plan."));
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            CreatePlanAsync(request, cancellationToken);
    }

    private sealed class ContextCompleteTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResult(Receipt("context.complete", ReceiptStatus.Succeeded) with
            {
                StepId = invocation.StepId
            }));
    }
}
