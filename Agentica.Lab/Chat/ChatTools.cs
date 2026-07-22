using System.Diagnostics;
using System.Text.Json;
using Agentica;
using Agentica.Artifacts;
using Agentica.Clients.Gemini;
using Agentica.Clients.Images;
using Agentica.Clients.Llm;
using Agentica.Observations;
using Agentica.Tools;
using static ChatToolHelpers;

internal static class ChatTools
{
    public static ToolCatalog CreateCatalog(
        ChatStore store,
        ChatConversation conversation,
        ChatPersona persona,
        string workspaceRoot) =>
        CreateCatalog(
            store,
            conversation,
            persona,
            workspaceRoot,
            new ChatToolDependencies(
                new GeminiLlmClient(),
                new GeminiImageGenerationClient()));

    internal static ToolCatalog CreateCatalog(
        ChatStore store,
        ChatConversation conversation,
        ChatPersona persona,
        string workspaceRoot,
        ChatToolDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        return ToolCatalog.Create(CreateRegistrations(
            store,
            conversation,
            persona,
            workspaceRoot,
            dependencies));
    }

    internal static ToolRegistration[] CreateRegistrations(
        ChatStore store,
        ChatConversation conversation,
        ChatPersona persona,
        string workspaceRoot,
        ChatToolDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        return
        [
            Registration(
                ChatToolIds.ContextRead,
                "Read Chat Context",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new ChatContextReadTool(store, conversation, persona),
                "Read recent messages, saved notes, summaries, active persona, and workspace metadata.",
                ToolInputSchema.Create(
                    new ToolInputField("focus", ToolInputValueType.String, Description: "Optional focus for the context read."),
                    new ToolInputField("maxMessages", ToolInputValueType.Integer, Description: "Recent message count.", Example: 12, Minimum: 1, Maximum: 40))),
            Registration(
                ChatToolIds.ContextAppendNote,
                "Append Context Note",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                new ChatAppendNoteTool(store, conversation),
                "Persist a concise note to this conversation's active context window when the user asks to remember something.",
                ToolInputSchema.Create(
                    new ToolInputField("content", ToolInputValueType.String, Required: true, Description: "Note content to save."),
                    new ToolInputField("kind", ToolInputValueType.String, Description: "Context item kind.", Example: "note"))),
            Registration(
                ChatToolIds.MemoryList,
                "List Chat Memory",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new ChatMemoryListTool(store, conversation),
                "List saved notes and summaries for this conversation.",
                ToolInputSchema.Create(
                    new ToolInputField("limit", ToolInputValueType.Integer, Description: "Maximum context items.", Example: 20, Minimum: 1, Maximum: 100))),
            Registration(
                ChatToolIds.MemorySummarize,
                "Save Chat Summary",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                new ChatSummarizeTool(store, conversation),
                "Persist a durable summary of recent conversation state.",
                ToolInputSchema.Create(
                    new ToolInputField("summary", ToolInputValueType.String, Required: true, Description: "Summary to save."))),
            Registration(
                ChatToolIds.WorkspaceFileRead,
                "Read Workspace File",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new WorkspaceFileReadTool(workspaceRoot),
                "Read an explicit file under the active workspace root.",
                ToolInputSchema.Create(
                    new ToolInputField("path", ToolInputValueType.String, Required: true, Description: "Relative or absolute path under the workspace root."),
                    new ToolInputField("maxChars", ToolInputValueType.Integer, Description: "Maximum characters to return.", Example: 12000, Minimum: 100, Maximum: 50000))),
            Registration(
                ChatToolIds.WorkspaceFileSearch,
                "Search Workspace Files",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new WorkspaceFileSearchTool(workspaceRoot),
                "Search text in the active workspace using ripgrep when available.",
                ToolInputSchema.Create(
                    new ToolInputField("pattern", ToolInputValueType.String, Required: true, Description: "Search pattern."),
                    new ToolInputField("path", ToolInputValueType.String, Description: "Optional relative path to search within."),
                    new ToolInputField("maxResults", ToolInputValueType.Integer, Description: "Maximum matching lines.", Example: 40, Minimum: 1, Maximum: 200))),
            Registration(
                ChatToolIds.WorkspaceImageCreate,
                "Create Workspace Image",
                ToolKind.Action,
                ToolEffect.ExternalSideEffect,
                new WorkspaceImageCreateTool(
                    store,
                    conversation,
                    persona,
                    workspaceRoot,
                    new ChatArtistPromptComposer(dependencies.PromptComposerClient),
                    dependencies.ImageGenerationClient),
                "Quarantined pending scoped external-transmission approvals. When enabled later, this tool will compose an artist brief with Gemini, generate an image, and save a durable workspace artifact.",
                ToolInputSchema.Create(
                    new ToolInputField("request", ToolInputValueType.String, Required: true, Description: "User image request or subject to visualize."),
                    new ToolInputField("styleRecipe", ToolInputValueType.String, Description: "Optional style recipe override. When omitted, the artist uses the chat host's default image style."),
                    new ToolInputField("aspectRatio", ToolInputValueType.String, Description: "Optional output aspect ratio.", AllowedValues: ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9"], Example: "1:1"),
                    new ToolInputField("imageSize", ToolInputValueType.String, Description: "Optional image size for supported Gemini image models.", AllowedValues: ["1K", "2K", "4K"], Example: "1K"),
                    new ToolInputField("outputMimeType", ToolInputValueType.String, Description: "Optional output MIME type.", AllowedValues: ["image/png", "image/jpeg", "image/webp"], Example: "image/png"),
                    new ToolInputField("outputCompressionQuality", ToolInputValueType.Integer, Description: "Optional compression quality for compressed formats.", Minimum: 1, Maximum: 100),
                    new ToolInputField("model", ToolInputValueType.String, Description: "Optional Gemini image model id.", Example: GeminiModelId.FlashImage31Preview),
                    new ToolInputField("composerModel", ToolInputValueType.String, Description: "Optional Gemini text model for the artist brief.", Example: GeminiModelId.Flash25)),
                requiresApproval: true),
            Registration(
                ChatToolIds.WorkspaceImageGenerate,
                "Generate Workspace Image",
                ToolKind.Action,
                ToolEffect.ExternalSideEffect,
                new WorkspaceImageGenerateTool(
                    store,
                    conversation,
                    workspaceRoot,
                    dependencies.ImageGenerationClient),
                "Quarantined pending scoped external-transmission approvals. When enabled later, this tool will send a shaped prompt to Gemini image generation and save a durable workspace artifact.",
                ToolInputSchema.Create(
                    new ToolInputField("prompt", ToolInputValueType.String, Required: true, Description: "Image generation prompt."),
                    new ToolInputField("aspectRatio", ToolInputValueType.String, Description: "Optional output aspect ratio.", AllowedValues: ["1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9"], Example: "1:1"),
                    new ToolInputField("imageSize", ToolInputValueType.String, Description: "Optional image size for supported Gemini image models.", AllowedValues: ["1K", "2K", "4K"], Example: "1K"),
                    new ToolInputField("outputMimeType", ToolInputValueType.String, Description: "Optional output MIME type.", AllowedValues: ["image/png", "image/jpeg", "image/webp"], Example: "image/png"),
                    new ToolInputField("outputCompressionQuality", ToolInputValueType.Integer, Description: "Optional compression quality for compressed formats.", Minimum: 1, Maximum: 100),
                    new ToolInputField("model", ToolInputValueType.String, Description: "Optional Gemini image model id.", Example: GeminiModelId.FlashImage31Preview)),
                requiresApproval: true),
            Registration(
                ChatToolIds.ResponseEmit,
                "Emit Chat Response",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                new ChatResponseEmitTool(),
                $"Emit the final assistant response for this user turn. Use this exactly once when ready. This produces the required '{ChatArtifactKinds.Response}' artifact.",
                ToolInputSchema.Create(
                    new ToolInputField("content", ToolInputValueType.String, Required: true, Description: "Final assistant response in the active persona.")))
        ];
    }

    private static ToolRegistration Registration(
        string toolId,
        string name,
        ToolKind kind,
        ToolEffect effect,
        ITool tool,
        string description,
        ToolInputSchema inputSchema,
        bool requiresApproval = false)
    {
        var retrySafety = effect switch
        {
            ToolEffect.ReadOnly => ToolRetrySafety.Idempotent,
            ToolEffect.ExternalSideEffect => ToolRetrySafety.Additive,
            _ => ToolRetrySafety.MutationUnsafe
        };
        return new ToolRegistration(
            new ToolDescriptor(
                ToolId: toolId,
                Name: name,
                Kind: kind,
                Effect: effect,
                RequiresApproval: requiresApproval,
                InputSchema: inputSchema,
                Description: description,
                RetrySafety: retrySafety),
            tool,
            SecurityFor(toolId, effect, requiresApproval, retrySafety));
    }

    private static ToolSecurityDeclaration SecurityFor(
        string toolId,
        ToolEffect effect,
        bool requiresApproval,
        ToolRetrySafety retrySafety)
    {
        var (reads, exposes, externalOutput) = toolId switch
        {
            ChatToolIds.ContextRead or ChatToolIds.MemoryList => (
                Boundaries(ToolDataBoundary.ConversationContent, ToolDataBoundary.HostState),
                Boundaries(ToolDataBoundary.ConversationContent, ToolDataBoundary.HostState),
                ToolExternalOutputClassification.None),
            ChatToolIds.ContextAppendNote or ChatToolIds.MemorySummarize or ChatToolIds.ResponseEmit => (
                Boundaries(ToolDataBoundary.UserContent, ToolDataBoundary.ConversationContent),
                Boundaries(ToolDataBoundary.ConversationContent, ToolDataBoundary.HostState),
                ToolExternalOutputClassification.None),
            ChatToolIds.WorkspaceFileRead or ChatToolIds.WorkspaceFileSearch => (
                Boundaries(ToolDataBoundary.WorkspaceContent),
                Boundaries(ToolDataBoundary.WorkspaceContent),
                ToolExternalOutputClassification.None),
            ChatToolIds.WorkspaceImageCreate => (
                Boundaries(ToolDataBoundary.UserContent, ToolDataBoundary.ConversationContent),
                Boundaries(ToolDataBoundary.ExternalUntrusted, ToolDataBoundary.HostState),
                ToolExternalOutputClassification.Mixed),
            ChatToolIds.WorkspaceImageGenerate => (
                Boundaries(ToolDataBoundary.UserContent, ToolDataBoundary.ConversationContent),
                Boundaries(ToolDataBoundary.ExternalUntrusted, ToolDataBoundary.HostState),
                ToolExternalOutputClassification.Mixed),
            _ => throw new ArgumentOutOfRangeException(nameof(toolId), toolId, "Unknown Chat tool security declaration.")
        };

        return new ToolSecurityDeclaration(
            effect,
            reads,
            exposes,
            externalOutput,
            requiresApproval ? ToolApprovalRequirement.ExplicitGrant : ToolApprovalRequirement.None,
            retrySafety,
            new ToolProvenance(ToolProvenanceKind.BuiltIn, "Agentica.Lab.Chat", "1"));
    }

    private static ToolDataBoundary[] Boundaries(params ToolDataBoundary[] boundaries) => boundaries;
}

internal sealed record ChatToolDependencies(
    ILlmClient PromptComposerClient,
    IImageGenerationClient ImageGenerationClient);

internal sealed class ChatContextReadTool : ITool
{
    private readonly ChatStore _store;
    private readonly ChatConversation _conversation;
    private readonly ChatPersona _persona;

