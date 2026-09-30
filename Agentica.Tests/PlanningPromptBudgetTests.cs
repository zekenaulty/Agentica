using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Clients.Llm;
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

    [Fact]
    public void Token_window_reserves_capacity_and_trims_multibyte_older_evidence()
    {
        var observations = Enumerable.Range(0, 4)
            .Select(index => new Observation($"observation_{index}", $"step_{index}",
                ObservationKind.ToolResult, $"context_{index}_" + new string('é', 900),
                new Dictionary<string, object?>(), []))
            .Append(new Observation("observation_latest", "step_latest",
                ObservationKind.ToolResult, "Current state is ready.",
                new Dictionary<string, object?>(), []))
            .ToArray();
        var request = new PlanningRequest(new RunRequest("Choose the next action"),
            [], observations, []);
        var options = new LlmPlannerOptions(InvalidJsonRepairAttempts: 0,
            MaxInputCharacters: 20_000);
        var unbounded = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request, options);
        var latestOnly = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(
            request with { Observations = [observations[^1]] }, options);
        var allowance = (int)Utf8ByteTokenProxy.Instance.EstimateTokens(latestOnly) +
            1200;
        Assert.True(Utf8ByteTokenProxy.Instance.EstimateTokens(unbounded) > allowance);
        var budget = new LlmContextWindowBudget(
            WindowTokens: allowance + 4096,
            ReservedOutputTokens: 2048,
            ReservedToolResultTokens: 1024,
            SafetyMarginTokens: 1024);

        var bounded = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request,
            options with { ContextWindowBudget = budget });
        var receipt = Assert.IsType<LlmInputCompilationReceipt>(
            bounded.InputCompilationReceipt);

        Assert.Equal(allowance, receipt.InputAllowanceTokens);
        Assert.True(receipt.EstimatedInputTokens <= allowance);
        Assert.Equal("utf8-request-byte-proxy-v1", receipt.TokenEstimator);
        Assert.Contains(receipt.Decisions, item =>
            item is { RefId: "observation_0", Included: false });
        Assert.Contains(receipt.Decisions, item =>
            item is { RefId: "observation_latest", Included: true });
        Assert.Equal(allowance.ToString(),
            bounded.Metadata?["agentica.planner.inputTokenAllowance"]);
        Assert.True(bounded.Messages.Sum(item => item.Content.Length) < 20_000);
    }

    [Fact]
    public void Mandatory_context_and_repair_fail_when_token_reserves_do_not_fit()
    {
        var request = new PlanningRequest(new RunRequest(
            "Required objective " + new string('é', 3000)), [], [], []);
        var options = new LlmPlannerOptions(InvalidJsonRepairAttempts: 0,
            MaxInputCharacters: 20_000);
        var withoutBudget = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(
            request, options);
        var charCount = withoutBudget.Messages.Sum(item => item.Content.Length);
        var allowance = charCount + 100;
        Assert.True(Utf8ByteTokenProxy.Instance.EstimateTokens(
            withoutBudget) > allowance);
        var budget = new LlmContextWindowBudget(allowance + 4096,
            ReservedOutputTokens: 2048,
            ReservedToolResultTokens: 1024,
            SafetyMarginTokens: 1024);

        Assert.Throws<LlmPlannerException>(() =>
            WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request,
                options with { ContextWindowBudget = budget }));

        var repairOptions = options with
        {
            InvalidJsonRepairAttempts = 1,
            MaxRepairPayloadCharacters = 1000,
            ContextWindowBudget = new LlmContextWindowBudget(
                WindowTokens: (int)Utf8ByteTokenProxy.Instance.EstimateTokens(
                    WorkflowPlanPromptBuilder.BuildInitialPlanRequest(
                        new PlanningRequest(new RunRequest("Plan"), [], [], []),
                        options)) + 4596,
                ReservedOutputTokens: 2048,
                ReservedToolResultTokens: 1024,
                SafetyMarginTokens: 1024)
        };
        var original = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(
            new PlanningRequest(new RunRequest("Plan"), [], [], []), repairOptions);
        Assert.Throws<LlmPlannerException>(() =>
            WorkflowPlanPromptBuilder.BuildInitialPlanRepairRequest(original,
                new string('x', 2000), new string('y', 2000), 1,
                repairOptions));
    }

    [Fact]
    public void Host_token_estimator_can_tighten_the_same_declared_window()
    {
        var request = new PlanningRequest(new RunRequest("Choose a bounded action"),
            [], [], []);
        var options = new LlmPlannerOptions(InvalidJsonRepairAttempts: 0);
        var candidate = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request, options);
        var baseline = Utf8ByteTokenProxy.Instance.EstimateTokens(candidate);
        var allowance = checked((int)baseline + 100);
        var budget = new LlmContextWindowBudget(allowance + 4096, 2048,
            ReservedToolResultTokens: 1024, SafetyMarginTokens: 1024);

        Assert.NotNull(WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request,
            options with { ContextWindowBudget = budget }));
        Assert.Throws<LlmPlannerException>(() =>
            WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request,
                options with
                {
                    ContextWindowBudget = budget,
                    InputTokenEstimator = new DoubledTokenEstimator()
                }));
    }

    private sealed class DoubledTokenEstimator : ILlmInputTokenEstimator
    {
        public string Name => "fixture-double-v1";

        public long EstimateTokens(LlmRequest request) =>
            Utf8ByteTokenProxy.Instance.EstimateTokens(request) * 2;
    }
}
