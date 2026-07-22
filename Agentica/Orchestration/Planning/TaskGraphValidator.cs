namespace Agentica.Orchestration.Planning;

public sealed class TaskGraphValidationException : InvalidOperationException
{
    public TaskGraphValidationException(string message)
        : base(message)
    {
    }
}

public static class TaskGraphValidator
{
    public static void Validate(
        TaskGraphPlan plan,
        OrchestrationState? state = null,
        TaskGraphPlan? previousPlan = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (string.IsNullOrWhiteSpace(plan.PlanId))
        {
            throw new TaskGraphValidationException("Task graph plan id is required.");
        }

        if (string.IsNullOrWhiteSpace(plan.Objective))
        {
            throw new TaskGraphValidationException("Task graph objective is required.");
        }

        if (plan.Tasks is null || plan.Tasks.Count == 0)
        {
            throw new TaskGraphValidationException("Task graph must contain at least one task.");
        }

        ValidateRequirements(plan.DefinitionOfDone, "Task graph definition of done");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in plan.Tasks)
        {
            if (task is null)
            {
                throw new TaskGraphValidationException("Task graph cannot contain a null task.");
            }

            if (string.IsNullOrWhiteSpace(task.TaskId))
            {
                throw new TaskGraphValidationException("Task id is required.");
            }

            if (!ids.Add(task.TaskId))
            {
                throw new TaskGraphValidationException($"Duplicate task id '{task.TaskId}'.");
            }

            if (string.IsNullOrWhiteSpace(task.Objective))
            {
                throw new TaskGraphValidationException($"Task '{task.TaskId}' objective is required.");
            }

            if (task.MaxRuns <= 0)
            {
                throw new TaskGraphValidationException($"Task '{task.TaskId}' must allow at least one run.");
            }

            if (task.DependsOn is null)
            {
                throw new TaskGraphValidationException($"Task '{task.TaskId}' dependencies are required.");
            }

            if (task.ContextProjection is null)
            {
                throw new TaskGraphValidationException($"Task '{task.TaskId}' context projection is required.");
            }

            ValidateRequirements(task.AcceptanceRequirements, $"Task '{task.TaskId}' acceptance criteria");

            if (task.DependsOn.Count != task.DependsOn.Distinct(StringComparer.Ordinal).Count())
            {
                throw new TaskGraphValidationException($"Task '{task.TaskId}' contains duplicate dependencies.");
            }
        }

        foreach (var task in plan.Tasks)
        {
            foreach (var dependency in task.DependsOn)
            {
                if (!ids.Contains(dependency))
                {
                    throw new TaskGraphValidationException($"Task '{task.TaskId}' depends on unknown task '{dependency}'.");
                }
            }
        }

        RejectCycles(plan);

