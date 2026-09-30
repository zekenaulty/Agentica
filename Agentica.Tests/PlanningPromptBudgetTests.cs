using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Clients.Planning;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Tests;

public sealed class PlanningPromptBudgetTests
{
    [Fact]
    public void Compiler_omits_older_evidence_and_receipts_every_decision()
    {
        var observations = Enumerable.Range(0, 5)
            .Select(index => new Observation($"observation_{index}", $"step_{index}",
                ObservationKind.ToolResult, $"evidence_{index}_" + new string('x', 2400),
                new Dictionary<string, object?>(), []))
            .ToArray();
        var receipts = Enumerable.Range(0, 3)
            .Select(index => new Receipt($"receipt_{index}", $"step_{index}", "query",
                ReceiptStatus.Succeeded, $"result_{index}_" + new string('y', 1400),
                DateTimeOffset.UnixEpoch, new Dictionary<string, object?>()))
            .ToArray();
        var request = new PlanningRequest(new RunRequest("Choose the next bounded action"),
            [], observations, receipts);
        var options = new LlmPlannerOptions(InvalidJsonRepairAttempts: 0,
            MaxInputCharacters: 12_000);

        var first = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request, options);
        var second = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request, options);
        var receipt = Assert.IsType<Agentica.Clients.Llm.LlmInputCompilationReceipt>(
            first.InputCompilationReceipt);
        var prompt = first.Messages[1].Content;

        Assert.True(first.Messages.Sum(message => message.Content.Length) <= 12_000);
        Assert.Equal(first.Messages.Sum(message => message.Content.Length), receipt.InputCharacters);
        Assert.Equal(8, receipt.Decisions.Count);
        Assert.Contains(receipt.Decisions, decision => !decision.Included);
        Assert.Contains(receipt.Decisions, decision => decision is
            { Kind: "observation", RefId: "observation_4", Included: true });
        Assert.Contains(receipt.Decisions, decision => decision is
            { Kind: "receipt", RefId: "receipt_2", Included: true });
        Assert.Contains("evidence_4_", prompt);
        Assert.DoesNotContain("evidence_0_", prompt);
        Assert.Equal(receipt.InputSha256, second.InputCompilationReceipt?.InputSha256);
        Assert.Equal(receipt.Decisions, second.InputCompilationReceipt?.Decisions);
        Assert.Equal(receipt.InputSha256,
            first.Metadata?["agentica.planner.inputSha256"]);
        Assert.DoesNotContain("InputCompilationReceipt", JsonSerializer.Serialize(first));
    }

    [Fact]
    public void Compiler_fails_closed_when_mandatory_context_exceeds_ceiling()
    {
        var request = new PlanningRequest(
            new RunRequest("Required objective " + new string('z', 20_000)),
            [], [], []);

        var error = Assert.Throws<LlmPlannerException>(() =>
            WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request,
                new LlmPlannerOptions(InvalidJsonRepairAttempts: 0,
                    MaxInputCharacters: 8192)));

        Assert.Contains("Mandatory planning context", error.Message);
    }

    [Fact]
    public void Repair_retains_ceiling_or_fails_before_dispatch()
    {
        var request = new PlanningRequest(new RunRequest("Use a bounded tool"),
            [], [], []);
        var options = new LlmPlannerOptions(InvalidJsonRepairAttempts: 1,
            MaxRepairPayloadCharacters: 1000, MaxInputCharacters: 20_000);
        var initial = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request, options);

        var repaired = WorkflowPlanPromptBuilder.BuildInitialPlanRepairRequest(initial,
            new string('n', 40_000), new string('e', 40_000), 1, options);

        Assert.True(repaired.Messages.Sum(message => message.Content.Length) <= 20_000);
        Assert.Throws<LlmPlannerException>(() =>
            WorkflowPlanPromptBuilder.BuildInitialPlanRepairRequest(initial,
                new string('n', 40_000), new string('e', 40_000), 1,
                options with { MaxInputCharacters = 8192 }));
    }
}
