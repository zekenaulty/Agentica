namespace Agentica.Orchestration.Planning;

public static class TaskGraphMutationApplier
{
    public static TaskGraphPlan Apply(TaskGraphPlan plan, TaskGraphRefinement refinement)
    {
        var tasks = plan.Tasks.ToList();
        var definitionOfDone = plan.DefinitionOfDone.ToList();

        foreach (var mutation in refinement.Mutations)
        {
            switch (mutation.Kind)
            {
                case TaskGraphMutationKind.AddTask:
                    if (mutation.Task is null)
                    {
                        throw new TaskGraphValidationException("AddTask mutation requires a task.");
                    }

                    tasks.Add(mutation.Task);
                    break;

                case TaskGraphMutationKind.ReplaceTask:
                    if (mutation.Task is null)
                    {
                        throw new TaskGraphValidationException("ReplaceTask mutation requires a task.");
                    }

                    Replace(tasks, mutation.TaskId, mutation.Task);
                    break;

                case TaskGraphMutationKind.RemoveTask:
                    tasks.RemoveAll(task => string.Equals(task.TaskId, mutation.TaskId, StringComparison.Ordinal));
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
                    tasks = tasks.Select(task => string.Equals(task.TaskId, mutation.TaskId, StringComparison.Ordinal)
                            ? task with
                            {
                                DependsOn = task.DependsOn
                                    .Concat([RequiredDependency(mutation)])
                                    .Distinct(StringComparer.Ordinal)
                                    .ToArray()
                            }
                            : task)
                        .ToList();
                    break;

                case TaskGraphMutationKind.RemoveDependency:
                    tasks = tasks.Select(task => string.Equals(task.TaskId, mutation.TaskId, StringComparison.Ordinal)
                            ? task with
                            {
                                DependsOn = task.DependsOn
                                    .Where(dependency => !string.Equals(dependency, RequiredDependency(mutation), StringComparison.Ordinal))
                                    .ToArray()
                            }
                            : task)
                        .ToList();
                    break;

                case TaskGraphMutationKind.ReorderPriority:
                    tasks = tasks.Select(task => string.Equals(task.TaskId, mutation.TaskId, StringComparison.Ordinal)
                            ? task with { Priority = mutation.Priority ?? task.Priority }
                            : task)
                        .ToList();
                    break;

                case TaskGraphMutationKind.ReviseAcceptanceCriteria:
                    tasks = tasks.Select(task => string.Equals(task.TaskId, mutation.TaskId, StringComparison.Ordinal)
                            ? task with { AcceptanceRequirements = mutation.AcceptanceRequirements ?? task.AcceptanceRequirements }
                            : task)
                        .ToList();
                    break;

                case TaskGraphMutationKind.ReviseDefinitionOfDone:
                    definitionOfDone = (mutation.DefinitionOfDone ?? definitionOfDone).ToList();
                    break;

                case TaskGraphMutationKind.MarkTaskBlocked:
                case TaskGraphMutationKind.MarkTaskAccepted:
                    break;
            }
        }

        return plan with
        {
            Tasks = tasks,
            DefinitionOfDone = definitionOfDone
        };
    }

    private static void Replace(List<TaskNode> tasks, string taskId, TaskNode replacement)
    {
        var index = tasks.FindIndex(task => string.Equals(task.TaskId, taskId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new TaskGraphValidationException($"Cannot replace unknown task '{taskId}'.");
        }

        tasks[index] = replacement;
    }

    private static string RequiredDependency(TaskGraphMutation mutation) =>
        string.IsNullOrWhiteSpace(mutation.DependencyTaskId)
            ? throw new TaskGraphValidationException($"{mutation.Kind} mutation requires a dependency task id.")
            : mutation.DependencyTaskId;
}
