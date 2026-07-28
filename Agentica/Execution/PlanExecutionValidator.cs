using System.Collections.ObjectModel;
using Agentica.Planning;
using Agentica.Tools;
using Agentica.Validation;

namespace Agentica.Execution;

internal sealed class PlanExecutionValidator
{
    private readonly ToolCatalog _toolCatalog;
    private readonly ExecutionPolicy _policy;
    private readonly IReadOnlyDictionary<string, ToolInputValidationSchema?> _inputSchemas;

    public PlanExecutionValidator(ToolCatalog toolCatalog, ExecutionPolicy policy)
    {
        _toolCatalog = toolCatalog;
        _policy = policy;

        var issues = new ValidationIssueCollector();
        var work = new ValidationWorkBudget();
        var schemas = new Dictionary<string, ToolInputValidationSchema?>(StringComparer.Ordinal);
        foreach (var registration in toolCatalog.Manifest.Registrations)
        {
            if (!work.TryConsume(issues))
            {
                break;
            }

            schemas.Add(
                registration.PlannerProjection.ToolId,
                ToolInputValidator.CompileSchema(
                    registration.PlannerProjection.InputSchema,
                    work,
                    issues));
        }

        var schemaIssues = issues.Complete();
        if (schemaIssues.Count > 0)
        {
            throw new InvalidOperationException(
                "Compiled tool manifest could not be converted to bounded input-validation schemas.");
        }

        _inputSchemas = new ReadOnlyDictionary<string, ToolInputValidationSchema?>(schemas);
    }

    public IReadOnlyList<ValidationIssue> ValidateDispatchInput(PlanStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var issues = new ValidationIssueCollector();
        var work = new ValidationWorkBudget();
        if (!_inputSchemas.TryGetValue(step.ToolId, out var inputSchema))
        {
            issues.Add(new ValidationIssue(
                "plan.step.unknown_tool",
                $"Step '{ValidationIssueCollector.Display(step.StepId)}' references an unknown tool.",
                ValidationIssueCollector.Display(step.StepId)));
            return issues.Complete();
        }

        ToolInputValidator.ValidateCompiled(step, inputSchema, work, issues);
        return issues.Complete();
    }

    public IReadOnlyList<ValidationIssue> Validate(WorkflowPlan plan) =>
        Validate(
            plan,
            Array.Empty<string>(),
            _policy.EffectiveSecurityPolicy.InitialBoundaries
                .Append(ToolDataBoundary.UserContent)
                .ToHashSet(),
            _toolCatalog.ManifestHash,
            authorizationScopeId: null,
            attemptNumber: 1,
            enforceAuthorizationScope: true);

    public IReadOnlyList<ValidationIssue> Validate(
        WorkflowPlan plan,
        string authorizationScopeId,
        int attemptNumber)
    {
        ArgumentNullException.ThrowIfNull(authorizationScopeId);
        if (!ValidationIssueCollector.IsDisplayBounded(authorizationScopeId))
        {
            return Array.AsReadOnly(new[]
            {
                new ValidationIssue(
                    "plan.authorization_scope.identifier.too_long",
                    "Authorization scope id exceeds the validation identifier limit.")
            });
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationScopeId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attemptNumber);

        return Validate(
            plan,
            Array.Empty<string>(),
            _policy.EffectiveSecurityPolicy.InitialBoundaries
                .Append(ToolDataBoundary.UserContent)
                .ToHashSet(),
            _toolCatalog.ManifestHash,
            authorizationScopeId,
            attemptNumber);
    }

    public IReadOnlyList<ValidationIssue> Validate(
        WorkflowPlan plan,
        IReadOnlyCollection<string> completedStepIds,
        IReadOnlySet<ToolDataBoundary> exposedBoundaries,
        string manifestHash,
        string? authorizationScopeId,
        int attemptNumber,
        bool enforceAuthorizationScope = true,
        IReadOnlyDictionary<string, string>? sourceIdsByCanonicalId = null,
        bool allowDeferredInputAuthorization = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(completedStepIds);
        ArgumentNullException.ThrowIfNull(exposedBoundaries);
        ArgumentNullException.ThrowIfNull(manifestHash);

        var issues = new ValidationIssueCollector();
        var validationWork = new ValidationWorkBudget();
        var completedStepIdSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var completedStepId in completedStepIds)
        {
            if (!validationWork.TryConsume(issues))
            {
                return issues.Complete();
            }

            if (!ValidationIssueCollector.IsDisplayBounded(completedStepId))
            {
                issues.Add(new ValidationIssue(
                    "plan.completed_step.identifier.too_long",
                    "A completed step id exceeds the validation identifier limit."));
                return issues.Complete();
            }

            if (string.IsNullOrWhiteSpace(completedStepId))
            {
                issues.Add(new ValidationIssue(
                    "plan.completed_step.identifier.required",
                    "Completed step ids cannot be blank."));
                return issues.Complete();
            }

            completedStepIdSet.Add(completedStepId);
        }

