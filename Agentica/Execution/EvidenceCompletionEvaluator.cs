using Agentica.Outcomes;

namespace Agentica.Execution;

public sealed class EvidenceCompletionEvaluator : ICompletionEvaluator
{
    private readonly IReadOnlyList<CompletionEvidenceRequirement> _requirements;
    private readonly bool _continueWhenMissing;

    public EvidenceCompletionEvaluator(
        IReadOnlyList<CompletionEvidenceRequirement> requirements,
        bool continueWhenMissing = true)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var requirementSnapshot = requirements
            .Select(requirement => requirement is null ? null! : requirement with { })
            .ToArray();
        if (requirementSnapshot.Length == 0)
        {
            throw new ArgumentException(
                "At least one completion evidence requirement is required.",
                nameof(requirements));
        }

        if (requirementSnapshot.Any(requirement =>
                requirement is null ||
                string.IsNullOrWhiteSpace(requirement.Kind) ||
                string.IsNullOrWhiteSpace(requirement.Value)))
        {
            throw new ArgumentException(
                "Completion evidence requirements must have nonempty kinds and values.",
                nameof(requirements));
        }

        _requirements = Array.AsReadOnly(requirementSnapshot);
        _continueWhenMissing = continueWhenMissing;
    }

    public static EvidenceCompletionEvaluator ForArtifactKind(
        string artifactKind,
        bool continueWhenMissing = true) =>
        new([CompletionEvidenceRequirement.ArtifactKind(artifactKind)], continueWhenMissing);

    public CompletionEvaluation Evaluate(CompletionContext context)
    {
        var resolved = _requirements
            .Select(requirement => new
            {
                Requirement = requirement,
                Evidence = requirement.Resolve(context.Artifacts, context.Receipts)
            })
            .ToArray();
        var missing = resolved
            .Where(item => item.Evidence is null)
            .Select(item => $"{item.Requirement.Kind}:{item.Requirement.Value}")
            .ToArray();

        if (missing.Length == 0)
        {
            return CompletionEvaluation.Complete(resolved
                .Select(item => item.Evidence!)
                .Distinct()
                .ToArray());
        }

        var blockers = missing
            .Select(missingRequirement => $"Completion evidence not satisfied: {missingRequirement}.")
            .ToArray();

        return _continueWhenMissing
            ? CompletionEvaluation.Continue(blockers)
            : CompletionEvaluation.Blocked(StopReason.CompletionNotSatisfied, blockers);
    }
}
