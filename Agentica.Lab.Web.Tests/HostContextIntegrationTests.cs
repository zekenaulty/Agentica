using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.FixtureHost;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Outcomes;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

public sealed class HostContextIntegrationTests
{
    [Fact]
    public async Task Compacted_run_preserves_discovery_for_a_later_streamed_run_and_exact_evidence_read()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentica-context-integration", Guid.NewGuid().ToString("N"));
        using var handler = new ContextPlannerHandler();
        using var http = new HttpClient(handler);
        using var factory = new LoopbackFixturePlannerFactory(new Uri("http://127.0.0.1:5081/v1/responses"), http);
        var original = new HostObservation("discovered-passage", 0, DateTimeOffset.UtcNow,
            HostProtocol.Element(new { sourceDetail = "exact-original-passage-evidence", padding = new string('x', 54_000) }),
            [new KnowledgeFact("passage.a", "A passage to room B was observed.", HostProtocol.Element(new { destination = "room-b" }))]);
        var request = Request(original, contextBudget: 81_920);
        try
        {
            var firstContext = new HostContextStore(root).Open(request);
            var originalHash = firstContext.ReadEvidence(original.ObservationId).Reference!.ContentHash;
            using (var first = new HostRun(request, firstContext, factory, new ActionCustodyStore(Path.Combine(root, "custody"))))
            {
                first.Attach(new CompletionHost(first));
                first.Start();
                await first.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(RunOutcomeStatus.Succeeded, Outcome(first).Outcome.Status);
            }
            Assert.Single(handler.Prompts);
            Assert.Contains("compact; current observation data and fact values omitted", handler.Prompts[0], StringComparison.Ordinal);
            Assert.DoesNotContain("exact-original-passage-evidence", handler.Prompts[0], StringComparison.Ordinal);

            // A new store and run restore the same scope from disk with a fresh observation.
            var current = new HostObservation("current-second-run", 1, DateTimeOffset.UtcNow,
                HostProtocol.Element(new { sourceDetail = "fresh-current-view" }));
            var next = request with { Observation = current, Provider = new ProviderSettings("fixture") };
            var restored = new HostContextStore(root).Open(next);
            Assert.Equal(originalHash, restored.ReadEvidence(original.ObservationId).Reference!.ContentHash);
            var fact = Assert.Single(restored.Snapshot().Facts, item => item.IsCurrent);
            Assert.Equal("host", fact.Source);
            Assert.Equal("observed", fact.State);
            Assert.Equal(original.ObservationId, Assert.Single(fact.Evidence).ObservationId);
            using (var second = new HostRun(next, restored, factory, new ActionCustodyStore(Path.Combine(root, "custody"))))
            {
                second.Attach(new CompletionHost(second));
                second.Start();
                await second.Completion.WaitAsync(TimeSpan.FromSeconds(15));
                var outcome = Outcome(second);
                Assert.True(outcome.Outcome.Status == RunOutcomeStatus.Succeeded,
                    HostProtocol.Element(new
                    {
                        outcome.Outcome,
                        outcome.Details.ValidationIssues,
                        prompts = handler.Prompts.Count,
                        events = outcome.Details.Events.TakeLast(3)
                    }).GetRawText());
                Assert.Contains(outcome.Receipts.Items, receipt => receipt.ToolId == "lab.evidence.read" && receipt.Status == ReceiptStatus.Succeeded);
                Assert.NotEmpty(outcome.Outcome.CompletionEvidence);
            }
            Assert.Equal(3, handler.Prompts.Count);
            Assert.Contains("passage.a", handler.Prompts[1], StringComparison.Ordinal);
            Assert.Contains(original.ObservationId, handler.Prompts[1], StringComparison.Ordinal);
            Assert.Contains("exact-original-passage-evidence", handler.Prompts[2], StringComparison.Ordinal);
            Assert.Equal(originalHash, restored.ReadEvidence(original.ObservationId).Reference!.ContentHash);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static OutcomeEnvelope Outcome(HostRun run) =>
        HostProtocol.Element(run.Snapshot()).GetProperty("outcome").Deserialize<OutcomeEnvelope>(HostProtocol.Json)!;

    private static HostRunRequest Request(HostObservation observation, int contextBudget) => new(
        1, "context-integration", "session", "epoch", "scope", "observer", "stable-objective", "Verify the current scope through host evidence.",
        observation,
        [new HostCapability("host.finish", "Verify scope", "Read and verify current host completion evidence.", ToolKind.Query, ToolEffect.ReadOnly, ToolInputSchema.Create())],
        new ProviderSettings("fixture", ContextWindowTokens: contextBudget));

    private sealed class CompletionHost(HostRun run) : IHostConnection
    {
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public void TrySendProgress(ServiceMessage message) { }
        public Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default)
        {
            if (message.Type != "action.request") return Task.CompletedTask;
            var action = (HostActionRequest)message.Payload;
            Assert.Equal("host.finish", action.CapabilityId);
            run.AcceptResult(new HostActionResult(action.ActionId, action.SessionId, action.SessionEpoch, "applied",
                action.ExpectedRevision, action.ExpectedRevision, "evidence-" + action.ActionId, "Host verified current scope.",
                new HostObservation("result-" + action.ActionId, action.ExpectedRevision, DateTimeOffset.UtcNow, HostProtocol.Element(new { verified = true })),
                new HostCompletion(run.Request.ObjectiveId, "completion-" + action.ActionId, "Objective verified by the host.")));
            return Task.CompletedTask;
        }
    }

    private sealed class ContextPlannerHandler : HttpMessageHandler
    {
        public List<string> Prompts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var prompt = body.RootElement.GetProperty("input").EnumerateArray()
                .Last(item => item.TryGetProperty("role", out var role) && role.GetString() == "user").GetProperty("content").GetString()!;
            Prompts.Add(prompt);
            var read = Prompts.Count == 2;
            var plan = new
            {
                planId = "context-plan-" + Prompts.Count,
                steps = new[] { new
                {
                    stepId = "context-step-" + Prompts.Count, toolId = read ? "lab.evidence.read" : "host.finish",
                    kind = "query", effect = "readOnly",
                    input = read ? new Dictionary<string, object?> { ["observationId"] = "discovered-passage" } : new Dictionary<string, object?>()
                } }
            };
            var text = Prompts.Count == 3 ? JsonSerializer.Serialize(new { reason = "observation", refinedPlan = plan }) : JsonSerializer.Serialize(plan);
            var delta = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = text });
            var complete = JsonSerializer.Serialize(new
            {
                type = "response.completed",
                response = new
                {
                    id = "context-response-" + Prompts.Count,
                    status = "completed",
                    output = new[] { new
                    {
                        type = "message", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_text", text } }
                    } }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("event: response.output_text.delta\ndata: " + delta +
                    "\n\nevent: response.completed\ndata: " + complete + "\n\n", Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