    public ChatContextReadTool(ChatStore store, ChatConversation conversation, ChatPersona persona)
    {
        _store = store;
        _conversation = conversation;
        _persona = persona;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var maxMessages = ChatToolInput.Int(invocation.Input, "maxMessages", 16, 1, 40);
        var messages = _store.GetRecentMessages(_conversation.ConversationId, maxMessages);
        var contextItems = _store.GetContextItems(_conversation.ConversationId, 30);
        var focus = ChatToolInput.String(invocation.Input, "focus");
        var data = new Dictionary<string, object?>
        {
            ["conversationId"] = _conversation.ConversationId,
            ["title"] = _conversation.Title,
            ["focus"] = focus,
            ["persona"] = _persona,
            ["workspaceRoot"] = _conversation.WorkspaceRoot,
            ["recentMessages"] = messages.Select(ToPublicMessage).ToArray(),
            ["contextItems"] = contextItems.Select(ToPublicContextItem).ToArray()
        };
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Chat context read.", data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.StateQuery,
            $"Read {messages.Count} messages and {contextItems.Count} context items.",
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return Task.FromResult(new ToolResult(receipt, observation));
    }

    private static object ToPublicMessage(ChatMessage message) =>
        new
        {
            id = message.MessageId,
            message.Role,
            message.Content,
            at = message.CreatedAt
        };

