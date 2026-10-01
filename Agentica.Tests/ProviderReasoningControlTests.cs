using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Anthropic;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;
using Agentica.Clients.Ollama;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;

namespace Agentica.Tests;

public sealed class ProviderReasoningControlTests
{
    [Theory]
    [InlineData("openai", LlmReasoningEffort.None, "none")]
    [InlineData("openai", LlmReasoningEffort.Minimal, "minimal")]
    [InlineData("openai", LlmReasoningEffort.Low, "low")]
    [InlineData("openai", LlmReasoningEffort.Medium, "medium")]
    [InlineData("openai", LlmReasoningEffort.High, "high")]
    [InlineData("openai", LlmReasoningEffort.XHigh, "xhigh")]
    [InlineData("openai", LlmReasoningEffort.Max, "max")]
    [InlineData("xai", LlmReasoningEffort.Low, "low")]
    [InlineData("xai", LlmReasoningEffort.Medium, "medium")]
    [InlineData("xai", LlmReasoningEffort.High, "high")]
    [InlineData("xai", LlmReasoningEffort.XHigh, "xhigh")]
    [InlineData("gemini", LlmReasoningEffort.Minimal, "minimal")]
    [InlineData("gemini", LlmReasoningEffort.Low, "low")]
    [InlineData("gemini", LlmReasoningEffort.Medium, "medium")]
    [InlineData("gemini", LlmReasoningEffort.High, "high")]
    [InlineData("ollama", LlmReasoningEffort.Low, "low")]
    [InlineData("ollama", LlmReasoningEffort.Medium, "medium")]
    [InlineData("ollama", LlmReasoningEffort.High, "high")]
    [InlineData("anthropic", LlmReasoningEffort.Low, "low")]
    [InlineData("anthropic", LlmReasoningEffort.Medium, "medium")]
    [InlineData("anthropic", LlmReasoningEffort.High, "high")]
    [InlineData("anthropic", LlmReasoningEffort.XHigh, "xhigh")]
    [InlineData("anthropic", LlmReasoningEffort.Max, "max")]
    public async Task Effort_reaches_the_provider_field_without_becoming_a_numeric_budget(
        string provider, LlmReasoningEffort effort, string expected)
    {
        using var handler = new CaptureHandler(provider);
        using var http = new HttpClient(handler);
        var client = CreateClient(provider, http);
        var response = await client.GenerateAsync(Request(
            LlmThinkingOptions.AtEffort(effort, includeThoughts: true)));
        using var continuation = response.NativeContinuation;
        Assert.Equal("ok", response.Text);
        using var document = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var body = document.RootElement;
        Assert.True(body.GetProperty("stream").GetBoolean());
        switch (provider)
        {
            case "openai":
            case "xai":
                Assert.Equal(expected, body.GetProperty("reasoning").GetProperty("effort").GetString());
                Assert.Equal("auto", body.GetProperty("reasoning").GetProperty("summary").GetString());
                Assert.False(body.GetProperty("store").GetBoolean());
                break;
            case "gemini":
                var config = body.GetProperty("generation_config");
                Assert.Equal(expected, config.GetProperty("thinking_level").GetString());
                Assert.Equal("auto", config.GetProperty("thinking_summaries").GetString());
                Assert.False(config.TryGetProperty("thinking_budget", out _));
                Assert.False(body.GetProperty("store").GetBoolean());
                break;
            case "ollama":
                Assert.Equal(expected, body.GetProperty("think").GetString());
                break;
            case "anthropic":
                Assert.Equal(expected, body.GetProperty("output_config").GetProperty("effort").GetString());
                Assert.Equal("adaptive", body.GetProperty("thinking").GetProperty("type").GetString());
                Assert.Equal("summarized", body.GetProperty("thinking").GetProperty("display").GetString());
                Assert.False(body.GetProperty("thinking").TryGetProperty("budget_tokens", out _));
                break;
        }
    }

