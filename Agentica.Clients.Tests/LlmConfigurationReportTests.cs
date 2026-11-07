using System.Net;
using System.Text.Json;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class LlmConfigurationReportTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("openai", "responses")]
    [InlineData("xai", "responses")]
    [InlineData("anthropic", "messages")]
    [InlineData("ollama", "chat")]
    [InlineData("gemini", "interactions")]
    [InlineData("gemini-content", "generate_content")]
    [InlineData("gemini-sdk", "sdk_generate_content_route_unresolved")]
    [InlineData("gemini-vertex", "vertex_sdk_generate_content_route_unresolved")]
    public void Reports_exact_named_control_and_keeps_readiness_and_qualification_unknown(string provider, string mode)
    {
        var report = Describe(provider, Request(LlmThinkingOptions.AtEffort(LlmReasoningEffort.High)));
        Assert.True(report.AdapterAcceptsRequest);
        Assert.Equal(mode, report.ApiMode);
        Assert.Equal("fixture-model", report.RequestedModelId);
        Assert.Equal("fixture-model", report.EffectiveModelId);
        var effort = Control(report, "reasoning_effort");
        Assert.Equal("high", effort.RequestedValue);
        Assert.Equal("high", effort.AppliedValue);
        Assert.Equal(LlmControlDisposition.Applied, effort.Disposition);
        Assert.Equal(LlmCapabilitySupport.Supported, report.AdapterEvidence.State);
        Assert.Equal(AsOf, report.AdapterEvidence.AsOf);
        Assert.Contains("Agentica.Clients/", report.AdapterEvidence.Source);
        Assert.Equal(LlmCapabilitySupport.Unknown, report.TechnicalReadiness.State);
        Assert.Equal(LlmCapabilitySupport.Unknown, report.ModelAccountQualification.State);
        Assert.Contains(provider == "openai" ? "native_count_available_separately" :
            "pre_dispatch_count_not_provided", report.InputAccountingBasis);
        Assert.Equal(provider is "gemini-sdk" or "gemini-vertex" ? LlmCapabilitySupport.Unknown
            : LlmCapabilitySupport.Supported, report.RouteResolution.State);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("anthropic")]
    [InlineData("ollama")]
    [InlineData("gemini")]
    [InlineData("gemini-content")]
    [InlineData("gemini-sdk")]
    public void Omitted_effort_is_an_unknown_provider_default_not_a_claimed_level(string provider)
    {
        var report = Describe(provider, Request(null));
        var effort = Control(report, "reasoning_effort");
        Assert.Null(effort.RequestedValue);
        Assert.Null(effort.AppliedValue);
        Assert.Equal(LlmControlDisposition.ProviderDefault, effort.Disposition);
        var max = Control(report, "max_output_tokens");
        Assert.Null(max.RequestedValue);
        Assert.Equal(provider == "anthropic" ? "4096" : null, max.AppliedValue);
        Assert.Equal(provider == "anthropic" ? LlmControlDisposition.AdapterDefault
            : LlmControlDisposition.ProviderDefault, max.Disposition);
    }

    [Theory]
    [InlineData("xai", LlmReasoningEffort.Max)]
    [InlineData("anthropic", LlmReasoningEffort.None)]
    [InlineData("ollama", LlmReasoningEffort.Minimal)]
    [InlineData("gemini", LlmReasoningEffort.XHigh)]
    [InlineData("gemini-content", LlmReasoningEffort.None)]
    public void Unsupported_effort_has_a_reason_and_never_an_applied_substitute(string provider, LlmReasoningEffort effort)
    {
        var report = Describe(provider, Request(LlmThinkingOptions.AtEffort(effort)));
        Assert.False(report.AdapterAcceptsRequest);
        Assert.Contains(report.Issues, issue => issue.Code == "unsupported_reasoning_effort");
        Assert.Null(Control(report, "reasoning_effort").AppliedValue);
        Assert.Equal(effort.ToString().ToLowerInvariant(), Control(report, "reasoning_effort").RequestedValue);
        Assert.Equal("fixture-model", report.EffectiveModelId);
    }

    [Fact]
    public void Budget_reports_preserve_numeric_adaptive_boolean_and_omitted_mechanics()
    {
        var anthropic = Describe("anthropic", Request(LlmThinkingOptions.Budget(2048)));
        Assert.Equal("2048", Control(anthropic, "thinking_budget_tokens").AppliedValue);
        Assert.Equal("enabled", Control(anthropic, "thinking_mode").AppliedValue);
        var adaptive = Describe("anthropic", Request(LlmThinkingOptions.Dynamic()));
        Assert.Null(Control(adaptive, "thinking_budget_tokens").AppliedValue);
        Assert.Equal("adaptive", Control(adaptive, "thinking_mode").AppliedValue);
        var content = Describe("gemini-content", Request(LlmThinkingOptions.Budget(1024)));
        Assert.Equal("1024", Control(content, "thinking_budget_tokens").AppliedValue);
        var interactions = Describe("gemini", Request(LlmThinkingOptions.Budget(1024)));
        Assert.Contains(interactions.Issues, issue => issue.Code == "unsupported_thinking_budget");
        var ollama = Describe("ollama", Request(LlmThinkingOptions.Off()));
        Assert.Null(Control(ollama, "thinking_budget_tokens").AppliedValue);
        Assert.Equal("false", Control(ollama, "thinking_mode").AppliedValue);
        var openai = Describe("openai", Request(LlmThinkingOptions.Dynamic()));
        Assert.Equal("-1", Control(openai, "thinking_budget_tokens").RequestedValue);
        Assert.Null(Control(openai, "thinking_budget_tokens").AppliedValue);
        Assert.Equal(LlmControlDisposition.ProviderDefault, Control(openai, "thinking_budget_tokens").Disposition);
    }

    [Fact]
    public void Unrelated_rejection_does_not_declare_unevaluated_controls_unsupported()
    {
        var request = Request(null) with
        {
            GenerationOptions = new(Temperature: 1, MaxOutputTokens: 4096,
            Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High))
        };
        var report = Describe("openai", request);
        Assert.False(report.AdapterAcceptsRequest);
        Assert.Equal(LlmCapabilitySupport.Unsupported, Control(report, "temperature").AdapterSupport);
        Assert.Equal(LlmCapabilitySupport.Unknown, Control(report, "reasoning_effort").AdapterSupport);
        Assert.Equal(LlmControlDisposition.Unresolved, Control(report, "reasoning_effort").Disposition);
        Assert.Equal(LlmCapabilitySupport.Unknown, Control(report, "max_output_tokens").AdapterSupport);
    }

    [Fact]
    public void Ollama_thinking_selection_is_separate_from_unsupported_thought_output_switch()
    {
        var report = Describe("ollama", Request(LlmThinkingOptions.AtEffort(LlmReasoningEffort.High, false)));
        Assert.Equal("high", Control(report, "reasoning_effort").AppliedValue);
        Assert.Equal("high", Control(report, "thinking_mode").AppliedValue);
        var output = Control(report, "thought_output");
        Assert.Equal("false", output.RequestedValue);
        Assert.Null(output.AppliedValue);
        Assert.Equal(LlmControlDisposition.NotApplied, output.Disposition);
        Assert.Equal(LlmCapabilitySupport.Unsupported, output.AdapterSupport);
    }

    [Fact]
    public void Control_support_is_independent_from_omission_and_application()
    {
        var report = Describe("openai", Request(LlmThinkingOptions.AtEffort(LlmReasoningEffort.High, false)));
        Assert.Equal(LlmControlDisposition.ProviderDefault, Control(report, "temperature").Disposition);
        Assert.Equal(LlmCapabilitySupport.Unsupported, Control(report, "temperature").AdapterSupport);
        Assert.Equal(LlmControlDisposition.NotApplied, Control(report, "thought_output").Disposition);
        Assert.Equal(LlmCapabilitySupport.Supported, Control(report, "thought_output").AdapterSupport);
        var requested = Describe("openai", Request(LlmThinkingOptions.AtEffort(LlmReasoningEffort.High, true)));
        Assert.Equal(LlmControlDisposition.Applied, Control(requested, "thought_output").Disposition);
        Assert.Equal(LlmCapabilitySupport.Supported, Control(requested, "thought_output").AdapterSupport);
    }

    [Fact]
    public void Scalar_schema_values_cannot_escape_into_report_display()
    {
        var report = Describe("ollama", Request(null) with
        { StructuredOutput = new(JsonSchema: "\"private-schema-fragment\"") });
        Assert.True(report.AdapterAcceptsRequest);
        Assert.Equal("json_schema", Control(report, "structured_output").AppliedValue);
        Assert.DoesNotContain("private-schema-fragment", JsonSerializer.Serialize(report));
    }

    [Fact]
    public void Structured_output_reports_constraint_and_enforcement_differences()
    {
        var request = Request(null) with
        {
            StructuredOutput = new(JsonSchema:
            """{"type":"object","properties":{},"additionalProperties":false}""")
        };
        var openai = Describe("openai", request);
        Assert.Equal("json_schema", Control(openai, "structured_output").AppliedValue);
        Assert.Equal("true", Control(openai, "schema_enforcement").AppliedValue);
        var anthropic = Describe("anthropic", request);
        Assert.True(anthropic.AdapterAcceptsRequest);
        Assert.Equal(LlmCapabilitySupport.Unsupported,
            Capability(anthropic, "native_structured_output").AdapterSupport);
        Assert.Null(Control(anthropic, "structured_output").AppliedValue);
        Assert.Equal(LlmControlDisposition.NotApplied, Control(anthropic, "structured_output").Disposition);
        Assert.Null(Control(Describe("gemini", request), "schema_enforcement").AppliedValue);
        Assert.Contains("compatibility", Control(Describe("xai", request), "schema_enforcement").Explanation);
    }

    [Fact]
    public void Fingerprint_tracks_effective_configuration_without_credentials_content_or_host_bundle()
    {
        var request = Request(null);
        var options = new OpenAiResponsesClientOptions("credential-alpha", Endpoint:
            new UriBuilder("https://provider.example/v1/responses")
            { UserName = "user", Password = "pass", Query = "key=secret-alpha" }.Uri);
        var context = new LlmConfigurationReportContext(AsOf, "host-operation-phase", "host-bundle-one");
        var first = LlmConfigurationReports.Describe(request, options, context);
        var second = LlmConfigurationReports.Describe(request with
        {
            Messages = [new(LlmMessageRole.User, "different-private-content")],
            Metadata = new Dictionary<string, string> { ["token"] = "private-metadata" }
        }, options with
        {
            ApiKey = "credential-beta",
            Endpoint =
            new UriBuilder("https://provider.example/v1/responses")
            { UserName = "other", Password = "password", Query = "key=secret-beta" }.Uri
        },
            context with { HostExecutionBundleReference = "host-bundle-two" });
        Assert.Equal(first.ConfigurationIdentity, second.ConfigurationIdentity);
        Assert.NotEqual(first.HostExecutionBundleReference, second.HostExecutionBundleReference);
        Assert.Equal("host-operation-phase", first.HostRouteReference);
        Assert.NotEqual(first.ConfigurationIdentity,
            LlmConfigurationReports.Describe(request with { ModelId = "other-model" }, options).ConfigurationIdentity);
        Assert.NotEqual(first.ConfigurationIdentity,
            LlmConfigurationReports.Describe(request, options with { Endpoint = new Uri("https://provider.example/v2/responses") }).ConfigurationIdentity);
        Assert.NotEqual(first.ConfigurationIdentity,
            LlmConfigurationReports.Describe(Request(LlmThinkingOptions.AtEffort(LlmReasoningEffort.High)), options).ConfigurationIdentity);
        var json = JsonSerializer.Serialize(second);
        foreach (var privateValue in new[] { "credential-beta", "secret-beta", "different-private-content", "private-metadata", "password" })
            Assert.DoesNotContain(privateValue, json);
    }

    [Fact]
    public void Explicit_model_wins_and_defaults_do_not_invent_a_model()
    {
        var options = new OllamaClientOptions(DefaultModelId: "configured-model");
        Assert.Equal("configured-model", LlmConfigurationReports.Describe(Request(null) with { ModelId = "" }, options).EffectiveModelId);
        Assert.Equal("fixture-model", LlmConfigurationReports.Describe(Request(null), options).EffectiveModelId);
        var missing = LlmConfigurationReports.Describe(Request(null) with { ModelId = "" }, new OllamaClientOptions());
        Assert.False(missing.AdapterAcceptsRequest);
        Assert.Contains(missing.Issues, issue => issue.Code == "missing_model");
    }

    [Fact]
    public void GenerateContent_default_endpoint_uses_the_transports_exact_model_validation()
    {
        var request = Request(null) with { ModelId = "../bad" };
        var report = LlmConfigurationReports.Describe(request, new GeminiGenerateContentClientOptions("fixture-key"));
        Assert.False(report.AdapterAcceptsRequest);
        Assert.Contains(report.Issues, issue => issue.Code == "invalid_model_id");
        var overridden = LlmConfigurationReports.Describe(request,
            new GeminiGenerateContentClientOptions("fixture-key", Endpoint: new Uri("https://fixture.invalid/stream")));
        Assert.True(overridden.AdapterAcceptsRequest); // Matches the transport's explicit endpoint override.
    }

    [Fact]
    public void Host_observations_are_separate_and_do_not_transfer_to_changed_configuration()
    {
        var request = Request(null);
        var report = Describe("openai", request);
        var context = new LlmConfigurationReportContext(AsOf, TechnicalReadiness:
            new(report.ConfigurationIdentity, LlmCapabilitySupport.Supported, "host-offline-http-fixture", AsOf, "Fixture only"));
        var matching = Describe("openai", request, context);
        Assert.Equal(LlmCapabilitySupport.Supported, matching.TechnicalReadiness.State);
        Assert.Equal(LlmCapabilitySupport.Unknown, matching.ModelAccountQualification.State);
        var changed = Describe("openai", request with { ModelId = "other-model" }, context);
        Assert.Equal(LlmCapabilitySupport.Unknown, changed.TechnicalReadiness.State);
        Assert.Equal("observation_binding_mismatch", changed.TechnicalReadiness.Source);
        Assert.Equal(report.ConfigurationIdentity, matching.ConfigurationIdentity);
    }

    [Theory]
    [InlineData("gemini-sdk")]
    [InlineData("gemini-vertex")]
    public void Legacy_SDK_ambient_route_is_explicitly_unresolved_and_cannot_reuse_observations(string provider)
    {
        var report = Describe(provider, Request(null));
        Assert.Equal(LlmCapabilitySupport.Unknown, report.RouteResolution.State);
        Assert.Equal("sdk_ambient_route_unresolved", report.RouteResolution.Source);
        Assert.StartsWith("unresolved:sha256:", report.ConfigurationIdentity);
        var observation = new LlmConfigurationObservation(report.ConfigurationIdentity,
            LlmCapabilitySupport.Supported, "host-fixture", AsOf, "Matching partial configuration");
        var repeated = Describe(provider, Request(null), new(AsOf,
            TechnicalReadiness: observation, ModelAccountQualification: observation));
        Assert.Equal(LlmCapabilitySupport.Unknown, repeated.TechnicalReadiness.State);
        Assert.Equal(LlmCapabilitySupport.Unknown, repeated.ModelAccountQualification.State);
        Assert.Equal("sdk_route_unresolved", repeated.TechnicalReadiness.Source);
    }

    [Fact]
    public void Rejected_configuration_cannot_reuse_an_accepted_configuration_observation()
    {
        var accepted = Describe("openai", Request(null));
        var context = new LlmConfigurationReportContext(AsOf, TechnicalReadiness:
            new(accepted.ConfigurationIdentity, LlmCapabilitySupport.Supported, "offline-fixture", AsOf, "Fixture only"));
        var rejected = Describe("openai", Request(null) with { GenerationOptions = new(MaxOutputTokens: 0) }, context);
        Assert.False(rejected.AdapterAcceptsRequest);
        Assert.NotEqual(accepted.ConfigurationIdentity, rejected.ConfigurationIdentity);
        Assert.Equal(LlmCapabilitySupport.Unknown, rejected.TechnicalReadiness.State);
    }

    [Fact]
    public void Identity_binds_actual_schema_and_sdk_route_but_ignores_unapplied_schema()
    {
        var request = Request(null) with
        {
            StructuredOutput = new(JsonSchema:
            """{"type":"object","properties":{},"additionalProperties":false}""")
        };
        var changed = request with
        {
            StructuredOutput = new(JsonSchema:
            """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""")
        };
        Assert.NotEqual(Describe("openai", request).ConfigurationIdentity, Describe("openai", changed).ConfigurationIdentity);
        Assert.Equal(Describe("anthropic", request).ConfigurationIdentity, Describe("anthropic", changed).ConfigurationIdentity);
        var vertex = new GeminiClientOptions(UseVertexAi: true, Project: "one", Location: "us-central1");
        Assert.NotEqual(LlmConfigurationReports.Describe(request, vertex).ConfigurationIdentity,
            LlmConfigurationReports.Describe(request, vertex with { Project = "two" }).ConfigurationIdentity);
    }

    [Theory]
    [InlineData("openai", "reasoning", "effort")]
    [InlineData("xai", "reasoning", "effort")]
    [InlineData("anthropic", "output_config", "effort")]
    [InlineData("gemini", "generation_config", "thinking_level")]
    [InlineData("gemini-content", "generationConfig", "thinkingConfig", "thinkingLevel")]
    [InlineData("ollama", "think")]
    public async Task Public_transport_wire_agrees_with_report_without_a_provider_call(string provider, params string[] effortPath)
    {
        var request = Request(LlmThinkingOptions.AtEffort(LlmReasoningEffort.High, true)) with
        { GenerationOptions = new(MaxOutputTokens: 4096, Thinking: LlmThinkingOptions.AtEffort(LlmReasoningEffort.High, true)) };
        var report = Describe(provider, request);
        using var handler = new RejectingCaptureHandler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<LlmClientException>(() => Client(provider, http).GenerateAsync(request));
        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var effort = body.RootElement;
        foreach (var part in effortPath) effort = effort.GetProperty(part);
        Assert.Equal(effort.GetString(), Control(report, "reasoning_effort").AppliedValue);
        Assert.Equal("4096", Control(report, "max_output_tokens").AppliedValue);
        Assert.Equal(1, handler.Calls);
    }

    private static LlmRequest Request(LlmThinkingOptions? thinking) =>
        new("fixture-model", [new(LlmMessageRole.User, "private-input")], new(Thinking: thinking));

    private static LlmControlReport Control(LlmConfigurationReport report, string name) =>
        Assert.Single(report.Controls, control => control.Name == name);

    private static LlmCapabilityReport Capability(LlmConfigurationReport report, string name) =>
        Assert.Single(report.Capabilities, capability => capability.Name == name);

    private static LlmConfigurationReport Describe(string provider, LlmRequest request, LlmConfigurationReportContext? context = null)
    {
        context ??= new(AsOf);
        return provider switch
        {
            "openai" => LlmConfigurationReports.Describe(request, new OpenAiResponsesClientOptions("fixture-key"), context),
            "xai" => LlmConfigurationReports.Describe(request, new XaiResponsesClientOptions("fixture-key"), context),
            "anthropic" => LlmConfigurationReports.Describe(request, new AnthropicMessagesClientOptions("fixture-key"), context),
            "ollama" => LlmConfigurationReports.Describe(request, new OllamaClientOptions(), context),
            "gemini" => LlmConfigurationReports.Describe(request, new GeminiInteractionsClientOptions("fixture-key"), context),
            "gemini-content" => LlmConfigurationReports.Describe(request, new GeminiGenerateContentClientOptions("fixture-key"), context),
            "gemini-sdk" => LlmConfigurationReports.Describe(request, new GeminiClientOptions("fixture-key"), context),
            "gemini-vertex" => LlmConfigurationReports.Describe(request, new GeminiClientOptions(UseVertexAi: true, Project: "fixture", Location: "us-central1"), context),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
    }

    private static ILlmClient Client(string provider, HttpClient http) => provider switch
    {
        "openai" => new OpenAiResponsesLlmClient(new("fixture-key"), http),
        "xai" => new XaiResponsesLlmClient(new("fixture-key"), http),
        "anthropic" => new AnthropicMessagesLlmClient(new("fixture-key"), http),
        "ollama" => new OllamaLlmClient(httpClient: http),
        "gemini" => new GeminiInteractionsLlmClient(new("fixture-key"), http),
        "gemini-content" => new GeminiGenerateContentLlmClient(new("fixture-key"), http),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private sealed class RejectingCaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{}") };
        }
    }
}