    private static object ToPublicContextItem(ChatContextItem item) =>
        new
        {
            id = item.ContextItemId,
            item.Kind,
            item.Content,
            item.Source,
            at = item.CreatedAt
        };
}

internal sealed class ChatAppendNoteTool : ITool
{
    private readonly ChatStore _store;
    private readonly ChatConversation _conversation;

    public ChatAppendNoteTool(ChatStore store, ChatConversation conversation)
    {
        _store = store;
        _conversation = conversation;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var content = ChatToolInput.String(invocation.Input, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(Refused(invocation, "Context note content is required."));
        }

        var kind = ChatToolInput.String(invocation.Input, "kind");
        if (string.IsNullOrWhiteSpace(kind))
        {
            kind = "note";
        }

        var item = _store.AddContextItem(_conversation.ConversationId, kind, content.Trim(), "chat.context.append_note");
        var data = new Dictionary<string, object?>
        {
            ["contextItemId"] = item.ContextItemId,
            ["kind"] = item.Kind,
            ["content"] = item.Content
        };
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Context note saved.", data);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            ChatArtifactKinds.ContextItem,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return Task.FromResult(new ToolResult(receipt, Artifact: artifact));
    }
}

internal sealed class ChatMemoryListTool : ITool
{
    private readonly ChatStore _store;
    private readonly ChatConversation _conversation;

