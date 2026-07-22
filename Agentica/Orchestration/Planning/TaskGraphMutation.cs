namespace Agentica.Orchestration.Planning;

public enum TaskGraphMutationKind
{
    AddTask,
    ReplaceTask,
    RemoveTask,
    AddDependency,
    RemoveDependency,
    ReorderPriority,
    ReviseAcceptanceCriteria,
    ReviseDefinitionOfDone
}

public sealed record TaskGraphMutation(
    TaskGraphMutationKind Kind,
    string TaskId,
    TaskNode? Task = null,
    string? DependencyTaskId = null,
    int? Priority = null,
    IReadOnlyList<TaskAcceptanceRequirement>? AcceptanceRequirements = null,
    IReadOnlyList<TaskAcceptanceRequirement>? DefinitionOfDone = null);
