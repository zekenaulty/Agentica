using Agentica.Runs;

namespace Agentica.Execution;

public interface ICompletionEvaluator
{
    CompletionEvaluation Evaluate(AgenticaRun run);
}