    public ChatMemoryListTool(ChatStore store, ChatConversation conversation)
    {
        _store = store;
        _conversation = conversation;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var limit = ChatToolInput.Int(invocation.Input, "limit", 30, 1, 100);
        var items = _store.GetContextItems(_conversation.ConversationId, limit);
        var data = new Dictionary<string, object?>
        {
            ["contextItems"] = items.Select(item => new
            {
                id = item.ContextItemId,
                item.Kind,
                item.Content,
                item.Source,
                at = item.CreatedAt
            }).ToArray()
        };
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Listed {items.Count} context items.", data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.StateQuery,
            $"Listed {items.Count} saved context items.",
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return Task.FromResult(new ToolResult(receipt, observation));
    }
}

internal sealed class ChatSummarizeTool : ITool
{
    private readonly ChatStore _store;
    private readonly ChatConversation _conversation;

    public ChatSummarizeTool(ChatStore store, ChatConversation conversation)
    {
        _store = store;
        _conversation = conversation;
    }

    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var summary = ChatToolInput.String(invocation.Input, "summary");
        if (string.IsNullOrWhiteSpace(summary))
        {
            return Task.FromResult(Refused(invocation, "Summary content is required."));
        }

        var item = _store.AddContextItem(_conversation.ConversationId, "summary", summary.Trim(), "chat.memory.summarize");
        var data = new Dictionary<string, object?>
        {
            ["contextItemId"] = item.ContextItemId,
            ["summary"] = item.Content
        };
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Conversation summary saved.", data);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            ChatArtifactKinds.ContextItem,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return Task.FromResult(new ToolResult(receipt, Artifact: artifact));
    }
}

