using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Projection;

namespace Agentica.Orchestration.Acceptance;

/// <summary>
/// Maps optional obligation declarations and an explicit host semantic decision to existing
/// acceptance. It does not infer host truth from a provider answer or successful tool call.
/// </summary>
public static class ObligationFulfillmentAdapter
{
    public static TaskAcceptanceResult Evaluate(
        ObligationFulfillment current,
        ObligationFulfillment candidate,
        OutcomeEnvelope outcome,
        TaskAcceptanceResult hostDecision)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(hostDecision);
        if (!Enum.IsDefined(hostDecision.Status))
            throw new ArgumentException("Host acceptance status is undefined.", nameof(hostDecision));
        var reasons = Bounded(hostDecision.Reasons, value =>
            ObligationContractGuard.Text(value, 2048, nameof(hostDecision)));
        var evidence = Bounded(hostDecision.EvidenceRefs, reference =>
        {
            ArgumentNullException.ThrowIfNull(reference);
            return new EvidenceRef(
                ObligationContractGuard.Identifier(reference.Kind, nameof(hostDecision)),
                ObligationContractGuard.Identifier(reference.RefId, nameof(hostDecision)));
        });

        TaskAcceptanceResult Result(TaskAcceptanceStatus status, string reason) =>
            new(status, Array.AsReadOnly(new[] { reason }), [], RequiresGraphRefinement: true);

        if (current.Obligation != candidate.Obligation ||
            current.Manifest.ContentIdentity != candidate.Manifest.ContentIdentity)
            return Result(TaskAcceptanceStatus.InvalidatedPlan, "The candidate targets stale obligation, acceptance or interpretation inputs.");
        try
        {
            foreach (var citation in candidate.Citations)
            {
                _ = current.Manifest.ReadCitation(citation);
                var original = candidate.Manifest.PresentedEvidence.Single(segment => segment.SegmentId == citation.SegmentId);
                var present = current.Manifest.PresentedEvidence.Single(segment => segment.SegmentId == citation.SegmentId);
                if (original.Source != present.Source || original.SourceRevision != present.SourceRevision ||
                    original.AttributionId != present.AttributionId || original.StartUtf16 != present.StartUtf16 ||
                    original.Text != present.Text)
                    return Result(TaskAcceptanceStatus.InvalidatedPlan, "Candidate citations require an exact current source/range rebind.");
            }
        }
        catch (ArgumentException)
        {
            return Result(TaskAcceptanceStatus.InvalidatedPlan, "Candidate citations do not resolve in the current presented manifest.");
        }

        // A candidate cannot override an explicit current host veto, including a partial proposal.
        if (hostDecision.Status is TaskAcceptanceStatus.Rejected or TaskAcceptanceStatus.Blocked or TaskAcceptanceStatus.InvalidatedPlan)
            return new(hostDecision.Status, Array.AsReadOnly(reasons), Array.AsReadOnly(evidence),
                hostDecision.RequiresGraphRefinement);

        if (Unresolved(current) || Unresolved(candidate))
            return Result(TaskAcceptanceStatus.Blocked, "Original custody or required evidence remains indeterminate; preserve residual work.");
        var currentAttempts = current.Attempts.ToDictionary(attempt => attempt.AttemptId, StringComparer.Ordinal);
        if (candidate.Attempts.Count != currentAttempts.Count || candidate.Attempts.Any(attempt =>
                !currentAttempts.TryGetValue(attempt.AttemptId, out var original) || original != attempt))
            return Result(TaskAcceptanceStatus.InvalidatedPlan, "Candidate original attempts differ from current host custody.");
        if (candidate.Satisfaction != current.Satisfaction || candidate.Resolution != current.Resolution ||
            !new HashSet<string>(current.ResidualRequirements, StringComparer.Ordinal).SetEquals(candidate.ResidualRequirements))
            return Result(TaskAcceptanceStatus.Rejected, "Candidate satisfaction and residual work must match the current host ledger.");
        if (evidence.Any(reference => !Resolves(reference, outcome)) ||
            (hostDecision.Status == TaskAcceptanceStatus.Accepted && evidence.Length == 0))
            return Result(TaskAcceptanceStatus.Rejected, "Host acceptance evidence must resolve to canonical proof in this outcome.");
        if (candidate.Satisfaction == ObligationSatisfaction.PartiallySatisfied)
            return Result(TaskAcceptanceStatus.PartiallyAccepted, "The obligation retains unsatisfied requirements.");
        if (candidate.Satisfaction != ObligationSatisfaction.Satisfied ||
            candidate.Resolution != ObligationResolutionState.Ready)
            return Result(TaskAcceptanceStatus.Rejected, "The candidate does not satisfy its declared obligation.");
        return new(hostDecision.Status, Array.AsReadOnly(reasons), Array.AsReadOnly(evidence),
            hostDecision.RequiresGraphRefinement);
    }

    private static bool Unresolved(ObligationFulfillment state) =>
        state.Satisfaction == ObligationSatisfaction.Indeterminate ||
        state.Resolution is ObligationResolutionState.Pending or ObligationResolutionState.Indeterminate ||
        state.Attempts.Any(attempt =>
            attempt.Execution is ObligationExecutionState.Active or ObligationExecutionState.OutcomeUnknown ||
            attempt.EffectCustody is ObligationEffectCustody.Admitted or ObligationEffectCustody.OutcomeUnknown);

    private static bool Resolves(EvidenceRef reference, OutcomeEnvelope outcome) => reference.Kind switch
    {
        "receipt" => outcome.Receipts.Items.Any(item => item.ReceiptId == reference.RefId),
        "observation" => outcome.Details.Observations.Any(item => item.ObservationId == reference.RefId),
        "artifact" => outcome.Details.Artifacts.Any(item => item.ArtifactId == reference.RefId),
        _ => false
    };

    private static T[] Bounded<T>(IReadOnlyList<T> source, Func<T, T> snapshot)
    {
        ArgumentNullException.ThrowIfNull(source);
        var copies = new List<T>();
        foreach (var item in source)
        {
            if (copies.Count >= ObligationFulfillment.MaximumReferences)
                throw new ArgumentException("Host acceptance exceeds its reference limits.", nameof(source));
            copies.Add(snapshot(item));
        }
        return copies.ToArray();
    }
}

