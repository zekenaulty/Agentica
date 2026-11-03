using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Planning;
using Agentica.Clients.Xai;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class ResponsesStructuredOutputTests
{
    private const string Schema = """
        {"type":"object","additionalProperties":false,"properties":{"answer":{"type":"string","enum":["done"]}},"required":["answer"]}
        """;

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    public async Task Supplied_schema_is_preserved_in_stateless_streaming_request(string provider)
    {
        var handler = new StubHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.False(root.TryGetProperty("previous_response_id", out _));
            Assert.False(root.TryGetProperty("conversation", out _));
            var format = root.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.True(format.GetProperty("strict").GetBoolean());
            Assert.Equal("agentica_response", format.GetProperty("name").GetString());
            using var expected = JsonDocument.Parse(Schema);
            Assert.True(JsonElement.DeepEquals(expected.RootElement, format.GetProperty("schema")));
            return CompletedResponse();
        });
        var response = await CreateClient(provider, handler).GenerateAsync(Request(
            new LlmStructuredOutputOptions(JsonSchema: Schema)));
        Assert.Equal("{\"answer\":\"done\"}", response.StructuredJson);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("openai", null)]
    [InlineData("openai", "")]
    [InlineData("openai", " \t\n")]
    [InlineData("xai", null)]
    public async Task Schema_free_JSON_mode_remains_explicitly_unconstrained(string provider, string? schema)
    {
        var handler = new StubHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var format = body.RootElement.GetProperty("text").GetProperty("format");
            Assert.Equal("json_object", format.GetProperty("type").GetString());
            Assert.Single(format.EnumerateObject());
            return CompletedResponse();
        });
        await CreateClient(provider, handler).GenerateAsync(Request(
            new LlmStructuredOutputOptions(JsonSchema: schema)));
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"object\"")]
    public async Task Malformed_or_nonobject_schema_fails_before_dispatch(string schema)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network called."));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            CreateClient("openai", handler).GenerateAsync(Request(
                new LlmStructuredOutputOptions(JsonSchema: schema))));
        Assert.Equal("invalid_json_schema", error.ErrorClass);
        Assert.Equal(LlmClientErrorKind.BadRequest, error.ErrorKind);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Unsupported_MIME_type_fails_before_dispatch()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network called."));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            CreateClient("openai", handler).GenerateAsync(Request(
                new LlmStructuredOutputOptions("text/xml", Schema))));
        Assert.Equal("unsupported_response_format", error.ErrorClass);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Unknown_enforcement_policy_cannot_downgrade_schema()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network called."));
        var output = new LlmStructuredOutputOptions(JsonSchema: Schema)
        {
            SchemaEnforcement = (LlmJsonSchemaEnforcement)999
        };
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            CreateClient("openai", handler).GenerateAsync(Request(output)));
        Assert.Equal("unsupported_schema_enforcement", error.ErrorClass);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":true}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}},\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"required\":[\"missing\"],\"additionalProperties\":false}")]
    [InlineData("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false,\"anyOf\":[{\"type\":\"string\"}]}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"a\":{\"type\":\"object\",\"additionalProperties\":true}},\"required\":[\"a\"]}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":false,\"allOf\":[{\"type\":\"object\"}]}")]
    public async Task Known_OpenAI_strict_schema_incompatibilities_fail_without_rewriting(string schema)
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network called."));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            CreateClient("openai", handler).GenerateAsync(Request(
                new LlmStructuredOutputOptions(JsonSchema: schema))));
        Assert.Equal("unsupported_json_schema", error.ErrorClass);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Planner_explicitly_transmits_open_input_schema_as_best_effort_and_keeps_host_parsing()
    {
        const string planJson = """
            {"planId":"fixture-plan","description":"Read one fixture","steps":[{"stepId":"read","toolId":"fixture.read","kind":"Query","effect":"ReadOnly","input":{"query":"fixture"},"dependsOn":[],"batchId":null,"reason":null,"intent":{"action":"Read","rationale":"Requested fixture","expectedOutcome":"Fixture evidence"}}],"completionCondition":"Fixture evidence returned"}
            """;
        var handler = new StubHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var format = body.RootElement.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.False(format.GetProperty("strict").GetBoolean());
            Assert.True(format.GetProperty("schema").GetProperty("properties")
                .GetProperty("steps").GetProperty("items").GetProperty("properties")
                .GetProperty("input").GetProperty("additionalProperties").GetBoolean());
            return CompletedResponse(planJson);
        });
        var options = new LlmPlannerOptions("fixture-model", InvalidJsonRepairAttempts: 0);
        var request = new PlanningRequest(new RunRequest("Read the fixture"),
            [new ToolDescriptor("fixture.read", "Read fixture", ToolKind.Query, ToolEffect.ReadOnly)], [], []);
        var initial = WorkflowPlanPromptBuilder.BuildInitialPlanRequest(request, options);
        var observation = new Observation("fixture-result", "read", ObservationKind.ToolResult,
            "Fixture found", new Dictionary<string, object?>(), []);
        var refinement = WorkflowPlanPromptBuilder.BuildRefinementRequest(request, observation, options);
        var repair = WorkflowPlanPromptBuilder.BuildInitialPlanRepairRequest(initial,
            "invalid", "Invalid JSON", 1, options);
        Assert.All(new[] { initial, refinement, repair }, candidate =>
            Assert.Equal(LlmJsonSchemaEnforcement.BestEffort, candidate.StructuredOutput!.SchemaEnforcement));
        Assert.Equal(initial.StructuredOutput, repair.StructuredOutput);

        var planner = new LlmWorkflowPlanner(CreateClient("openai", handler), options);
        var plan = await planner.CreatePlanAsync(request);
        Assert.Equal("fixture", plan.Steps[0].Input["query"]);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Best_effort_does_not_accept_invalid_plans_as_host_validated()
    {
        var handler = new StubHandler(_ => Task.FromResult(CompletedResponse("{\"answer\":\"done\"}")));
        var planner = new LlmWorkflowPlanner(CreateClient("openai", handler),
            new LlmPlannerOptions("fixture-model", InvalidJsonRepairAttempts: 0));
        await Assert.ThrowsAsync<LlmPlannerException>(() => planner.CreatePlanAsync(
            new PlanningRequest(new RunRequest("Read the fixture"), [], [], [])));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Provider_schema_rejection_does_not_retry_with_JSON_mode()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            CreateClient("openai", handler).GenerateAsync(Request(
                new LlmStructuredOutputOptions(JsonSchema: Schema))));
        Assert.Equal("http_status", error.ErrorClass);
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    private static LlmRequest Request(LlmStructuredOutputOptions output) =>
        new("fixture-model", [new LlmMessage(LlmMessageRole.User, "Return JSON.")], StructuredOutput: output);

    private static ILlmClient CreateClient(string provider, HttpMessageHandler handler) =>
        provider == "xai"
            ? new XaiResponsesLlmClient(new XaiResponsesClientOptions("fixture-key",
                Endpoint: new Uri("https://example.test/responses")), new HttpClient(handler))
            : new OpenAiResponsesLlmClient(new OpenAiResponsesClientOptions("fixture-key",
                Endpoint: new Uri("https://example.test/responses")), new HttpClient(handler));

    private static HttpResponseMessage CompletedResponse(string output = "{\"answer\":\"done\"}") => new(HttpStatusCode.OK)
    {
        Content = new StringContent("event: response.completed\ndata: " + JsonSerializer.Serialize(new
        {
            type = "response.completed",
            response = new
            {
                status = "completed",
                output = new[] { new { type = "message", role = "assistant",
                    content = new[] { new { type = "output_text", text = output } } } }
            }
        }) + "\n\n", Encoding.UTF8, "text/event-stream")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> factory)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return factory(request);
        }
    }
}
