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
        if (string.IsNullOrWhiteSpace(Kind) ||
            !Enum.TryParse<TaskGraphMutationKind>(Kind, ignoreCase: true, out var kind) ||
            !Enum.IsDefined(kind) ||
            !string.Equals(kind.ToString(), Kind, StringComparison.OrdinalIgnoreCase))
        {
            throw new LlmTaskPlannerException($"Task graph mutation has invalid kind '{Kind}'.");
        }

        if (string.IsNullOrWhiteSpace(TaskId))
        {
            throw new LlmTaskPlannerException($"Task graph mutation '{Kind}' is missing taskId.");
        }

        var mutation = new TaskGraphMutation(
            kind,
            TaskId,
            Task?.ToTaskNode(),
            DependencyTaskId,
            Priority,
            AcceptanceRequirements?.Select(requirement => requirement.ToRequirement()).ToArray(),
            DefinitionOfDone?.Select(requirement => requirement.ToRequirement()).ToArray());

        ValidatePayload(mutation);
        return mutation;
    }

    private static void ValidatePayload(TaskGraphMutation mutation)
    {
        try
        {
            switch (mutation.Kind)
            {
                case TaskGraphMutationKind.AddTask:
                case TaskGraphMutationKind.ReplaceTask:
                    RejectUnexpectedPayload(mutation, allowTask: true);
                    if (mutation.Task is null)
                    {
                        throw new TaskGraphValidationException($"{mutation.Kind} mutation requires task.");
                    }

                    if (!string.Equals(mutation.TaskId, mutation.Task.TaskId, StringComparison.Ordinal))
                    {
                        throw new TaskGraphValidationException(
                            $"{mutation.Kind} mutation taskId must match the payload taskId.");
                    }

                    break;

                case TaskGraphMutationKind.RemoveTask:
                    RejectUnexpectedPayload(mutation);
                    break;

                case TaskGraphMutationKind.AddDependency:
                case TaskGraphMutationKind.RemoveDependency:
                    RejectUnexpectedPayload(mutation, allowDependency: true);
                    if (string.IsNullOrWhiteSpace(mutation.DependencyTaskId))
                    {
                        throw new TaskGraphValidationException(
                            $"{mutation.Kind} mutation requires dependencyTaskId.");
                    }

                    break;

                case TaskGraphMutationKind.ReorderPriority:
                    RejectUnexpectedPayload(mutation, allowPriority: true);
                    if (!mutation.Priority.HasValue)
                    {
                        throw new TaskGraphValidationException("ReorderPriority mutation requires priority.");
                    }

                    break;

                case TaskGraphMutationKind.ReviseAcceptanceCriteria:
                    RejectUnexpectedPayload(mutation, allowAcceptance: true);
                    TaskGraphValidator.ValidateRequirements(
                        mutation.AcceptanceRequirements,
                        $"Task '{mutation.TaskId}' revised acceptance criteria");
                    break;

                case TaskGraphMutationKind.ReviseDefinitionOfDone:
                    RejectUnexpectedPayload(mutation, allowDefinitionOfDone: true);
                    TaskGraphValidator.ValidateRequirements(
                        mutation.DefinitionOfDone,
                        "Revised task graph definition of done");
                    break;
            }
        }
        catch (TaskGraphValidationException exception)
        {
            throw new LlmTaskPlannerException(exception.Message, exception);
        }
    }

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
}
