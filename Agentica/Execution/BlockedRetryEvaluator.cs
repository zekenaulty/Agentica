using Agentica.Outcomes;
using Agentica.Tools;

namespace Agentica.Execution;

internal sealed class BlockedRetryEvaluator
{
    private readonly ToolCatalog _toolCatalog;
    private readonly BlockedRetryPolicy _policy;

    public BlockedRetryEvaluator(ToolCatalog toolCatalog, BlockedRetryPolicy policy)
    {
        _toolCatalog = toolCatalog;
        _policy = policy;
    }

    public bool CanRetry(IReadOnlyList<OutcomeEnvelope> attempts)
    {
        if (attempts.Count == 0)
        {
            return false;
        }

        var currentAttempt = attempts[^1];
        if (currentAttempt.Outcome.Status != RunOutcomeStatus.Blocked ||
            !_policy.RetryableStopReasons.Contains(currentAttempt.Outcome.StopReason))
        {
            return false;
        }

        return attempts.All(IsRetrySafe);
    }

    private bool IsRetrySafe(OutcomeEnvelope attempt)
    {
        foreach (var completedStepId in attempt.Outcome.CompletedSteps)
        {
            var matchingSteps = attempt.Details.PlanVersions
                .SelectMany(plan => plan.Steps)
                .Where(step => string.Equals(step.StepId, completedStepId, StringComparison.Ordinal))
                .ToArray();
            if (matchingSteps.Length != 1)
            {
                return false;
            }

            var step = matchingSteps[0];
            var registration = _toolCatalog.Resolve(step.ToolId);
            if (registration is null ||
                registration.PlannerProjection.Kind != step.Kind ||
                registration.Security.Effect != step.Effect ||
                registration.Security.Effect == ToolEffect.Unknown)
            {
                return false;
            }

            if (registration.Security.Effect == ToolEffect.ReadOnly)
            {
                continue;
            }

            if (registration.Security.RetrySafety != ToolRetrySafety.Idempotent ||
                !_policy.AuthorizedMutationToolIds.Contains(registration.PlannerProjection.ToolId))
            {
                return false;
            }
        }

        return true;
    }
}
