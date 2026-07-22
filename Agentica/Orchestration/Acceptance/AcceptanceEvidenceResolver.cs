using Agentica.Observations;
using Agentica.Outcomes;

namespace Agentica.Orchestration.Acceptance;

internal static class AcceptanceEvidenceResolver
{
    public static bool IsResolved(
        EvidenceRef evidenceRef,
        OutcomeEnvelope outcome,
        IReadOnlyDictionary<string, object?> hostState) =>
        ResolvedEvidence(outcome, hostState).Contains(evidenceRef);

    public static IReadOnlySet<EvidenceRef> ResolvedEvidence(
        OutcomeEnvelope outcome,
        IReadOnlyDictionary<string, object?> hostState)
    {
        var resolved = new HashSet<EvidenceRef>();
        foreach (var attempt in Attempts(outcome))
        {
            resolved.Add(new EvidenceRef("run", attempt.Outcome.RunId));

            foreach (var artifact in attempt.Details.Artifacts)
            {
                resolved.Add(new EvidenceRef("artifact", artifact.ArtifactId));
            }

            foreach (var observation in attempt.Details.Observations)
            {
                resolved.Add(new EvidenceRef("observation", observation.ObservationId));
            }

            foreach (var receipt in attempt.Receipts.Items)
            {
                resolved.Add(new EvidenceRef("receipt", receipt.ReceiptId));
            }
        }

        foreach (var key in hostState.Keys)
        {
            resolved.Add(new EvidenceRef("host_state", key));
        }

        return resolved;
    }

    public static IEnumerable<OutcomeEnvelope> Attempts(OutcomeEnvelope outcome)
    {
        foreach (var priorAttempt in outcome.PriorAttempts)
        {
            foreach (var nested in Attempts(priorAttempt))
            {
                yield return nested;
            }
        }

        yield return outcome;
    }
}
