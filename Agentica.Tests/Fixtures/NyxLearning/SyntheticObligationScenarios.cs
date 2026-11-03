using Agentica.Observations;
using Agentica.Projection;

namespace Agentica.Tests.Fixtures.NyxLearning;

/// <summary>Declared reference semantics, not a replay or qualification of a running Nyx host.</summary>
internal sealed record SyntheticObligationScenario(
    string ScenarioId,
    ObligationInputManifest Manifest,
    ObligationFulfillment ExpectedFulfillment,
    string Ownership,
    string Acceptance,
    string ExpectedInterpretation,
    string BoundCapabilityId,
    bool RuntimeReady,
    int ExpectedNewEffectDispatches,
    SyntheticThoughtTest ThoughtTest)
{
    public bool NoProductionCredentials => true;
}

internal sealed record SyntheticThoughtTest(
    string Hypothesis,
    string SupportingEvidence,
    string Falsifier,
    string EvaluationRule);

internal static class SyntheticObligationScenarios
{
    public const int CorpusVersion = 1;

    public static IReadOnlyList<SyntheticObligationScenario> All() =>
        Array.AsReadOnly(new[] { HistoricalCorrection(), UnavailableExactSource(), LostEffectResponse() });

    private static SyntheticObligationScenario HistoricalCorrection()
    {
        PresentedEvidenceSegment[] segments =
        [
            new("earlier", new EvidenceRef("transcript", "synthetic.message.1"), "1", 0,
                "Use morning delivery.", "speaker.user"),
            new("correction", new EvidenceRef("transcript", "synthetic.message.2"), "1", 0,
                "Correction: use evening delivery.", "speaker.user")
        ];
        var manifest = Manifest(
            new("history.obligation", "history.objective", "current-preference", "1",
                "synthetic.delivery-preference", "2"),
            "What is the current delivery preference and which source corrected it?",
            "synthetic.history", "history.reader", segments,
            new(ObligationCoverageStatus.Complete, "messages.1-through-2", "synthetic.history.2"));
        var expected = new ObligationFulfillment(manifest, ObligationSatisfaction.Satisfied,
            ObligationResolutionState.Ready, [new("correction", 0, segments[1].Text.Length)], []);
        return new("historical-correction-v1", manifest, expected,
            "The synthetic host owns source chronology and current acceptance; interpretation does not rewrite either message.",
            "Identify evening as current, cite the later user correction, and retain the earlier message as historical.",
            "Evening delivery supersedes morning delivery at the declared two-message horizon.",
            "history.read", true, 0,
            new("The later attributable correction changes the current preference.",
                "Exact later user text and its order within this horizon.",
                "A later eligible source explicitly reverses the correction.",
                "The host checks source identity/order and the answer; model agreement alone supplies no independent evidence."));
    }

    private static SyntheticObligationScenario UnavailableExactSource()
    {
        PresentedEvidenceSegment[] segments =
        [
            new("secondary", new EvidenceRef("document", "synthetic.secondary-note"), "1", 0,
                "A secondary note says the primary document recommends evening delivery.", "speaker.editor")
        ];
        var manifest = Manifest(
            new("exact.obligation", "exact.objective", "exact-primary-passage", "4",
                "synthetic.primary-document", "4"),
            "Return the exact attributable passage from primary document revision 4.",
            "synthetic.documents", "document.reader", segments,
            new(ObligationCoverageStatus.Unknown, "primary-document.revision-4.exact-passage", "synthetic.documents.4"));
        var expected = new ObligationFulfillment(manifest, ObligationSatisfaction.Unsatisfied,
            ObligationResolutionState.Unavailable, [new("secondary", 0, segments[0].Text.Length)],
            ["obtain.primary-revision-4-passage"]);
        return new("exact-source-unavailable-v1", manifest, expected,
            "The host retains the installed document binding and controls exact source eligibility; an outage grants no substitute identity.",
            "Only the exact primary revision and passage satisfy the contract. A cited secondary report remains secondary.",
            "The primary exact passage is unavailable; the secondary note cannot satisfy exact quotation.",
            "document.open-exact", false, 0,
            new("The secondary note is sufficient for the exact-source obligation.",
                "Only an attributable passage resolving to the required primary revision could support fulfillment.",
                "The presented citation resolves to a different document.",
                "Reject the sufficient-evidence hypothesis while preserving the binding and residual exact-source requirement."));
    }

    private static SyntheticObligationScenario LostEffectResponse()
    {
        PresentedEvidenceSegment[] segments =
        [
            new("state", new EvidenceRef("host-observation", "synthetic.inventory.observation-7"), "7", 0,
                "Item status is accepted.", "host.inventory"),
            new("attempt", new EvidenceRef("host-attempt", "original.accept.1"), "1", 0,
                "Original attempt was cancelled; its effect outcome is unknown.", "host.inventory")
        ];
        var manifest = Manifest(
            new("effect.obligation", "effect.objective", "accept-item-once", "1",
                "synthetic.item.1", "7", "effect.parent"),
            "Has the original accept-item attempt fulfilled the objective exactly once?",
            "synthetic.inventory.alpha", "inventory.operator", segments,
            new(ObligationCoverageStatus.Complete, "observation-7-and-original-attempt", "synthetic.inventory.7"));
        ObligationAttemptReference[] attempts =
        [
            new("original.accept.1", "host.inventory", "inventory.accept.v1",
                ObligationExecutionState.Cancelled, ObligationEffectCustody.OutcomeUnknown)
        ];
        var expected = new ObligationFulfillment(manifest, ObligationSatisfaction.Indeterminate,
            ObligationResolutionState.Ready,
            [new("state", 0, segments[0].Text.Length), new("attempt", 0, segments[1].Text.Length)],
            ["reconcile.original.accept.1"], attempts);
        return new("lost-effect-response-v1", manifest, expected,
            "The host owns original attempt/result custody and cumulative resources. Provider change does not create another effect allowance.",
            "Resolve the exact original attempt and verify accepted state before fulfillment; no new accept dispatch while custody is unknown.",
            "Accepted state is observed, but causation and the original effect outcome remain unresolved.",
            "inventory.accept", true, 0,
            new("Observed accepted state proves the original attempt succeeded.",
                "A matching original-attempt result plus authoritative state would support the claim.",
                "A matching original-attempt refusal or another actor's confirmed transition contradicts causation.",
                "Keep the hypothesis unconfirmed until exact attempt reconciliation; neither a new model answer nor a similar state snapshot settles custody."));
    }

    private static ObligationInputManifest Manifest(
        ObligationIdentity identity, string question, string scope, string perspective,
        IReadOnlyList<PresentedEvidenceSegment> segments, ObligationCoverage coverage) =>
        new(identity, question, scope, perspective, segments, coverage,
            "synthetic-compiler.v1", "presented-evidence.v1", "interpretation.v1", "source-grounded-policy.v1");
}