        if (state is not null && previousPlan is not null)
        {
            var previousById = previousPlan.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
            var currentById = plan.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
            foreach (var completedTaskId in state.CompletedTaskIds)
            {
                if (!previousById.TryGetValue(completedTaskId, out var previous))
                {
                    continue;
                }

                if (!currentById.TryGetValue(completedTaskId, out var current))
                {
                    throw new TaskGraphValidationException($"Completed task '{completedTaskId}' cannot be removed.");
                }

                if (!TaskEquals(previous, current))
                {
                    throw new TaskGraphValidationException($"Completed task '{completedTaskId}' cannot be rewritten.");
                }
            }
        }
    }

    private static bool TaskEquals(TaskNode left, TaskNode right) =>
        string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) &&
        string.Equals(left.Objective, right.Objective, StringComparison.Ordinal) &&
        left.Optional == right.Optional &&
        left.Priority == right.Priority &&
        left.MaxRuns == right.MaxRuns &&
        left.DependsOn.SequenceEqual(right.DependsOn, StringComparer.Ordinal) &&
        DictionaryEquals(left.ContextProjection, right.ContextProjection) &&
        RequirementsEqual(left.AcceptanceRequirements, right.AcceptanceRequirements);

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right) =>
        StructuralValueEquality.AreEqual(left, right);

    private static bool RequirementsEqual(
        IReadOnlyList<TaskAcceptanceRequirement> left,
        IReadOnlyList<TaskAcceptanceRequirement> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair =>
            pair.First.Kind == pair.Second.Kind &&
            pair.First.RequiredOutcomeStatus == pair.Second.RequiredOutcomeStatus &&
            string.Equals(pair.First.ArtifactKind, pair.Second.ArtifactKind, StringComparison.Ordinal) &&
            string.Equals(pair.First.ToolId, pair.Second.ToolId, StringComparison.Ordinal) &&
            string.Equals(pair.First.HostStateKey, pair.Second.HostStateKey, StringComparison.Ordinal) &&
            StructuralValueEquality.AreEqual(pair.First.HostStateValue, pair.Second.HostStateValue));

    public static void ValidateRequirements(
        IReadOnlyList<TaskAcceptanceRequirement>? requirements,
        string contractName)
    {
        if (requirements is null || requirements.Count == 0)
        {
            throw new TaskGraphValidationException($"{contractName} must contain at least one requirement.");
        }

        for (var index = 0; index < requirements.Count; index++)
        {
            var requirement = requirements[index]
                ?? throw new TaskGraphValidationException($"{contractName} requirement {index + 1} is required.");
            var prefix = $"{contractName} requirement {index + 1}";

            switch (requirement.Kind)
            {
                case TaskAcceptanceRequirementKind.OutcomeStatus:
                    if (!requirement.RequiredOutcomeStatus.HasValue)
                    {
                        throw new TaskGraphValidationException($"{prefix} requires requiredOutcomeStatus.");
                    }

                    RejectUnexpectedFields(requirement, prefix, allowOutcomeStatus: true);
                    break;

                case TaskAcceptanceRequirementKind.Artifact:
                    if (string.IsNullOrWhiteSpace(requirement.ArtifactKind))
                    {
                        throw new TaskGraphValidationException($"{prefix} requires artifactKind.");
                    }

                    RejectUnexpectedFields(requirement, prefix, allowArtifactKind: true);
                    break;

                case TaskAcceptanceRequirementKind.Receipt:
                    if (string.IsNullOrWhiteSpace(requirement.ToolId))
                    {
                        throw new TaskGraphValidationException($"{prefix} requires toolId.");
                    }

                    RejectUnexpectedFields(requirement, prefix, allowToolId: true);
                    break;

                case TaskAcceptanceRequirementKind.HostState:
                    if (string.IsNullOrWhiteSpace(requirement.HostStateKey))
                    {
                        throw new TaskGraphValidationException($"{prefix} requires hostStateKey.");
                    }

                    RejectUnexpectedFields(requirement, prefix, allowHostState: true);
                    break;

                default:
                    throw new TaskGraphValidationException($"{prefix} has unsupported kind '{requirement.Kind}'.");
            }
        }
    }

    private static void RejectUnexpectedFields(
        TaskAcceptanceRequirement requirement,
        string prefix,
        bool allowOutcomeStatus = false,
        bool allowArtifactKind = false,
        bool allowToolId = false,
        bool allowHostState = false)
    {
        if (!allowOutcomeStatus && requirement.RequiredOutcomeStatus.HasValue)
        {
            throw new TaskGraphValidationException($"{prefix} contains requiredOutcomeStatus for kind '{requirement.Kind}'.");
        }

        if (!allowArtifactKind && !string.IsNullOrWhiteSpace(requirement.ArtifactKind))
        {
            throw new TaskGraphValidationException($"{prefix} contains artifactKind for kind '{requirement.Kind}'.");
        }

        if (!allowToolId && !string.IsNullOrWhiteSpace(requirement.ToolId))
        {
            throw new TaskGraphValidationException($"{prefix} contains toolId for kind '{requirement.Kind}'.");
        }

        if (!allowHostState &&
            (!string.IsNullOrWhiteSpace(requirement.HostStateKey) || requirement.HostStateValue is not null))
        {
            throw new TaskGraphValidationException($"{prefix} contains host-state fields for kind '{requirement.Kind}'.");
        }
    }

    private static void RejectCycles(TaskGraphPlan plan)
    {
        var tasks = plan.Tasks.ToDictionary(task => task.TaskId, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        foreach (var task in plan.Tasks)
        {
            Visit(task.TaskId);
        }

        void Visit(string taskId)
        {
            if (visited.Contains(taskId))
            {
                return;
            }

            if (!visiting.Add(taskId))
            {
                throw new TaskGraphValidationException($"Task graph contains a cycle at task '{taskId}'.");
            }

            foreach (var dependency in tasks[taskId].DependsOn)
            {
                Visit(dependency);
            }

            visiting.Remove(taskId);
            visited.Add(taskId);
        }
    }
}
