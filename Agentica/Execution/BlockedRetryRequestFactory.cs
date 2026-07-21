using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Requests;

namespace Agentica.Execution;

internal sealed class BlockedRetryRequestFactory
{
    private readonly ExecutionPolicy _policy;

    public BlockedRetryRequestFactory(ExecutionPolicy policy)
    {
        _policy = policy;
    }

    public RunRequest Create(
        RunRequest originalRequest,
        OutcomeEnvelope blockedEnvelope,
        int nextAttemptNumber)
    {
        var context = originalRequest.Context is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(originalRequest.Context, StringComparer.Ordinal);

        context.Remove("agentica.retry");
        context["agentica.retry"] = CreateContext(blockedEnvelope, nextAttemptNumber);

        return originalRequest with
        {
            Origin = RequestOrigin.Agent,
            Context = context
        };
    }

    private IReadOnlyDictionary<string, object?> CreateContext(
        OutcomeEnvelope blockedEnvelope,
        int nextAttemptNumber)
    {
        var context = _policy.EffectivePlanningContext;
        var previousAttempt = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runId"] = blockedEnvelope.Outcome.RunId,
            ["status"] = blockedEnvelope.Outcome.Status.ToString(),
            ["stopReason"] = blockedEnvelope.Outcome.StopReason.ToString(),
            ["blockers"] = blockedEnvelope.Outcome.Blockers,
            ["completedSteps"] = blockedEnvelope.Outcome.CompletedSteps,
            ["completionEvidence"] = blockedEnvelope.Outcome.CompletionEvidence
        };

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attemptNumber"] = nextAttemptNumber,
            ["previousAttemptNumber"] = nextAttemptNumber - 1,
            ["maxBlockedRetries"] = _policy.MaxBlockedRetries,
            ["remainingBlockedRetries"] = Math.Max(0, _policy.MaxBlockedRetries - (nextAttemptNumber - 1)),
            ["retryableStopReasons"] = _policy.EffectiveBlockedRetries.RetryableStopReasons
                .Select(reason => reason.ToString())
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToArray(),
            ["authorizedMutationRetryToolIds"] = _policy.EffectiveBlockedRetries.AuthorizedMutationToolIds
                .OrderBy(toolId => toolId, StringComparer.Ordinal)
                .ToArray(),
            ["instruction"] = "The previous Agentica run ended blocked. Use the supplied status, blockers, recent observations, recent receipts, validation issues, and available tools to plan a bounded strategy to unblock or resume. Do not claim success; Agentica will validate and execute any proposed plan.",
            ["previousAttempt"] = previousAttempt,
            ["recentReceipts"] = PlanningRequestFactory.Limit(blockedEnvelope.Receipts.Items, context.MaxRecentReceipts)
                .Select(CreateReceiptContext)
                .ToArray(),
            ["recentObservations"] = PlanningRequestFactory.Limit(blockedEnvelope.Details.Observations, context.MaxRecentObservations)
                .Select(CreateObservationContext)
                .ToArray(),
            ["validationIssues"] = blockedEnvelope.Details.ValidationIssues
                .Select(issue => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = issue.Code,
                    ["message"] = issue.Message,
                    ["stepId"] = issue.StepId
                })
                .ToArray()
        };
    }

    private static IReadOnlyDictionary<string, object?> CreateReceiptContext(Receipt receipt) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["receiptId"] = receipt.ReceiptId,
            ["stepId"] = receipt.StepId,
            ["toolId"] = receipt.ToolId,
            ["status"] = receipt.Status.ToString(),
            ["message"] = receipt.Message,
            ["data"] = receipt.Data
        };

    private static IReadOnlyDictionary<string, object?> CreateObservationContext(Observation observation) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["observationId"] = observation.ObservationId,
            ["stepId"] = observation.StepId,
            ["kind"] = observation.Kind.ToString(),
            ["summary"] = observation.Summary,
            ["data"] = observation.Data,
            ["evidence"] = observation.Evidence
        };
}
