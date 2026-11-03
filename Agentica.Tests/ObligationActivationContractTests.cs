using Agentica.Observations;
using Agentica.Projection;
using Agentica.Tests.Fixtures.NyxLearning;

namespace Agentica.Tests;

public sealed class ObligationActivationContractTests
{
    [Fact]
    public void Equivalent_interpretation_is_shared_across_consumers_without_rewriting_their_acceptance()
    {
        var first = Manifest();
        var second = Manifest(
            identity: Identity() with
            {
                ObligationId = "consumer.2",
                GoverningObjectiveId = "objective.2",
                ParentObligationId = "parent.2",
                AcceptanceContractId = "acceptance.another-consumer",
                AcceptanceRevision = "9"
            },
            segments: [Segment() with { SegmentId = "consumer-local-label" }]);

        Assert.Equal(first.ContentIdentity, second.ContentIdentity);
        Assert.NotEqual(first.Obligation, second.Obligation);
        var fulfillment = new ObligationFulfillment(second, ObligationSatisfaction.Satisfied,
            ObligationResolutionState.Ready, [new("consumer-local-label", 10, 6)], []);
        Assert.Equal("9", fulfillment.Obligation.AcceptanceRevision);
        Assert.Equal("parent.2", fulfillment.Obligation.ParentObligationId);
        Assert.Equal("before", second.ReadCitation(Assert.Single(fulfillment.Citations)));
    }

    [Fact]
    public void Meaning_changing_subject_evidence_scope_horizon_and_policy_coordinates_change_identity()
    {
        var baseline = Manifest();
        var variants = new[]
        {
            Manifest(identity: Identity() with { SubjectId = "subject.other" }),
            Manifest(identity: Identity() with { SubjectRevision = "2" }),
            Manifest(question: "A different question."),
            Manifest(scope: "scope.other"),
            Manifest(perspective: "perspective.other"),
            Manifest(segments: [Segment() with { Source = new("source", "source.other") }]),
            Manifest(segments: [Segment() with { SourceRevision = "2" }]),
            Manifest(segments: [Segment() with { AttributionId = "speaker.other" }]),
            Manifest(segments: [Segment() with { StartUtf16 = 11 }]),
            Manifest(segments: [Segment() with { Text = "after!" }]),
            Manifest(coverage: new(ObligationCoverageStatus.Partial, "cut.1", "corpus.1")),
            Manifest(coverage: new(ObligationCoverageStatus.Complete, "cut.2", "corpus.1")),
            Manifest(coverage: new(ObligationCoverageStatus.Complete, "cut.1", "corpus.2")),
            Manifest(compiler: "compiler.2"),
            Manifest(inputSchema: "input.2"),
            Manifest(outputSchema: "output.2"),
            Manifest(policy: "policy.2")
        };

        Assert.All(variants, variant => Assert.NotEqual(baseline.ContentIdentity, variant.ContentIdentity));
        var a = Segment();
        var b = Segment() with { SegmentId = "part.2", Text = "second", StartUtf16 = 20 };
        Assert.NotEqual(Manifest(segments: [a, b]).ContentIdentity,
            Manifest(segments: [b, a]).ContentIdentity);
    }

