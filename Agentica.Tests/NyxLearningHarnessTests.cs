using Agentica.Clients.Llm;
using Agentica.NyxLearningHost;
using Agentica.Orchestration.Acceptance;
using Agentica.Outcomes;
using Agentica.Projection;

namespace Agentica.Tests;

public sealed class NyxLearningHarnessTests
{
    [Theory]
    [InlineData(LearningScenario.ExactSource, 3)]
    [InlineData(LearningScenario.EffectRecovery, 2)]
    public async Task Real_loop_chooses_observes_refines_and_reaches_independent_host_acceptance(
        LearningScenario scenario, int expectedCalls)
    {
        var report = await LearningExperiment.RunAsync(scenario);
        Assert.True(report.MatchesReference);
        Assert.Equal(TaskAcceptanceStatus.Accepted, report.Acceptance.Status);
        Assert.Equal(ObligationSatisfaction.Satisfied, report.Fulfillment.Satisfaction);
        Assert.Equal(RunOutcomeStatus.Succeeded, report.Outcome!.Outcome.Status);
        Assert.Equal(expectedCalls, report.Metrics.ProviderFixtureCalls);
        Assert.Equal(expectedCalls, report.Outcome.Receipts.Items.Count);
        Assert.Equal(expectedCalls - 1, report.Outcome.Details.PlanRefinements.Count);
        Assert.Empty(report.Outcome.Details.ValidationIssues);
        Assert.Contains(report.ProviderPrompts, prompt => prompt.Contains("New observation:", StringComparison.Ordinal));
        Assert.NotEmpty(report.Outcome.Outcome.CompletionEvidence);
        Assert.Equal(0, report.Metrics.NewDomainEffectDispatches);
        Assert.DoesNotContain("inventory.accept", report.InvokedTools);
        Assert.Equal("learning.parent", report.Fulfillment.Obligation.ParentObligationId);
        Assert.Equal("1", report.Fulfillment.Obligation.AcceptanceRevision);
        Assert.All(report.Outcome.Details.PlanningFrames.Where(frame => frame.Kind == "obligation-activation"),
            frame => Assert.NotNull(frame.ToolSurfaceId));
    }

