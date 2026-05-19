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

        return ParsePlan(response.StructuredJson ?? response.Text, response.FinishReason);
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
                ?? throw new LlmTaskPlannerException(InvalidPayloadMessage("refinement", response.FinishReason, "empty payload"));

            return contract.ToRefinement();
        }
        catch (JsonException exception)
        {
            throw new LlmTaskPlannerException(InvalidPayloadMessage("refinement", response.FinishReason, "invalid JSON"), exception);
        }
    }

    private static TaskGraphPlan ParsePlan(
        string json,
        LlmFinishReason finishReason)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<TaskGraphPlanJsonContract>(json, JsonOptions)
                ?? throw new LlmTaskPlannerException(InvalidPayloadMessage("plan", finishReason, "empty payload"));

            var plan = contract.ToTaskGraphPlan();
            TaskGraphValidator.Validate(plan);
            return plan;
        }
        catch (JsonException exception)
        {
            throw new LlmTaskPlannerException(InvalidPayloadMessage("plan", finishReason, "invalid JSON"), exception);
        }
    }

    private static string InvalidPayloadMessage(
        string payloadKind,
        LlmFinishReason finishReason,
        string failure) =>
        finishReason == LlmFinishReason.MaxTokens
            ? $"Task planner returned truncated {payloadKind} JSON ({failure}); provider finish reason was MaxTokens."
            : $"Task planner returned invalid {payloadKind} JSON ({failure}); provider finish reason was {finishReason}.";

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
