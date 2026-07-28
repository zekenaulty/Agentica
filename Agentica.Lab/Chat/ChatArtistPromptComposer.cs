using System.Text.Json;
using Agentica.Clients.Gemini;
using Agentica.Clients.Llm;

internal sealed class ChatArtistPromptComposer
{
    private const int RecentMessageLimit = 16;
    private const int ContextItemLimit = 12;
    private const int MaxTranscriptChars = 12000;
    private const int MaxContextChars = 4000;
    private const int MaxPromptChars = 2500;

    private readonly ILlmClient _llmClient;

    public ChatArtistPromptComposer(ILlmClient llmClient)
    {
        _llmClient = llmClient;
    }

    public async Task<ChatArtistPromptComposition> ComposeAsync(
        ChatArtistPromptCompositionRequest request,
        CancellationToken cancellationToken)
    {
        var messages = request.RecentMessages.TakeLast(RecentMessageLimit).ToArray();
        var contextItems = request.ContextItems.Take(ContextItemLimit).ToArray();
        var styleRecipe = string.IsNullOrWhiteSpace(request.StyleRecipe)
            ? DefaultStyleRecipe
            : request.StyleRecipe.Trim();

        var providerResponse = await _llmClient.GenerateAsync(
                new LlmRequest(
                    request.ComposerModelId,
                    [
                        new LlmMessage(LlmMessageRole.System, ComposerSystemPrompt),
                        new LlmMessage(
                            LlmMessageRole.User,
                            BuildUserPrompt(request, styleRecipe, messages, contextItems))
                    ],
                    new LlmGenerationOptions(
                        Temperature: 0.55,
                        MaxOutputTokens: 1800),
                    new LlmStructuredOutputOptions(JsonSchema: PromptPlanJsonSchema),
                    new Dictionary<string, string>
                    {
                        ["tool"] = ChatToolIds.WorkspaceImageCreate
                    }),
                cancellationToken)
            .ConfigureAwait(false);
        var response = ChatProviderResponseCompiler.Compile(providerResponse);

        var plan = ParsePlan(response.StructuredJson ?? response.Text);
        if (string.IsNullOrWhiteSpace(plan.FinalPrompt))
        {
            plan = plan with
            {
                FinalPrompt = BuildFallbackPrompt(request.UserRequest, styleRecipe, request.AspectRatio)
            };
        }

        plan = SnapshotPlan(plan, request.AspectRatio);

        return new ChatArtistPromptComposition(
            plan,
            response.ProviderName,
            response.ModelId,
            response.Usage,
            response.Metadata,
            response.ProviderRequestId);
    }

    private static string BuildUserPrompt(
        ChatArtistPromptCompositionRequest request,
        string styleRecipe,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ChatContextItem> contextItems) =>
        $$"""
        User image request:
        {{request.UserRequest}}

        Requested aspect ratio:
        {{(string.IsNullOrWhiteSpace(request.AspectRatio) ? "not specified" : request.AspectRatio)}}

        Active persona:
        {{request.Persona.Name}} ({{request.Persona.PersonaId}})
        {{request.Persona.Summary}}
        Response style: {{request.Persona.ResponseStyle}}

        Style recipe:
        {{styleRecipe}}

        Recent conversation:
        {{BuildTranscript(messages)}}

        Saved context:
        {{BuildContext(contextItems)}}

        Produce the final image brief JSON now.
        """;

    private static ChatArtistPromptPlan ParsePlan(string json)
    {
        try
        {
            var trimmed = StripMarkdownFence(json);
            return JsonSerializer.Deserialize<ChatArtistPromptPlan>(
                    trimmed,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new ChatArtistPromptPlan();
        }
        catch (JsonException)
        {
            return new ChatArtistPromptPlan
            {
                FinalPrompt = StripMarkdownFence(json)
            };
        }
    }

    private static string BuildTranscript(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return "(none)";
        }

        var lines = messages.Select(message =>
            $"{message.Role}: {Limit(message.Content.ReplaceLineEndings(" "), 1200)}");
        return Limit(string.Join(Environment.NewLine, lines), MaxTranscriptChars);
    }

    private static string BuildContext(IReadOnlyList<ChatContextItem> contextItems)
    {
        var relevant = contextItems
            .Where(item =>
                item.Kind.Equals("summary", StringComparison.OrdinalIgnoreCase) ||
                item.Kind.Equals("note", StringComparison.OrdinalIgnoreCase) ||
                item.Kind.Equals("image_prompt", StringComparison.OrdinalIgnoreCase))
            .Take(ContextItemLimit)
            .Select(item => $"{item.Kind}: {Limit(item.Content.ReplaceLineEndings(" "), 800)}")
            .ToArray();

        return relevant.Length == 0
            ? "(none)"
            : Limit(string.Join(Environment.NewLine, relevant), MaxContextChars);
    }

    private static string BuildFallbackPrompt(string userRequest, string styleRecipe, string? aspectRatio)
    {
        var aspect = string.IsNullOrWhiteSpace(aspectRatio)
            ? string.Empty
            : $" Compose for a {aspectRatio} frame.";
        return Limit($"{userRequest.Trim()}.{aspect} Visual style: {styleRecipe}", MaxPromptChars);
    }

