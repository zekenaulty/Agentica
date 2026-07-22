using Agentica.Lab.Scenarios.MazeQuest;
using Agentica.Lab.Scenarios.WorkbenchQuest;

namespace Agentica.Lab.Benchmarks;

internal sealed record ProductProofOracleResult(
    bool Success,
    string Evidence,
    string? Failure);

internal static class ProductProofBenchmarkOracles
{
    public static ProductProofOracleResult Evaluate(WorkbenchQuestSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var state = session.State;
        var firstPatch = state.PatchHistory.FirstOrDefault(item => item.Applied);
        var lastPatch = state.PatchHistory.LastOrDefault(item => item.Applied);
        var failedCheckBeforeMutation = firstPatch is not null &&
            state.CheckHistory.Any(item => !item.Passed && item.ActionOrder < firstPatch.ActionOrder);
        var relevantFileRead = session.Scenario.RelevantPaths.Any(state.ReadPaths.Contains);
        var latestCheck = state.CheckHistory.LastOrDefault();
        var passingCheckAfterMutation = lastPatch is not null &&
            latestCheck is { Passed: true } &&
            latestCheck.ActionOrder > lastPatch.ActionOrder;
        var authoritativeCurrentCheck = session.EvaluateCurrentState();

        var failures = new List<string>();
        if (!state.Completed)
        {
            failures.Add("host_completion_flag_missing");
        }

        if (!failedCheckBeforeMutation)
        {
            failures.Add("failed_check_before_mutation_missing");
        }

        if (!relevantFileRead)
        {
            failures.Add("relevant_read_missing");
        }

        if (firstPatch is null)
        {
            failures.Add("applied_patch_missing");
        }

        if (!passingCheckAfterMutation)
        {
            failures.Add("passing_check_after_mutation_missing");
        }

        if (!authoritativeCurrentCheck.Passed)
        {
            failures.Add("authoritative_current_check_failed");
        }

        return new ProductProofOracleResult(
            Success: failures.Count == 0,
            Evidence: $"scenario={session.Scenario.Descriptor.ScenarioId};completed={state.Completed};failedBeforeMutation={failedCheckBeforeMutation};relevantRead={relevantFileRead};appliedPatches={state.PatchHistory.Count(item => item.Applied)};passingAfterMutation={passingCheckAfterMutation};currentCheckPassed={authoritativeCurrentCheck.Passed};currentCheckOutput={Compact(authoritativeCurrentCheck.Output)}",
            Failure: failures.Count == 0 ? null : string.Join(',', failures));
    }

    public static ProductProofOracleResult Evaluate(MazeQuestSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var requiredObjectives = session.Stage.Quest.Objectives
            .Where(objective => objective.Required && objective.Kind != MazeObjectiveKind.Complete)
            .Select(objective => objective.ObjectiveId)
            .ToArray();
        var incomplete = requiredObjectives
            .Where(objectiveId => !session.State.CompletedObjectives.Contains(objectiveId))
            .ToArray();
        var terminalReceiptBackedState = session.State.ObjectiveCompleted &&
            session.State.CompletedObjectives.Contains("complete");

        var failures = new List<string>();
        if (incomplete.Length > 0)
        {
            failures.Add("required_objectives_incomplete");
        }

        if (!terminalReceiptBackedState)
        {
            failures.Add("host_completion_flag_missing");
        }

        return new ProductProofOracleResult(
            Success: failures.Count == 0,
            Evidence: $"scenario={session.Stage.Quest.QuestId};seed={session.Stage.Seed};completed={session.State.ObjectiveCompleted};requiredCompleted={requiredObjectives.Length - incomplete.Length}/{requiredObjectives.Length};terminalState={terminalReceiptBackedState}",
            Failure: failures.Count == 0 ? null : string.Join(',', failures));
    }

    private static string Compact(string value) =>
        string.Join(' ', value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
