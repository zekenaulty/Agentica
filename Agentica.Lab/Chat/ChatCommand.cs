using Agentica.Execution;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;
using System.Diagnostics;
using System.Text.Json;

internal static class ChatCommand
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CliCommandServices services)
    {
        var options = ChatOptions.Parse(args);
        if (!options.IsValid)
        {
            Console.Error.WriteLine(options.Error);
            PrintUsage();
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        if (options.ListPersonas)
        {
            PrintPersonas();
            return 0;
        }

        ChatPersona requestedPersona;
        try
        {
            requestedPersona = ChatPersonaCatalog.Resolve(options.PersonaId);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var selectedPlanner = options.Planner ?? PlannerKind.Deterministic;
        if (selectedPlanner == PlannerKind.Gemini && !services.GeminiCredentialsAvailable())
        {
            Console.Error.WriteLine("Gemini planner requested, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.");
            return 2;
        }

        ChatSession session;
        try
        {
            session = ResolveSession(options, requestedPersona);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var store = session.Store;
        var conversation = session.Conversation;
        var persona = ChatPersonaCatalog.Resolve(conversation.PersonaId);

        PrintHeader(session, persona, selectedPlanner);

        if (!string.IsNullOrWhiteSpace(options.InitialMessage))
        {
            var result = await RunTurnAsync(
                    options.InitialMessage,
                    store,
                    conversation,
                    persona,
                    options,
                    selectedPlanner,
                    services,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return result.Envelope.Outcome.Status == RunOutcomeStatus.Succeeded ? 0 : 1;
        }

        while (true)
        {
            Console.WriteLine();
            Console.Write("you> ");
            var input = Console.ReadLine();
            if (input is null)
            {
                return 0;
            }

            input = input.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (input.StartsWith("/", StringComparison.Ordinal))
            {
                var commandResult = HandleSlashCommand(input, ref session, ref store, ref conversation, ref persona);
                if (commandResult == SlashCommandResult.Exit)
                {
                    return 0;
                }

                continue;
            }

            await RunTurnAsync(
                    input,
                    store,
                    conversation,
                    persona,
                    options,
                    selectedPlanner,
                    services,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static async Task<ChatTurnResult> RunTurnAsync(
        string userInput,
        ChatStore store,
        ChatConversation conversation,
        ChatPersona persona,
        ChatOptions options,
        PlannerKind selectedPlanner,
        CliCommandServices services,
        CancellationToken cancellationToken)
    {
        var userMessage = store.AddMessage(conversation.ConversationId, "user", userInput);
        var context = BuildRequestContext(store, conversation, persona);
        var request = new RunRequest(userInput, RequestOrigin.User, context);
        var eventSink = new ChatEventSink(options.VerboseEvents);
        var planner = CreatePlanner(selectedPlanner, options, services);
        var runner = new AgenticaRunner(
            planner,
            ChatTools.CreateCatalog(store, conversation, persona, conversation.WorkspaceRoot),
            eventSink,
            new ChatOutcomeReporter(),
            policy: new ExecutionPolicy(
                MaxSteps: 8,
                MaxRefinements: 2,
                Timeout: TimeSpan.FromMinutes(5),
                PlanningMode: PlanningMode.Stepwise,
                MaxPlanContinuations: 4,
                MaxBlockedRetries: 1,
                MaxBatchSize: 4,
                MaxParallelism: 4,
                SecurityPolicy: CreateSecurityPolicy(selectedPlanner)),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind(ChatArtifactKinds.Response),
            planningFrameProjector: new ChatPlanningFrameProjector());

        var envelope = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        var assistantMessage = ChatRenderer.AssistantMessage(envelope);
        var outcomeJson = ChatRenderer.SerializeOutcome(envelope);
        var events = envelope.Details.Events.Count > 0
            ? envelope.Details.Events
            : eventSink.Events;

        store.AddRun(
            envelope.Outcome.RunId,
            conversation.ConversationId,
            userMessage.MessageId,
            userInput,
            envelope.Outcome.Status.ToString(),
            outcomeJson,
            events);
        store.AddMessage(
            conversation.ConversationId,
            "assistant",
            assistantMessage,
            $$"""{"runId":"{{envelope.Outcome.RunId}}","status":"{{envelope.Outcome.Status}}"}""");

        ChatRenderer.PrintTurn(envelope, assistantMessage);
        return new ChatTurnResult(envelope, assistantMessage);
    }

    private static ToolSecurityPolicy CreateSecurityPolicy(PlannerKind selectedPlanner)
    {
        ToolDataBoundary[] initialBoundaries =
        [
            ToolDataBoundary.UserContent,
            ToolDataBoundary.ConversationContent,
            ToolDataBoundary.HostState
        ];
        return selectedPlanner == PlannerKind.Gemini
            ? new ToolSecurityPolicy(
                InitialBoundaries: initialBoundaries,
                ExternalPlannerAllowedBoundaries:
                [
                    ToolDataBoundary.UserContent,
                    ToolDataBoundary.ConversationContent,
                    ToolDataBoundary.HostState,
                    ToolDataBoundary.ExternalUntrusted
                ])
            : new ToolSecurityPolicy(InitialBoundaries: initialBoundaries);
    }

    private static IWorkflowPlanner CreatePlanner(
        PlannerKind selectedPlanner,
        ChatOptions options,
        CliCommandServices services)
    {
        if (selectedPlanner == PlannerKind.Deterministic)
        {
            return new ChatDeterministicPlanner();
        }

        return services.CreatePlanner(new CliRunOptions(
            Objective: "chat",
            Planner: PlannerKind.Gemini,
            ModelId: options.ModelId,
            ThinkingBudget: options.ThinkingBudget,
            IncludeThoughts: options.IncludeThoughts,
            MaxOutputTokens: options.MaxOutputTokens,
            PlanningMode: PlanningMode.Stepwise,
            MaxBlockedRetries: 1,
            LogRun: false,
            LogDir: null,
            IsValid: true,
            Error: null));
    }

    private static IReadOnlyDictionary<string, object?> BuildRequestContext(
        ChatStore store,
        ChatConversation conversation,
        ChatPersona persona)
    {
        var messages = store.GetRecentMessages(conversation.ConversationId, 18)
            .Select(message => new
            {
                id = message.MessageId,
                message.Role,
                message.Content,
                at = message.CreatedAt
            })
            .ToArray();
        var contextItems = store.GetContextItems(conversation.ConversationId, 40)
            .Select(item => new
            {
                id = item.ContextItemId,
                item.Kind,
                item.Content,
                item.Source,
                at = item.CreatedAt
            })
            .ToArray();
        var recentRuns = store.GetRecentRuns(conversation.ConversationId, 5)
            .Select(run => new
            {
                id = run.RunId,
                run.Status,
                run.Objective,
                at = run.CreatedAt
            })
            .ToArray();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["persona"] = persona,
            ["chat"] = new
            {
                conversationId = conversation.ConversationId,
                conversation.Title,
                recentMessages = messages,
                contextItems,
                recentRuns,
                requiredFinalTool = ChatToolIds.ResponseEmit,
                requiredFinalArtifactKind = ChatArtifactKinds.Response
            },
            ["workspace"] = new
            {
                root = conversation.WorkspaceRoot
            },
            ["operatorContract"] =
                "Answer as a chat turn. Use tools for missing current facts. Use chat.context.append_note or chat.memory.summarize only when the user asks to preserve memory or a durable summary. External image tools are quarantined until scoped transmission approvals exist; do not select them. Explain that limitation when relevant. Do not claim a tool result unless a receipt or artifact exists. End by emitting chat.response.emit."
        };
    }

    private static ChatSession ResolveSession(
        ChatOptions options,
        ChatPersona requestedPersona)
    {
        if (!string.IsNullOrWhiteSpace(options.DatabasePathOverride))
        {
            return ResolveOverrideSession(options, requestedPersona);
        }

        var appHome = Path.GetFullPath(options.AppHome);
        var agentId = requestedPersona.PersonaId;
        Directory.CreateDirectory(appHome);

        if (!string.IsNullOrWhiteSpace(options.ConversationId))
        {
            var located = ChatPaths.FindConversation(appHome, options.ConversationId)
                ?? throw new InvalidOperationException($"Conversation '{options.ConversationId}' was not found under {appHome}.");
            var store = new ChatStore(located.DatabasePath);
            store.EnsureCreated();
            var conversation = options.PersonaExplicit
                ? store.UpdateConversationPersona(located.Conversation, requestedPersona.PersonaId)
                : located.Conversation;
            Directory.CreateDirectory(conversation.WorkspaceRoot);
            return new ChatSession(store, conversation, appHome, located.AgentId, located.ConversationRoot, UsesScopedPaths: true, ChatSessionResolution.ExplicitConversation);
        }

        if (!options.NewConversation)
        {
            var latest = ChatPaths.FindLatestConversation(appHome, agentId);
            if (latest is not null)
            {
                var store = new ChatStore(latest.DatabasePath);
                store.EnsureCreated();
                var conversation = options.PersonaExplicit
                    ? store.UpdateConversationPersona(latest.Conversation, requestedPersona.PersonaId)
                    : latest.Conversation;
                Directory.CreateDirectory(conversation.WorkspaceRoot);
                return new ChatSession(store, conversation, appHome, latest.AgentId, latest.ConversationRoot, UsesScopedPaths: true, ChatSessionResolution.ResumedLatest);
            }
        }

        return CreateScopedSession(options, requestedPersona, appHome, agentId);
    }

    private static ChatSession ResolveOverrideSession(
        ChatOptions options,
        ChatPersona requestedPersona)
    {
        var databasePath = Path.GetFullPath(options.DatabasePathOverride
            ?? throw new InvalidOperationException("Database path is required."));
        var store = new ChatStore(databasePath);
        store.EnsureCreated();

        var defaultWorkspaceRoot = Path.Combine(
            Path.GetDirectoryName(databasePath) ?? Environment.CurrentDirectory,
            "workspace");
        var workspaceRoot = options.WorkspaceRootOverride ?? defaultWorkspaceRoot;

        ChatConversation conversation;
        if (!string.IsNullOrWhiteSpace(options.ConversationId))
        {
            conversation = store.GetConversation(options.ConversationId)
                ?? throw new InvalidOperationException($"Conversation '{options.ConversationId}' was not found.");
            if (options.PersonaExplicit)
            {
                conversation = store.UpdateConversationPersona(conversation, requestedPersona.PersonaId);
            }
        }
        else if (!options.NewConversation)
        {
            var latest = options.WorkspaceRootOverride is null
                ? store.GetLatestConversation()
                : store.GetLatestConversation(workspaceRoot);
            conversation = latest is null
                ? CreateConversation(store, options, requestedPersona, workspaceRoot, conversationId: null)
                : options.PersonaExplicit
                    ? store.UpdateConversationPersona(latest, requestedPersona.PersonaId)
                    : latest;
        }
        else
        {
            conversation = CreateConversation(store, options, requestedPersona, workspaceRoot, conversationId: null);
        }

        Directory.CreateDirectory(conversation.WorkspaceRoot);
        return new ChatSession(
            store,
            conversation,
            Path.GetFullPath(options.AppHome),
            requestedPersona.PersonaId,
            Path.GetDirectoryName(databasePath) ?? Environment.CurrentDirectory,
            UsesScopedPaths: false,
            options.ConversationId is not null
                ? ChatSessionResolution.ExplicitConversation
                : options.NewConversation
                    ? ChatSessionResolution.Created
                    : ChatSessionResolution.ExplicitDatabase);
    }

    private static ChatSession CreateScopedSession(
        ChatOptions options,
        ChatPersona requestedPersona,
        string appHome,
        string agentId)
    {
        var conversationId = ChatPaths.NewConversationId();
        var conversationRoot = ChatPaths.ConversationRoot(appHome, agentId, conversationId);
        var databasePath = ChatPaths.DatabasePath(appHome, agentId, conversationId);
        var workspaceRoot = options.WorkspaceRootOverride ?? ChatPaths.WorkspaceRoot(appHome, agentId, conversationId);
        Directory.CreateDirectory(workspaceRoot);

        var store = new ChatStore(databasePath);
        store.EnsureCreated();
        var conversation = CreateConversation(store, options, requestedPersona, workspaceRoot, conversationId);
        return new ChatSession(store, conversation, appHome, agentId, conversationRoot, UsesScopedPaths: true, ChatSessionResolution.Created);
    }

    private static ChatSession ResolvePersonaSession(
        ChatSession currentSession,
        ChatPersona requestedPersona)
    {
        if (!currentSession.UsesScopedPaths)
        {
            var updatedConversation = currentSession.Store.UpdateConversationPersona(
                currentSession.Conversation,
                requestedPersona.PersonaId);
            return currentSession with
            {
                Conversation = updatedConversation,
                Resolution = ChatSessionResolution.ExplicitDatabase
            };
        }

        var appHome = Path.GetFullPath(currentSession.AppHome);
        var agentId = requestedPersona.PersonaId;
        var latest = ChatPaths.FindLatestConversation(appHome, agentId);
        if (latest is not null)
        {
            var store = new ChatStore(latest.DatabasePath);
            store.EnsureCreated();
            Directory.CreateDirectory(latest.Conversation.WorkspaceRoot);
            return new ChatSession(
                store,
                latest.Conversation,
                appHome,
                latest.AgentId,
                latest.ConversationRoot,
                UsesScopedPaths: true,
                ChatSessionResolution.ResumedLatest);
        }

        var conversationId = ChatPaths.NewConversationId();
        var conversationRoot = ChatPaths.ConversationRoot(appHome, agentId, conversationId);
        var databasePath = ChatPaths.DatabasePath(appHome, agentId, conversationId);
        var workspaceRoot = ChatPaths.WorkspaceRoot(appHome, agentId, conversationId);
        Directory.CreateDirectory(workspaceRoot);

        var newStore = new ChatStore(databasePath);
        newStore.EnsureCreated();
        var conversation = newStore.CreateConversation(
            $"Chat {DateTimeOffset.Now:yyyy-MM-dd HH:mm}",
            requestedPersona.PersonaId,
            workspaceRoot,
            conversationId);

        return new ChatSession(
            newStore,
            conversation,
            appHome,
            agentId,
            conversationRoot,
            UsesScopedPaths: true,
            ChatSessionResolution.Created);
    }

    private static ChatConversation CreateConversation(
        ChatStore store,
        ChatOptions options,
        ChatPersona requestedPersona,
        string workspaceRoot,
        string? conversationId)
    {
        var title = options.InitialMessage is null
            ? $"Chat {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"
            : CreateTitle(options.InitialMessage);
        return store.CreateConversation(title, requestedPersona.PersonaId, workspaceRoot, conversationId);
    }

    private static SlashCommandResult HandleSlashCommand(
        string input,
        ref ChatSession session,
        ref ChatStore store,
        ref ChatConversation conversation,
        ref ChatPersona persona)
    {
        if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
        {
            return SlashCommandResult.Exit;
        }

        if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            PrintInteractiveHelp();
            return SlashCommandResult.Continue;
        }

        if (input.Equals("/context", StringComparison.OrdinalIgnoreCase))
        {
            PrintContext(store, conversation);
            return SlashCommandResult.Continue;
        }

        if (input.Equals("/memory", StringComparison.OrdinalIgnoreCase))
        {
            PrintMemory(store, conversation);
            return SlashCommandResult.Continue;
        }

        if (input.Equals("/runs", StringComparison.OrdinalIgnoreCase))
        {
            PrintRuns(store, conversation);
            return SlashCommandResult.Continue;
        }

        if (input.Equals("/images", StringComparison.OrdinalIgnoreCase))
        {
            PrintImages(store, conversation);
            return SlashCommandResult.Continue;
        }

        if (input.Equals("/view", StringComparison.OrdinalIgnoreCase) ||
            input.StartsWith("/view ", StringComparison.OrdinalIgnoreCase))
        {
            OpenImage(input, store, conversation);
            return SlashCommandResult.Continue;
        }

        if (input.StartsWith("/note ", StringComparison.OrdinalIgnoreCase))
        {
            var content = input["/note ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                Console.WriteLine("note content is required.");
            }
            else
            {
                var item = store.AddContextItem(conversation.ConversationId, "note", content, "operator");
                Console.WriteLine($"saved note {item.ContextItemId}");
            }

            return SlashCommandResult.Continue;
        }

        if (input.StartsWith("/summary ", StringComparison.OrdinalIgnoreCase))
        {
            var content = input["/summary ".Length..].Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                Console.WriteLine("summary content is required.");
            }
            else
            {
                var item = store.AddContextItem(conversation.ConversationId, "summary", content, "operator");
                Console.WriteLine($"saved summary {item.ContextItemId}");
            }

            return SlashCommandResult.Continue;
        }

        if (input.Equals("/persona", StringComparison.OrdinalIgnoreCase))
        {
            PrintPersonas();
            Console.WriteLine($"active persona: {persona.PersonaId}");
            return SlashCommandResult.Continue;
        }

        if (input.StartsWith("/persona ", StringComparison.OrdinalIgnoreCase))
        {
            var personaId = input["/persona ".Length..].Trim();
            try
            {
                persona = ChatPersonaCatalog.Resolve(personaId);
                session = ResolvePersonaSession(session, persona);
                store = session.Store;
                conversation = session.Conversation;
                Console.WriteLine($"active persona: {persona.PersonaId} ({persona.Name})");
                Console.WriteLine($"conversation: {conversation.ConversationId} ({SessionResolutionText(session.Resolution)})");
                Console.WriteLine($"workspace:    {conversation.WorkspaceRoot}");
            }
            catch (InvalidOperationException exception)
            {
                Console.WriteLine(exception.Message);
            }

            return SlashCommandResult.Continue;
        }

        Console.WriteLine("unknown chat command. Use /help.");
        return SlashCommandResult.Continue;
    }

    private static void PrintHeader(
        ChatSession session,
        ChatPersona persona,
        PlannerKind planner)
    {
        var conversation = session.Conversation;
        Console.WriteLine("Agentica chat host");
        Console.WriteLine($"conversation: {conversation.ConversationId} ({conversation.Title})");
        Console.WriteLine($"persona:      {persona.PersonaId} ({persona.Name})");
        Console.WriteLine($"planner:      {planner}");
        Console.WriteLine($"session:      {SessionResolutionText(session.Resolution)}");
        Console.WriteLine($"storage:      {(session.UsesScopedPaths ? "app-home scoped" : "explicit override")}");
        Console.WriteLine($"app home:     {session.AppHome}");
        Console.WriteLine($"agent id:     {session.AgentId}");
        Console.WriteLine($"conv root:    {session.ConversationRoot}");
        Console.WriteLine($"workspace:    {conversation.WorkspaceRoot}");
        Console.WriteLine($"database:     {session.Store.DatabasePath}");
        Console.WriteLine("commands:     /help /context /memory /runs /images /view [latest|n|path] /note <text> /summary <text> /persona [id] /exit");
    }

    private static string SessionResolutionText(ChatSessionResolution resolution) =>
        resolution switch
        {
            ChatSessionResolution.ResumedLatest => "resumed latest persona conversation",
            ChatSessionResolution.ExplicitConversation => "explicit conversation",
            ChatSessionResolution.ExplicitDatabase => "resumed explicit database conversation",
            _ => "created new conversation"
        };

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  Agentica.Lab chat [message] [--planner deterministic|gemini] [--persona agentica|bookforge|mara|nanda|nyx|plain|thal] [--conversation <id>] [--new] [--app-home <path>] [--workspace <path>] [--db <path>] [--model <model-id>] [--thinking-budget dynamic|off|<tokens>] [--max-output-tokens <count>] [--include-thoughts] [--verbose-events]");
        Console.Error.WriteLine("  Agentica.Lab chat --personas");
    }

    private static void PrintInteractiveHelp()
    {
        Console.WriteLine("/context          Show recent messages and saved context items.");
        Console.WriteLine("/memory           Show saved notes, summaries, image prompts, and other context items.");
        Console.WriteLine("/runs             Show recent Agentica runs for this conversation.");
        Console.WriteLine("/images           Show generated image files for this conversation.");
        Console.WriteLine("/view [target]    Open an image. Target can be latest, a list number, or a workspace path.");
        Console.WriteLine("image requests    External generation is quarantined pending scoped transmission approvals.");
        Console.WriteLine("/note <text>      Save a durable note into the active context window.");
        Console.WriteLine("/summary <text>   Save a durable conversation summary.");
        Console.WriteLine("/persona          List personas and show the active persona.");
        Console.WriteLine("/persona <id>     Switch to that persona's latest conversation, creating one if needed.");
        Console.WriteLine("/exit             Leave chat.");
    }

    private static void PrintPersonas()
    {
        foreach (var persona in ChatPersonaCatalog.List())
        {
            var source = string.IsNullOrWhiteSpace(persona.SourcePath)
                ? string.Empty
                : $" source={persona.SourcePath}";
            Console.WriteLine($"{persona.PersonaId,-10} {persona.Name}: {persona.Summary ?? persona.ResponseStyle}{source}");
        }
    }

    private static void PrintContext(ChatStore store, ChatConversation conversation)
    {
        var messages = store.GetRecentMessages(conversation.ConversationId, 12);
        var items = store.GetContextItems(conversation.ConversationId, 20);
        Console.WriteLine("recent messages:");
        foreach (var message in messages)
        {
            Console.WriteLine($"  {message.Role,-9} {Compact(message.Content)}");
        }

        Console.WriteLine("context items:");
        foreach (var item in items)
        {
            Console.WriteLine($"  {item.Kind,-9} {Compact(item.Content)}");
        }
    }

    private static void PrintMemory(ChatStore store, ChatConversation conversation)
    {
        var items = store.GetContextItems(conversation.ConversationId, 50);
        if (items.Count == 0)
        {
            Console.WriteLine("no saved memory items for this conversation.");
            return;
        }

        foreach (var item in items)
        {
            var source = string.IsNullOrWhiteSpace(item.Source)
                ? string.Empty
                : $" source={item.Source}";
            Console.WriteLine($"{item.Kind,-12} {item.ContextItemId}{source}");
            Console.WriteLine($"  {Compact(item.Content)}");
        }
    }

    private static void PrintRuns(ChatStore store, ChatConversation conversation)
    {
        var runs = store.GetRecentRuns(conversation.ConversationId, 10);
        foreach (var run in runs)
        {
            Console.WriteLine($"{run.RunId} {run.Status,-18} {Compact(run.Objective)}");
        }
    }

    private static void PrintImages(ChatStore store, ChatConversation conversation)
    {
        var images = GetImageItems(store, conversation, 20);
        if (images.Count == 0)
        {
            Console.WriteLine("no generated images for this conversation.");
            return;
        }

        for (var index = 0; index < images.Count; index++)
        {
            var item = images[index];
            var prompt = ImagePrompt(item);
            Console.WriteLine($"{index + 1,2}. {item.Content}");
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                Console.WriteLine($"    {Compact(prompt)}");
            }
        }
    }

    private static void OpenImage(
        string input,
        ChatStore store,
        ChatConversation conversation)
    {
        var target = input.Length > "/view".Length
            ? input["/view".Length..].Trim().Trim('"')
            : "latest";
        if (string.IsNullOrWhiteSpace(target))
        {
            target = "latest";
        }

        if (!TryResolveImageTarget(target, store, conversation, out var imagePath, out var error))
        {
            Console.WriteLine(error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = imagePath,
                UseShellExecute = true
            });
            Console.WriteLine($"opened {imagePath}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"could not open image: {exception.Message}");
        }
    }

    private static bool TryResolveImageTarget(
        string target,
        ChatStore store,
        ChatConversation conversation,
        out string imagePath,
        out string error)
    {
        var images = GetImageItems(store, conversation, 50);
        if (target.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            imagePath = images.FirstOrDefault()?.Content ?? string.Empty;
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                error = "no generated images for this conversation.";
                return false;
            }
        }
        else if (int.TryParse(target, out var imageNumber))
        {
            if (imageNumber < 1 || imageNumber > images.Count)
            {
                imagePath = string.Empty;
                error = $"image number must be between 1 and {images.Count}.";
                return false;
            }

            imagePath = images[imageNumber - 1].Content;
        }
        else if (Path.IsPathRooted(target))
        {
            imagePath = Path.GetFullPath(target);
        }
        else if (ChatToolHelpers.TryResolveWorkspacePath(conversation.WorkspaceRoot, target, out var resolvedPath, out error))
        {
            imagePath = resolvedPath;
        }
        else
        {
            imagePath = string.Empty;
            return false;
        }

        if (!File.Exists(imagePath))
        {
            error = $"image file was not found: {imagePath}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static IReadOnlyList<ChatContextItem> GetImageItems(
        ChatStore store,
        ChatConversation conversation,
        int limit) =>
        store.GetContextItems(conversation.ConversationId, limit)
            .Where(item => item.Kind.Equals("image", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static string? ImagePrompt(ChatContextItem item)
    {
        if (string.IsNullOrWhiteSpace(item.MetadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(item.MetadataJson);
            return document.RootElement.TryGetProperty("prompt", out var prompt) &&
                prompt.ValueKind == JsonValueKind.String
                    ? prompt.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CreateTitle(string message)
    {
        var title = string.Join(' ', message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return title.Length <= 48 ? title : title[..45] + "...";
    }

    private static string Compact(string value)
    {
        var compact = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 120 ? compact : compact[..117] + "...";
    }

    private enum SlashCommandResult
    {
        Continue,
        Exit
    }
}
