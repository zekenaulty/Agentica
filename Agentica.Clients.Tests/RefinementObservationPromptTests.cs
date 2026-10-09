using System.Text;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Tests;

public sealed class RefinementObservationPromptTests
{
    [Fact]
    public void Large_trigger_fits_default_budget_once_with_older_evidence_and_complete_accounting()
    {
        var payload = "new-evidence-marker-" + new string('x', 60_000);
        var newest = Observation("new-observation", payload);
        var older = Observation("old-observation", "older-evidence-marker");
        var request = Request(older, newest);
        var options = new LlmPlannerOptions(InvalidJsonRepairAttempts: 1)
        {
            ContextWindowBudget = new LlmContextWindowBudget(131072, 4096)
        };
        var result = WorkflowPlanPromptBuilder.BuildRefinementRequest(request, newest, options);
        var prompt = result.Messages.Single(item => item.Role == LlmMessageRole.User).Content;
        Assert.Equal(1, Count(prompt, payload));
        Assert.Contains("older-evidence-marker", prompt, StringComparison.Ordinal);
        Assert.Equal("workflow-plan-refinement-prompt-v3", result.Metadata![WorkflowPlanPromptBuilder.PromptVersionMetadataKey]);
        var receipt = Assert.IsType<LlmInputCompilationReceipt>(result.InputCompilationReceipt);
        Assert.True(receipt.EstimatedInputTokens <= receipt.InputAllowanceTokens);
        // The former duplicate alone would put this exact request above its token allowance.
        Assert.True(receipt.EstimatedInputTokens + Encoding.UTF8.GetByteCount(payload) > receipt.InputAllowanceTokens);
        Assert.Contains(receipt.Decisions, item => item.Kind == "observation" && item.RefId == newest.ObservationId &&
            item.Included && item.Representation == "trigger");
        Assert.Contains(receipt.Decisions, item => item.Kind == "observation" && item.RefId == older.ObservationId && item.Included);
        Assert.Equal("0", result.Metadata["agentica.planner.omittedObservations"]);
        Assert.Equal(result.Messages.Sum(item => item.Content.Length), receipt.InputCharacters);
        Assert.Equal(2, request.Observations.Count);
    }

    [Fact]
    public void Canonically_equal_trigger_payload_with_different_dictionary_order_is_deduplicated()
    {
        var trigger = Observation("new-observation", "payload") with
        {
            Data = new Dictionary<string, object?> { ["first"] = 1, ["second"] = new[] { "a", "b" } }
        };
        var existing = trigger with
        {
            Data = new Dictionary<string, object?> { ["second"] = new[] { "a", "b" }, ["first"] = 1 }
        };
        var result = WorkflowPlanPromptBuilder.BuildRefinementRequest(Request(existing), trigger, new LlmPlannerOptions());
        var prompt = result.Messages[^1].Content;
        Assert.Equal(1, Count(prompt, "\"second\""));
        Assert.Single(result.InputCompilationReceipt!.Decisions, item => item.RefId == trigger.ObservationId && item.Included);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("summary")]
    [InlineData("step")]
    [InlineData("evidence")]
    public void Conflicting_trigger_identity_fails_before_any_payload_is_hidden(string conflict)
    {
        var trigger = Observation("same-id", "authoritative-new-data");
        var existing = conflict switch
        {
            "data" => trigger with { Data = new Dictionary<string, object?> { ["payload"] = "conflicting-private-data" } },
            "summary" => trigger with { Summary = "Different summary" },
            "step" => trigger with { StepId = "different-step" },
            "evidence" => trigger with { Evidence = [new EvidenceRef("receipt", "different-receipt")] },
            _ => throw new InvalidOperationException()
        };
        var error = Assert.Throws<LlmPlannerException>(() =>
            WorkflowPlanPromptBuilder.BuildRefinementRequest(Request(existing), trigger, new LlmPlannerOptions()));
        Assert.Contains("identity conflicts", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("conflicting-private-data", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Trigger_is_accounted_when_not_already_in_observation_history()
    {
        var trigger = Observation("new-observation", "new-evidence-marker");
        var older = Observation("older", "older-evidence-marker");
        var result = WorkflowPlanPromptBuilder.BuildRefinementRequest(Request(older), trigger, new LlmPlannerOptions());
        Assert.Equal(1, Count(result.Messages[^1].Content, "new-evidence-marker"));
        Assert.Equal(2, result.InputCompilationReceipt!.Decisions.Count);
        Assert.All(result.InputCompilationReceipt.Decisions, item => Assert.True(item.Included));
    }

    [Fact]
    public void Trimming_history_preserves_honest_trigger_and_old_evidence_decisions()
    {
        var trigger = Observation("trigger", "trigger-marker");
        var oversized = Observation("omitted-old", new string('x', 60_000));
        var retained = Observation("retained-old", "retained-old-marker");
        // Trigger deliberately appears first: dropping this history entry must not mark
        // mandatory New observation content omitted from the resulting prompt.
        var result = WorkflowPlanPromptBuilder.BuildRefinementRequest(Request(trigger, oversized, retained), trigger,
            new LlmPlannerOptions(InvalidJsonRepairAttempts: 0, MaxInputCharacters: 32768));
        var receipt = result.InputCompilationReceipt!;
        Assert.Equal(3, receipt.Decisions.Count);
        Assert.Contains(receipt.Decisions, item => item.RefId == trigger.ObservationId && item.Included && item.Representation == "trigger");
        Assert.Contains(receipt.Decisions, item => item.RefId == oversized.ObservationId && !item.Included);
        Assert.Contains(receipt.Decisions, item => item.RefId == retained.ObservationId && item.Included);
        Assert.Equal("1", result.Metadata!["agentica.planner.omittedObservations"]);
        Assert.Equal(1, Count(result.Messages[^1].Content, "trigger-marker"));
        Assert.Contains("retained-old-marker", result.Messages[^1].Content, StringComparison.Ordinal);
    }

    private static Observation Observation(string id, string payload) =>
        new(id, "step", ObservationKind.ToolResult, "Observed evidence.",
            new Dictionary<string, object?> { ["payload"] = payload }, [new EvidenceRef("receipt", "receipt-" + id)]);

    private static PlanningRequest Request(params Observation[] observations) =>
        new(new RunRequest("Choose the next step using the retrieved evidence."), [], observations, []);

    private static int Count(string text, string value) => text.Split(value, StringSplitOptions.None).Length - 1;
}