    [Fact]
    public void Input_and_fulfillment_snapshots_do_not_expose_mutable_collections()
    {
        var source = new List<PresentedEvidenceSegment> { Segment() };
        var manifest = Manifest(segments: source);
        var hash = manifest.ContentIdentity;
        source[0] = Segment() with { Text = "changed" };
        source.Clear();
        Assert.Equal("before", Assert.Single(manifest.PresentedEvidence).Text);
        Assert.Equal(hash, manifest.ContentIdentity);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PresentedEvidenceSegment>)manifest.PresentedEvidence).Clear());

        var citations = new List<PresentedEvidenceCitation> { new("part.1", 10, 6) };
        var residuals = new List<string> { "need.result" };
        var attempts = new List<ObligationAttemptReference>
        {
            new("attempt.1", "owner", "provider.one", ObligationExecutionState.OutcomeUnknown,
                ObligationEffectCustody.OutcomeUnknown)
        };
        var fulfillment = new ObligationFulfillment(manifest, ObligationSatisfaction.Indeterminate,
            ObligationResolutionState.Indeterminate, citations, residuals, attempts);
        citations.Clear();
        residuals[0] = "unreviewed";
        attempts.Clear();
        Assert.Single(fulfillment.Citations);
        Assert.Equal("need.result", Assert.Single(fulfillment.ResidualRequirements));
        Assert.Equal("attempt.1", Assert.Single(fulfillment.Attempts).AttemptId);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ObligationAttemptReference>)fulfillment.Attempts).Clear());
    }

    [Theory]
    [InlineData("retained-but-not-presented", 10, 1)]
    [InlineData("part.1", 9, 1)]
    [InlineData("part.1", 10, 7)]
    [InlineData("part.1", 10, 0)]
    [InlineData("part.1", int.MaxValue, int.MaxValue)]
    public void Citations_cannot_claim_omitted_or_unpresented_material(string segment, int start, int length)
    {
        var manifest = Manifest();
        Assert.Throws<ArgumentException>(() => new ObligationFulfillment(manifest,
            ObligationSatisfaction.PartiallySatisfied, ObligationResolutionState.Ready,
            [new(segment, start, length)], ["need.original"]));
    }

    [Fact]
    public void Exact_nonzero_source_offsets_and_unicode_quotes_preserve_the_original_text()
    {
        var segment = Segment() with { Text = "A\U0001F642B", StartUtf16 = 100 };
        var manifest = Manifest(segments: [segment]);
        Assert.Equal("\U0001F642", manifest.ReadCitation(new("part.1", 101, 2)));
        Assert.Throws<ArgumentException>(() => manifest.ReadCitation(new("part.1", 101, 1)));
        Assert.Throws<ArgumentException>(() => Manifest(segments: [Segment() with { Text = "\uD800" }]));
        Assert.Throws<ArgumentException>(() => Manifest(question: "\uDC00"));
    }

    [Theory]
    [InlineData(ObligationCoverageStatus.Partial)]
    [InlineData(ObligationCoverageStatus.Unknown)]
    public void Incomplete_required_coverage_cannot_be_satisfied(ObligationCoverageStatus coverage)
    {
        Assert.Throws<ArgumentException>(() => Satisfied(
            Manifest(coverage: new(coverage, "cut.1", "corpus.1"))));
    }

    [Theory]
    [InlineData(ObligationResolutionState.Pending)]
    [InlineData(ObligationResolutionState.Unavailable)]
    [InlineData(ObligationResolutionState.Indeterminate)]
    public void Ready_is_distinct_from_fulfilled_and_unready_cannot_be_satisfied(ObligationResolutionState resolution)
    {
        Assert.Throws<ArgumentException>(() => new ObligationFulfillment(Manifest(),
            ObligationSatisfaction.Satisfied, resolution, [new("part.1", 10, 6)], []));
        var partial = new ObligationFulfillment(Manifest(), ObligationSatisfaction.PartiallySatisfied,
            ObligationResolutionState.Ready, [new("part.1", 10, 6)], ["need.other-evidence"]);
        Assert.Equal(ObligationResolutionState.Ready, partial.Resolution);
        Assert.Equal(ObligationSatisfaction.PartiallySatisfied, partial.Satisfaction);
    }

    [Theory]
    [InlineData(ObligationExecutionState.Active, ObligationEffectCustody.NotAttempted)]
    [InlineData(ObligationExecutionState.OutcomeUnknown, ObligationEffectCustody.NotAttempted)]
    [InlineData(ObligationExecutionState.Succeeded, ObligationEffectCustody.Admitted)]
    [InlineData(ObligationExecutionState.Cancelled, ObligationEffectCustody.OutcomeUnknown)]
    public void Unknown_or_pending_original_attempts_cannot_be_erased_by_similar_ready_evidence(
        ObligationExecutionState execution, ObligationEffectCustody custody)
    {
        ObligationAttemptReference[] attempts = [new("original", "owner", "provider.old", execution, custody)];
        Assert.Throws<ArgumentException>(() => Satisfied(Manifest(), attempts));
        var retained = new ObligationFulfillment(Manifest(), ObligationSatisfaction.Indeterminate,
            ObligationResolutionState.Ready, [new("part.1", 10, 6)], ["reconcile.original"], attempts);
        Assert.Equal(attempts[0], Assert.Single(retained.Attempts));
    }

    [Fact]
    public void Realization_change_keeps_obligation_and_parent_acceptance_without_resetting_old_custody()
    {
        var manifest = Manifest();
        ObligationAttemptReference[] attempts =
        [
            new("old", "host", "provider.old", ObligationExecutionState.Cancelled,
                ObligationEffectCustody.OutcomeUnknown),
            new("new-read-only", "host", "provider.new", ObligationExecutionState.Succeeded,
                ObligationEffectCustody.NotAttempted)
        ];
        var result = new ObligationFulfillment(manifest, ObligationSatisfaction.Indeterminate,
            ObligationResolutionState.Ready, [new("part.1", 10, 6)], ["reconcile.old"], attempts);

        Assert.Equal(manifest.Obligation, result.Obligation);
        Assert.Equal("parent.1", result.Obligation.ParentObligationId);
        Assert.Equal("acceptance.1", result.Obligation.AcceptanceContractId);
        Assert.Equal(2, result.Attempts.Count);
        Assert.Equal(ObligationEffectCustody.OutcomeUnknown, result.Attempts[0].EffectCustody);
        Assert.Throws<ArgumentException>(() => Satisfied(manifest, attempts));
    }

    [Fact]
    public void Confirmed_effect_and_failed_execution_remain_independent_axes()
    {
        var result = Satisfied(Manifest(),
        [
            new("effect.1", "owner", "realization.1", ObligationExecutionState.Cancelled,
                ObligationEffectCustody.Confirmed)
        ]);
        Assert.Equal(ObligationSatisfaction.Satisfied, result.Satisfaction);
        Assert.Equal(ObligationExecutionState.Cancelled, Assert.Single(result.Attempts).Execution);
        Assert.Equal(ObligationEffectCustody.Confirmed, result.Attempts[0].EffectCustody);
    }

    [Fact]
    public void Malformed_duplicate_or_oversized_contracts_fail_at_the_snapshot_boundary()
    {
        var segment = Segment();
        Assert.Throws<ArgumentException>(() => Manifest(segments: [segment, segment]));
        Assert.Throws<ArgumentException>(() => Manifest(segments:
            [segment with { Text = new string('a', ObligationInputManifest.MaximumSegmentCharacters + 1) }]));
        Assert.Throws<ArgumentException>(() => Manifest(segments: Enumerable.Range(0, 129)
            .Select(i => segment with { SegmentId = "part." + i }).ToArray()));
        Assert.Throws<ArgumentException>(() => Manifest(segments: Enumerable.Range(0, 5)
            .Select(i => segment with
            {
                SegmentId = "part." + i,
                Text = new string('a', ObligationInputManifest.MaximumSegmentCharacters)
            }).ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => Manifest(segments: [segment with { StartUtf16 = -1 }]));
        Assert.Throws<ArgumentException>(() => Manifest(segments: [segment with { StartUtf16 = int.MaxValue }]));
        Assert.Throws<ArgumentException>(() => Manifest(identity: Identity() with { ParentObligationId = "consumer.1" }));
        Assert.Throws<ArgumentException>(() => Manifest(coverage: new((ObligationCoverageStatus)99, "cut")));
        Assert.Throws<ArgumentException>(() => Manifest(scope: " scope "));
        Assert.Throws<ArgumentException>(() => Satisfied(Manifest(), [
            new("attempt", "host", "one", ObligationExecutionState.Succeeded, ObligationEffectCustody.NotAttempted),
            new("attempt", "host", "two", ObligationExecutionState.Succeeded, ObligationEffectCustody.NotAttempted)
        ]));
        Assert.Throws<ArgumentException>(() => Satisfied(Manifest(), [
            new("attempt", "host", "one", ObligationExecutionState.NotAttempted, ObligationEffectCustody.Confirmed)
        ]));
        Assert.Throws<ArgumentException>(() => new ObligationFulfillment(Manifest(),
            ObligationSatisfaction.Satisfied, ObligationResolutionState.Ready, [], []));
        Assert.Throws<ArgumentException>(() => new ObligationFulfillment(Manifest(),
            ObligationSatisfaction.Unsatisfied, ObligationResolutionState.Unavailable, [], []));
    }

    [Fact]
    public void Enumeration_is_bounded_even_when_an_input_collection_misreports_its_count()
    {
        var segments = new MisreportedList<PresentedEvidenceSegment>(Enumerable.Range(0, 129)
            .Select(i => Segment() with { SegmentId = "segment." + i }).ToArray());
        Assert.Throws<ArgumentException>(() => Manifest(segments: segments));
        var citations = new MisreportedList<PresentedEvidenceCitation>(Enumerable.Range(0, 129)
            .Select(_ => new PresentedEvidenceCitation("part.1", 10, 1)).ToArray());
        Assert.Throws<ArgumentException>(() => new ObligationFulfillment(Manifest(),
            ObligationSatisfaction.PartiallySatisfied, ObligationResolutionState.Ready,
            citations, ["need.result"]));
        var residuals = new MisreportedList<string>(Enumerable.Range(0, 129).Select(i => "need." + i).ToArray());
        Assert.Throws<ArgumentException>(() => new ObligationFulfillment(Manifest(),
            ObligationSatisfaction.Unsatisfied, ObligationResolutionState.Unavailable, [], residuals));
        var attempts = new MisreportedList<ObligationAttemptReference>(Enumerable.Range(0, 129)
            .Select(i => new ObligationAttemptReference("attempt." + i, "host", "read.v1",
                ObligationExecutionState.Succeeded, ObligationEffectCustody.NotAttempted)).ToArray());
        Assert.Throws<ArgumentException>(() => new ObligationFulfillment(Manifest(),
            ObligationSatisfaction.PartiallySatisfied, ObligationResolutionState.Ready,
            [], ["need.result"], attempts));
    }

    [Fact]
    public void Versioned_synthetic_corpus_preserves_distinct_evidence_acceptance_and_unknown_effects()
    {
        var scenarios = SyntheticObligationScenarios.All();
        var goldenHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["historical-correction-v1"] = "sha256-v1:2900718ba2cc8b93e5b2ad1c356128573090935ddbb6fe9eebe7e806854f6594",
            ["exact-source-unavailable-v1"] = "sha256-v1:a7b16a19620a6b82cc968a2405306c4c86eb0ca03f727cbe63b04295560b20d6",
            ["lost-effect-response-v1"] = "sha256-v1:82c52c580775c1c3a88c076822f1a5d451ebca42f29a5f65ebe0bd7b05cdab04"
        };
        Assert.Equal(3, scenarios.Count);
        Assert.Equal(3, scenarios.Select(scenario => scenario.ScenarioId).Distinct().Count());
        Assert.All(scenarios, scenario =>
        {
            var copy = SyntheticObligationScenarios.All().Single(item => item.ScenarioId == scenario.ScenarioId);
            Assert.Equal(scenario.Manifest.ContentIdentity, copy.Manifest.ContentIdentity);
            Assert.Equal(goldenHashes[scenario.ScenarioId], scenario.Manifest.ContentIdentity);
            Assert.NotEmpty(scenario.Ownership);
            Assert.NotEmpty(scenario.Acceptance);
            Assert.True(scenario.NoProductionCredentials);
        });
        var history = scenarios.Single(scenario => scenario.ScenarioId == "historical-correction-v1");
        Assert.Equal(["speaker.user", "speaker.user"],
            history.Manifest.PresentedEvidence.Select(segment => segment.AttributionId));
        Assert.Contains("Correction", history.Manifest.PresentedEvidence[1].Text, StringComparison.Ordinal);
        var exact = scenarios.Single(scenario => scenario.ScenarioId == "exact-source-unavailable-v1");
        Assert.Equal(ObligationCoverageStatus.Unknown, exact.Manifest.Coverage.Status);
        Assert.Equal(ObligationSatisfaction.Unsatisfied, exact.ExpectedFulfillment.Satisfaction);
        var effect = scenarios.Single(scenario => scenario.ScenarioId == "lost-effect-response-v1");
        Assert.Equal(ObligationSatisfaction.Indeterminate, effect.ExpectedFulfillment.Satisfaction);
        Assert.Equal(ObligationEffectCustody.OutcomeUnknown, Assert.Single(effect.ExpectedFulfillment.Attempts).EffectCustody);
        Assert.All(scenarios, scenario => Assert.Equal(0, scenario.ExpectedNewEffectDispatches));
    }

    private static ObligationIdentity Identity() =>
        new("consumer.1", "objective.1", "acceptance.1", "1", "subject.1", "1", "parent.1");

    private static PresentedEvidenceSegment Segment() =>
        new("part.1", new EvidenceRef("source", "source.1"), "1", 10, "before", "speaker.1");

    private static ObligationInputManifest Manifest(
        ObligationIdentity? identity = null,
        string question = "What is established?",
        string scope = "scope.1",
        string perspective = "perspective.1",
        IReadOnlyList<PresentedEvidenceSegment>? segments = null,
        ObligationCoverage? coverage = null,
        string compiler = "compiler.1",
        string inputSchema = "input.1",
        string outputSchema = "output.1",
        string policy = "policy.1") =>
        new(identity ?? Identity(), question, scope, perspective, segments ?? [Segment()],
            coverage ?? new(ObligationCoverageStatus.Complete, "cut.1", "corpus.1"),
            compiler, inputSchema, outputSchema, policy);

    private static ObligationFulfillment Satisfied(
        ObligationInputManifest manifest, IReadOnlyList<ObligationAttemptReference>? attempts = null) =>
        new(manifest, ObligationSatisfaction.Satisfied, ObligationResolutionState.Ready,
            [new("part.1", 10, 6)], [], attempts);

    private sealed class MisreportedList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int Count => 1;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