internal sealed class WorkspaceFileReadTool : ITool
{
    private readonly WorkspacePathBoundary _workspaceBoundary;
    private readonly string _workspaceRoot;

    public WorkspaceFileReadTool(string workspaceRoot)
    {
        _workspaceBoundary = new WorkspacePathBoundary(workspaceRoot);
        _workspaceRoot = _workspaceBoundary.WorkspaceRoot;
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = ChatToolInput.String(invocation.Input, "path");
        if (!_workspaceBoundary.TryResolveExistingFile(path, out var resolvedPath, out var error))
        {
            return Refused(invocation, error);
        }

        var maxChars = ChatToolInput.Int(invocation.Input, "maxChars", 12000, 100, 50000);
        if (!_workspaceBoundary.TryResolveExistingFile(resolvedPath, out resolvedPath, out error))
        {
            return Refused(invocation, error);
        }

        var content = await File.ReadAllTextAsync(resolvedPath, cancellationToken).ConfigureAwait(false);
        var truncated = content.Length > maxChars;
        if (truncated)
        {
            content = content[..maxChars];
        }

        var data = new Dictionary<string, object?>
        {
            ["path"] = resolvedPath,
            ["content"] = content,
            ["truncated"] = truncated,
            ["length"] = content.Length
        };
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Read workspace file: {Relative(_workspaceRoot, resolvedPath)}", data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.StateQuery,
            $"Read file {Relative(_workspaceRoot, resolvedPath)}.",
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            ChatArtifactKinds.FileRead,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return new ToolResult(receipt, observation, artifact);
    }
}

internal sealed class WorkspaceFileSearchTool : ITool
{
    private readonly WorkspacePathBoundary _workspaceBoundary;
    private readonly string _workspaceRoot;

    public WorkspaceFileSearchTool(string workspaceRoot)
    {
        _workspaceBoundary = new WorkspacePathBoundary(workspaceRoot);
        _workspaceRoot = _workspaceBoundary.WorkspaceRoot;
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var pattern = ChatToolInput.String(invocation.Input, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Refused(invocation, "Search pattern is required.");
        }

        var path = ChatToolInput.String(invocation.Input, "path");
        if (!_workspaceBoundary.TryResolveExistingPath(path, out var searchRoot, out var error))
        {
            return Refused(invocation, error);
        }

        if (!_workspaceBoundary.TryEnumerateFiles(searchRoot, out var searchFiles, out error))
        {
            return Refused(invocation, error);
        }

        var maxResults = ChatToolInput.Int(invocation.Input, "maxResults", 40, 1, 200);
        var (matches, usedFallback, searchError) = await SearchAsync(
                _workspaceBoundary,
                searchRoot,
                searchFiles,
                pattern,
                maxResults,
                cancellationToken)
            .ConfigureAwait(false);
        if (searchError is not null)
        {
            return Refused(invocation, searchError);
        }

        var data = new Dictionary<string, object?>
        {
            ["pattern"] = pattern,
            ["path"] = searchRoot,
            ["usedFallback"] = usedFallback,
            ["matches"] = matches
        };
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, $"Search completed with {matches.Count} result(s).", data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.StateQuery,
            $"Search found {matches.Count} result(s) for '{pattern}'.",
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            ChatArtifactKinds.FileSearch,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return new ToolResult(receipt, observation, artifact);
    }

