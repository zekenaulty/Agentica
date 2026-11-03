extern alias AgenticaLab;

using System.Net;
using System.Text;
using System.Text.Json;
using Agentica.Clients.Llm;
using Agentica.Clients.OpenAI;
using Agentica.Clients.Xai;
using LabArtistComposer = AgenticaLab::ChatArtistPromptComposer;
using LabArtistRequest = AgenticaLab::ChatArtistPromptCompositionRequest;
using LabArtistProfile = AgenticaLab::ChatArtistPromptProfile;
using LabPlannerKind = AgenticaLab::PlannerKind;
using LabConversation = AgenticaLab::ChatConversation;
using LabPersona = AgenticaLab::ChatPersona;

namespace Agentica.Tests;

public sealed class ChatArtistStructuredOutputTests
{
    [Theory]
    [InlineData("openai", null)]
    [InlineData("xai", null)]
    [InlineData("openai", "explicit-artist-model")]
    [InlineData("xai", "explicit-artist-model")]
    public async Task Artist_schema_reaches_Responses_with_host_profile_and_exact_model_override(
        string provider, string? overrideModel)
    {
        const string output = """
            {"subject":"A fox","setting":"A forest","style":"Ink","mood":"Quiet","composition":"Wide","lighting":"Dawn","layers":{"foreground":"Grass","midground":"Fox","background":"Trees"},"finalPrompt":"An ink fox in a quiet forest at dawn."}
            """;
        var handler = new StubHandler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.Equal(overrideModel ?? "configured-artist-model", root.GetProperty("model").GetString());
            var format = root.GetProperty("text").GetProperty("format");
            Assert.Equal("json_schema", format.GetProperty("type").GetString());
            Assert.False(format.GetProperty("strict").GetBoolean());
            var schema = format.GetProperty("schema");
            var properties = schema.GetProperty("properties");
            Assert.True(properties.TryGetProperty("aspectRatio", out _));
            Assert.True(properties.TryGetProperty("sourceSignals", out _));
            var required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.DoesNotContain("aspectRatio", required);
            Assert.DoesNotContain("sourceSignals", required);
            Assert.False(schema.TryGetProperty("additionalProperties", out _));
            Assert.False(properties.GetProperty("layers").TryGetProperty("additionalProperties", out _));
            return new HttpResponseMessage(HttpStatusCode.OK)
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
        });
        ILlmClient responses = provider == "xai"
            ? new XaiResponsesLlmClient(new XaiResponsesClientOptions("fixture-key",
                Endpoint: new Uri("https://example.test/responses")), new HttpClient(handler))
            : new OpenAiResponsesLlmClient(new OpenAiResponsesClientOptions("fixture-key",
                Endpoint: new Uri("https://example.test/responses")), new HttpClient(handler));
        var profile = LabArtistProfile.ForPlanner(provider == "xai" ? LabPlannerKind.Grok : LabPlannerKind.OpenAI,
            "configured-artist-model");
        var composer = new LabArtistComposer(responses, profile);
        var conversation = new LabConversation("fixture-chat", "Fixture", "plain", "fixture-workspace",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var result = await composer.ComposeAsync(new LabArtistRequest("Draw a fox", overrideModel, null, "16:9",
            conversation, new LabPersona("plain", "Plain", "Fixture instructions", "Plain"), [], []), CancellationToken.None);

        Assert.Equal(provider, result.ProviderName);
        Assert.Equal("16:9", result.Plan.AspectRatio);
        Assert.Empty(result.Plan.SourceSignals);
        Assert.Equal("An ink fox in a quiet forest at dawn.", result.Plan.FinalPrompt);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("Gemini", 0.55)]
    [InlineData("Ollama", 0.55)]
    [InlineData("OpenAI", null)]
    [InlineData("Anthropic", null)]
    [InlineData("Grok", null)]
    public async Task Host_artist_profile_preserves_supported_options_without_assuming_Gemini(
        string provider, double? temperature)
    {
        var capture = new RecordingClient();
        var profile = LabArtistProfile.ForPlanner(Enum.Parse<LabPlannerKind>(provider), "configured-model");
        var composer = new LabArtistComposer(capture, profile);
        var conversation = new LabConversation("fixture-chat", "Fixture", "plain", "fixture-workspace",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await composer.ComposeAsync(new LabArtistRequest("Draw a fox", null, null, "16:9", conversation,
            new LabPersona("plain", "Plain", "Fixture instructions", "Plain"), [], []), CancellationToken.None);
        var request = Assert.Single(capture.Requests);
        Assert.Equal("configured-model", request.ModelId);
        Assert.Equal(temperature, request.GenerationOptions!.Temperature);
        Assert.Equal(LlmJsonSchemaEnforcement.BestEffort, request.StructuredOutput!.SchemaEnforcement);
    }

    [Fact]
    public async Task Unconfigured_local_artist_requires_a_model_before_dispatch()
    {
        var capture = new RecordingClient();
        var composer = new LabArtistComposer(capture, LabArtistProfile.ForPlanner(LabPlannerKind.Ollama, null));
        Assert.Null(composer.DefaultModelId);
        var conversation = new LabConversation("fixture-chat", "Fixture", "plain", "fixture-workspace",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => composer.ComposeAsync(
            new LabArtistRequest("Draw a fox", null, null, null, conversation,
                new LabPersona("plain", "Plain", "Fixture instructions", "Plain"), [], []), CancellationToken.None));
        Assert.Empty(capture.Requests);
        await composer.ComposeAsync(new LabArtistRequest("Draw a fox", "explicit-local-model", null, null,
            conversation, new LabPersona("plain", "Plain", "Fixture instructions", "Plain"), [], []), CancellationToken.None);
        Assert.Equal("explicit-local-model", Assert.Single(capture.Requests).ModelId);
    }

    [Fact]
    public void Artist_default_models_come_from_the_selected_client_configuration()
    {
        Assert.Equal(new OpenAiResponsesClientOptions().DefaultModelId,
            LabArtistProfile.ForPlanner(LabPlannerKind.OpenAI, null).DefaultModelId);
        Assert.Equal(new XaiResponsesClientOptions().DefaultModelId,
            LabArtistProfile.ForPlanner(LabPlannerKind.Grok, null).DefaultModelId);
        Assert.Equal(new Agentica.Clients.Anthropic.AnthropicMessagesClientOptions().DefaultModelId,
            LabArtistProfile.ForPlanner(LabPlannerKind.Anthropic, null).DefaultModelId);
        Assert.Equal(LabArtistProfile.GeminiDefault, LabArtistProfile.ForPlanner(LabPlannerKind.Deterministic, null));
    }

    private sealed class RecordingClient : ILlmClient
    {
        public List<LlmRequest> Requests { get; } = [];
        public Task<LlmResponse> GenerateAsync(LlmRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new LlmResponse("fixture", request.ModelId, "{\"finalPrompt\":\"A fox.\"}",
                FinishReason: LlmFinishReason.Stop));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> factory) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return factory(request);
        }
    }
}
