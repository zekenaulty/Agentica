using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Observations;
using Agentica.Planning;

namespace Agentica.Clients.Planning;

public sealed class LlmWorkflowPlanner : IExternalWorkflowPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILlmClient _client;
    private readonly LlmPlannerOptions _options;

    public LlmWorkflowPlanner(ILlmClient client, LlmPlannerOptions? options = null)
    {
        _client = client;
        _options = options ?? LlmPlannerOptions.Default;
    }

    public async Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        var llmRequest = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request, _options);
        var response = await GenerateAsync(
            llmRequest,
            cancellationToken).ConfigureAwait(false);

        return await ParsePlanWithRepairAsync(
                llmRequest,
                response.StructuredJson ?? response.Text,
                response.FinishReason,
                version: 1,
                isRefinement: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default)
    {
        var llmRequest = WorkflowPlanPromptBuilder.BuildRefinementRequest(request, observation, _options);
        var response = await GenerateAsync(
            llmRequest,
            cancellationToken).ConfigureAwait(false);

        return await ParsePlanWithRepairAsync(
                llmRequest,
                response.StructuredJson ?? response.Text,
                response.FinishReason,
                version: 2,
                isRefinement: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowPlan> ParsePlanWithRepairAsync(
        LlmRequest originalRequest,
        string json,
        LlmFinishReason finishReason,
        int version,
        bool isRefinement,
        CancellationToken cancellationToken)
    {
        try
        {
            return isRefinement
                ? ParseRefinementPlan(json, version, finishReason)
                : ParsePlan(json, version, finishReason);
        }
        catch (LlmPlannerException exception) when (_options.InvalidJsonRepairAttempts > 0)
        {
            return await RepairPlanAsync(
                    originalRequest,
                    json,
                    finishReason,
                    version,
                    isRefinement,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<WorkflowPlan> RepairPlanAsync(
        LlmRequest originalRequest,
        string invalidJson,
        LlmFinishReason initialFinishReason,
        int version,
        bool isRefinement,
        LlmPlannerException firstException,
        CancellationToken cancellationToken)
    {
        var lastJson = invalidJson;
        LlmPlannerException lastException = firstException;
        var lastRepairFinishReason = LlmFinishReason.Unknown;

        for (var attempt = 1; attempt <= _options.InvalidJsonRepairAttempts; attempt++)
        {
            var repairRequest = isRefinement
                ? WorkflowPlanPromptBuilder.BuildRefinementRepairRequest(
                    originalRequest,
                    lastJson,
                    lastException.Message,
                    attempt,
                    _options)
                : WorkflowPlanPromptBuilder.BuildInitialPlanRepairRequest(
                    originalRequest,
                    lastJson,
                    lastException.Message,
                    attempt,
                    _options);

            var repairResponse = await GenerateAsync(repairRequest, cancellationToken).ConfigureAwait(false);
            lastJson = repairResponse.StructuredJson ?? repairResponse.Text;
            lastRepairFinishReason = repairResponse.FinishReason;

            try
            {
                return isRefinement
                    ? ParseRefinementPlan(lastJson, version, repairResponse.FinishReason)
                    : ParsePlan(lastJson, version, repairResponse.FinishReason);
            }
            catch (LlmPlannerException exception)
            {
                lastException = exception;
            }
        }

        var payloadKind = isRefinement ? "refinement" : "plan";
        var truncation = initialFinishReason == LlmFinishReason.MaxTokens ||
            lastRepairFinishReason == LlmFinishReason.MaxTokens;
        throw new LlmPlannerException(
            $"Planner returned invalid {payloadKind} JSON and repair failed after {_options.InvalidJsonRepairAttempts} attempt(s). Initial finish reason: {initialFinishReason}; last repair finish reason: {lastRepairFinishReason}; truncation suspected: {truncation}. Last repair error: {lastException.Message}",
            lastException);
    }

    private static WorkflowPlan ParseRefinementPlan(
        string json,
        int version,
        LlmFinishReason finishReason)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<PlanRefinementJsonContract>(json, JsonOptions)
                ?? throw new LlmPlannerException(InvalidPayloadMessage("refinement", finishReason, "empty payload"));

            if (contract.RefinedPlan is null)
            {
                throw new LlmPlannerException(InvalidPayloadMessage("refinement", finishReason, "payload did not include refinedPlan"));
            }

            return contract.RefinedPlan.ToWorkflowPlan(version) with
            {
                PlanningReason = PlanRefinementReasons.Normalize(
                    contract.Reason,
                    PlanRefinementReasons.Observation)
            };
        }
        catch (JsonException exception)
        {
            throw new LlmPlannerException(InvalidPayloadMessage("refinement", finishReason, "invalid JSON"), exception);
        }
    }

    private static WorkflowPlan ParsePlan(
        string json,
        int version,
        LlmFinishReason finishReason)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<WorkflowPlanJsonContract>(json, JsonOptions)
                ?? throw new LlmPlannerException(InvalidPayloadMessage("plan", finishReason, "empty payload"));

            return contract.ToWorkflowPlan(version);
        }
        catch (JsonException exception)
        {
            throw new LlmPlannerException(InvalidPayloadMessage("plan", finishReason, "invalid JSON"), exception);
        }
    }

    private static string InvalidPayloadMessage(
        string payloadKind,
        LlmFinishReason finishReason,
        string failure) =>
        finishReason == LlmFinishReason.MaxTokens
            ? $"Planner returned truncated {payloadKind} JSON ({failure}); provider finish reason was MaxTokens."
            : $"Planner returned invalid {payloadKind} JSON ({failure}); provider finish reason was {finishReason}.";

    private async Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmClientException exception)
        {
            throw new WorkflowPlannerException(
                WorkflowPlannerFailureKind.Unavailable,
                "planner.unavailable",
                exception.Message,
                exception);
        }
    }
}
