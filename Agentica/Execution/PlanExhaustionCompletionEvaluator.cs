namespace Agentica.Execution;

/// <summary>
/// Explicitly treats exhaustion of a valid plan as completion. This policy is intended for
/// procedural demos whose definition of done is exactly "all planned steps ran"; it does not
/// prove an external objective or artifact exists.
/// </summary>
public sealed class PlanExhaustionCompletionEvaluator : ICompletionEvaluator
{
    public static PlanExhaustionCompletionEvaluator Instance { get; } = new();

    private PlanExhaustionCompletionEvaluator()
    {
    }

    public CompletionEvaluation Evaluate(CompletionContext context) =>
        CompletionEvaluation.Complete();
}
