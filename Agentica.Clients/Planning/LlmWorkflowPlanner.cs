using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Observations;
using Agentica.Planning;

namespace Agentica.Clients.Planning;

public sealed class LlmWorkflowPlanner : IWorkflowPlanner
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
                version: 2,
                isRefinement: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowPlan> ParsePlanWithRepairAsync(
        LlmRequest originalRequest,
        string json,
        int version,
        bool isRefinement,
        CancellationToken cancellationToken)
    {
        try
        {
            return isRefinement
                ? ParseRefinementPlan(json, version)
                : ParsePlan(json, version);
        }
        catch (LlmPlannerException exception) when (_options.InvalidJsonRepairAttempts > 0)
        {
            return await RepairPlanAsync(
                    originalRequest,
                    json,
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
        int version,
        bool isRefinement,
        LlmPlannerException firstException,
        CancellationToken cancellationToken)
    {
        var lastJson = invalidJson;
        LlmPlannerException lastException = firstException;

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

            try
            {
                return isRefinement
                    ? ParseRefinementPlan(lastJson, version)
                    : ParsePlan(lastJson, version);
            }
            catch (LlmPlannerException exception)
            {
                lastException = exception;
            }
        }

        var payloadKind = isRefinement ? "refinement" : "plan";
        throw new LlmPlannerException(
            $"Planner returned invalid {payloadKind} JSON and repair failed after {_options.InvalidJsonRepairAttempts} attempt(s). Last repair error: {lastException.Message}",
            lastException);
    }

    private static WorkflowPlan ParseRefinementPlan(string json, int version)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<PlanRefinementJsonContract>(json, JsonOptions)
                ?? throw new LlmPlannerException("Planner returned an empty refinement payload.");

            if (contract.RefinedPlan is null)
            {
                throw new LlmPlannerException("Planner refinement payload did not include refinedPlan.");
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
            throw new LlmPlannerException("Planner returned invalid refinement JSON.", exception);
        }
    }

    private static WorkflowPlan ParsePlan(string json, int version)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<WorkflowPlanJsonContract>(json, JsonOptions)
                ?? throw new LlmPlannerException("Planner returned an empty plan payload.");

            return contract.ToWorkflowPlan(version);
        }
        catch (JsonException exception)
        {
            throw new LlmPlannerException("Planner returned invalid plan JSON.", exception);
        }
    }

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
