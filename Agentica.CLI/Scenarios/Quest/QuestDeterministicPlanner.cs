using Agentica.Observations;
using Agentica.Planning;
using Agentica.Tools;

namespace Agentica.CLI.Scenarios.Quest;

public enum QuestDeterministicPlannerMode
{
    ObserveThenSolve,
    TryLockedGateFirst
}

public sealed class QuestDeterministicPlanner : IWorkflowPlanner
{
    private readonly QuestDeterministicPlannerMode _mode;

    public QuestDeterministicPlanner(
        QuestDeterministicPlannerMode mode = QuestDeterministicPlannerMode.ObserveThenSolve)
    {
        _mode = mode;
    }

    public Task<WorkflowPlan> CreatePlanAsync(
        PlanningRequest request,
        CancellationToken cancellationToken = default)
    {
        var step = _mode == QuestDeterministicPlannerMode.TryLockedGateFirst
            ? Step(1, QuestToolIds.Move, ToolKind.Action, ToolEffect.WritesLocalState, ("direction", "north"))
            : Step(1, QuestToolIds.GetState, ToolKind.Query, ToolEffect.ReadOnly);

        return Task.FromResult(Plan(1, step, "Initial quest plan slice."));
    }

    public Task<WorkflowPlan> RefinePlanAsync(
        PlanningRequest request,
        Observation observation,
        CancellationToken cancellationToken = default)
    {
        var stepNumber = request.Receipts.Count + 1;
        var step = NextStep(stepNumber, observation);
        return Task.FromResult(Plan(stepNumber, step, "Refined quest plan slice."));
    }

    private static PlanStep NextStep(int stepNumber, Observation observation)
    {
        var action = ReadString(observation, "action");
        var blocker = ReadString(observation, "blocker");
        var location = ReadString(observation, "location") ?? "foyer";
        var inventory = ReadStringArray(observation, "inventory");
        var openedLocks = ReadStringArray(observation, "openedLocks");

        if (string.Equals(blocker, "locked_exit", StringComparison.Ordinal))
        {
            return Step(stepNumber, QuestToolIds.Move, ToolKind.Action, ToolEffect.WritesLocalState, ("direction", "east"));
        }

        if (string.Equals(action, "get_state", StringComparison.Ordinal))
        {
            return Step(stepNumber, QuestToolIds.ListLegalActions, ToolKind.Query, ToolEffect.ReadOnly);
        }

        if (string.Equals(action, "list_legal_actions", StringComparison.Ordinal))
        {
            return Step(stepNumber, QuestToolIds.Move, ToolKind.Action, ToolEffect.WritesLocalState, ("direction", "east"));
        }

        if (string.Equals(location, "study", StringComparison.Ordinal) &&
            !inventory.Contains("sun_key", StringComparer.Ordinal))
        {
            return Step(stepNumber, QuestToolIds.Take, ToolKind.Action, ToolEffect.WritesLocalState, ("item", "sun_key"));
        }

        if (string.Equals(location, "study", StringComparison.Ordinal))
        {
            return Step(stepNumber, QuestToolIds.Move, ToolKind.Action, ToolEffect.WritesLocalState, ("direction", "west"));
        }

        if (string.Equals(location, "foyer", StringComparison.Ordinal) &&
            inventory.Contains("sun_key", StringComparer.Ordinal) &&
            !openedLocks.Contains("sun_gate", StringComparer.Ordinal))
        {
            return Step(
                stepNumber,
                QuestToolIds.Use,
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                ("item", "sun_key"),
                ("target", "sun_gate"));
        }

        if (string.Equals(location, "foyer", StringComparison.Ordinal) &&
            openedLocks.Contains("sun_gate", StringComparer.Ordinal))
        {
            return Step(stepNumber, QuestToolIds.Move, ToolKind.Action, ToolEffect.WritesLocalState, ("direction", "north"));
        }

        if (string.Equals(location, "hall", StringComparison.Ordinal))
        {
            return Step(stepNumber, QuestToolIds.CompleteObjective, ToolKind.Action, ToolEffect.WritesLocalState);
        }

        return Step(stepNumber, QuestToolIds.Move, ToolKind.Action, ToolEffect.WritesLocalState, ("direction", "east"));
    }

    private static WorkflowPlan Plan(int version, PlanStep step, string description) =>
        new(
            PlanId: $"quest_plan_{version:000}",
            Version: version,
            Steps: [step],
            Description: description);

    private static PlanStep Step(
        int number,
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        params (string Key, object? Value)[] input) =>
        new(
            StepId: $"quest_step_{number:000}",
            ToolId: toolId,
            Kind: kind,
            Effect: effect,
            Input: input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private static string? ReadString(Observation observation, string key) =>
        observation.Data.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static IReadOnlyList<string> ReadStringArray(Observation observation, string key)
    {
        if (!observation.Data.TryGetValue(key, out var value) || value is null)
        {
            return [];
        }

        if (value is IEnumerable<string> strings)
        {
            return strings.ToArray();
        }

        return [];
    }
}