    private static string StripMarkdownFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstLineEnd + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body[..closing].Trim() : body.Trim();
    }

    private static string Limit(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars];

    private static ChatArtistPromptPlan SnapshotPlan(
        ChatArtistPromptPlan plan,
        string? requestedAspectRatio)
    {
        const int maxPlanFieldChars = 2000;
        const int maxAspectRatioChars = 32;
        const int maxSourceSignals = 32;
        const int maxSourceSignalChars = 512;
        var layers = plan.Layers ?? new ChatArtistPromptLayers();
        return new ChatArtistPromptPlan
        {
            Subject = Limit(plan.Subject ?? string.Empty, maxPlanFieldChars),
            Setting = Limit(plan.Setting ?? string.Empty, maxPlanFieldChars),
            Style = Limit(plan.Style ?? string.Empty, maxPlanFieldChars),
            Mood = Limit(plan.Mood ?? string.Empty, maxPlanFieldChars),
            Composition = Limit(plan.Composition ?? string.Empty, maxPlanFieldChars),
            Lighting = Limit(plan.Lighting ?? string.Empty, maxPlanFieldChars),
            AspectRatio = Limit(
                string.IsNullOrWhiteSpace(plan.AspectRatio)
                    ? requestedAspectRatio ?? string.Empty
                    : plan.AspectRatio.Trim(),
                maxAspectRatioChars),
            Layers = new ChatArtistPromptLayers
            {
                Foreground = Limit(layers.Foreground ?? string.Empty, maxPlanFieldChars),
                Midground = Limit(layers.Midground ?? string.Empty, maxPlanFieldChars),
                Background = Limit(layers.Background ?? string.Empty, maxPlanFieldChars)
            },
            SourceSignals = Array.AsReadOnly(
                (plan.SourceSignals ?? [])
                .Take(maxSourceSignals)
                .Select(signal => Limit(signal ?? string.Empty, maxSourceSignalChars))
                .ToArray()),
            FinalPrompt = Limit(plan.FinalPrompt.Trim(), MaxPromptChars)
        };
    }

    private const string ComposerSystemPrompt =
        """
        You are Agentica's bounded image prompt artist. Convert recent chat and the user's image request into one concrete image generation brief.

        Constraints:
        - Return valid JSON only.
        - Do not call tools, roleplay, ask follow-up questions, or continue a conversation.
        - Prefer the user's explicit image request over vague recent-chat mood.
        - Capture visual subject, setting, style, layers, composition, lighting, and mood.
        - The finalPrompt must be one polished prompt, 1-4 sentences, under 2500 characters.
        - The finalPrompt must not include JSON labels, markdown, disclaimers, or the transcript.
        - Avoid copyrighted logos/characters unless they were explicitly requested.
        """;

    private const string DefaultStyleRecipe =
        """
        Ultra-sharp anime lines with impressionistic micro-textures. Volumetric lighting, HDR colors,
        sparing cinematic bloom, painterly periphery, layered background/foreground depth, and a
        tack-sharp primary focus with softened edges.
        """;

    private const string PromptPlanJsonSchema =
        """
        {
          "type": "object",
          "properties": {
            "subject": { "type": "string" },
            "setting": { "type": "string" },
            "style": { "type": "string" },
            "mood": { "type": "string" },
            "composition": { "type": "string" },
            "lighting": { "type": "string" },
            "aspectRatio": { "type": "string" },
            "layers": {
              "type": "object",
              "properties": {
                "foreground": { "type": "string" },
                "midground": { "type": "string" },
                "background": { "type": "string" }
              },
              "required": [ "foreground", "midground", "background" ]
            },
            "sourceSignals": {
              "type": "array",
              "items": { "type": "string" }
            },
            "finalPrompt": { "type": "string" }
          },
          "required": [ "subject", "setting", "style", "mood", "composition", "lighting", "layers", "finalPrompt" ]
        }
        """;
}

internal sealed record ChatArtistPromptCompositionRequest(
    string UserRequest,
    string ComposerModelId,
    string? StyleRecipe,
    string? AspectRatio,
    ChatConversation Conversation,
    ChatPersona Persona,
    IReadOnlyList<ChatMessage> RecentMessages,
    IReadOnlyList<ChatContextItem> ContextItems);

internal sealed record ChatArtistPromptComposition(
    ChatArtistPromptPlan Plan,
    string ProviderName,
    string ModelId,
    LlmUsage? Usage,
    IReadOnlyDictionary<string, string> Metadata,
    string? ProviderRequestId);

internal sealed record ChatArtistPromptPlan
{
    public string Subject { get; init; } = string.Empty;
    public string Setting { get; init; } = string.Empty;
    public string Style { get; init; } = string.Empty;
    public string Mood { get; init; } = string.Empty;
    public string Composition { get; init; } = string.Empty;
    public string Lighting { get; init; } = string.Empty;
    public string AspectRatio { get; init; } = string.Empty;
    public ChatArtistPromptLayers Layers { get; init; } = new();
    public IReadOnlyList<string> SourceSignals { get; init; } = Array.Empty<string>();
    public string FinalPrompt { get; init; } = string.Empty;
}

internal sealed record ChatArtistPromptLayers
{
    public string Foreground { get; init; } = string.Empty;
    public string Midground { get; init; } = string.Empty;
    public string Background { get; init; } = string.Empty;
}
