namespace Agentica.Orchestration.Planning;

public static class TaskGraphMutationApplier
{
    public static TaskGraphPlan Apply(TaskGraphPlan plan, TaskGraphRefinement refinement)
    {
        if (refinement.Mutations.Count == 0)
        {
            throw new TaskGraphValidationException("Task graph refinement must contain at least one mutation.");
        }

        // All changes are made against private copies. If any mutation fails, the caller's
        // previously validated plan remains unchanged.
        var tasks = plan.Tasks.ToList();
        var definitionOfDone = plan.DefinitionOfDone.ToList();

        foreach (var mutation in refinement.Mutations)
        {
            if (mutation is null)
            {
                throw new TaskGraphValidationException("Task graph refinement cannot contain a null mutation.");
            }

            switch (mutation.Kind)
            {
                case TaskGraphMutationKind.AddTask:
                    RejectUnexpectedPayload(mutation, allowTask: true);
                    var addedTask = RequiredTask(mutation);
                    RequireMatchingTaskId(mutation, addedTask);
                    if (FindIndex(tasks, mutation.TaskId) >= 0)
                    {
                        throw new TaskGraphValidationException($"Cannot add duplicate task '{mutation.TaskId}'.");
                    }

                    tasks.Add(addedTask);
                    break;

                case TaskGraphMutationKind.ReplaceTask:
                    RejectUnexpectedPayload(mutation, allowTask: true);
                    var replacement = RequiredTask(mutation);
                    RequireMatchingTaskId(mutation, replacement);
                    var replacementIndex = RequiredTaskIndex(tasks, mutation.TaskId, "replace");
                    tasks[replacementIndex] = replacement;
                    break;

                case TaskGraphMutationKind.RemoveTask:
                    RejectUnexpectedPayload(mutation);
                    var removedIndex = RequiredTaskIndex(tasks, mutation.TaskId, "remove");
                    tasks.RemoveAt(removedIndex);
                    tasks = tasks
                        .Select(task => task with
                        {
                            DependsOn = task.DependsOn
                                .Where(dependency => !string.Equals(dependency, mutation.TaskId, StringComparison.Ordinal))
                                .ToArray()
                        })
                        .ToList();
                    break;

                case TaskGraphMutationKind.AddDependency:
                    RejectUnexpectedPayload(mutation, allowDependency: true);
                    var addTargetIndex = RequiredTaskIndex(tasks, mutation.TaskId, "add a dependency to");
                    var addedDependency = RequiredDependency(mutation);
                    RequiredTaskIndex(tasks, addedDependency, "use as a dependency");
                    if (tasks[addTargetIndex].DependsOn.Contains(addedDependency, StringComparer.Ordinal))
                    {
                        throw new TaskGraphValidationException(
                            $"Task '{mutation.TaskId}' already depends on '{addedDependency}'.");
                    }

                    tasks[addTargetIndex] = tasks[addTargetIndex] with
                    {
                        DependsOn = tasks[addTargetIndex].DependsOn.Append(addedDependency).ToArray()
                    };
                    break;

                case TaskGraphMutationKind.RemoveDependency:
                    RejectUnexpectedPayload(mutation, allowDependency: true);
                    var removeTargetIndex = RequiredTaskIndex(tasks, mutation.TaskId, "remove a dependency from");
                    var removedDependency = RequiredDependency(mutation);
                    if (!tasks[removeTargetIndex].DependsOn.Contains(removedDependency, StringComparer.Ordinal))
                    {
                        throw new TaskGraphValidationException(
                            $"Task '{mutation.TaskId}' does not depend on '{removedDependency}'.");
                    }

                    tasks[removeTargetIndex] = tasks[removeTargetIndex] with
                    {
                        DependsOn = tasks[removeTargetIndex].DependsOn
                            .Where(dependency => !string.Equals(dependency, removedDependency, StringComparison.Ordinal))
                            .ToArray()
                    };
                    break;

                case TaskGraphMutationKind.ReorderPriority:
                    RejectUnexpectedPayload(mutation, allowPriority: true);
                    var priorityIndex = RequiredTaskIndex(tasks, mutation.TaskId, "reorder");
                    var priority = mutation.Priority
                        ?? throw new TaskGraphValidationException("ReorderPriority mutation requires priority.");
                    if (tasks[priorityIndex].Priority == priority)
                    {
                        throw new TaskGraphValidationException(
                            $"ReorderPriority mutation does not change task '{mutation.TaskId}'.");
                    }

                    tasks[priorityIndex] = tasks[priorityIndex] with { Priority = priority };
                    break;

                case TaskGraphMutationKind.ReviseAcceptanceCriteria:
                    RejectUnexpectedPayload(mutation, allowAcceptance: true);
                    var acceptanceIndex = RequiredTaskIndex(tasks, mutation.TaskId, "revise acceptance criteria for");
                    TaskGraphValidator.ValidateRequirements(
                        mutation.AcceptanceRequirements,
                        $"Task '{mutation.TaskId}' revised acceptance criteria");
                    if (tasks[acceptanceIndex].AcceptanceRequirements.SequenceEqual(mutation.AcceptanceRequirements!))
                    {
                        throw new TaskGraphValidationException(
                            $"ReviseAcceptanceCriteria mutation does not change task '{mutation.TaskId}'.");
                    }

                    tasks[acceptanceIndex] = tasks[acceptanceIndex] with
                    {
                        AcceptanceRequirements = mutation.AcceptanceRequirements!.ToArray()
                    };
                    break;

                case TaskGraphMutationKind.ReviseDefinitionOfDone:
                    RejectUnexpectedPayload(mutation, allowDefinitionOfDone: true);
                    TaskGraphValidator.ValidateRequirements(
                        mutation.DefinitionOfDone,
                        "Revised task graph definition of done");
                    if (definitionOfDone.SequenceEqual(mutation.DefinitionOfDone!))
                    {
                        throw new TaskGraphValidationException(
                            "ReviseDefinitionOfDone mutation does not change the definition of done.");
                    }

                    definitionOfDone = mutation.DefinitionOfDone!.ToList();
                    break;

                default:
                    throw new TaskGraphValidationException(
                        $"Task graph mutation kind '{mutation.Kind}' is not supported.");
            }
        }

        var candidate = plan with
        {
            Tasks = tasks,
            DefinitionOfDone = definitionOfDone
        };
        TaskGraphValidator.Validate(candidate);
        return candidate;
    }

