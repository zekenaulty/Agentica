using System.Text.Json;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;

namespace Agentica.Clients.Orchestration;

public sealed record TaskGraphPlanJsonContract(
    string? PlanId,
    string? Objective,
    IReadOnlyList<TaskNodeJsonContract>? Tasks,
    IReadOnlyList<TaskAcceptanceRequirementJsonContract>? DefinitionOfDone)
{
    public TaskGraphPlan ToTaskGraphPlan()
    {
        if (string.IsNullOrWhiteSpace(PlanId))
        {
            throw new LlmTaskPlannerException("Task graph payload is missing planId.");
        }

        if (string.IsNullOrWhiteSpace(Objective))
        {
            throw new LlmTaskPlannerException("Task graph payload is missing objective.");
        }

        if (Tasks is null || Tasks.Count == 0)
        {
            throw new LlmTaskPlannerException("Task graph payload did not include any tasks.");
        }

        return new TaskGraphPlan(
            PlanId,
            Objective,
            Tasks.Select(task => task.ToTaskNode()).ToArray(),
            DefinitionOfDone?.Select(requirement => requirement.ToRequirement()).ToArray() ?? [],
            DateTimeOffset.UtcNow);
    }
}

public sealed record TaskNodeJsonContract(
    string? TaskId,
    string? Objective,
    IReadOnlyList<string>? DependsOn,
    bool? Optional,
    int? Priority,
    int? MaxRuns,
    IReadOnlyDictionary<string, JsonElement>? ContextProjection,
    IReadOnlyList<TaskAcceptanceRequirementJsonContract>? AcceptanceRequirements)
{
    public TaskNode ToTaskNode()
    {
        if (string.IsNullOrWhiteSpace(TaskId))
        {
            throw new LlmTaskPlannerException("Task payload is missing taskId.");
        }

        if (string.IsNullOrWhiteSpace(Objective))
        {
            throw new LlmTaskPlannerException($"Task '{TaskId}' is missing objective.");
        }

        return new TaskNode(
            TaskId,
            Objective,
            DependsOn?
                .Where(dependency => !string.IsNullOrWhiteSpace(dependency))
                .Select(dependency => dependency.Trim())
                .ToArray() ?? [],
            Optional ?? false,
            Priority ?? 1,
            MaxRuns ?? 1,
            ContextProjection?.ToDictionary(
                pair => pair.Key,
                pair => Agentica.Clients.Planning.JsonValueConverter.Convert(pair.Value),
                StringComparer.Ordinal) ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            AcceptanceRequirements?.Select(requirement => requirement.ToRequirement()).ToArray() ??
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Succeeded)
            ]);
    }
}

public sealed record TaskAcceptanceRequirementJsonContract(
    string? Kind,
    string? RequiredOutcomeStatus,
    string? ArtifactKind,
    string? ToolId,
    string? HostStateKey,
    JsonElement? HostStateValue)
{
    public TaskAcceptanceRequirement ToRequirement()
    {
        if (!TryResolveKind(out var kind))
        {
            throw new LlmTaskPlannerException($"Task acceptance requirement has invalid kind '{Kind}'.");
        }

        RunOutcomeStatus? status = null;
        if (!string.IsNullOrWhiteSpace(RequiredOutcomeStatus))
        {
            if (!Enum.TryParse<RunOutcomeStatus>(RequiredOutcomeStatus, ignoreCase: true, out var parsed))
            {
                throw new LlmTaskPlannerException($"Task acceptance requirement has invalid outcome status '{RequiredOutcomeStatus}'.");
            }

            status = parsed;
        }

        return new TaskAcceptanceRequirement(
            kind,
            status,
            ArtifactKind,
            ToolId,
            HostStateKey,
            HostStateValue.HasValue
                ? Agentica.Clients.Planning.JsonValueConverter.Convert(HostStateValue.Value)
                : null);
    }

    private bool TryResolveKind(out TaskAcceptanceRequirementKind kind)
    {
        if (Enum.TryParse(Kind, ignoreCase: true, out kind))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ArtifactKind))
        {
            kind = TaskAcceptanceRequirementKind.Artifact;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(ToolId))
        {
            kind = TaskAcceptanceRequirementKind.Receipt;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(HostStateKey))
        {
            kind = TaskAcceptanceRequirementKind.HostState;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(RequiredOutcomeStatus))
        {
            kind = TaskAcceptanceRequirementKind.OutcomeStatus;
            return true;
        }

        if (Kind is not null &&
            (Kind.Equals("ObjectiveVerifier", StringComparison.OrdinalIgnoreCase) ||
             Kind.Equals("ObjectiveVerification", StringComparison.OrdinalIgnoreCase) ||
             Kind.Equals("Verifier", StringComparison.OrdinalIgnoreCase)))
        {
            kind = TaskAcceptanceRequirementKind.OutcomeStatus;
            return true;
        }

        return false;
    }
}
