using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Orchestration.Planning;
using Agentica.Planning;

namespace Agentica.Clients.Orchestration;

public sealed class LlmTaskPlanner : ITaskPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILlmClient _client;
    private readonly LlmTaskPlannerOptions _options;

    public LlmTaskPlanner(ILlmClient client, LlmTaskPlannerOptions? options = null)
    {
        _client = client;
        _options = options ?? LlmTaskPlannerOptions.Default;
    }

    public async Task<TaskGraphPlan> CreatePlanAsync(
        TaskPlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await GenerateAsync(
            TaskGraphPromptBuilder.BuildInitialPlanRequest(request, _options),
            cancellationToken).ConfigureAwait(false);

        return ParsePlan(response.StructuredJson ?? response.Text);
    }

    public async Task<TaskGraphRefinement> RefinePlanAsync(
        TaskRefinementRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await GenerateAsync(
            TaskGraphPromptBuilder.BuildRefinementRequest(request, _options),
            cancellationToken).ConfigureAwait(false);

        var json = response.StructuredJson ?? response.Text;
        try
        {
            var contract = JsonSerializer.Deserialize<TaskGraphRefinementJsonContract>(json, JsonOptions)
                ?? throw new LlmTaskPlannerException("Task planner returned an empty refinement payload.");

            return contract.ToRefinement();
        }
        catch (JsonException exception)
        {
            throw new LlmTaskPlannerException("Task planner returned invalid refinement JSON.", exception);
        }
    }

    private static TaskGraphPlan ParsePlan(string json)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<TaskGraphPlanJsonContract>(json, JsonOptions)
                ?? throw new LlmTaskPlannerException("Task planner returned an empty plan payload.");

            var plan = contract.ToTaskGraphPlan();
            TaskGraphValidator.Validate(plan);
            return plan;
        }
        catch (JsonException exception)
        {
            throw new LlmTaskPlannerException("Task planner returned invalid plan JSON.", exception);
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
                "task_planner.unavailable",
                exception.Message,
                exception);
        }
    }
}
