using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.Planning;
using Agentica.Planning;

namespace Agentica.Lab.Web.Runtime;

/// <summary>A labeled scripted provider for the browser-owned inventory sample, not a model benchmark.</summary>
public static class DemoPlanner
{
    public static IWorkflowPlanner Create(Action<LlmStreamEvent> observer) =>
        new LlmWorkflowPlanner(new DemoClient(), new LlmPlannerOptions(ModelId: "scripted-inventory", InvalidJsonRepairAttempts: 0), observer);

    private sealed class DemoClient : ILlmStreamingClient
    {
        private int _calls;

        public async Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            await foreach (var item in StreamAsync(request, cancellationToken).ConfigureAwait(false))
                if (item.Response is not null) return item.Response;
            throw new InvalidOperationException("Scripted stream did not complete.");
        }

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = ++_calls;
            yield return new LlmStreamEvent(LlmStreamEventKind.Activity, "scripted_fixture");
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            var plan = new
            {
                planId = "demo-plan-" + call,
                description = "Scripted inventory demonstration; no language model was called.",
                steps = new[] { new
                {
                    stepId = "demo-step-" + call,
                    toolId = call == 1 ? "demo.inspect" : "demo.accept",
                    kind = call == 1 ? "query" : "action",
                    effect = call == 1 ? "readOnly" : "writesLocalState",
                    input = call == 1 ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["itemId"] = "sample-1" },
                    reason = call == 1 ? "Inspect the host-owned sample." : "Request acceptance; require the host result."
                } }
            };
            // The runtime's refinement prompt expects its existing refinement envelope.
            var refinement = request.Messages.Any(m => m.Content.Contains("\"refinedPlan\"", StringComparison.Ordinal));
            var json = refinement ? JsonSerializer.Serialize(new { reason = "observation", refinedPlan = plan }) : JsonSerializer.Serialize(plan);
            for (var i = 0; i < json.Length; i += 96)
            {
                yield return new LlmStreamEvent(LlmStreamEventKind.TextDelta, json.Substring(i, Math.Min(96, json.Length - i)));
                await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            }
            yield return new LlmStreamEvent(LlmStreamEventKind.Completed,
                Response: new LlmResponse("demo", request.ModelId, json, json, FinishReason: LlmFinishReason.Stop));
        }
    }
}
