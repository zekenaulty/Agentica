using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Artifacts;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class StructuredPlanningContextTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Long_run_preserves_structured_evidence_and_execution_history_within_default_budget(bool refinement)
    {
        var at = DateTimeOffset.Parse("2026-10-09T12:00:00Z");
        var completed = Enumerable.Range(1, 96).Select(index => new CompletedStepContext(
            $"step_{index:D3}", "host.apply", $"plan_{index:D32}", index,
            $"receipt_{index:D32}", "Succeeded", $"observation_{index:D32}", null)).ToArray();
        var observation = new Observation("exact-source-result", "step_096", ObservationKind.ToolResult,
            "Exact retained public evidence.", new Dictionary<string, object?>
            {
                ["result"] = new
                {
                    status = "available",
                    reference = new { observationId = "host-observation-096", contentHash = new string('a', 64) },
                    observation = new
                    {
                        observationId = "host-observation-096",
                        revision = 96,
                        data = new
                        {
                            entities = Enumerable.Range(0, 128).Select(index => new
                            {
                                entityId = $"entity_{index:D3}",
                                location = new { coordinates = new { x = index, y = index % 11 }, scope = "scope-alpha" },
                                state = new { status = "observed", properties = new { available = true, observedAt = at } }
                            }).ToArray()
                        }
                    }
                }
            }, [new EvidenceRef("receipt", "receipt_096")]);
        var receipt = new Receipt("receipt_096", "step_096", "host.read", ReceiptStatus.Succeeded,
            "Original source retrieved.", at, new Dictionary<string, object?> { ["observationId"] = observation.ObservationId });
        var frame = new PlanningFrame("frame_096", "host.context", "1", at,
            new Dictionary<string, object?> { ["scopeId"] = "scope-alpha", ["sourceObservationId"] = "host-observation-096" },
            [new EvidenceRef("observation", observation.ObservationId)]);
        var request = new PlanningRequest(new RunRequest("Choose a bounded action from the exact public evidence."),
            [new ToolDescriptor("host.apply", "Apply bounded action", ToolKind.Action, ToolEffect.WritesLocalState)],
            [observation], [receipt])
        {
            ExecutionContext = new(completed.Select(item => item.StepId).ToArray(), completed, "plan_096", 96),
            ContextFrames = [frame]
        };
        var options = new LlmPlannerOptions(InvalidJsonRepairAttempts: 1)
        {
            ContextWindowBudget = new LlmContextWindowBudget(131072, 4096)
        };

        var result = refinement
            ? WorkflowPlanPromptBuilder.BuildRefinementRequest(request, observation, options)
            : WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request, options);
        var prompt = result.Messages.Single(item => item.Role == LlmMessageRole.User).Content;
        var compilation = Assert.IsType<LlmInputCompilationReceipt>(result.InputCompilationReceipt);
        Assert.True(compilation.EstimatedInputTokens <= compilation.InputAllowanceTokens);
        Assert.True(compilation.InputCharacters <= compilation.MaxInputCharacters);
        Assert.All(compilation.Decisions, decision => Assert.True(decision.Included));
        Assert.Contains(compilation.Decisions, decision => decision.Kind == "frame" && decision.Representation == "full");
        Assert.Equal(refinement ? "workflow-plan-refinement-prompt-v3" : "workflow-plan-initial-prompt-v2",
            result.Metadata![WorkflowPlanPromptBuilder.PromptVersionMetadataKey]);

        AssertSectionEquals(request.ExecutionContext, prompt, "Execution context", "Tool catalog");
        AssertSectionEquals(request.ToolDescriptors, prompt, "Tool catalog", refinement ? "New observation" : "Existing observations");
        AssertSectionEquals(request.ContextFrames, prompt, "Projected context frames", "Current planning constraints");
        AssertSectionEquals(request.Receipts, prompt, "Existing receipts", "Required JSON shape");
        if (refinement)
        {
            AssertSectionEquals(observation, prompt, "New observation", "Existing observations");
            AssertSectionEquals(Array.Empty<Observation>(), prompt, "Existing observations", "Existing receipts");
        }
        else AssertSectionEquals(request.Observations, prompt, "Existing observations", "Existing receipts");

        // Recreate the former representation of these same mandatory sections.
        // The fixture must fail that budget while the complete compact data fits.
        var prettyPrompt = prompt;
        var headings = new List<string>
        {
            "Request context", "Projected context frames", "Current planning constraints", "Execution context", "Tool catalog"
        };
        if (refinement) headings.Add("New observation");
        headings.AddRange(["Existing observations", "Existing receipts", "Required JSON shape"]);
        var prettyOptions = new JsonSerializerOptions(JsonOptions) { WriteIndented = true };
        for (var index = 0; index < headings.Count - 1; index++)
        {
            var section = Section(prompt, headings[index], headings[index + 1]);
            using var parsed = JsonDocument.Parse(section);
            prettyPrompt = prettyPrompt.Replace(section, JsonSerializer.Serialize(parsed.RootElement, prettyOptions), StringComparison.Ordinal);
        }
        var prettyRequest = result with
        {
            Messages = result.Messages.Select(message => message.Role == LlmMessageRole.User
                ? message with { Content = prettyPrompt } : message).ToArray()
        };
        Assert.True(options.InputTokenEstimator.EstimateTokens(prettyRequest) > compilation.InputAllowanceTokens);
    }

    private static void AssertSectionEquals<T>(T expected, string prompt, string heading, string nextHeading)
    {
        using var actual = JsonDocument.Parse(Section(prompt, heading, nextHeading));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected, JsonOptions), actual.RootElement), heading);
    }

    private static string Section(string prompt, string heading, string nextHeading)
    {
        var start = prompt.IndexOf(heading + ":", StringComparison.Ordinal) + heading.Length + 1;
        var end = prompt.IndexOf(nextHeading + ":", start, StringComparison.Ordinal);
        Assert.True(start > heading.Length && end > start);
        return prompt[start..end].Trim();
    }
}