    [Fact]
    public async Task Unavailable_primary_binding_survives_and_model_selects_an_exact_source_substitute()
    {
        var report = await LearningExperiment.RunAsync(LearningScenario.ExactSource);
        Assert.Equal(["evidence.primary", "evidence.mirror", "obligation.submit"], report.InvokedTools);
        Assert.True(report.PrimaryBindingPreserved);
        Assert.Contains("primary.unavailable", report.ProviderPrompts[1], StringComparison.Ordinal);
        Assert.Contains("Use evening delivery.", report.ProviderPrompts[2], StringComparison.Ordinal);
        Assert.Equal("synthetic.primary", Assert.Single(report.Fulfillment.Manifest.PresentedEvidence).Source.RefId);
        Assert.Equal("4", report.Fulfillment.Manifest.PresentedEvidence[0].SourceRevision);
        Assert.Equal(ObligationCoverageStatus.Complete, report.Fulfillment.Manifest.Coverage.Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Missing_or_wrong_source_evidence_leaves_residual_work(bool mirrorAvailable, bool mirrorExact)
    {
        var report = await LearningExperiment.RunAsync(LearningScenario.ExactSource,
            new(MirrorAvailable: mirrorAvailable, MirrorExact: mirrorExact));
        Assert.True(report.MatchesReference);
        Assert.NotEqual(TaskAcceptanceStatus.Accepted, report.Acceptance.Status);
        Assert.NotEqual(RunOutcomeStatus.Succeeded, report.Outcome!.Outcome.Status);
        Assert.Equal(ObligationSatisfaction.Unsatisfied, report.Fulfillment.Satisfaction);
        Assert.NotEmpty(report.Fulfillment.ResidualRequirements);
        Assert.NotEqual(ObligationCoverageStatus.Complete, report.Fulfillment.Manifest.Coverage.Status);
        Assert.Equal(3, report.Metrics.ProviderFixtureCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Original_attempt_is_reconciled_or_retained_unknown_without_redispatch(bool known)
    {
        var report = await LearningExperiment.RunAsync(LearningScenario.EffectRecovery, new(ReconcileKnown: known));
        Assert.True(report.MatchesReference);
        Assert.Equal(["attempt.reconcile", "obligation.submit"], report.InvokedTools);
        var original = Assert.Single(report.Fulfillment.Attempts);
        Assert.Equal("original.accept", original.AttemptId);
        Assert.Equal("host.inventory", original.OwnerId);
        Assert.Equal(ObligationExecutionState.Cancelled, original.Execution);
        Assert.Equal(known ? ObligationEffectCustody.Confirmed : ObligationEffectCustody.OutcomeUnknown,
            original.EffectCustody);
        Assert.Equal(known ? TaskAcceptanceStatus.Accepted : TaskAcceptanceStatus.Blocked, report.Acceptance.Status);
        Assert.Equal(0, report.Metrics.NewDomainEffectDispatches);
        Assert.Contains("OutcomeUnknown", report.ProviderPrompts[0], StringComparison.Ordinal);
        Assert.Contains(known ? "Confirmed" : "OutcomeUnknown", report.ProviderPrompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Perspective_mismatch_cannot_be_promoted_by_a_well_formed_answer()
    {
        var report = await LearningExperiment.RunAsync(LearningScenario.ExactSource, new(WrongPerspectiveProposal: true));
        Assert.True(report.MatchesReference);
        Assert.Equal(ObligationSatisfaction.Unsatisfied, report.Fulfillment.Satisfaction);
        Assert.NotEqual(TaskAcceptanceStatus.Accepted, report.Acceptance.Status);
        Assert.NotEmpty(report.Fulfillment.ResidualRequirements);
    }

    [Fact]
    public async Task Shadow_computes_only_a_proposal_without_tool_execution_or_canonical_result()
    {
        var report = await LearningExperiment.RunAsync(LearningScenario.EffectRecovery, new(InertShadow: true));
        Assert.True(report.MatchesReference);
        Assert.Null(report.Outcome);
        Assert.Empty(report.InvokedTools);
        Assert.Equal(1, report.Metrics.ProviderFixtureCalls);
        Assert.Equal(0, report.Metrics.RuntimeReceipts);
        Assert.Equal(0, report.Metrics.NewDomainEffectDispatches);
        Assert.Equal(ObligationEffectCustody.OutcomeUnknown, Assert.Single(report.Fulfillment.Attempts).EffectCustody);
        Assert.NotEqual(TaskAcceptanceStatus.Accepted, report.Acceptance.Status);
    }

    [Fact]
    public async Task Actual_fixture_streaming_and_cost_limits_are_reported_without_fabricated_usage()
    {
        var report = await LearningExperiment.RunAsync(LearningScenario.ExactSource);
        Assert.Equal(LlmStreamEventKind.Started, report.StreamOrder[0]);
        Assert.Equal(LlmStreamEventKind.TextDelta, report.StreamOrder[1]);
        Assert.Equal(LlmStreamEventKind.Completed, report.StreamOrder[3]);
        Assert.NotNull(report.Metrics.FirstFixtureTextMilliseconds);
        Assert.True(report.Metrics.FirstFixtureTextMilliseconds <= report.Metrics.ElapsedMilliseconds);
        Assert.True(report.Metrics.PlannerUserPromptCharacters > 0);
        Assert.Equal(report.ProviderPrompts.Sum(prompt => prompt.Length), report.Metrics.PlannerUserPromptCharacters);
        Assert.Null(report.Metrics.ProviderCost);
        Assert.Null(report.Metrics.ProviderTokens);
        Assert.StartsWith("NOT-MEASURED", report.Metrics.WarmReuse, StringComparison.Ordinal);
        Assert.Equal(report.Metrics.CumulativeStepsBefore + report.Metrics.RuntimeReceipts,
            report.Metrics.CumulativeStepsAfter);
    }

    [Fact]
    public async Task Parent_budget_is_preserved_and_exhaustion_never_becomes_completion()
    {
        var report = await LearningExperiment.RunAsync(LearningScenario.ExactSource,
            new(CumulativeStepsUsed: 11, ParentStepLimit: 12));
        Assert.True(report.Metrics.CumulativeStepsAfter <= report.Metrics.ParentStepLimit);
        Assert.NotEqual(TaskAcceptanceStatus.Accepted, report.Acceptance.Status);
        Assert.NotEqual(RunOutcomeStatus.Succeeded, report.Outcome!.Outcome.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => LearningExperiment.RunAsync(
            LearningScenario.ExactSource, new(CumulativeStepsUsed: 12, ParentStepLimit: 12)));
    }
}

