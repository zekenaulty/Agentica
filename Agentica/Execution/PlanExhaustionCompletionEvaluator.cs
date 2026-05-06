using Agentica.Runs;

namespace Agentica.Execution;

public sealed class PlanExhaustionCompletionEvaluator : ICompletionEvaluator
{
    public static PlanExhaustionCompletionEvaluator Instance { get; } = new();

    private PlanExhaustionCompletionEvaluator()
    {
    }

    public CompletionEvaluation Evaluate(AgenticaRun run) =>
        CompletionEvaluation.Complete();
}
