using Agentica.NyxLearningHost;
using Agentica.Observations;
using Agentica.Orchestration.Acceptance;
using Agentica.Planning;
using Agentica.Projection;
using Agentica.Requests;
using Agentica.Tests.Fixtures.NyxLearning;
using System.Text.Json;

namespace Agentica.Tests;

public sealed class ObligationHostConformanceTests
{
    [Fact]
    public void Optional_projector_binds_the_existing_surface_and_freezes_current_custody()
    {
        var current = SyntheticObligationScenarios.All().Single(item => item.ScenarioId == "lost-effect-response-v1").ExpectedFulfillment;
        var projector = new ObligationPlanningFrameProjector(_ => current);
        var request = new RunRequest("Inspect the current original attempt.");
        var projectionRequest = new PlanningFrameProjectionRequest("run", 1, request,
            PlanningExecutionContext.Empty, [], [], [], null);
        var first = Assert.Single(projector.Project(projectionRequest));
        var second = Assert.Single(projector.Project(projectionRequest));
        Assert.Equal(first.FrameId, second.FrameId);
        Assert.Equal("obligation-activation", first.Kind);
        var payload = Assert.IsType<JsonElement>(first.Payload["fulfillment"]);
        Assert.Equal("OutcomeUnknown", payload.GetProperty("attempts")[0].GetProperty("effectCustody").GetString());
        Assert.Equal(current.Manifest.ContentIdentity,
            payload.GetProperty("manifest").GetProperty("contentIdentity").GetString());
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, object?>)first.Payload).Clear());

        var previous = current;
        current = new(CopyManifest(current.Manifest, current.Obligation with { AcceptanceRevision = "2" }),
            current.Satisfaction, current.Resolution, current.Citations, current.ResidualRequirements, current.Attempts);
        var revised = Assert.Single(projector.Project(projectionRequest));
        Assert.Equal(previous.Manifest.ContentIdentity, current.Manifest.ContentIdentity);
        Assert.NotEqual(first.FrameId, revised.FrameId);
        Assert.Equal("1", payload.GetProperty("manifest").GetProperty("obligation").GetProperty("acceptanceRevision").GetString());
    }

    [Fact]
    public void Projector_refuses_an_oversized_actual_payload()
    {
        var original = SyntheticObligationScenarios.All()[0].ExpectedFulfillment;
        var manifest = CopyManifest(original.Manifest, segments:
            [original.Manifest.PresentedEvidence[0] with { Text = new string('a', 5000) }]);
        var state = new ObligationFulfillment(manifest, ObligationSatisfaction.Unsatisfied,
            ObligationResolutionState.Ready, [], ["need.acceptance"]);
        var projector = new ObligationPlanningFrameProjector(_ => state, maximumPayloadBytes: 1024);
        Assert.Throws<InvalidOperationException>(() => projector.Project(new("run", 1,
            new RunRequest("Inspect."), PlanningExecutionContext.Empty, [], [], [], null)));
    }

    [Fact]
    public async Task Adapter_requires_current_acceptance_and_canonical_envelope_evidence()
    {
        var run = await LearningExperiment.RunAsync(LearningScenario.ExactSource);
        var outcome = Assert.IsType<Agentica.Outcomes.OutcomeEnvelope>(run.Outcome);
        var manifest = run.Fulfillment.Manifest;
        var invented = new TaskAcceptanceResult(TaskAcceptanceStatus.Accepted, ["Looks correct."],
            [new("artifact", "invented")]);
        Assert.Equal(TaskAcceptanceStatus.Rejected,
            ObligationFulfillmentAdapter.Evaluate(run.Fulfillment, run.Fulfillment, outcome, invented).Status);
        var stale = CopyManifest(manifest, manifest.Obligation with { AcceptanceRevision = "2" });
        Assert.Equal(TaskAcceptanceStatus.InvalidatedPlan,
            ObligationFulfillmentAdapter.Evaluate(CopyFulfillment(run.Fulfillment, stale), run.Fulfillment, outcome, run.Acceptance).Status);
        var changed = CopyManifest(manifest, segments:
            [manifest.PresentedEvidence[0] with { SourceRevision = "5" }]);
        Assert.Equal(TaskAcceptanceStatus.InvalidatedPlan,
            ObligationFulfillmentAdapter.Evaluate(CopyFulfillment(run.Fulfillment, changed), run.Fulfillment, outcome, run.Acceptance).Status);
        var hostRejected = new TaskAcceptanceResult(TaskAcceptanceStatus.Rejected,
            ["The consumer applies an additional current acceptance rule."], run.Acceptance.EvidenceRefs);
        Assert.Equal(TaskAcceptanceStatus.Rejected,
            ObligationFulfillmentAdapter.Evaluate(run.Fulfillment, run.Fulfillment, outcome, hostRejected).Status);
    }

    [Fact]
    public async Task Host_decision_cannot_promote_unknown_original_custody_and_returned_lists_are_detached()
    {
        var unresolved = await LearningExperiment.RunAsync(LearningScenario.EffectRecovery, new(ReconcileKnown: false));
        var blocked = ObligationFulfillmentAdapter.Evaluate(unresolved.Fulfillment,
            unresolved.Fulfillment, unresolved.Outcome!,
            new(TaskAcceptanceStatus.Accepted, ["A similar state looks accepted."],
                [new("artifact", unresolved.Outcome!.Details.Artifacts[0].ArtifactId)]));
        Assert.Equal(TaskAcceptanceStatus.Blocked, blocked.Status);
        Assert.Empty(blocked.EvidenceRefs);
        Assert.Equal("original.accept", Assert.Single(unresolved.Fulfillment.Attempts).AttemptId);

        var accepted = await LearningExperiment.RunAsync(LearningScenario.ExactSource);
        var reasons = new List<string> { "Host exact-source check passed." };
        var evidence = accepted.Acceptance.EvidenceRefs.ToList();
        var mapped = ObligationFulfillmentAdapter.Evaluate(accepted.Fulfillment, accepted.Fulfillment,
            accepted.Outcome!, new(TaskAcceptanceStatus.Accepted, reasons, evidence));
        reasons[0] = "Unreviewed replacement.";
        evidence.Clear();
        Assert.Equal("Host exact-source check passed.", Assert.Single(mapped.Reasons));
        Assert.NotEmpty(mapped.EvidenceRefs);
        Assert.Throws<NotSupportedException>(() => ((IList<EvidenceRef>)mapped.EvidenceRefs).Clear());
    }

    [Fact]
    public async Task Same_content_identity_does_not_allow_unmapped_consumer_local_citation_labels()
    {
        var run = await LearningExperiment.RunAsync(LearningScenario.ExactSource);
        var original = run.Fulfillment.Manifest;
        var relabeled = CopyManifest(original, segments:
            [original.PresentedEvidence[0] with { SegmentId = "other-consumer-label" }]);
        Assert.Equal(original.ContentIdentity, relabeled.ContentIdentity);
        var current = CopyFulfillment(run.Fulfillment, relabeled,
            [run.Fulfillment.Citations[0] with { SegmentId = "other-consumer-label" }]);
        var result = ObligationFulfillmentAdapter.Evaluate(current, run.Fulfillment, run.Outcome!, run.Acceptance);
        Assert.Equal(TaskAcceptanceStatus.InvalidatedPlan, result.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Forged_omission_or_confirmation_cannot_settle_current_unknown_original_custody(bool omitOriginal)
    {
        var run = await LearningExperiment.RunAsync(LearningScenario.EffectRecovery, new(ReconcileKnown: false));
        var current = run.Fulfillment;
        var forged = new ObligationFulfillment(current.Manifest, ObligationSatisfaction.Satisfied,
            ObligationResolutionState.Ready, current.Citations, [], omitOriginal ? [] :
                [current.Attempts[0] with { EffectCustody = ObligationEffectCustody.Confirmed }]);
        var claimed = new TaskAcceptanceResult(TaskAcceptanceStatus.Accepted, ["The proposal claims completion."],
            [new("artifact", run.Outcome!.Details.Artifacts[0].ArtifactId)]);
        var result = ObligationFulfillmentAdapter.Evaluate(current, forged, run.Outcome!, claimed);
        Assert.Equal(TaskAcceptanceStatus.Blocked, result.Status);
        Assert.Equal(ObligationEffectCustody.OutcomeUnknown, Assert.Single(current.Attempts).EffectCustody);
        Assert.NotEmpty(current.ResidualRequirements);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("owner")]
    [InlineData("realization")]
    public async Task Settled_original_attempt_identity_cannot_be_dropped_or_reassigned(string change)
    {
        var run = await LearningExperiment.RunAsync(LearningScenario.EffectRecovery);
        var original = Assert.Single(run.Fulfillment.Attempts);
        ObligationAttemptReference[] attempts = change switch
        {
            "omitted" => [],
            "owner" => [original with { OwnerId = "another.host" }],
            _ => [original with { RealizationId = "another.realization" }]
        };
        var forged = new ObligationFulfillment(run.Fulfillment.Manifest, ObligationSatisfaction.Satisfied,
            ObligationResolutionState.Ready, run.Fulfillment.Citations, [], attempts);
        Assert.Equal(TaskAcceptanceStatus.InvalidatedPlan,
            ObligationFulfillmentAdapter.Evaluate(run.Fulfillment, forged, run.Outcome!, run.Acceptance).Status);
    }

    [Fact]
    public async Task Candidate_cannot_discharge_residual_work_still_retained_by_the_host()
    {
        var run = await LearningExperiment.RunAsync(LearningScenario.ExactSource);
        var current = new ObligationFulfillment(run.Fulfillment.Manifest, ObligationSatisfaction.Unsatisfied,
            ObligationResolutionState.Ready, [], ["host.additional-proof"]);
        Assert.Equal(TaskAcceptanceStatus.Rejected,
            ObligationFulfillmentAdapter.Evaluate(current, run.Fulfillment, run.Outcome!, run.Acceptance).Status);

        current = new(current.Manifest, ObligationSatisfaction.PartiallySatisfied,
            ObligationResolutionState.Ready, run.Fulfillment.Citations, ["host.first", "host.second"]);
        var partial = new ObligationFulfillment(current.Manifest, current.Satisfaction, current.Resolution,
            current.Citations, ["host.first"]);
        var decision = run.Acceptance with { Status = TaskAcceptanceStatus.PartiallyAccepted };
        Assert.Equal(TaskAcceptanceStatus.Rejected,
            ObligationFulfillmentAdapter.Evaluate(current, partial, run.Outcome!, decision).Status);
    }

    [Theory]
    [InlineData(TaskAcceptanceStatus.Rejected)]
    [InlineData(TaskAcceptanceStatus.Blocked)]
    [InlineData(TaskAcceptanceStatus.InvalidatedPlan)]
    public async Task Partial_candidate_preserves_the_explicit_host_veto(TaskAcceptanceStatus status)
    {
        var run = await LearningExperiment.RunAsync(LearningScenario.ExactSource);
        var partial = new ObligationFulfillment(run.Fulfillment.Manifest, ObligationSatisfaction.PartiallySatisfied,
            ObligationResolutionState.Ready, run.Fulfillment.Citations, ["host.remaining"]);
        var decision = new TaskAcceptanceResult(status, ["The current consumer vetoes this candidate."],
            run.Acceptance.EvidenceRefs, RequiresGraphRefinement: true);
        var result = ObligationFulfillmentAdapter.Evaluate(partial, partial, run.Outcome!, decision);
        Assert.Equal(status, result.Status);
        Assert.Equal(decision.Reasons, result.Reasons);
        Assert.True(result.RequiresGraphRefinement);
    }

    [Fact]
    public async Task Reused_existing_citation_label_cannot_silently_bind_a_different_source()
    {
        var run = await LearningExperiment.RunAsync(LearningScenario.ExactSource);
        var first = run.Fulfillment.Manifest.PresentedEvidence[0] with { SegmentId = "a", Text = "same words" };
        var second = first with { SegmentId = "b", Source = new("document", "another.source") };
        var before = CopyManifest(run.Fulfillment.Manifest, segments: [first, second]);
        var after = CopyManifest(before, segments: [first with { SegmentId = "b" }, second with { SegmentId = "a" }]);
        Assert.Equal(before.ContentIdentity, after.ContentIdentity);
        PresentedEvidenceCitation[] citations = [new("a", 0, first.Text.Length)];
        var candidate = new ObligationFulfillment(before, ObligationSatisfaction.Satisfied,
            ObligationResolutionState.Ready, citations, []);
        var current = new ObligationFulfillment(after, candidate.Satisfaction, candidate.Resolution, citations, []);
        // Even equal quoted text cannot substitute a different attributable source.
        Assert.Equal(before.ReadCitation(citations[0]), after.ReadCitation(citations[0]));
        Assert.Equal(TaskAcceptanceStatus.InvalidatedPlan,
            ObligationFulfillmentAdapter.Evaluate(current, candidate, run.Outcome!, run.Acceptance).Status);
        var rebound = new ObligationFulfillment(after, candidate.Satisfaction, candidate.Resolution,
            [citations[0] with { SegmentId = "b" }], []);
        Assert.Equal(TaskAcceptanceStatus.Accepted,
            ObligationFulfillmentAdapter.Evaluate(current, rebound, run.Outcome!, run.Acceptance).Status);
    }

    private static ObligationFulfillment CopyFulfillment(ObligationFulfillment value, ObligationInputManifest manifest,
        IReadOnlyList<PresentedEvidenceCitation>? citations = null) =>
        new(manifest, value.Satisfaction, value.Resolution, citations ?? value.Citations,
            value.ResidualRequirements, value.Attempts);

    private static ObligationInputManifest CopyManifest(
        ObligationInputManifest value, ObligationIdentity? identity = null,
        IReadOnlyList<PresentedEvidenceSegment>? segments = null) =>
        new(identity ?? value.Obligation, value.Question, value.ScopeId, value.PerspectiveId,
            segments ?? value.PresentedEvidence, value.Coverage, value.CompilerRevision,
            value.InputSchemaRevision, value.OutputSchemaRevision, value.GenerationPolicyRevision);
}

