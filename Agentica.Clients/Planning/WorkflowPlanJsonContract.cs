using System.Text.Json;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.Clients.Planning;

public sealed record WorkflowPlanJsonContract(
    string? PlanId,
    string? Description,
    IReadOnlyList<WorkflowPlanStepJsonContract>? Steps,
    string? CompletionCondition)
{
    public WorkflowPlan ToWorkflowPlan(int version)
    {
        if (string.IsNullOrWhiteSpace(PlanId))
        {
            throw new LlmPlannerException("Planner plan payload is missing planId.");
        }

        if (Steps is null || Steps.Count == 0)
        {
            throw new LlmPlannerException("Planner plan payload did not include any steps.");
        }

        return new WorkflowPlan(
            PlanId: PlanId,
            Version: version,
            Steps: Steps.Select(step => step.ToPlanStep()).ToArray(),
            Description: Description ?? string.Empty);
    }
}

public sealed record WorkflowPlanStepJsonContract(
    string? StepId,
    string? ToolId,
    string? Kind,
    string? Effect,
    IReadOnlyDictionary<string, JsonElement>? Input,
    string? Reason)
{
    public PlanStep ToPlanStep()
    {
        if (string.IsNullOrWhiteSpace(StepId))
        {
            throw new LlmPlannerException("Planner step payload is missing stepId.");
        }

        if (string.IsNullOrWhiteSpace(ToolId))
        {
            throw new LlmPlannerException($"Planner step '{StepId}' is missing toolId.");
        }

        if (!Enum.TryParse<ToolKind>(Kind, ignoreCase: true, out var kind))
        {
            throw new LlmPlannerException($"Planner step '{StepId}' has invalid kind '{Kind}'.");
        }

        if (!Enum.TryParse<ToolEffect>(Effect, ignoreCase: true, out var effect))
        {
            throw new LlmPlannerException($"Planner step '{StepId}' has invalid effect '{Effect}'.");
        }

        var input = Input?.ToDictionary(
            pair => pair.Key,
            pair => JsonValueConverter.Convert(pair.Value),
            StringComparer.Ordinal) ?? new Dictionary<string, object?>(StringComparer.Ordinal);

        return new PlanStep(StepId, ToolId, kind, effect, input);
    }
}
