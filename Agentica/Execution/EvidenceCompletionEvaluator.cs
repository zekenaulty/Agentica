using Agentica.Runs;
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
        _requirements = requirements;
        _continueWhenMissing = continueWhenMissing;
    }

    public static EvidenceCompletionEvaluator ForArtifactKind(
        string artifactKind,
        bool continueWhenMissing = true) =>
        new([CompletionEvidenceRequirement.ArtifactKind(artifactKind)], continueWhenMissing);

    public CompletionEvaluation Evaluate(AgenticaRun run)
    {
        var missing = _requirements
            .Where(requirement => !requirement.IsSatisfiedBy(run.Artifacts, run.Receipts))
            .Select(requirement => $"{requirement.Kind}:{requirement.Value}")
            .ToArray();

        if (missing.Length == 0)
        {
            return CompletionEvaluation.Complete();
        }

        var blockers = missing
            .Select(missingRequirement => $"Completion evidence not satisfied: {missingRequirement}.")
            .ToArray();

        return _continueWhenMissing
            ? CompletionEvaluation.Continue(blockers)
            : CompletionEvaluation.Blocked(StopReason.CompletionNotSatisfied, blockers);
    }
}
