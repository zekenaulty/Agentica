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
        if (string.IsNullOrWhiteSpace(plan.PlanId))
        {
            throw new TaskGraphValidationException("Task graph plan id is required.");
        }

        if (string.IsNullOrWhiteSpace(plan.Objective))
        {
            throw new TaskGraphValidationException("Task graph objective is required.");
        }

        if (plan.Tasks.Count == 0)
        {
            throw new TaskGraphValidationException("Task graph must contain at least one task.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in plan.Tasks)
        {
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

                if (!Equals(previous, current))
                {
                    throw new TaskGraphValidationException($"Completed task '{completedTaskId}' cannot be rewritten.");
                }
            }
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
