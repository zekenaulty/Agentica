using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Lab.Web.Contracts;
using Agentica.Lab.Web.Providers;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Lab.Web.Tests;

public sealed class OpenAiSettingsTests
{
    private const string SettingsPath = "/api/providers/openai/settings";
    private const string EnvironmentKey = "fixture-environment-credential";
    private const string MemoryKey = "fixture-memory-credential";
    private const string InvalidKeyMarker = "fixture-rejected-credential";
    private const string PlanJson = """
        {"planId":"settings-plan","steps":[{"stepId":"inspect","toolId":"observe","kind":"Query","effect":"ReadOnly","input":{}}]}
        """;

    [Fact]
    public async Task Http_settings_update_readiness_without_dispatch_and_reject_invalid_updates_atomically()
    {
        var storage = Directory.CreateTempSubdirectory("agentica-openai-settings-tests-");
        var configuration = new OpenAiProviderConfiguration(Environment);
        using var handler = new StreamHandler();
        using var providerHttp = new HttpClient(handler);
        using var factory = new CountingPlannerFactory(new LabPlannerFactory(providerHttp, _ => null, configuration));
        var app = LabWebApplication.Create(["--urls", "http://127.0.0.1:0"], factory, storage.FullName, configuration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await app.StartAsync(deadline.Token);
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using (var response = await http.GetAsync(SettingsPath, deadline.Token))
            {
                var body = await SafeSettingsBodyAsync(response, HttpStatusCode.OK, deadline.Token);
                using var initial = JsonDocument.Parse(body);
                Assert.Equal("gpt-6-luna", initial.RootElement.GetProperty("model").GetString());
                Assert.Equal("high", initial.RootElement.GetProperty("thinkingEffort").GetString());
                Assert.Equal("environment", initial.RootElement.GetProperty("credentialSource").GetString());
                Assert.True(initial.RootElement.GetProperty("configured").GetBoolean());
            }

            await PutAndCheckAsync(http, new { model = "gpt-6-luna", thinkingEffort = "high", credentialAction = "set", apiKey = MemoryKey },
                "serviceMemory", "high", deadline.Token);
            await PutAndCheckAsync(http, new { model = "gpt-6-luna", thinkingEffort = "low", credentialAction = "keep" },
                "serviceMemory", "low", deadline.Token);

            var beforeInvalid = JsonSerializer.Serialize(configuration.GetSettings());
            string[] invalidBodies =
            [
                "null",
                "{\"model\":\"gpt-6-luna\",\"apiKey\":\"" + InvalidKeyMarker + "\"}",
                "{\"apiKey\":\"" + InvalidKeyMarker + "\",",
                JsonSerializer.Serialize(new { model = "gpt-6-luna", thinkingEffort = "high", credentialAction = "set", apiKey = InvalidKeyMarker, unexpected = true }),
                JsonSerializer.Serialize(new { model = (string?)null, thinkingEffort = "high", credentialAction = "set", apiKey = InvalidKeyMarker }),
                JsonSerializer.Serialize(new { model = "gpt-6-luna", thinkingEffort = "high", credentialAction = (string?)null, apiKey = InvalidKeyMarker }),
                JsonSerializer.Serialize(new { model = "gpt-6-luna", thinkingEffort = "minimal", credentialAction = "set", apiKey = InvalidKeyMarker }),
                JsonSerializer.Serialize(new { model = "gpt-6-luna", thinkingEffort = "high", credentialAction = "replace", apiKey = InvalidKeyMarker }),
                JsonSerializer.Serialize(new { model = "gpt-6-luna", thinkingEffort = "high", credentialAction = "set" }),
                JsonSerializer.Serialize(new { model = "gpt-6-luna", thinkingEffort = "high", credentialAction = "set", apiKey = InvalidKeyMarker + " invalid" }),
                JsonSerializer.Serialize(new { model = "gpt-6-luna", thinkingEffort = "high", credentialAction = "set", apiKey = new string('x', 8193) }),
                JsonSerializer.Serialize(new { model = "gpt-6-luna", thinkingEffort = "high", credentialAction = "keep", apiKey = InvalidKeyMarker })
            ];
            foreach (var invalid in invalidBodies)
            {
                using var content = new StringContent(invalid, Encoding.UTF8, "application/json");
                using var response = await http.PutAsync(SettingsPath, content, deadline.Token);
                var body = await SafeSettingsBodyAsync(response, HttpStatusCode.BadRequest, deadline.Token);
                using var error = JsonDocument.Parse(body);
                Assert.Equal("configuration.invalid", error.RootElement.GetProperty("code").GetString());
                Assert.Equal(beforeInvalid, JsonSerializer.Serialize(configuration.GetSettings()));
            }

            using (var response = await http.GetAsync("/api/providers", deadline.Token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync(deadline.Token);
                AssertNoCredentials(body);
                using var metadata = JsonDocument.Parse(body);
                var openAi = metadata.RootElement.EnumerateArray().Single(item => item.GetProperty("provider").GetString() == "openai");
                Assert.True(openAi.GetProperty("configured").GetBoolean());
                Assert.Equal("low", openAi.GetProperty("defaultThinkingEffort").GetString());
            }

            await PutAndCheckAsync(http, new { model = "custom-no-reasoning", thinkingEffort = (string?)null, credentialAction = "keep" },
                "serviceMemory", null, deadline.Token);
            await PutAndCheckAsync(http, new { model = "gpt-6-luna", thinkingEffort = "high", credentialAction = "environment" },
                "environment", "high", deadline.Token);
            Assert.Equal(0, factory.CreateCalls);
            Assert.Empty(handler.Bodies);
            Assert.Empty(handler.Credentials);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(cleanup.Token);
            await app.DisposeAsync();
            storage.Delete(recursive: true);
        }
    }

    [Fact]
    public void Overrides_are_process_local_and_update_serialization_never_discloses_the_key()
    {
        var configuration = new OpenAiProviderConfiguration(_ => null);
        Assert.False(configuration.GetSettings().Configured);
        var update = new OpenAiSettingsUpdate
        {
            Model = "custom-model",
            ThinkingEffort = "low",
            CredentialAction = "set",
            ApiKey = MemoryKey
        };
        var configured = configuration.Update(update);
        Assert.True(configured.Configured);
        Assert.Equal("serviceMemory", configured.CredentialSource);
        Assert.Equal("custom-model", configured.Model);
        AssertNoCredentials(JsonSerializer.Serialize(update, HostProtocol.Json));
        AssertNoCredentials(JsonSerializer.Serialize(configured, HostProtocol.Json));

        var freshProcess = new OpenAiProviderConfiguration(_ => null).GetSettings();
        Assert.False(freshProcess.Configured);
        Assert.Equal("none", freshProcess.CredentialSource);
        Assert.Equal("gpt-6-luna", freshProcess.Model);
        Assert.Equal("high", freshProcess.ThinkingEffort);
        var reverted = configuration.Update(new OpenAiSettingsUpdate
        {
            Model = "gpt-6-luna",
            ThinkingEffort = "high",
            CredentialAction = "environment"
        });
        Assert.False(reverted.Configured);
        Assert.Equal("none", reverted.CredentialSource);
    }

    [Theory]
    [InlineData(null, null, "gpt-6-luna", "high")]
    [InlineData(null, "none", "gpt-6-luna", "none")]
    [InlineData("different-model", null, "different-model", null)]
    public async Task Planner_applies_configured_defaults_and_preserves_per_run_overrides(
        string? model, string? effort, string expectedModel, string? expectedEffort)
    {
        using var handler = new StreamHandler();
        using var http = new HttpClient(handler);
        using var factory = new LabPlannerFactory(http, Environment);
        var events = new List<LlmStreamEvent>();
        var planner = factory.Create(new ProviderSettings("openai", model, effort,
            MaxOutputTokens: 8192, IncludeThoughtSummaries: true), events.Add);
        var plan = await planner.CreatePlanAsync(Request());
        Assert.Equal("settings-plan", plan.PlanId);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(expectedModel, body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("previous_response_id", out _));
        Assert.Equal(8192, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        var reasoning = body.RootElement.GetProperty("reasoning");
        Assert.Equal("auto", reasoning.GetProperty("summary").GetString());
        if (expectedEffort is null) Assert.False(reasoning.TryGetProperty("effort", out _));
        else Assert.Equal(expectedEffort, reasoning.GetProperty("effort").GetString());
        Assert.Contains(events, item => item.Kind == LlmStreamEventKind.TextDelta);
        Assert.Equal(LlmStreamEventKind.Completed, events[^1].Kind);
        AssertNoCredentials(JsonSerializer.Serialize(events));
    }

    [Fact]
    public async Task Custom_environment_model_omits_reasoning_without_an_explicit_effort()
    {
        static string? CustomEnvironment(string name) => name switch
        {
            "OPENAI_API_KEY" => EnvironmentKey,
            "AGENTICA_OPENAI_MODEL" => "custom-no-reasoning",
            _ => null
        };
        var configuration = new OpenAiProviderConfiguration(CustomEnvironment);
        Assert.Equal("custom-no-reasoning", configuration.GetSettings().Model);
        Assert.Null(configuration.GetSettings().ThinkingEffort);
        using var handler = new StreamHandler();
        using var http = new HttpClient(handler);
        using var factory = new LabPlannerFactory(http, CustomEnvironment, configuration);
        Assert.Null(factory.GetProviders().Single(item => item.Provider == "openai").DefaultThinkingEffort);
        await factory.Create(new ProviderSettings("openai"), _ => { }).CreatePlanAsync(Request());
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("custom-no-reasoning", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("reasoning", out _));
    }

    [Fact]
    public async Task Existing_planners_keep_their_credential_snapshot_when_configuration_changes()
    {
        var configuration = new OpenAiProviderConfiguration(Environment);
        using var handler = new StreamHandler();
        using var http = new HttpClient(handler);
        using var factory = new LabPlannerFactory(http, _ => null, configuration);
        var before = factory.Create(new ProviderSettings("openai"), _ => { });
        configuration.Update(new OpenAiSettingsUpdate
        {
            Model = "gpt-6-luna",
            ThinkingEffort = "low",
            CredentialAction = "set",
            ApiKey = MemoryKey
        });
        var after = factory.Create(new ProviderSettings("openai"), _ => { });
        await before.CreatePlanAsync(Request());
        await after.CreatePlanAsync(Request());
        Assert.Equal([EnvironmentKey, MemoryKey], handler.Credentials);
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        using var second = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("high", first.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal("low", second.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        foreach (var body in handler.Bodies) AssertNoCredentials(body);

        configuration.Update(new OpenAiSettingsUpdate
        {
            Model = "gpt-6-luna",
            ThinkingEffort = "high",
            CredentialAction = "environment"
        });
        await factory.Create(new ProviderSettings("openai"), _ => { }).CreatePlanAsync(Request());
        Assert.Equal(EnvironmentKey, handler.Credentials[2]);
    }

    private static string? Environment(string name) => name == "OPENAI_API_KEY" ? EnvironmentKey : null;
    private static PlanningRequest Request() => new(new RunRequest("Inspect the current area."), [], [], []);

    private static async Task PutAndCheckAsync(HttpClient http, object update, string source, string? effort, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(update), Encoding.UTF8, "application/json");
        using var response = await http.PutAsync(SettingsPath, content, cancellationToken);
        var body = await SafeSettingsBodyAsync(response, HttpStatusCode.OK, cancellationToken);
        using var settings = JsonDocument.Parse(body);
        Assert.Equal(source, settings.RootElement.GetProperty("credentialSource").GetString());
        Assert.Equal(effort, settings.RootElement.GetProperty("thinkingEffort").GetString());
        Assert.True(settings.RootElement.GetProperty("configured").GetBoolean());
    }

    private static async Task<string> SafeSettingsBodyAsync(HttpResponseMessage response, HttpStatusCode status, CancellationToken cancellationToken)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        AssertNoCredentials(body);
        return body;
    }

    private static void AssertNoCredentials(string text)
    {
        Assert.DoesNotContain(EnvironmentKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(MemoryKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain(InvalidKeyMarker, text, StringComparison.Ordinal);
    }

    private sealed class CountingPlannerFactory(LabPlannerFactory inner) : ILabPlannerFactory, IDisposable
    {
        public int CreateCalls { get; private set; }
        public IWorkflowPlanner Create(ProviderSettings settings, Action<LlmStreamEvent> onStreamEvent)
        {
            CreateCalls++;
            throw new InvalidOperationException("Settings must not create a planner.");
        }
        public IReadOnlyList<ProviderMetadata> GetProviders() => inner.GetProviders();
        public void Dispose() => inner.Dispose();
    }

    private sealed class StreamHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string?> Credentials { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Credentials.Add(request.Headers.Authorization?.Parameter);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var delta = JsonSerializer.Serialize(new { type = "response.output_text.delta", delta = PlanJson });
            var completed = JsonSerializer.Serialize(new
            {
                type = "response.completed",
                response = new
                {
                    id = "settings-response",
                    status = "completed",
                    output = new[] { new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = PlanJson } } } }
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: response.output_text.delta\ndata: {delta}\n\nevent: response.completed\ndata: {completed}\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