    private static async Task<(IReadOnlyList<string> Matches, bool UsedFallback, string? Error)> SearchAsync(
        WorkspacePathBoundary workspaceBoundary,
        string searchRoot,
        IReadOnlyList<string> searchFiles,
        string pattern,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (!workspaceBoundary.TryResolveExistingPath(searchRoot, out var validatedSearchRoot, out var boundaryError))
        {
            return (Array.Empty<string>(), UsedFallback: false, boundaryError);
        }

        searchRoot = validatedSearchRoot;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "rg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.StartInfo.ArgumentList.Add("--line-number");
            process.StartInfo.ArgumentList.Add("--column");
            process.StartInfo.ArgumentList.Add("--hidden");
            process.StartInfo.ArgumentList.Add("--no-follow");
            process.StartInfo.ArgumentList.Add("--glob");
            process.StartInfo.ArgumentList.Add("!bin");
            process.StartInfo.ArgumentList.Add("--glob");
            process.StartInfo.ArgumentList.Add("!obj");
            process.StartInfo.ArgumentList.Add("--glob");
            process.StartInfo.ArgumentList.Add("!.git");
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(pattern);
            process.StartInfo.ArgumentList.Add(searchRoot);
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode is not 0 and not 1)
            {
                throw new InvalidOperationException($"ripgrep exited with code {process.ExitCode}.");
            }

            var lines = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Take(maxResults)
                .ToArray();
            return (lines, UsedFallback: false, Error: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var fallback = FallbackSearch(workspaceBoundary, searchFiles, pattern, maxResults);
            return (fallback.Matches, UsedFallback: true, fallback.Error);
        }
    }

    private static (IReadOnlyList<string> Matches, string? Error) FallbackSearch(
        WorkspacePathBoundary workspaceBoundary,
        IReadOnlyList<string> searchFiles,
        string pattern,
        int maxResults)
    {
        var matches = new List<string>();
        foreach (var file in searchFiles)
        {
            if (matches.Count >= maxResults)
            {
                break;
            }

            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] lines;
            if (!workspaceBoundary.TryResolveExistingFile(file, out var resolvedFile, out var error))
            {
                return (matches, error);
            }

            try
            {
                lines = File.ReadAllLines(resolvedFile);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return (matches, "Workspace boundary refused: workspace file changed or became unreadable during search.");
            }

            for (var index = 0; index < lines.Length && matches.Count < maxResults; index++)
            {
                if (lines[index].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add($"{resolvedFile}:{index + 1}:1:{lines[index]}");
                }
            }
        }

        return (matches, Error: null);
    }
}

internal sealed class WorkspaceImageCreateTool : ITool
{
    private readonly ChatStore _store;
    private readonly ChatConversation _conversation;
    private readonly ChatPersona _persona;
    private readonly WorkspacePathBoundary _workspaceBoundary;
    private readonly string _workspaceRoot;
    private readonly ChatArtistPromptComposer _composer;
    private readonly IImageGenerationClient _imageClient;