    private static TaskNode RequiredTask(TaskGraphMutation mutation) =>
        mutation.Task ?? throw new TaskGraphValidationException($"{mutation.Kind} mutation requires a task.");

    private static void RejectUnexpectedPayload(
        TaskGraphMutation mutation,
        bool allowTask = false,
        bool allowDependency = false,
        bool allowPriority = false,
        bool allowAcceptance = false,
        bool allowDefinitionOfDone = false)
    {
        if (!allowTask && mutation.Task is not null)
        {
            throw new TaskGraphValidationException($"{mutation.Kind} mutation contains an unexpected task payload.");
        }

        if (!allowDependency && !string.IsNullOrWhiteSpace(mutation.DependencyTaskId))
        {
            throw new TaskGraphValidationException($"{mutation.Kind} mutation contains unexpected dependencyTaskId.");
        }

        if (!allowPriority && mutation.Priority.HasValue)
        {
            throw new TaskGraphValidationException($"{mutation.Kind} mutation contains unexpected priority.");
        }

        if (!allowAcceptance && mutation.AcceptanceRequirements is not null)
        {
            throw new TaskGraphValidationException(
                $"{mutation.Kind} mutation contains unexpected acceptanceRequirements.");
        }

        if (!allowDefinitionOfDone && mutation.DefinitionOfDone is not null)
        {
            throw new TaskGraphValidationException($"{mutation.Kind} mutation contains unexpected definitionOfDone.");
        }
    }

    private static void RequireMatchingTaskId(TaskGraphMutation mutation, TaskNode task)
    {
        if (string.IsNullOrWhiteSpace(mutation.TaskId))
        {
            throw new TaskGraphValidationException($"{mutation.Kind} mutation requires taskId.");
        }

        if (!string.Equals(mutation.TaskId, task.TaskId, StringComparison.Ordinal))
        {
            throw new TaskGraphValidationException(
                $"{mutation.Kind} mutation taskId '{mutation.TaskId}' does not match payload taskId '{task.TaskId}'.");
        }
    }

    private static int RequiredTaskIndex(List<TaskNode> tasks, string taskId, string operation)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new TaskGraphValidationException($"Cannot {operation} a task without taskId.");
        }

        var index = FindIndex(tasks, taskId);
        return index >= 0
            ? index
            : throw new TaskGraphValidationException($"Cannot {operation} unknown task '{taskId}'.");
    }

    private static int FindIndex(List<TaskNode> tasks, string taskId) =>
        tasks.FindIndex(task => string.Equals(task.TaskId, taskId, StringComparison.Ordinal));

    private static string RequiredDependency(TaskGraphMutation mutation) =>
        string.IsNullOrWhiteSpace(mutation.DependencyTaskId)
            ? throw new TaskGraphValidationException($"{mutation.Kind} mutation requires dependencyTaskId.")
            : mutation.DependencyTaskId;
}
