using Agentica.Outcomes;
using Agentica.Clients.Llm;

internal sealed record ChatOptions(
    string? InitialMessage,
    PlannerKind? Planner,
    string? ModelId,
    string? ThinkingBudget,
    bool IncludeThoughts,
    int? MaxOutputTokens,
    string AppHome,
    string? WorkspaceRootOverride,
    string? DatabasePathOverride,
    string? ConversationId,
    bool NewConversation,
    string? PersonaId,
    bool PersonaExplicit,
    bool VerboseEvents,
    bool ShowHelp,
    bool ListPersonas,
    bool IsValid,
    string? Error)
{
    public int? MaxInputCharacters { get; init; }

    public int? ContextWindowTokens { get; init; }

    public bool StreamEventsJsonl { get; init; }

    public LlmReasoningEffort? ReasoningEffort { get; init; }

    public static ChatOptions Parse(IReadOnlyList<string> args)
    {
        var messageParts = new List<string>();
        PlannerKind? planner = null;
        string? modelId = null;
        string? thinkingBudget = null;
        LlmReasoningEffort? reasoningEffort = null;
        var includeThoughts = false;
        int? maxOutputTokens = null;
        int? maxInputCharacters = null;
        int? contextWindowTokens = null;
        var streamEventsJsonl = false;
        var appHome = ChatPaths.DefaultAppHome();
        string? workspaceRootOverride = null;
        string? databasePathOverride = null;
        string? conversationId = null;
        var newConversation = false;
        string? personaId = null;
        var personaExplicit = false;
        var verboseEvents = false;
        var showHelp = false;
        var listPersonas = false;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                messageParts.Add(arg);
                continue;
            }

            switch (arg)
            {
                case "--help":
                case "-h":
                    showHelp = true;
                    break;

                case "--personas":
                    listPersonas = true;
                    break;

                case "--planner":
                    if (!TryReadValue(args, ref index, out var plannerValue))
                    {
                        return Invalid("Missing value for --planner.");
                    }

                    if (!Enum.TryParse<PlannerKind>(plannerValue, ignoreCase: true, out var parsedPlanner))
                    {
                        return Invalid($"Unknown planner '{plannerValue}'.");
                    }

                    planner = parsedPlanner;
                    break;

                case "--model":
                    if (!TryReadValue(args, ref index, out modelId))
                    {
                        return Invalid("Missing value for --model.");
                    }

                    break;

                case "--thinking-budget":
                    if (!TryReadValue(args, ref index, out thinkingBudget))
                    {
                        return Invalid("Missing value for --thinking-budget.");
                    }

                    thinkingBudget = thinkingBudget.ToLowerInvariant();
                    if (thinkingBudget is not "dynamic" and not "off" &&
                        (!int.TryParse(thinkingBudget, out var tokens) || tokens < 0))
                    {
                        return Invalid($"Invalid thinking budget '{thinkingBudget}'.");
                    }

                    break;

                case "--reasoning-effort":
                    if (!TryReadValue(args, ref index, out var reasoningValue) ||
                        !CliParsing.TryParseReasoningEffort(reasoningValue, out var parsedEffort))
                    {
                        return Invalid("Missing or invalid value for --reasoning-effort (none|minimal|low|medium|high|xhigh|max).");
                    }
                    reasoningEffort = parsedEffort;
                    break;

                case "--max-output-tokens":
                    if (!TryReadValue(args, ref index, out var maxOutputTokensValue) ||
                        !int.TryParse(maxOutputTokensValue, out var parsedMaxOutputTokens) ||
                        parsedMaxOutputTokens <= 0)
                    {
                        return Invalid("Missing or invalid value for --max-output-tokens.");
                    }

                    maxOutputTokens = parsedMaxOutputTokens;
                    break;

                case "--max-input-characters":
                    if (!TryReadValue(args, ref index, out var maxInputValue) ||
                        !int.TryParse(maxInputValue, out var parsedMaxInput) ||
                        parsedMaxInput < 8192)
                    {
                        return Invalid("Missing or invalid value for --max-input-characters (minimum 8192).");
                    }

                    maxInputCharacters = parsedMaxInput;
                    break;

                case "--context-window-tokens":
                    if (!TryReadValue(args, ref index, out var contextWindowValue) ||
                        !int.TryParse(contextWindowValue, out var parsedWindow) ||
                        parsedWindow <= 0)
                    {
                        return Invalid("Missing or invalid value for --context-window-tokens.");
                    }

                    contextWindowTokens = parsedWindow;
                    break;

                case "--include-thoughts":
                    includeThoughts = true;
                    break;

                case "--stream-events-jsonl":
                    streamEventsJsonl = true;
                    break;

                case "--app-home":
                    if (!TryReadValue(args, ref index, out var appHomeValue))
                    {
                        return Invalid("Missing value for --app-home.");
                    }

                    appHome = Path.GetFullPath(appHomeValue);
                    break;

                case "--workspace":
                    if (!TryReadValue(args, ref index, out var workspaceValue))
                    {
                        return Invalid("Missing value for --workspace.");
                    }

                    workspaceRootOverride = Path.GetFullPath(workspaceValue);
                    break;

                case "--db":
                    if (!TryReadValue(args, ref index, out databasePathOverride))
                    {
                        return Invalid("Missing value for --db.");
                    }

                    databasePathOverride = Path.GetFullPath(databasePathOverride);
                    break;

                case "--conversation":
                    if (!TryReadValue(args, ref index, out conversationId))
                    {
                        return Invalid("Missing value for --conversation.");
                    }

                    break;

                case "--new":
                    newConversation = true;
                    break;

                case "--persona":
                    if (!TryReadValue(args, ref index, out personaId))
                    {
                        return Invalid("Missing value for --persona.");
                    }

                    personaExplicit = true;
                    break;

                case "--verbose-events":
                    verboseEvents = true;
                    break;

                default:
                    return Invalid($"Unknown option '{arg}'.");
            }
        }

        var initialMessage = string.Join(' ', messageParts).Trim();
        if (reasoningEffort is not null && thinkingBudget is not null)
            return Invalid("Choose either --reasoning-effort or --thinking-budget.");
        if (contextWindowTokens is { } window &&
            window <= (long)(maxOutputTokens ?? 12_288) + 4096)
            return Invalid("Context window must exceed output, tool-result, and safety reserves.");

        return new ChatOptions(
            string.IsNullOrWhiteSpace(initialMessage) ? null : initialMessage,
            planner,
            modelId,
            thinkingBudget,
            includeThoughts,
            maxOutputTokens,
            Path.GetFullPath(appHome),
            workspaceRootOverride,
            databasePathOverride,
            conversationId,
            newConversation,
            personaId,
            personaExplicit,
            verboseEvents,
            showHelp,
            listPersonas,
            IsValid: true,
            Error: null)
        {
            MaxInputCharacters = maxInputCharacters,
            ContextWindowTokens = contextWindowTokens,
            StreamEventsJsonl = streamEventsJsonl,
            ReasoningEffort = reasoningEffort
        };
    }

    private static bool TryReadValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static ChatOptions Invalid(string error) =>
        new(null, null, null, null, false, null, ChatPaths.DefaultAppHome(), null, null, null, false, null, false, false, false, false, false, error);
}

