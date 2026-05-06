using Agentica.Planning;
using Agentica.Tools;
using Agentica.Validation;

namespace Agentica.Execution;

internal sealed class PlanExecutionValidator
{
    private readonly ToolCatalog _toolCatalog;
    private readonly ExecutionPolicy _policy;

    public PlanExecutionValidator(ToolCatalog toolCatalog, ExecutionPolicy policy)
    {
        _toolCatalog = toolCatalog;
        _policy = policy;
    }

    public IReadOnlyList<ValidationIssue> Validate(WorkflowPlan plan)
    {
        var issues = new List<ValidationIssue>();

        if (plan.Steps.Count == 0)
        {
            issues.Add(new ValidationIssue(
                "plan.steps.required",
                $"Plan '{plan.PlanId}' must include at least one step."));
        }

        foreach (var step in plan.Steps)
        {
            var registration = _toolCatalog.Resolve(step.ToolId);
            if (registration is null)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.unknown_tool",
                    $"Step '{step.StepId}' references unknown tool '{step.ToolId}'.",
                    step.StepId));
                continue;
            }

            if (registration.Descriptor.Kind != step.Kind)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.kind_mismatch",
                    $"Step '{step.StepId}' kind '{step.Kind}' does not match tool kind '{registration.Descriptor.Kind}'.",
                    step.StepId));
            }

            if (registration.Descriptor.Effect != step.Effect)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.effect_mismatch",
                    $"Step '{step.StepId}' effect '{step.Effect}' does not match tool effect '{registration.Descriptor.Effect}'.",
                    step.StepId));
            }

            if (!_policy.EffectiveEffectPolicy.Allows(registration.Descriptor.Effect))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.effect_not_allowed",
                    $"Step '{step.StepId}' references tool effect '{registration.Descriptor.Effect}' which is not allowed by policy.",
                    step.StepId));
            }

            if (step.Effect != ToolEffect.ReadOnly && step.Kind != ToolKind.Action)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.mutation_hidden",
                    $"Step '{step.StepId}' has mutation effect but is not an action step.",
                    step.StepId));
            }

            issues.AddRange(ToolInputValidator.Validate(step, registration.Descriptor.InputSchema));
        }

        return issues;
    }
}