        var projectedExposedBoundaries = new HashSet<ToolDataBoundary>();
        foreach (var boundary in exposedBoundaries)
        {
            if (!validationWork.TryConsume(issues))
            {
                return issues.Complete();
            }

            projectedExposedBoundaries.Add(boundary);
        }

        var reservedGrantIds = new HashSet<string>(StringComparer.Ordinal);

        if (!ValidationIssueCollector.IsDisplayBounded(plan.PlanId))
        {
            issues.Add(new ValidationIssue(
                "plan.identifier.too_long",
                "Plan id exceeds the validation identifier limit."));
            return issues.Complete();
        }

        if (string.IsNullOrWhiteSpace(plan.PlanId))
        {
            issues.Add(new ValidationIssue(
                "plan.identifier.required",
                "Plan id is required."));
            return issues.Complete();
        }

        if (authorizationScopeId is not null &&
            !ValidationIssueCollector.IsDisplayBounded(authorizationScopeId))
        {
            issues.Add(new ValidationIssue(
                "plan.authorization_scope.identifier.too_long",
                "Authorization scope id exceeds the validation identifier limit."));
            return issues.Complete();
        }

        var remainingStepBudget = Math.Max(0, _policy.MaxSteps - completedStepIdSet.Count);
        if (plan.Steps.Count > remainingStepBudget)
        {
            issues.Add(new ValidationIssue(
                "plan.steps.limit",
                $"Plan '{plan.PlanId}' contains {plan.Steps.Count} steps, exceeding the remaining " +
                $"execution budget of {remainingStepBudget}."));
            return issues.Complete();
        }

        if (plan.Steps.Count == 0)
        {
            issues.Add(new ValidationIssue(
                "plan.steps.required",
                $"Plan '{plan.PlanId}' must include at least one step."));
            return issues.Complete();
        }

