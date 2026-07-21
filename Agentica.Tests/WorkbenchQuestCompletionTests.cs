extern alias AgenticaLab;

using Agentica.Artifacts;
using Agentica.Tools;
using WorkbenchQuestBoard = AgenticaLab::Agentica.Lab.Scenarios.WorkbenchQuest.WorkbenchQuestBoard;
using WorkbenchQuestSession = AgenticaLab::Agentica.Lab.Scenarios.WorkbenchQuest.WorkbenchQuestSession;
using WorkbenchQuestToolIds = AgenticaLab::Agentica.Lab.Scenarios.WorkbenchQuest.WorkbenchQuestToolIds;

namespace Agentica.Tests;

public sealed class WorkbenchQuestCompletionTests
{
    [Fact]
    public void Workbench_completion_refuses_mutation_after_the_last_passing_check()
    {
        var session = new WorkbenchQuestSession(new WorkbenchQuestBoard().Load("broken_check"));

        var baseline = session.Execute(Invocation(WorkbenchQuestToolIds.RunCheck));
        var read = session.Execute(Invocation(
            WorkbenchQuestToolIds.ReadFile,
            ("path", "src/Calculator.txt")));
        var repair = session.Execute(Invocation(
            WorkbenchQuestToolIds.ApplyPatch,
            ("path", "src/Calculator.txt"),
            ("find", "return left - right"),
            ("replace", "return left + right"),
            ("rationale", "Repair the failing Add implementation.")));
        var passingCheck = session.Execute(Invocation(WorkbenchQuestToolIds.RunCheck));
        var corruption = session.Execute(Invocation(
            WorkbenchQuestToolIds.ApplyPatch,
            ("path", "src/Calculator.txt"),
            ("find", "return left + right"),
            ("replace", "return left - right"),
            ("rationale", "Regress the implementation after verification.")));

        Assert.Equal("failed", baseline.Receipt.Data["status"]);
        Assert.Equal(ReceiptStatus.Succeeded, read.Receipt.Status);
        Assert.Equal(ReceiptStatus.Succeeded, repair.Receipt.Status);
        Assert.Equal("passed", passingCheck.Receipt.Data["status"]);
        Assert.Equal(ReceiptStatus.Succeeded, corruption.Receipt.Status);

        var checkCount = session.State.CheckHistory.Count;
        var nextActionOrder = session.State.NextActionOrder;
        var currentStateCheck = session.EvaluateCurrentState();

        Assert.False(currentStateCheck.Passed);
        Assert.Equal(checkCount, session.State.CheckHistory.Count);
        Assert.Equal(nextActionOrder, session.State.NextActionOrder);

        var completion = session.Execute(Invocation(WorkbenchQuestToolIds.Complete));

        Assert.Equal(ReceiptStatus.Refused, completion.Receipt.Status);
        Assert.Null(completion.Artifact);
        Assert.False(session.State.Completed);
        var blockers = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            completion.Receipt.Data["completionBlockers"]);
        Assert.Contains(blockers, blocker => blocker.Contains("latest check", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(blockers, blocker => blocker.Contains("current workbench state", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("failed", completion.Receipt.Data["currentCheckStatus"]);
    }

    [Fact]
    public void Workbench_completion_accepts_a_passing_latest_check_for_current_state()
    {
        var session = new WorkbenchQuestSession(new WorkbenchQuestBoard().Load("broken_check"));

        session.Execute(Invocation(WorkbenchQuestToolIds.RunCheck));
        session.Execute(Invocation(
            WorkbenchQuestToolIds.ReadFile,
            ("path", "src/Calculator.txt")));
        session.Execute(Invocation(
            WorkbenchQuestToolIds.ApplyPatch,
            ("path", "src/Calculator.txt"),
            ("find", "return left - right"),
            ("replace", "return left + right"),
            ("rationale", "Repair the failing Add implementation.")));
        var passingCheck = session.Execute(Invocation(WorkbenchQuestToolIds.RunCheck));

        Assert.Equal("passed", passingCheck.Receipt.Data["status"]);
        Assert.True(session.EvaluateCurrentState().Passed);

        var completion = session.Execute(Invocation(WorkbenchQuestToolIds.Complete));

        Assert.Equal(ReceiptStatus.Succeeded, completion.Receipt.Status);
        Assert.Equal("workbench.objective_completed", completion.Artifact?.Kind);
        Assert.True(session.State.Completed);
    }

    private static ToolInvocation Invocation(
        string toolId,
        params (string Key, object? Value)[] input) =>
        new(
            "run_test",
            $"step_{Guid.NewGuid():N}",
            toolId,
            input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
}