    [Theory]
    [InlineData("xai", LlmReasoningEffort.None)]
    [InlineData("xai", LlmReasoningEffort.Minimal)]
    [InlineData("xai", LlmReasoningEffort.Max)]
    [InlineData("gemini", LlmReasoningEffort.None)]
    [InlineData("gemini", LlmReasoningEffort.XHigh)]
    [InlineData("gemini", LlmReasoningEffort.Max)]
    [InlineData("ollama", LlmReasoningEffort.None)]
    [InlineData("ollama", LlmReasoningEffort.Minimal)]
    [InlineData("ollama", LlmReasoningEffort.XHigh)]
    [InlineData("ollama", LlmReasoningEffort.Max)]
    [InlineData("anthropic", LlmReasoningEffort.None)]
    [InlineData("anthropic", LlmReasoningEffort.Minimal)]
    public async Task Unsupported_provider_effort_is_rejected_before_network(
        string provider, LlmReasoningEffort effort)
    {
        using var handler = new CaptureHandler(provider);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            CreateClient(provider, http).GenerateAsync(Request(LlmThinkingOptions.AtEffort(effort))));
        Assert.Equal("unsupported_reasoning_effort", error.ErrorClass);
        Assert.Equal(LlmClientErrorKind.BadRequest, error.ErrorKind);
        Assert.Equal(provider == "gemini" ? GeminiLlmClient.ProviderName : provider, error.ProviderName);
        Assert.Null(handler.Body);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("gemini")]
    [InlineData("ollama")]
    [InlineData("anthropic")]
    public async Task Invalid_enum_and_negative_budget_are_never_silently_ignored(string provider)
    {
        using var handler = new CaptureHandler(provider);
        using var http = new HttpClient(handler);
        var client = CreateClient(provider, http);
        var invalidEffort = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(Request(LlmThinkingOptions.AtEffort((LlmReasoningEffort)123))));
        Assert.Equal("invalid_reasoning_effort", invalidEffort.ErrorClass);
        var invalidBudget = await Assert.ThrowsAsync<LlmClientException>(() =>
            client.GenerateAsync(Request(new LlmThinkingOptions(ThinkingBudgetTokens: -2))));
        Assert.Equal("invalid_thinking_budget", invalidBudget.ErrorClass);
        Assert.Null(handler.Body);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("gemini")]
    [InlineData("ollama")]
    public async Task Effort_does_not_turn_an_unsupported_numeric_budget_into_an_allowed_request(string provider)
    {
        using var handler = new CaptureHandler(provider);
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<LlmClientException>(() =>
            CreateClient(provider, http).GenerateAsync(Request(
                LlmThinkingOptions.Budget(1024) with { Effort = LlmReasoningEffort.Low })));
        Assert.Equal("unsupported_thinking_budget", error.ErrorClass);
        Assert.Null(handler.Body);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("xai")]
    [InlineData("gemini")]
    [InlineData("ollama")]
    [InlineData("anthropic")]
    public async Task Omitted_thinking_leaves_provider_defaults_unchanged(string provider)
    {
        using var handler = new CaptureHandler(provider);
        using var http = new HttpClient(handler);
        var response = await CreateClient(provider, http).GenerateAsync(Request(null));
        using var continuation = response.NativeContinuation;
        using var document = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        foreach (var field in new[] { "reasoning", "thinking", "think", "output_config", "generation_config" })
            Assert.False(document.RootElement.TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ollama_preserves_boolean_controls_and_rejects_off_combined_with_effort(bool enabled)
    {
        var thinking = enabled ? LlmThinkingOptions.Dynamic() : LlmThinkingOptions.Off();
        var body = OllamaLlmClient.BuildRequestBody(Request(thinking), "qwen3");
        Assert.Equal(enabled, Assert.IsType<bool>(body["think"]));
        var error = Assert.Throws<LlmClientException>(() =>
            OllamaLlmClient.BuildRequestBody(Request(
                LlmThinkingOptions.Off() with { Effort = LlmReasoningEffort.Low }), "gpt-oss:20b"));
        Assert.Equal("conflicting_thinking_controls", error.ErrorClass);
    }

    [Fact]
    public void Anthropic_manual_budget_and_effort_compose_without_becoming_adaptive()
    {
        var request = Request(LlmThinkingOptions.Budget(2048) with { Effort = LlmReasoningEffort.High }) with
        {
            GenerationOptions = new LlmGenerationOptions(MaxOutputTokens: 4096,
                Thinking: LlmThinkingOptions.Budget(2048) with { Effort = LlmReasoningEffort.High })
        };
        var body = JsonSerializer.SerializeToElement(
            AnthropicMessagesLlmClient.BuildRequestBody(request, "claude-opus-4-5"));
        Assert.Equal("enabled", body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(2048, body.GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
        Assert.Equal("omitted", body.GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal("high", body.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public void Gemini_legacy_sdk_maps_level_and_rejects_conflicting_budget()
    {
        var thinking = LlmThinkingOptions.AtEffort(LlmReasoningEffort.Low, includeThoughts: true);
        var sdk = Assert.IsType<Google.GenAI.Types.ThinkingConfig>(GeminiThinkingOptionsMapper.ToSdk(thinking));
        Assert.Equal(Google.GenAI.Types.ThinkingLevel.Low, sdk.ThinkingLevel);
        Assert.True(sdk.IncludeThoughts);
        Assert.Null(sdk.ThinkingBudget);
        Assert.Equal(new GeminiThinkingConfigSnapshot(null, true, "low"), GeminiThinkingOptionsMapper.Map(thinking));
        var error = Assert.Throws<LlmClientException>(() =>
            GeminiThinkingOptionsMapper.ToSdk(thinking with { ThinkingBudgetTokens = 1024 }));
        Assert.Equal("conflicting_thinking_controls", error.ErrorClass);
    }

    private static LlmRequest Request(LlmThinkingOptions? thinking) =>
        new("fixture-model", [new LlmMessage(LlmMessageRole.User, "hello")],
            new LlmGenerationOptions(Thinking: thinking));

    private static ILlmClient CreateClient(string provider, HttpClient http) => provider switch
    {
        "openai" => new OpenAiResponsesLlmClient(new OpenAiResponsesClientOptions("fixture-key"), http),
        "xai" => new XaiResponsesLlmClient(new XaiResponsesClientOptions("fixture-key"), http),
        "gemini" => new GeminiInteractionsLlmClient(new GeminiInteractionsClientOptions("fixture-key"), http),
        "ollama" => new OllamaLlmClient(httpClient: http),
        "anthropic" => new AnthropicMessagesLlmClient(new AnthropicMessagesClientOptions("fixture-key"), http),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    private sealed class CaptureHandler(string provider) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var payload = provider switch
            {
                "openai" or "xai" => """
                    event: response.completed
                    data: {"type":"response.completed","response":{"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}}


                    """,
                "gemini" => """
                    event: step.start
                    data: {"event_type":"step.start","index":0,"step":{"type":"model_output"}}

                    event: step.delta
                    data: {"event_type":"step.delta","index":0,"delta":{"type":"text","text":"ok"}}

                    event: step.stop
                    data: {"event_type":"step.stop","index":0}

                    event: interaction.completed
                    data: {"event_type":"interaction.completed","interaction":{"id":"i","status":"completed"}}


                    """,
                "ollama" => "{\"message\":{\"content\":\"ok\"},\"done\":true,\"done_reason\":\"stop\"}\n",
                "anthropic" => """
                    event: message_start
                    data: {"type":"message_start","message":{"id":"m"}}

                    event: content_block_start
                    data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"ok"}}

                    event: content_block_stop
                    data: {"type":"content_block_stop","index":0}

                    event: message_delta
                    data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

                    event: message_stop
                    data: {"type":"message_stop"}


                    """,
                _ => throw new InvalidOperationException("Unsupported fixture provider.")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8,
                    provider == "ollama" ? "application/x-ndjson" : "text/event-stream")
            };
        }
    }
}