        var stepIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicateStepIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Steps.Count; index++)
        {
            if (!validationWork.TryConsume(issues))
            {
                return issues.Complete();
            }

            var step = plan.Steps[index];
            if (!ValidationIssueCollector.IsDisplayBounded(step.StepId))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.identifier.too_long",
                    "A plan step id exceeds the validation identifier limit.",
                    ValidationIssueCollector.Display(step.StepId)));
                return issues.Complete();
            }

            if (string.IsNullOrWhiteSpace(step.StepId))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.identifier.required",
                    "Plan step ids cannot be blank."));
                return issues.Complete();
            }

            if (!ValidationIssueCollector.IsDisplayBounded(step.ToolId))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.tool_identifier.too_long",
                    "A plan step tool id exceeds the validation identifier limit.",
                    step.StepId));
                return issues.Complete();
            }

            if (string.IsNullOrWhiteSpace(step.ToolId))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.tool_identifier.required",
                    $"Step '{step.StepId}' must identify a tool.",
                    step.StepId));
                return issues.Complete();
            }

            if (step.BatchId is not null)
            {
                if (!ValidationIssueCollector.IsDisplayBounded(step.BatchId))
                {
                    issues.Add(new ValidationIssue(
                        "plan.batch.identifier.too_long",
                        $"Step '{step.StepId}' has a batch id exceeding the validation identifier limit.",
                        step.StepId));
                    return issues.Complete();
                }

                if (string.IsNullOrWhiteSpace(step.BatchId))
                {
                    issues.Add(new ValidationIssue(
                        "plan.batch.identifier.required",
                        $"Step '{step.StepId}' has a blank batch id.",
                        step.StepId));
                    return issues.Complete();
                }
            }

            foreach (var dependency in step.DependsOn)
            {
                if (!validationWork.TryConsume(issues))
                {
                    return issues.Complete();
                }

                if (!ValidationIssueCollector.IsDisplayBounded(dependency))
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.dependency.identifier.too_long",
                        $"Step '{step.StepId}' has a dependency id exceeding the validation identifier limit.",
                        step.StepId));
                    return issues.Complete();
                }

                if (string.IsNullOrWhiteSpace(dependency))
                {
                    issues.Add(new ValidationIssue(
                        "plan.step.dependency.identifier.required",
                        $"Step '{step.StepId}' contains a blank dependency id.",
                        step.StepId));
                    return issues.Complete();
                }
            }

            if (duplicateStepIds.Contains(step.StepId) ||
                !stepIndexById.TryAdd(step.StepId, index))
            {
                duplicateStepIds.Add(step.StepId);
                stepIndexById.Remove(step.StepId);
            }
        }

        foreach (var stepId in duplicateStepIds)
        {
            if (issues.IsFull || !validationWork.TryConsume(issues))
            {
                break;
            }

            issues.Add(new ValidationIssue(
                "plan.step.duplicate_id",
                $"Plan '{plan.PlanId}' contains duplicate step id '{stepId}'.",
                stepId));
        }

        foreach (var step in plan.Steps)
        {
            if (issues.IsFull || !validationWork.TryConsume(issues))
            {
                break;
            }

            if (completedStepIdSet.Contains(step.StepId))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.reused_completed_id",
                    $"Plan '{plan.PlanId}' reuses completed step id '{step.StepId}'.",
                    step.StepId));
            }
        }

        foreach (var step in plan.Steps)
        {
            if (issues.IsFull || !validationWork.TryConsume(issues))
            {
                break;
            }

            ValidateDependencies(
                plan,
                step,
                stepIndexById,
                completedStepIdSet,
                validationWork,
                issues);
            if (issues.IsFull)
            {
                break;
            }

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

            string? operationalInputDigest = null;
            if (sourceIdsByCanonicalId is { Count: > 0 })
            {
                try
                {
                    var restoredInput = ToolResultNormalizer.RestoreSourceIdentities(
                        step.Input,
                        sourceIdsByCanonicalId);
                    var frozenInput = ToolResultNormalizer.SnapshotStructuredData(restoredInput);
                    operationalInputDigest = ToolInvocationAuthorization.ComputeOperationalInputDigest(frozenInput);
                }
                catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
                {
                    issues.Add(new ValidationIssue(
                        "tool.security.input_digest_invalid",
                        $"Step '{step.StepId}' operational input could not be safely bound to an execution grant: " +
                        $"{exception.GetType().Name}.",
                        step.StepId));
                }
            }

            var hasIncompleteDependencies = step.DependsOn.Any(
                dependency => !completedStepIdSet.Contains(dependency));
            if ((security.Effect == ToolEffect.ExternalSideEffect ||
                 security.ApprovalRequirement != ToolApprovalRequirement.None) &&
                !validationWork.TryConsume(
                    issues,
                    GrantEvaluationWorkUnits(
                        _policy.EffectiveSecurityPolicy.ExecutionGrants.Count)))
            {
                break;
            }

            var grant = allowDeferredInputAuthorization && hasIncompleteDependencies
                ? ToolSecurityEvaluator.EvaluateDeferredPlanAuthorization(
                    _policy.EffectiveSecurityPolicy,
                    manifestHash,
                    registration,
                    step,
                    authorizationScopeId,
                    attemptNumber,
                    projectedExposedBoundaries,
                    DateTimeOffset.UtcNow,
                    reservedGrantIds)
                : ToolSecurityEvaluator.EvaluateDispatch(
                    _policy.EffectiveSecurityPolicy,
                    manifestHash,
                    registration,
                    step,
                    authorizationScopeId,
                    attemptNumber,
                    projectedExposedBoundaries,
                    DateTimeOffset.UtcNow,
                    enforceAuthorizationScope,
                    reservedGrantIds,
                    operationalInputDigest);
            if (!grant.Allowed)
            {
                issues.Add(new ValidationIssue(
                    grant.Code ?? "plan.step.security_grant_required",
                    grant.Message ?? $"Step '{step.StepId}' is not authorized for dispatch.",
                    step.StepId));
            }
            else if (grant.Grant is not null)
            {
                reservedGrantIds.Add(grant.Grant.GrantId);
            }

            if (security.Effect != ToolEffect.ReadOnly && step.Kind != ToolKind.Action)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.mutation_hidden",
                    $"Step '{step.StepId}' has mutation effect but is not an action step.",
                    step.StepId));
            }

            if (!_inputSchemas.TryGetValue(descriptor.ToolId, out var inputSchema))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.registration_schema_missing",
                    $"Step '{step.StepId}' could not resolve its compiled input schema.",
                    step.StepId));
            }
            else if (!issues.IsFull)
            {
                ToolInputValidator.ValidateCompiled(
                    step,
                    inputSchema,
                    validationWork,
                    issues);
            }

            if (issues.IsFull)
            {
                break;
            }

            projectedExposedBoundaries.UnionWith(security.ExposesToPlanner);
        }

        ValidateBatches(plan, validationWork, issues);

        return issues.Complete();
    }

    private static int GrantEvaluationWorkUnits(int grantCount)
    {
        const int passesPerGrant = 8;
        var units = 1L + ((long)Math.Max(0, grantCount) * passesPerGrant);
        return (int)Math.Min(int.MaxValue, units);
    }

    private static void ValidateDependencies(
        WorkflowPlan plan,
        PlanStep step,
        IReadOnlyDictionary<string, int> stepIndexById,
        IReadOnlySet<string> completedStepIds,
        ValidationWorkBudget work,
        ValidationIssueCollector issues)
    {
        if (!stepIndexById.TryGetValue(step.StepId, out var stepIndex))
        {
            return;
        }

        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in step.DependsOn)
        {
            if (issues.IsFull || !work.TryConsume(issues))
            {
                break;
            }

            if (!dependencies.Add(dependency))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.dependency.invalid",
                    $"Step '{step.StepId}' contains a duplicate dependency id.",
                    step.StepId));
                continue;
            }

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

    private void ValidateBatches(
        WorkflowPlan plan,
        ValidationWorkBudget work,
        ValidationIssueCollector issues)
    {
        var batches = new Dictionary<string, List<PlanStep>>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            if (issues.IsFull || !work.TryConsume(issues))
            {
                break;
            }

            if (step.BatchId is null)
            {
                continue;
            }

            if (!batches.TryGetValue(step.BatchId, out var steps))
            {
                steps = [];
                batches.Add(step.BatchId, steps);
            }

            steps.Add(step);
        }

        foreach (var (batchId, steps) in batches)
        {
            if (issues.IsFull || !work.TryConsume(issues))
            {
                break;
            }

            if (!_policy.AllowReadOnlyParallelBatches)
            {
                foreach (var step in steps)
                {
                    if (issues.IsFull || !work.TryConsume(issues))
                    {
                        break;
                    }

                    issues.Add(new ValidationIssue(
                        "plan.batch.not_allowed",
                        $"Step '{step.StepId}' uses batch '{batchId}', but read-only parallel batches are disabled by policy.",
                        step.StepId));
                }
            }

            if (steps.Count > _policy.MaxBatchSize)
            {
                foreach (var step in steps)
                {
                    if (issues.IsFull || !work.TryConsume(issues))
                    {
                        break;
                    }

                    issues.Add(new ValidationIssue(
                        "plan.batch.size",
                        $"Batch '{batchId}' has {steps.Count} steps, exceeding policy max batch size {_policy.MaxBatchSize}.",
                        step.StepId));
                }
            }

            if (steps.Count > _policy.MaxParallelism)
            {
                foreach (var step in steps)
                {
                    if (issues.IsFull || !work.TryConsume(issues))
                    {
                        break;
                    }

                    issues.Add(new ValidationIssue(
                        "plan.batch.parallelism",
                        $"Batch '{batchId}' has {steps.Count} steps, exceeding policy max parallelism {_policy.MaxParallelism}.",
                        step.StepId));
                }
            }

            foreach (var step in steps)
            {
                if (issues.IsFull || !work.TryConsume(issues))
                {
                    break;
                }

                if (step.Kind != ToolKind.Query || step.Effect != ToolEffect.ReadOnly)
                {
                    issues.Add(new ValidationIssue(
                        "plan.batch.readonly_only",
                        $"Batch '{batchId}' contains step '{step.StepId}' which is not a read-only query step.",
                        step.StepId));
                }
            }

            var batchStepIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in steps)
            {
                if (issues.IsFull || !work.TryConsume(issues))
                {
                    break;
                }

                batchStepIds.Add(step.StepId);
            }

            foreach (var step in steps)
            {
                if (issues.IsFull || !work.TryConsume(issues))
                {
                    break;
                }

                foreach (var dependency in step.DependsOn)
                {
                    if (issues.IsFull || !work.TryConsume(issues))
                    {
                        break;
                    }

                    if (batchStepIds.Contains(dependency))
                    {
                        issues.Add(new ValidationIssue(
                            "plan.batch.internal_dependency",
                            $"Batch '{batchId}' contains step '{step.StepId}' with a dependency inside the same batch.",
                            step.StepId));
                        break;
                    }
                }
            }
        }
    }
}
