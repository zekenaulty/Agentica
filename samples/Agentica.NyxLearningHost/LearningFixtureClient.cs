using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Agentica.NyxLearningHost;

/// <summary>
/// A deterministic provider fixture, not a simulated intelligence benchmark. Every choice
/// reads the current real planner prompt and canonical observation; it holds no host object.
/// </summary>
internal sealed class LearningFixtureClient(bool wrongPerspective) : ILlmStreamingClient
{
    public List<string> Prompts { get; } = [];

    public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("This fixture must be consumed through its streaming seam.");

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Prompts.Count >= 5) throw new InvalidOperationException("The fixture provider-call allowance is exhausted.");
        var prompt = request.Messages.Last(message => message.Role == LlmMessageRole.User).Content;
        Prompts.Add(prompt);
        using var frames = JsonDocument.Parse(Section(prompt, "Projected context frames:"));
        var state = frames.RootElement.EnumerateArray()
            .Single(frame => frame.GetProperty("kind").GetString() == "obligation-activation")
            .GetProperty("payload").GetProperty("fulfillment");
        var manifest = state.GetProperty("manifest");
        var subject = manifest.GetProperty("obligation").GetProperty("subjectId").GetString();
        var refinement = request.Metadata?[WorkflowPlanPromptBuilder.RequestKindMetadataKey] ==
            WorkflowPlanPromptBuilder.RefinementRequestKind;
        JsonElement? observation = null;
        if (refinement)
        {
            using var current = JsonDocument.Parse(Section(prompt, "New observation:"));
            observation = current.RootElement.Clone();
        }
        var stage = observation?.GetProperty("data").GetProperty("stage").GetString();
        var toolId = !refinement
            ? subject == "synthetic.primary" ? "evidence.primary" : "attempt.reconcile"
            : stage == "primary.unavailable" ? "evidence.mirror" : "obligation.submit";
        var input = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (toolId == "obligation.submit")
        {
            var segments = manifest.GetProperty("presentedEvidence").EnumerateArray().ToArray();
            var selected = subject == "synthetic.primary"
                ? segments.FirstOrDefault()
                : segments.Single(segment => segment.GetProperty("source").GetProperty("refId").GetString() == "original.accept");
            var selectedPresent = selected.ValueKind == JsonValueKind.Object;
            input["segmentId"] = selectedPresent ? selected.GetProperty("segmentId").GetString() : string.Empty;
            input["answer"] = selectedPresent
                ? subject == "synthetic.primary" ? selected.GetProperty("text").GetString()
                    : state.GetProperty("attempts")[0].GetProperty("effectCustody").GetString() == "Confirmed"
                        ? "original-confirmed" : "original-unknown"
                : "unavailable";
            input["perspectiveId"] = wrongPerspective ? "not-admitted" : manifest.GetProperty("perspectiveId").GetString();
        }

        var call = Prompts.Count;
        var plan = new
        {
            planId = "learning.plan." + call,
            description = "Bounded choice from the current presented state and source result.",
            steps = new[]
            {
                new
                {
                    stepId = "learning.step." + call,
                    toolId,
                    kind = toolId == "obligation.submit" ? "Action" : "Query",
                    effect = toolId == "obligation.submit" ? "WritesLocalState" : "ReadOnly",
                    input,
                    dependsOn = Array.Empty<string>(),
                    batchId = (string?)null,
                    reason = "Use the latest source/result evidence while preserving the original objective.",
                    intent = new
                    {
                        action = "Invoke " + toolId,
                        rationale = "Current source availability or original attempt custody determines this bounded next move.",
                        expectedOutcome = "Exact source evidence, original result reconciliation, or an explicit residual disposition."
                    }
                }
            },
            completionCondition = "The host independently accepts exact evidence and settled original custody."
        };
        string json;
        if (refinement)
        {
            using var execution = JsonDocument.Parse(Section(prompt, "Execution context:"));
            json = JsonSerializer.Serialize(new
            {
                fromPlanId = execution.RootElement.GetProperty("currentPlanId").GetString(),
                reason = "observation",
                evidence = new[] { new
                {
                    kind = "observation",
                    refId = observation!.Value.GetProperty("observationId").GetString()
                }},
                refinedPlan = plan
            });
        }
        else
        {
            json = JsonSerializer.Serialize(plan);
        }

        yield return new(LlmStreamEventKind.TextDelta, json[..(json.Length / 2)]);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new(LlmStreamEventKind.TextDelta, json[(json.Length / 2)..]);
        yield return new(LlmStreamEventKind.Completed,
            Response: new("fixture", request.ModelId, json, json, FinishReason: LlmFinishReason.Stop));
    }

    private static string Section(string prompt, string header)
    {
        // Raw-string prompt templates retain the checkout's line endings. Match
        // whole headers and read the actual compact JSON line on either OS.
        using var reader = new StringReader(prompt);
        while (reader.ReadLine() is { } line)
        {
            if (line != header) continue;
            return reader.ReadLine()?.Trim() ??
                throw new InvalidOperationException("The current planner section has no payload.");
        }
        throw new InvalidOperationException("The current planner prompt lacks a required section: " + header);
    }
}

