namespace Agentica.Execution;

/// <summary>
/// Evaluates an immutable attempt snapshot and returns a terminal or continuation decision plus
/// the exact in-envelope evidence that proves completion.
/// </summary>
public interface ICompletionEvaluator
{
    CompletionEvaluation Evaluate(CompletionContext context);
}
