using Agentica.Outcomes;

namespace Agentica.Orchestration.Planning;

public sealed record TaskGraphPlan(
    string PlanId,
    string Objective,
    IReadOnlyList<TaskNode> Tasks,
    IReadOnlyList<TaskAcceptanceRequirement> DefinitionOfDone,
    DateTimeOffset CreatedAt);

public sealed record TaskNode(
    string TaskId,
    string Objective,
    IReadOnlyList<string> DependsOn,
    bool Optional,
    int Priority,
    int MaxRuns,
    IReadOnlyDictionary<string, object?> ContextProjection,
    IReadOnlyList<TaskAcceptanceRequirement> AcceptanceRequirements);

public sealed record TaskAcceptanceRequirement(
    TaskAcceptanceRequirementKind Kind,
    RunOutcomeStatus? RequiredOutcomeStatus = null,
    string? ArtifactKind = null,
    string? ToolId = null,
    string? HostStateKey = null,
    object? HostStateValue = null);

public enum TaskAcceptanceRequirementKind
{
    OutcomeStatus,
    Artifact,
    Receipt,
    HostState
}

public static class TaskGraph
{
    public static IReadOnlyList<TaskNode> AvailableTasks(
        TaskGraphPlan plan,
        OrchestrationState state)
    {
        var completed = state.CompletedTaskIds.ToHashSet(StringComparer.Ordinal);
        var blocked = state.BlockedTaskIds.ToHashSet(StringComparer.Ordinal);

        return plan.Tasks
            .Where(task =>
                !completed.Contains(task.TaskId) &&
                !blocked.Contains(task.TaskId) &&
                task.DependsOn.All(completed.Contains))
            .Select((task, index) => new { Task = task, Index = index })
            .OrderBy(item => item.Task.Priority)
            .ThenBy(item => item.Index)
            .Select(item => item.Task)
            .ToArray();
    }

    public static bool RequiredTasksComplete(
        TaskGraphPlan plan,
        OrchestrationState state)
    {
        var completed = state.CompletedTaskIds.ToHashSet(StringComparer.Ordinal);
        return plan.Tasks
            .Where(task => !task.Optional)
            .All(task => completed.Contains(task.TaskId));
    }
}
