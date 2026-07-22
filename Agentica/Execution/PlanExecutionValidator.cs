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

    public IReadOnlyList<ValidationIssue> Validate(WorkflowPlan plan) =>
        Validate(
            plan,
            Array.Empty<string>(),
            _policy.EffectiveSecurityPolicy.InitialBoundaries
                .Append(ToolDataBoundary.UserContent)
                .ToHashSet(),
            _toolCatalog.ManifestHash);

    public IReadOnlyList<ValidationIssue> Validate(
        WorkflowPlan plan,
        IReadOnlyCollection<string> completedStepIds,
        IReadOnlySet<ToolDataBoundary> exposedBoundaries,
        string manifestHash)
    {
        var issues = new List<ValidationIssue>();
        var completedStepIdSet = completedStepIds.ToHashSet(StringComparer.Ordinal);
        var projectedExposedBoundaries = exposedBoundaries.ToHashSet();

        if (plan.Steps.Count == 0)
        {
            issues.Add(new ValidationIssue(
                "plan.steps.required",
                $"Plan '{plan.PlanId}' must include at least one step."));
        }

        var seenStepIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateStepIds = plan.Steps
            .Where(step => !seenStepIds.Add(step.StepId))
            .Select(step => step.StepId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var stepId in duplicateStepIds)
        {
            issues.Add(new ValidationIssue(
                "plan.step.duplicate_id",
                $"Plan '{plan.PlanId}' contains duplicate step id '{stepId}'.",
                stepId));
        }

        foreach (var step in plan.Steps.Where(step => completedStepIdSet.Contains(step.StepId)))
        {
            issues.Add(new ValidationIssue(
                "plan.step.reused_completed_id",
                $"Plan '{plan.PlanId}' reuses completed step id '{step.StepId}'.",
                step.StepId));
        }

        var stepIndexById = plan.Steps
            .Select((step, index) => new { step.StepId, Index = index })
            .GroupBy(item => item.StepId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Index, StringComparer.Ordinal);

        foreach (var step in plan.Steps)
        {
            ValidateDependencies(plan, step, stepIndexById, completedStepIdSet, issues);

            var registration = _toolCatalog.Resolve(step.ToolId);
            if (registration is null)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.unknown_tool",
                    $"Step '{step.StepId}' references unknown tool '{step.ToolId}'.",
                    step.StepId));
                continue;
            }

            var descriptor = registration.PlannerProjection;
            var security = registration.Security;

            if (descriptor.Kind != step.Kind)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.kind_mismatch",
                    $"Step '{step.StepId}' kind '{step.Kind}' does not match tool kind '{descriptor.Kind}'.",
                    step.StepId));
            }

            if (security.Effect != step.Effect)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.effect_mismatch",
                    $"Step '{step.StepId}' effect '{step.Effect}' does not match authoritative tool effect '{security.Effect}'.",
                    step.StepId));
            }

            if (!_policy.EffectiveEffectPolicy.Allows(security.Effect))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.effect_not_allowed",
                    $"Step '{step.StepId}' references tool effect '{security.Effect}' which is not allowed by policy.",
                    step.StepId));
            }

            var plannerBoundaryViolations = ToolSecurityEvaluator.PlannerBoundaryViolations(
                _policy.EffectiveSecurityPolicy,
                security.ExposesToPlanner);
            if (plannerBoundaryViolations.Count > 0)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.planner_boundary_not_allowed",
                    $"Step '{step.StepId}' would expose disallowed data boundaries to the external planner: " +
                    $"{string.Join(", ", plannerBoundaryViolations)}.",
                    step.StepId));
            }

            var grant = ToolSecurityEvaluator.EvaluateDispatch(
                _policy.EffectiveSecurityPolicy,
                manifestHash,
                registration,
                projectedExposedBoundaries,
                DateTimeOffset.UtcNow);
            if (!grant.Allowed)
            {
                issues.Add(new ValidationIssue(
                    grant.Code ?? "plan.step.security_grant_required",
                    grant.Message ?? $"Step '{step.StepId}' is not authorized for dispatch.",
                    step.StepId));
            }

            if (security.Effect != ToolEffect.ReadOnly && step.Kind != ToolKind.Action)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.mutation_hidden",
                    $"Step '{step.StepId}' has mutation effect but is not an action step.",
                    step.StepId));
            }

            issues.AddRange(ToolInputValidator.Validate(step, descriptor.InputSchema));
            projectedExposedBoundaries.UnionWith(security.ExposesToPlanner);
        }

        ValidateBatches(plan, issues);

        return issues;
    }

    private static void ValidateDependencies(
        WorkflowPlan plan,
        PlanStep step,
        IReadOnlyDictionary<string, int> stepIndexById,
        IReadOnlySet<string> completedStepIds,
        List<ValidationIssue> issues)
    {
        var dependencies = step.DependsOn
            .Where(dependency => !string.IsNullOrWhiteSpace(dependency))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (dependencies.Length != step.DependsOn.Count)
        {
            issues.Add(new ValidationIssue(
                "plan.step.dependency.invalid",
                $"Step '{step.StepId}' contains empty or duplicate dependencies.",
                step.StepId));
        }

        if (!stepIndexById.TryGetValue(step.StepId, out var stepIndex))
        {
            return;
        }

        foreach (var dependency in dependencies)
        {
            if (!stepIndexById.TryGetValue(dependency, out var dependencyIndex))
            {
                if (!completedStepIds.Contains(dependency))
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.dependency.unknown",
                        $"Step '{step.StepId}' depends on unknown step '{dependency}'.",
                        step.StepId));
                }

                continue;
            }

            if (dependencyIndex >= stepIndex)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.dependency.order",
                    $"Step '{step.StepId}' depends on step '{dependency}' which does not appear earlier in the plan.",
                    step.StepId));
            }
        }
    }

    private void ValidateBatches(WorkflowPlan plan, List<ValidationIssue> issues)
    {
        foreach (var group in plan.Steps
            .Where(step => !string.IsNullOrWhiteSpace(step.BatchId))
            .GroupBy(step => step.BatchId!, StringComparer.Ordinal))
        {
            var steps = group.ToArray();
            if (!_policy.AllowReadOnlyParallelBatches)
            {
                foreach (var step in steps)
                {
                    issues.Add(new ValidationIssue(
                        "plan.batch.not_allowed",
                        $"Step '{step.StepId}' uses batch '{group.Key}', but read-only parallel batches are disabled by policy.",
                        step.StepId));
                }
            }

            if (steps.Length > _policy.MaxBatchSize)
            {
                foreach (var step in steps)
                {
                    issues.Add(new ValidationIssue(
                        "plan.batch.size",
                        $"Batch '{group.Key}' has {steps.Length} steps, exceeding policy max batch size {_policy.MaxBatchSize}.",
                        step.StepId));
                }
            }

            if (steps.Length > _policy.MaxParallelism)
            {
                foreach (var step in steps)
                {
                    issues.Add(new ValidationIssue(
                        "plan.batch.parallelism",
                        $"Batch '{group.Key}' has {steps.Length} steps, exceeding policy max parallelism {_policy.MaxParallelism}.",
                        step.StepId));
                }
            }

            if (steps.Any(step => step.Kind != ToolKind.Query || step.Effect != ToolEffect.ReadOnly))
            {
                foreach (var step in steps.Where(step => step.Kind != ToolKind.Query || step.Effect != ToolEffect.ReadOnly))
                {
                    issues.Add(new ValidationIssue(
                        "plan.batch.readonly_only",
                        $"Batch '{group.Key}' contains step '{step.StepId}' which is not a read-only query step.",
                        step.StepId));
                }
            }

            var batchStepIds = steps.Select(step => step.StepId).ToHashSet(StringComparer.Ordinal);
            foreach (var step in steps)
            {
                if (step.DependsOn.Any(batchStepIds.Contains))
                {
                    issues.Add(new ValidationIssue(
                        "plan.batch.internal_dependency",
                        $"Batch '{group.Key}' contains step '{step.StepId}' with a dependency inside the same batch.",
                        step.StepId));
                }
            }
        }
    }
}
