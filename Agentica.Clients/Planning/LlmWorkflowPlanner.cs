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
        var response = await GenerateAsync(
            WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request, _options),
            cancellationToken).ConfigureAwait(false);

        return ParsePlan(response.StructuredJson ?? response.Text, version: 1);
    }

    public async Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default)
    {
        var response = await GenerateAsync(
            WorkflowPlanPromptBuilder.BuildRefinementRequest(request, observation, _options),
            cancellationToken).ConfigureAwait(false);

        var json = response.StructuredJson ?? response.Text;
        try
        {
            var contract = JsonSerializer.Deserialize<PlanRefinementJsonContract>(json, JsonOptions)
                ?? throw new LlmPlannerException("Planner returned an empty refinement payload.");

            if (contract.RefinedPlan is null)
            {
                throw new LlmPlannerException("Planner refinement payload did not include refinedPlan.");
            }

            return contract.RefinedPlan.ToWorkflowPlan(version: 2);
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
