using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Providers;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Lab.Web.Tests;

public sealed class ProviderFactoryTests
{
    private const string PlanJson = """
        {"planId":"provider-plan","steps":[{"stepId":"inspect","toolId":"observe","kind":"Query","effect":"ReadOnly","input":{}}]}
        """;

    [Theory]
    [InlineData("gemini", "generativelanguage.googleapis.com")]
    [InlineData("openai", "api.openai.com")]
    [InlineData("anthropic", "api.anthropic.com")]
    [InlineData("grok", "api.x.ai")]
    [InlineData("ollama", "127.0.0.1")]
    public async Task Every_provider_uses_real_streaming_and_builds_a_plan(string provider, string expectedHost)
    {
        using var handler = new StreamHandler(provider);
        using var client = new HttpClient(handler);
        using var factory = new LabPlannerFactory(client, ConfiguredEnvironment);
        var events = new List<LlmStreamEvent>();
        var planner = factory.Create(new ProviderSettings(provider, "fixture-model"), events.Add);
        var plan = await planner.CreatePlanAsync(new PlanningRequest(new RunRequest("Inspect the current area."), [], [], []));

        Assert.Equal("provider-plan", plan.PlanId);
        Assert.Single(plan.Steps);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(expectedHost, handler.Endpoint!.Host);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("fixture-model", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal(LlmStreamEventKind.Started, events[0].Kind);
        Assert.Contains(events, item => item.Kind == LlmStreamEventKind.TextDelta);
        Assert.Equal(LlmStreamEventKind.Completed, events[^1].Kind);
        if (provider is "openai" or "grok" or "gemini")
            Assert.False(sent.RootElement.GetProperty("store").GetBoolean());
        // Completion owns private continuations; no continuation may leak through JSON.
        Assert.DoesNotContain("HistoryStepsJson", JsonSerializer.Serialize(events), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gemini_defaults_to_interactions_and_preserves_explicit_controls()
    {
        using var handler = new StreamHandler("gemini");
        using var client = new HttpClient(handler);
        using var factory = new LabPlannerFactory(client, name => name switch
        {
            "GEMINI_API_KEY" => " ",
            "GOOGLE_API_KEY" => "fixture-key",
            "AGENTICA_GEMINI_API" => "legacy", // The service's required streaming default is explicit.
            _ => null
        });
        var planner = factory.Create(new ProviderSettings(Model: "gemini-3.8-flash", ThinkingEffort: "high",
            MaxOutputTokens: 8192, IncludeThoughtSummaries: true), _ => { });
        await planner.CreatePlanAsync(new PlanningRequest(new RunRequest("Inspect."), [], [], []));
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.EndsWith("/interactions", handler.Endpoint!.AbsolutePath, StringComparison.Ordinal);
        var generation = sent.RootElement.GetProperty("generation_config");
        Assert.Equal("high", generation.GetProperty("thinking_level").GetString());
        Assert.Equal("auto", generation.GetProperty("thinking_summaries").GetString());
        Assert.Equal(8192, generation.GetProperty("max_output_tokens").GetInt32());
        Assert.False(sent.RootElement.TryGetProperty("previous_interaction_id", out _));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("generateContent")]
    public async Task Explicit_GenerateContent_route_uses_real_streaming(string api)
    {
        using var handler = new StreamHandler("generateContent");
        using var client = new HttpClient(handler);
        using var factory = new LabPlannerFactory(client, ConfiguredEnvironment);
        var events = new List<LlmStreamEvent>();
        var plan = await factory.Create(new ProviderSettings(Model: "fixture-model", GeminiApi: api,
            ThinkingEffort: "high", IncludeThoughtSummaries: true), events.Add).CreatePlanAsync(
            new PlanningRequest(new RunRequest("Inspect."), [], [], []));
        Assert.Equal("provider-plan", plan.PlanId);
        Assert.EndsWith("/models/fixture-model:streamGenerateContent", handler.Endpoint!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("?alt=sse", handler.Endpoint.Query);
        using var sent = JsonDocument.Parse(handler.Body!);
        var config = sent.RootElement.GetProperty("generationConfig");
        Assert.Equal("high", config.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.True(config.GetProperty("thinkingConfig").GetProperty("includeThoughts").GetBoolean());
        Assert.Equal(4096, config.GetProperty("maxOutputTokens").GetInt32());
        Assert.False(sent.RootElement.TryGetProperty("previous_interaction_id", out _));
        Assert.Contains(events, item => item.Kind == LlmStreamEventKind.TextDelta);
        Assert.Equal(LlmStreamEventKind.Completed, events[^1].Kind);
    }

    [Theory]
    [InlineData(0, 32768)]
    [InlineData(65537, 131072)]
    [InlineData(4096, 8192)]
    [InlineData(4096, 1048577)]
    public void Invalid_budgets_fail_before_provider_dispatch(int output, int context)
    {
        using var factory = new LabPlannerFactory(environment: ConfiguredEnvironment);
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.Create(
            new ProviderSettings(MaxOutputTokens: output, ContextWindowTokens: context), _ => { }));
    }

    [Theory]
    [InlineData("7")]
    [InlineData("aggressive")]
    [InlineData("")]
    public void Thinking_control_accepts_only_named_values(string effort)
    {
        using var factory = new LabPlannerFactory(environment: ConfiguredEnvironment);
        Assert.Throws<ArgumentException>(() => factory.Create(new ProviderSettings(ThinkingEffort: effort), _ => { }));
    }

    [Fact]
    public void Metadata_reports_configuration_without_keys_or_network_health_claims()
    {
        using var factory = new LabPlannerFactory(environment: name => name == "GOOGLE_API_KEY" ? "private-fixture-key" : null);
        var providers = factory.GetProviders();
        Assert.Equal(5, providers.Count);
        Assert.True(providers.Single(item => item.Provider == "gemini").Configured);
        Assert.False(providers.Single(item => item.Provider == "openai").Configured);
        Assert.True(providers.Single(item => item.Provider == "ollama").Configured);
        Assert.NotNull(providers.Single(item => item.Provider == "ollama").ConfigurationIssue);
        Assert.DoesNotContain("private-fixture-key", JsonSerializer.Serialize(providers), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => factory.Create(new ProviderSettings("openai"), _ => { }));
        Assert.Throws<ArgumentException>(() => factory.Create(new ProviderSettings("ollama"), _ => { }));
    }

    [Fact]
    public void Required_stream_observer_cannot_be_omitted()
    {
        using var factory = new LabPlannerFactory(environment: ConfiguredEnvironment);
        Assert.Throws<ArgumentNullException>(() => factory.Create(new ProviderSettings(), null!));
    }

    [Fact]
    public async Task Ollama_honors_service_endpoint_and_model_without_accepting_them_from_browser()
    {
        using var handler = new StreamHandler("ollama");
        using var client = new HttpClient(handler);
        using var factory = new LabPlannerFactory(client, name => name switch
        {
            "OLLAMA_HOST" => "127.0.0.1:12400",
            "OLLAMA_MODEL" => "local-model",
            _ => null
        });
        await factory.Create(new ProviderSettings("ollama"), _ => { }).CreatePlanAsync(
            new PlanningRequest(new RunRequest("Inspect."), [], [], []));
        Assert.Equal("http://127.0.0.1:12400/api/chat", handler.Endpoint!.AbsoluteUri);
        using var sent = JsonDocument.Parse(handler.Body!);
        Assert.Equal("local-model", sent.RootElement.GetProperty("model").GetString());
    }

    private static string? ConfiguredEnvironment(string name) => name.EndsWith("API_KEY", StringComparison.Ordinal) ? "fixture-key" : null;

    private sealed class StreamHandler(string provider) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Body { get; private set; }
        public Uri? Endpoint { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Endpoint = request.RequestUri;
            var stream = provider switch
            {
                "openai" or "grok" => Event("response.output_text.delta", new { type = "response.output_text.delta", delta = PlanJson }) +
                    Event("response.completed", new
                    {
                        type = "response.completed",
                        response = new
                        {
                            id = "response_1",
                            status = "completed",
                            output = new[] { new
                        {
                            id = "message_1", type = "message", role = "assistant", status = "completed",
                            content = new[] { new { type = "output_text", text = PlanJson, annotations = Array.Empty<string>() } }
                        } }
                        }
                    }),
                "gemini" => Event("step.start", new { event_type = "step.start", index = 0, step = new { type = "model_output" } }) +
                    Event("step.delta", new { event_type = "step.delta", index = 0, delta = new { type = "text", text = PlanJson } }) +
                    Event("step.stop", new { event_type = "step.stop", index = 0 }) +
                    Event("interaction.completed", new { event_type = "interaction.completed", interaction = new { id = "interaction_1", status = "completed" } }),
                "generateContent" => Event("message", new
                {
                    candidates = new[] { new { index = 0, content = new { role = "model", parts = new[] { new { text = PlanJson } } } } }
                }) + Event("message", new { candidates = new[] { new { index = 0, finishReason = "STOP" } } }),
                "anthropic" => Event("message_start", new { type = "message_start", message = new { id = "message_1", usage = new { input_tokens = 10 } } }) +
                    Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }) +
                    Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = PlanJson } }) +
                    Event("content_block_stop", new { type = "content_block_stop", index = 0 }) +
                    Event("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 100 } }) +
                    Event("message_stop", new { type = "message_stop" }),
                "ollama" => JsonSerializer.Serialize(new { message = new { role = "assistant", content = PlanJson }, done = false }) + "\n" +
                    JsonSerializer.Serialize(new { done = true, done_reason = "stop", prompt_eval_count = 10, eval_count = 100 }) + "\n",
                _ => throw new InvalidOperationException()
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, provider == "ollama" ? "application/x-ndjson" : "text/event-stream")
            };
        }
        private static string Event(string name, object value) => $"event: {name}\ndata: {JsonSerializer.Serialize(value)}\n\n";
    }
}
