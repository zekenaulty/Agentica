using Agentica.Orchestration.Planning;

namespace Agentica.Clients.Orchestration;

public sealed record TaskGraphRefinementJsonContract(
    string? Reason,
    IReadOnlyList<TaskGraphMutationJsonContract>? Mutations,
    IReadOnlyList<string>? Blockers,
    bool? RequiresUserInput)
{
    public TaskGraphRefinement ToRefinement() =>
        new(
            string.IsNullOrWhiteSpace(Reason) ? "observation" : Reason,
            Mutations?.Select(mutation => mutation.ToMutation()).ToArray() ?? [],
            Blockers?
                .Where(blocker => !string.IsNullOrWhiteSpace(blocker))
                .Select(blocker => blocker.Trim())
                .ToArray() ?? [],
            RequiresUserInput ?? false);
}

public sealed record TaskGraphMutationJsonContract(
    string? Kind,
    string? TaskId,
    TaskNodeJsonContract? Task,
    string? DependencyTaskId,
    int? Priority,
    IReadOnlyList<TaskAcceptanceRequirementJsonContract>? AcceptanceRequirements,
    IReadOnlyList<TaskAcceptanceRequirementJsonContract>? DefinitionOfDone)
{
    public TaskGraphMutation ToMutation()
    {
        if (!Enum.TryParse<TaskGraphMutationKind>(Kind, ignoreCase: true, out var kind))
        {
            throw new LlmTaskPlannerException($"Task graph mutation has invalid kind '{Kind}'.");
        }

        if (string.IsNullOrWhiteSpace(TaskId))
        {
            throw new LlmTaskPlannerException($"Task graph mutation '{Kind}' is missing taskId.");
        }

        return new TaskGraphMutation(
            kind,
            TaskId,
            Task?.ToTaskNode(),
            DependencyTaskId,
            Priority,
            AcceptanceRequirements?.Select(requirement => requirement.ToRequirement()).ToArray(),
            DefinitionOfDone?.Select(requirement => requirement.ToRequirement()).ToArray());
    }
}