internal sealed record ChatSession(
    ChatStore Store,
    ChatConversation Conversation,
    string AppHome,
    string AgentId,
    string ConversationRoot,
    bool UsesScopedPaths,
    ChatSessionResolution Resolution);

internal enum ChatSessionResolution
{
    Created,
    ResumedLatest,
    ExplicitConversation,
    ExplicitDatabase
}

internal sealed record ChatConversation(
    string ConversationId,
    string Title,
    string PersonaId,
    string WorkspaceRoot,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

internal sealed record ChatMessage(
    string MessageId,
    string ConversationId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    string? MetadataJson);

internal sealed record ChatContextItem(
    string ContextItemId,
    string ConversationId,
    string Kind,
    string Content,
    string? Source,
    DateTimeOffset CreatedAt,
    string? MetadataJson);

internal sealed record ChatRunRecord(
    string RunId,
    string ConversationId,
    string MessageId,
    string Objective,
    string Status,
    DateTimeOffset CreatedAt,
    string OutcomeJson);

internal sealed record ChatTurnResult(
    OutcomeEnvelope Envelope,
    string AssistantMessage);

internal sealed record ChatPersona(
    string PersonaId,
    string Name,
    string SystemMessage,
    string ResponseStyle,
    string? SourcePath = null,
    string? Summary = null);