    public WorkspaceImageCreateTool(
        ChatStore store,
        ChatConversation conversation,
        ChatPersona persona,
        string workspaceRoot,
        ChatArtistPromptComposer composer,
        IImageGenerationClient imageClient)
    {
        _store = store;
        _conversation = conversation;
        _persona = persona;
        _workspaceBoundary = new WorkspacePathBoundary(workspaceRoot);
        _workspaceRoot = _workspaceBoundary.WorkspaceRoot;
        _composer = composer;
        _imageClient = imageClient;
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var request = ChatToolInput.String(invocation.Input, "request")?.Trim();
        if (string.IsNullOrWhiteSpace(request))
        {
            request = ChatToolInput.String(invocation.Input, "prompt")?.Trim();
        }

        if (string.IsNullOrWhiteSpace(request))
        {
            request = _store
                .GetRecentMessages(_conversation.ConversationId, 8)
                .LastOrDefault(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                ?.Content
                .Trim();
        }

        if (string.IsNullOrWhiteSpace(request))
        {
            return Refused(invocation, "Image creation request is required.");
        }

        if (!ChatImageToolSupport.TryReadOptions(invocation.Input, out var imageOptions, out var error))
        {
            return Refused(invocation, error);
        }

        if (!_workspaceBoundary.TryPrepareDirectory(
                Path.Combine("images", "prompts"),
                out _,
                out error))
        {
            return Refused(invocation, error);
        }

        var composerModelId = ChatImageToolSupport.EmptyToNull(ChatToolInput.String(invocation.Input, "composerModel"))
            ?? GeminiModelId.Flash25;
        var styleRecipe = ChatToolInput.String(invocation.Input, "styleRecipe");
        var recentMessages = _store.GetRecentMessages(_conversation.ConversationId, 16);
        var contextItems = _store.GetContextItems(_conversation.ConversationId, 40);

        ChatArtistPromptComposition composition;
        try
        {
            composition = await _composer.ComposeAsync(
                    new ChatArtistPromptCompositionRequest(
                        request,
                        composerModelId,
                        styleRecipe,
                        imageOptions.AspectRatio,
                        _conversation,
                        _persona,
                        recentMessages,
                        contextItems),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlmClientException exception)
        {
            return Refused(invocation, $"Image artist prompt composition failed: {exception.Message}");
        }

        var promptPlanData = new Dictionary<string, object?>
        {
            ["sourceRequest"] = request,
            ["styleRecipe"] = string.IsNullOrWhiteSpace(styleRecipe) ? null : styleRecipe.Trim(),
            ["aspectRatio"] = imageOptions.AspectRatio,
            ["workspaceRoot"] = _workspaceRoot,
            ["conversationId"] = _conversation.ConversationId,
            ["personaId"] = _persona.PersonaId,
            ["composerProvider"] = composition.ProviderName,
            ["composerModel"] = composition.ModelId,
            ["composerUsage"] = composition.Usage,
            ["composerMetadata"] = composition.Metadata,
            ["plan"] = composition.Plan,
            ["finalPrompt"] = composition.Plan.FinalPrompt
        };

        string promptPlanPath;
        try
        {
            promptPlanPath = await SavePromptPlanAsync(promptPlanData, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return Refused(invocation, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refused(invocation, "Workspace boundary refused: prompt plan could not be written safely.");
        }

        promptPlanData["promptPlanPath"] = promptPlanPath;
        var promptItem = _store.AddContextItem(
            _conversation.ConversationId,
            "image_prompt",
            composition.Plan.FinalPrompt,
            ChatToolIds.WorkspaceImageCreate,
            JsonSerializer.Serialize(promptPlanData, JsonOptions.Create()));

        ChatSavedWorkspaceImages saved;
        try
        {
            saved = await ChatImageToolSupport.GenerateAndSaveAsync(
                    _store,
                    _conversation,
                    _workspaceBoundary,
                    _imageClient,
                    composition.Plan.FinalPrompt,
                    imageOptions,
                    ChatToolIds.WorkspaceImageCreate,
                    new Dictionary<string, object?>
                    {
                        ["sourceRequest"] = request,
                        ["artistPromptContextItemId"] = promptItem.ContextItemId,
                        ["artistPromptPlanPath"] = promptPlanPath,
                        ["artistBrief"] = composition.Plan,
                        ["artistComposer"] = new Dictionary<string, object?>
                        {
                            ["provider"] = composition.ProviderName,
                            ["model"] = composition.ModelId,
                            ["usage"] = composition.Usage,
                            ["metadata"] = composition.Metadata
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlmClientException exception)
        {
            return Refused(invocation, $"Image prompt composed, but generation failed: {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return Refused(invocation, $"Image prompt composed, but generation failed: {exception.Message}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refused(invocation, "Image prompt composed, but workspace output could not be written safely.");
        }

        var receipt = Receipt(
            invocation,
            ReceiptStatus.Succeeded,
            $"Composed artist prompt and generated {saved.ImageCount} image(s). First image: {saved.FirstPath}",
            saved.Data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.ToolResult,
            $"Composed artist prompt and generated {saved.ImageCount} workspace image(s).",
            saved.Data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            ChatArtifactKinds.WorkspaceImage,
            saved.Data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return new ToolResult(receipt, observation, artifact);
    }

    private async Task<string> SavePromptPlanAsync(
        IReadOnlyDictionary<string, object?> promptPlanData,
        CancellationToken cancellationToken)
    {
        if (!_workspaceBoundary.TryPrepareDirectory(
                Path.Combine("images", "prompts"),
                out _,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        var createdAt = DateTimeOffset.UtcNow;
        var baseName = $"{createdAt:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24];
        var relativePath = Path.Combine("images", "prompts", $"{baseName}.artist.json");
        if (!_workspaceBoundary.TryResolveNewFile(relativePath, out var path, out error))
        {
            throw new InvalidOperationException(error);
        }

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(
                JsonSerializer.Serialize(promptPlanData, JsonOptions.Create()).AsMemory(),
                cancellationToken)
            .ConfigureAwait(false);
        return path;
    }
}

internal sealed class WorkspaceImageGenerateTool : ITool
{
    private readonly ChatStore _store;
    private readonly ChatConversation _conversation;
    private readonly WorkspacePathBoundary _workspaceBoundary;
    private readonly IImageGenerationClient _imageClient;

    public WorkspaceImageGenerateTool(
        ChatStore store,
        ChatConversation conversation,
        string workspaceRoot,
        IImageGenerationClient imageClient)
    {
        _store = store;
        _conversation = conversation;
        _workspaceBoundary = new WorkspacePathBoundary(workspaceRoot);
        _imageClient = imageClient;
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var prompt = ChatToolInput.String(invocation.Input, "prompt")?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Refused(invocation, "Image prompt is required.");
        }

        if (!ChatImageToolSupport.TryReadOptions(invocation.Input, out var imageOptions, out var error))
        {
            return Refused(invocation, error);
        }

        ChatSavedWorkspaceImages saved;
        try
        {
            saved = await ChatImageToolSupport.GenerateAndSaveAsync(
                    _store,
                    _conversation,
                    _workspaceBoundary,
                    _imageClient,
                    prompt,
                    imageOptions,
                    ChatToolIds.WorkspaceImageGenerate,
                    additionalData: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlmClientException exception)
        {
            return Refused(invocation, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Refused(invocation, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refused(invocation, "Workspace boundary refused: image output could not be written safely.");
        }

        var receipt = Receipt(
            invocation,
            ReceiptStatus.Succeeded,
            $"Generated {saved.ImageCount} image(s). First image: {saved.FirstPath}",
            saved.Data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.ToolResult,
            $"Generated {saved.ImageCount} workspace image(s).",
            saved.Data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            ChatArtifactKinds.WorkspaceImage,
            saved.Data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return new ToolResult(receipt, observation, artifact);
    }
}

internal sealed class ChatResponseEmitTool : ITool
{
    public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var content = ChatToolInput.String(invocation.Input, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(Refused(invocation, "Response content is required."));
        }

        var data = new Dictionary<string, object?>
        {
            ["content"] = content.Trim()
        };
        var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Chat response emitted.", data);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            ChatArtifactKinds.Response,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return Task.FromResult(new ToolResult(receipt, Artifact: artifact));
    }
}

internal static class ChatToolHelpers
{
    public static Receipt Receipt(
        ToolInvocation invocation,
        ReceiptStatus status,
        string message,
        IReadOnlyDictionary<string, object?> data) =>
        new(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            status,
            message,
            DateTimeOffset.UtcNow,
            data);

    public static ToolResult Refused(ToolInvocation invocation, string message)
    {
        var receipt = Receipt(
            invocation,
            ReceiptStatus.Refused,
            message,
            new Dictionary<string, object?>
            {
                ["refusal"] = message
            });
        return new ToolResult(receipt);
    }

    public static bool TryResolveWorkspacePath(
        string workspaceRoot,
        string? path,
        out string resolvedPath,
        out string error)
    {
        try
        {
            return new WorkspacePathBoundary(workspaceRoot)
                .TryResolveContainedPath(path, out resolvedPath, out error);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            resolvedPath = string.Empty;
            error = "Workspace boundary refused: invalid workspace root.";
            return false;
        }
    }

    public static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path);
}
