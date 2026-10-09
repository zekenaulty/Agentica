using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Context;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.FixtureHost;
using Agentica.Lab.Web.Providers;
using Agentica.Lab.Web.Runtime;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Lab.Web.Tests;

public sealed class LoopbackFixtureHostTests
{
    [Theory]
    [InlineData("https://127.0.0.1:5081/v1/responses")]
    [InlineData("https://api.openai.com/v1/responses")]
    [InlineData("http://example.com/v1/responses")]
    [InlineData("http://192.168.1.1/v1/responses")]
    [InlineData("http://127.0.0.1:5081/v1/responses?token=fixture")]
    [InlineData("http://127.0.0.1:5081/v1/responses#fragment")]
    [InlineData("/v1/responses")]
    public void Fixture_endpoint_rejects_external_or_ambiguous_routes(string endpoint) =>
        Assert.Throws<ArgumentException>(() => LoopbackFixturePlannerFactory.ValidateEndpoint(new Uri(endpoint, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void Fixture_endpoint_rejects_embedded_credentials()
    {
        var endpoint = new UriBuilder("http://127.0.0.1:5081/v1/responses")
        {
            UserName = "fixture-user",
            Password = "fixture-password"
        };
        Assert.Throws<ArgumentException>(() => LoopbackFixturePlannerFactory.ValidateEndpoint(endpoint.Uri));
    }

    [Fact]
    public void Explicit_endpoint_is_required_once_and_never_inferred_from_environment()
    {
        Assert.Throws<ArgumentException>(() => FixtureHostOptions.ParseEndpoint([]));
        Assert.Throws<ArgumentException>(() => FixtureHostOptions.ParseEndpoint(["--fixture-endpoint"]));
        Assert.Throws<ArgumentException>(() => FixtureHostOptions.ParseEndpoint([
            "--fixture-endpoint", "http://localhost:5081/v1/responses", "--fixture-endpoint=http://127.0.0.1:5082/v1/responses"]));
        Assert.Equal("http://127.0.0.1:5081/v1/responses", FixtureHostOptions.ParseEndpoint([
            "--fixture-endpoint", "http://127.0.0.1:5081/v1/responses", "--urls", "http://127.0.0.1:5079"]).AbsoluteUri);
    }

    [Theory]
    [InlineData("openai", null)]
    [InlineData("gemini", null)]
    [InlineData("fixture", "a-production-model")]
    public void Fixture_factory_rejects_provider_or_model_fallback(string provider, string? model)
    {
        using var factory = new LoopbackFixturePlannerFactory(new Uri("http://127.0.0.1:5081/v1/responses"));
        Assert.Throws<ArgumentException>(() => factory.Create(new ProviderSettings(provider, model), _ => { }));
        Assert.Equal("fixture", Assert.Single(factory.GetProviders()).Provider);
    }

    [Fact]
    public async Task Fixture_uses_fixed_endpoint_model_credential_and_actual_streaming_adapter()
    {
        using var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        using var factory = new LoopbackFixturePlannerFactory(new Uri("http://127.0.0.1:5081/v1/responses"), http);
        var events = new List<LlmStreamEvent>();
        var planner = factory.Create(new ProviderSettings("fixture"), events.Add);
        var plan = await planner.CreatePlanAsync(new PlanningRequest(new RunRequest("Inspect the fixture."), [], [], []));
        Assert.Equal("fixture-plan", plan.PlanId);
        Assert.Equal("http://127.0.0.1:5081/v1/responses", handler.Endpoint?.AbsoluteUri);
        Assert.Equal(LoopbackFixturePlannerFactory.FixtureCredential, handler.Credential);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(LoopbackFixturePlannerFactory.FixtureModel, body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Contains(events, item => item.Kind == LlmStreamEventKind.TextDelta);
        Assert.Equal(LlmStreamEventKind.Completed, events[^1].Kind);
    }

    [Fact]
    public async Task Default_budget_runs_real_streamed_planner_session_with_full_host_tool_surface()
    {
        var storage = Path.Combine(Path.GetTempPath(), "agentica-default-budget-tests", Guid.NewGuid().ToString("N"));
        using var handler = new FixtureHandler(hostLoop: true);
        using var http = new HttpClient(handler);
        using var factory = new LoopbackFixturePlannerFactory(new Uri("http://127.0.0.1:5081/v1/responses"), http);
        var request = new HostRunRequest(1, "budget-host", "budget-session", "budget-epoch", "inventory", "operator",
            "budget-objective", "Inspect the scoped inventory, accept one eligible item, and verify the resulting host state.",
            InventoryObservation("initial-inventory", 0),
            [
                new HostCapability("demo.inspect", "Inspect", "Observe the scoped inventory.", ToolKind.Query, ToolEffect.ReadOnly, ToolInputSchema.Create()),
                new HostCapability("demo.accept", "Accept", "Accept an eligible pending record.", ToolKind.Action, ToolEffect.WritesLocalState,
                    ToolInputSchema.Create(new ToolInputField("itemId", Required: true)))
            ], new ProviderSettings("fixture")); // Deliberately use DEFAULT context and output budgets.
        using var run = new HostRun(request, new HostContextStore(storage).Open(request), factory,
            new ActionCustodyStore(Path.Combine(storage, "custody")));
        var host = new InventoryHostConnection(run);
        try
        {
            run.Attach(host);
            run.Start();
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(15));
            var snapshot = HostProtocol.Element(run.Snapshot());
            var outcome = snapshot.GetProperty("outcome").Deserialize<OutcomeEnvelope>(HostProtocol.Json)!;
            Assert.Equal(RunOutcomeStatus.Succeeded, outcome.Outcome.Status);
            Assert.Equal(["demo.inspect", "demo.accept"], host.Actions.Select(action => action.CapabilityId));
            Assert.Equal(2, handler.Bodies.Count);
            foreach (var localTool in new[] { "lab.evidence.read", "lab.hypothesis.record", "lab.knowledge.query" })
                Assert.Contains(localTool, handler.Bodies[0], StringComparison.Ordinal);
            Assert.Contains("initial-inventory", handler.Bodies[0], StringComparison.Ordinal);
            Assert.Contains(host.Progress, message => message.Type == "progress");
            Assert.Equal(2, outcome.Receipts.Items.Count);
            Assert.NotEmpty(outcome.Outcome.CompletionEvidence);
            Assert.False(run.HasUnresolvedActions);
        }
        finally
        {
            run.Cancel();
            await run.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
        }
    }

    private static HostObservation InventoryObservation(string id, long revision) => new(id, revision, DateTimeOffset.UtcNow,
        HostProtocol.Element(new { items = new[] { new { id = "sample-1", eligible = true, status = revision == 0 ? "pending" : "accepted" } } }));

    private sealed class InventoryHostConnection(HostRun run) : IHostConnection
    {
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public List<HostActionRequest> Actions { get; } = [];
        public ConcurrentQueue<ServiceMessage> Progress { get; } = new();
        public void TrySendProgress(ServiceMessage message) => Progress.Enqueue(message);
        public Task SendAsync(ServiceMessage message, CancellationToken cancellationToken = default)
        {
            if (message.Type != "action.request") return Task.CompletedTask;
            var action = (HostActionRequest)message.Payload;
            Actions.Add(action);
            var accepted = action.CapabilityId == "demo.accept";
            run.AcceptResult(new HostActionResult(action.ActionId, action.SessionId, action.SessionEpoch, "applied",
                action.ExpectedRevision, accepted ? 1 : 0, "receipt-" + action.ActionId, accepted ? "Item accepted." : "Inventory observed.",
                InventoryObservation("observed-" + action.ActionId, accepted ? 1 : 0),
                accepted ? new HostCompletion(run.Request.ObjectiveId, "complete-" + action.ActionId, "Host verified one eligible item was accepted.") : null));
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureHandler(bool hostLoop = false) : HttpMessageHandler
    {
        public Uri? Endpoint { get; private set; }
        public string? Credential { get; private set; }
        public string? Body { get; private set; }
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri;
            Credential = request.Headers.Authorization?.Parameter;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(Body);
            var plan = """{"planId":"fixture-plan","steps":[{"stepId":"inspect","toolId":"host.inspect","kind":"query","effect":"readOnly","input":{}}]}""";
            if (hostLoop)
            {
                using var sent = JsonDocument.Parse(Body);
                var latest = sent.RootElement.GetProperty("input").EnumerateArray()
                    .Last(item => item.TryGetProperty("role", out var role) && role.GetString() == "user").GetProperty("content").GetString()!;
                var refinement = latest.Contains("\"refinedPlan\"", StringComparison.Ordinal);
                Assert.Equal(Bodies.Count == 2, refinement);
                var workflow = new
                {
                    planId = "budget-plan-" + Bodies.Count,
                    steps = new[] { new
                    {
                        stepId = "budget-step-" + Bodies.Count, toolId = refinement ? "demo.accept" : "demo.inspect",
                        kind = refinement ? "action" : "query", effect = refinement ? "writesLocalState" : "readOnly",
                        input = refinement ? new Dictionary<string, object?> { ["itemId"] = "sample-1" } : new Dictionary<string, object?>()
                    } }
                };
                plan = refinement ? JsonSerializer.Serialize(new { reason = "observation", refinedPlan = workflow }) : JsonSerializer.Serialize(workflow);
            }
            var delta = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = plan });
            var complete = JsonSerializer.Serialize(new
            {
                type = "response.completed",
                response = new
                {
                    id = "fixture-response-" + Bodies.Count,
                    status = "completed",
                    output = new[] { new
                    {
                        id = "fixture-message", type = "message", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_text", text = plan, annotations = Array.Empty<string>() } }
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
