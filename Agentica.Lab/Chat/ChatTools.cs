using System.ComponentModel;
using System.Diagnostics;
using System.Text;
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
    private const int MaxReadBytes = 256 * 1024;
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
        WorkspaceTextPrefix read;
        try
        {
            read = await WorkspaceTextResourceReader.ReadPrefixAsync(
                    resolvedPath,
                    maxChars,
                    MaxReadBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkspaceTextResourceException exception)
        {
            return Refused(
                invocation,
                exception.Message,
                exception.Code,
                exception.Reason,
                "workspace_file");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refused(
                invocation,
                "Workspace boundary refused: workspace file changed or became unreadable before the bounded read completed.");
        }

        var data = new Dictionary<string, object?>
        {
            ["path"] = resolvedPath,
            ["content"] = read.Content,
            ["truncated"] = read.Truncated,
            ["length"] = read.Content.Length,
            ["bytesRead"] = read.BytesRead,
            ["maxChars"] = maxChars,
            ["maxBytes"] = MaxReadBytes,
            ["limitReason"] = read.CharLimitReached
                ? "character_limit"
                : read.ByteLimitReached
                    ? "byte_limit"
                    : null
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
    private const int MaxPatternChars = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly IReadOnlySet<string> ExcludedDirectoryNames = new HashSet<string>(
        ["bin", "obj", ".git"],
        StringComparer.OrdinalIgnoreCase);

    private readonly WorkspacePathBoundary _workspaceBoundary;
    private readonly WorkspaceSearchProcessSpec _processSpec;
    private readonly WorkspaceSearchResourceLimits _limits;

    public WorkspaceFileSearchTool(string workspaceRoot)
        : this(
            workspaceRoot,
            WorkspaceSearchProcessSpec.Ripgrep,
            WorkspaceSearchResourceLimits.Default)
    {
    }

    internal WorkspaceFileSearchTool(
        string workspaceRoot,
        WorkspaceSearchProcessSpec processSpec,
        WorkspaceSearchResourceLimits limits)
    {
        ArgumentNullException.ThrowIfNull(processSpec);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        _workspaceBoundary = new WorkspacePathBoundary(workspaceRoot);
        _processSpec = processSpec;
        _limits = limits;
    }

    public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var pattern = ChatToolInput.String(invocation.Input, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Refused(invocation, "Search pattern is required.");
        }

        if (pattern.Length > MaxPatternChars)
        {
            return Refused(invocation, $"Search pattern exceeds the {MaxPatternChars}-character limit.");
        }

        var maxResults = ChatToolInput.Int(invocation.Input, "maxResults", 40, 1, 200);
        using var durationCancellation = new CancellationTokenSource(_limits.MaxSearchDuration);
        using var searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            durationCancellation.Token);
        try
        {
            return await ExecuteBoundedSearchAsync(
                    invocation,
                    pattern,
                    ChatToolInput.String(invocation.Input, "path"),
                    maxResults,
                    searchCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            durationCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            return Refused(
                invocation,
                "Workspace search refused: the owned search-duration limit expired.",
                "workspace.search.duration",
                "search_duration",
                "workspace_search");
        }
    }

    private async Task<ToolResult> ExecuteBoundedSearchAsync(
        ToolInvocation invocation,
        string pattern,
        string? path,
        int maxResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_workspaceBoundary.TryResolveExistingPath(path, out var searchRoot, out var error))
        {
            return Refused(invocation, error);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_workspaceBoundary.TryEnumerateFiles(
                searchRoot,
                _limits.MaxTraversalEntries,
                _limits.MaxTraversalFiles,
                ExcludedDirectoryNames,
                cancellationToken,
                out var searchFiles,
                out var traversedEntries,
                out var traversalLimitReached,
                out error))
        {
            return Refused(invocation, error);
        }

        if (traversalLimitReached)
        {
            return Refused(
                invocation,
                "Workspace search refused: bounded preflight traversal reached its entry or file limit.");
        }

        WorkspaceSearchResult search;
        try
        {
            search = await SearchAsync(
                    _workspaceBoundary,
                    searchRoot,
                    searchFiles,
                    pattern,
                    maxResults,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (WorkspaceTextResourceException exception)
        {
            return Refused(
                invocation,
                exception.Message,
                exception.Code,
                exception.Reason,
                "workspace_file");
        }
        catch (WorkspaceSearchTerminationException)
        {
            return Refused(
                invocation,
                "Workspace search refused: search process tree termination could not be confirmed.",
                "workspace.search.process_termination_unconfirmed",
                "process_termination_unconfirmed",
                "workspace_search_process");
        }

        if (search.Error is not null)
        {
            return Refused(invocation, search.Error);
        }

        var data = new Dictionary<string, object?>
        {
            ["pattern"] = pattern,
            ["path"] = searchRoot,
            ["usedFallback"] = search.UsedFallback,
            ["matches"] = search.Matches,
            ["truncated"] = search.Truncated,
            ["limitReason"] = search.LimitReason,
            ["traversedEntries"] = traversedEntries,
            ["scannedFiles"] = search.ScannedFiles,
            ["bytesRead"] = search.BytesRead,
            ["outputChars"] = search.OutputChars
        };
        var receipt = Receipt(
            invocation,
            ReceiptStatus.Succeeded,
            $"Search completed with {search.Matches.Count} result(s).",
            data);
        var observation = new Observation(
            AgenticaIds.New("observation"),
            invocation.StepId,
            ObservationKind.StateQuery,
            $"Search found {search.Matches.Count} result(s) for '{pattern}'.",
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        var artifact = new Artifact(
            AgenticaIds.New("artifact"),
            ChatArtifactKinds.FileSearch,
            data,
            [new EvidenceRef("receipt", receipt.ReceiptId)]);
        return new ToolResult(receipt, observation, artifact);
    }

    private async Task<WorkspaceSearchResult> SearchAsync(
        WorkspacePathBoundary workspaceBoundary,
        string searchRoot,
        IReadOnlyList<string> searchFiles,
        string pattern,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (!workspaceBoundary.TryResolveExistingPath(searchRoot, out var validatedSearchRoot, out var boundaryError))
        {
            return EmptySearch(usedFallback: false, boundaryError);
        }

        searchRoot = validatedSearchRoot;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _processSpec.FileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = StrictUtf8,
                StandardErrorEncoding = StrictUtf8
            };
            foreach (var argument in _processSpec.PrefixArguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            if (_processSpec.AppendRipgrepArguments)
            {
                AddRipgrepArguments(
                    process.StartInfo,
                    pattern,
                    searchRoot,
                    _limits.MaxFallbackFileBytes);
            }

            process.Start();

            var limitSignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var readerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var outputTask = ReadBoundedLinesAsync(
                process.StandardOutput,
                maxResults,
                _limits.MaxSearchOutputChars,
                _limits.MaxSearchLineChars,
                limitSignal,
                readerCancellation.Token);
            var errorTask = ReadBoundedTextAsync(
                process.StandardError,
                _limits.MaxSearchErrorChars,
                limitSignal,
                readerCancellation.Token);
            var exitTask = process.WaitForExitAsync(cancellationToken);
            string? processLimit = null;
            var processWaitCompleted = false;

            try
            {
                var completed = await Task.WhenAny(exitTask, limitSignal.Task).ConfigureAwait(false);
                if (completed == limitSignal.Task)
                {
                    processLimit = await limitSignal.Task.ConfigureAwait(false);
                    await TerminateProcessTreeAsync(process).ConfigureAwait(false);
                }
                else
                {
                    await exitTask.ConfigureAwait(false);
                }

                processWaitCompleted = true;
            }
            catch (OperationCanceledException)
            {
                await TerminateProcessTreeAsync(process).ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (!processWaitCompleted)
                {
                    await readerCancellation.CancelAsync().ConfigureAwait(false);
                    await ObserveDrainTasksAsync(
                            _limits.ProcessTerminationGrace,
                            outputTask,
                            errorTask)
                        .ConfigureAwait(false);
                }
            }

            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            if (processLimit is null && limitSignal.Task.IsCompletedSuccessfully)
            {
                processLimit = await limitSignal.Task.ConfigureAwait(false);
            }

            if (string.Equals(processLimit, "binary_content", StringComparison.Ordinal) ||
                string.Equals(processLimit, "stderr_binary_content", StringComparison.Ordinal))
            {
                throw WorkspaceTextResourceException.BinaryContent();
            }

            if (string.Equals(processLimit, "invalid_utf8", StringComparison.Ordinal) ||
                string.Equals(processLimit, "stderr_invalid_utf8", StringComparison.Ordinal))
            {
                throw WorkspaceTextResourceException.InvalidUtf8(
                    new DecoderFallbackException("Search process emitted invalid UTF-8."));
            }

            if (string.Equals(processLimit, "stderr_chars", StringComparison.Ordinal))
            {
                return await FallbackSearchAsync(
                        workspaceBoundary,
                        searchFiles,
                        pattern,
                        maxResults,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (processLimit is null && process.ExitCode is not 0 and not 1)
            {
                throw new InvalidOperationException($"ripgrep exited with code {process.ExitCode}.");
            }

            return new WorkspaceSearchResult(
                output.Lines,
                UsedFallback: false,
                Truncated: processLimit is not null,
                LimitReason: processLimit,
                ScannedFiles: 0,
                BytesRead: 0,
                OutputChars: output.OutputChars,
                Error: null);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return await FallbackSearchAsync(
                    workspaceBoundary,
                    searchFiles,
                    pattern,
                    maxResults,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<WorkspaceSearchResult> FallbackSearchAsync(
        WorkspacePathBoundary workspaceBoundary,
        IReadOnlyList<string> searchFiles,
        string pattern,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var matches = new List<string>();
        var scannedFiles = 0;
        long bytesRead = 0;
        var outputChars = 0;
        var truncated = false;
        string? limitReason = null;
        foreach (var file in searchFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxResults)
            {
                truncated = true;
                limitReason = "result_count";
                break;
            }

            var remainingBytes = _limits.MaxFallbackTotalBytes - bytesRead;
            var remainingOutputChars = _limits.MaxSearchOutputChars - outputChars;
            if (remainingBytes <= 0 || remainingOutputChars <= 0)
            {
                truncated = true;
                limitReason = remainingBytes <= 0 ? "total_bytes" : "output_chars";
                break;
            }

            if (!workspaceBoundary.TryResolveExistingFile(file, out var resolvedFile, out var error))
            {
                return new WorkspaceSearchResult(
                    matches,
                    UsedFallback: true,
                    truncated,
                    limitReason,
                    scannedFiles,
                    bytesRead,
                    outputChars,
                    error);
            }

            WorkspaceFileSearchResult fileResult;
            try
            {
                fileResult = await WorkspaceTextResourceReader.SearchFileAsync(
                        resolvedFile,
                        pattern,
                        maxResults - matches.Count,
                        remainingOutputChars,
                        _limits.MaxSearchLineChars,
                        (int)Math.Min(_limits.MaxFallbackFileBytes, remainingBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new WorkspaceSearchResult(
                    matches,
                    UsedFallback: true,
                    truncated,
                    limitReason,
                    scannedFiles,
                    bytesRead,
                    outputChars,
                    "Workspace boundary refused: workspace file changed or became unreadable during bounded search.");
            }

            scannedFiles++;
            bytesRead += fileResult.BytesRead;
            outputChars += fileResult.OutputChars;
            matches.AddRange(fileResult.Matches);
            if (fileResult.Truncated)
            {
                truncated = true;
                limitReason ??= fileResult.LimitReason;
            }
        }

        return new WorkspaceSearchResult(
            matches,
            UsedFallback: true,
            truncated,
            limitReason,
            scannedFiles,
            bytesRead,
            outputChars,
            Error: null);
    }

    private static void AddRipgrepArguments(
        ProcessStartInfo startInfo,
        string pattern,
        string searchRoot,
        int maxFileBytes)
    {
        startInfo.ArgumentList.Add("--line-number");
        startInfo.ArgumentList.Add("--column");
        startInfo.ArgumentList.Add("--hidden");
        startInfo.ArgumentList.Add("--no-follow");
        startInfo.ArgumentList.Add("--text");
        startInfo.ArgumentList.Add("--max-filesize");
        startInfo.ArgumentList.Add(maxFileBytes.ToString());
        startInfo.ArgumentList.Add("--glob");
        startInfo.ArgumentList.Add("!bin");
        startInfo.ArgumentList.Add("--glob");
        startInfo.ArgumentList.Add("!obj");
        startInfo.ArgumentList.Add("--glob");
        startInfo.ArgumentList.Add("!.git");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(pattern);
        startInfo.ArgumentList.Add(searchRoot);
    }

    private static async Task<(IReadOnlyList<string> Lines, int OutputChars)> ReadBoundedLinesAsync(
        StreamReader reader,
        int maxLines,
        int maxChars,
        int maxLineChars,
        TaskCompletionSource<string> limitSignal,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var line = new StringBuilder(Math.Min(maxLineChars, 4096));
        var buffer = new char[4096];
        var outputChars = 0;

        bool CompleteLine()
        {
            if (line.Length > 0 && line[^1] == '\r')
            {
                line.Length--;
            }

            if (line.Length > 0)
            {
                if (line.Length > maxChars - outputChars)
                {
                    limitSignal.TrySetResult("output_chars");
                    return false;
                }

                lines.Add(line.ToString());
                outputChars += line.Length;
                if (lines.Count >= maxLines)
                {
                    limitSignal.TrySetResult("result_count");
                    return false;
                }
            }

            line.Clear();
            return true;
        }

        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\0')
                    {
                        limitSignal.TrySetResult("binary_content");
                        return (lines, outputChars);
                    }

                    if (character == '\n')
                    {
                        if (!CompleteLine())
                        {
                            return (lines, outputChars);
                        }

                        continue;
                    }

                    if (line.Length >= maxLineChars)
                    {
                        limitSignal.TrySetResult("line_chars");
                        return (lines, outputChars);
                    }

                    line.Append(character);
                }
            }
        }
        catch (DecoderFallbackException)
        {
            limitSignal.TrySetResult("invalid_utf8");
            return (lines, outputChars);
        }

        if (line.Length > 0)
        {
            _ = CompleteLine();
        }

        return (lines, outputChars);
    }

    private static async Task<string> ReadBoundedTextAsync(
        StreamReader reader,
        int maxChars,
        TaskCompletionSource<string> limitSignal,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder(Math.Min(maxChars, 4096));
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (Array.IndexOf(buffer, '\0', 0, read) >= 0)
                {
                    limitSignal.TrySetResult("stderr_binary_content");
                    break;
                }

                if (read > maxChars - text.Length)
                {
                    var retained = Math.Max(0, maxChars - text.Length);
                    text.Append(buffer, 0, retained);
                    limitSignal.TrySetResult("stderr_chars");
                    break;
                }

                text.Append(buffer, 0, read);
            }
        }
        catch (DecoderFallbackException)
        {
            limitSignal.TrySetResult("stderr_invalid_utf8");
        }

        return text.ToString();
    }

    private async Task TerminateProcessTreeAsync(Process process)
    {
        if (_processSpec.TerminationOverride is not null)
        {
            try
            {
                await _processSpec.TerminationOverride(process).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                exception is not OutOfMemoryException and
                not StackOverflowException and
                not AccessViolationException)
            {
                throw new WorkspaceSearchTerminationException(
                    "Search process tree termination could not be confirmed by the process adapter.",
                    exception);
            }
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            TryKillProcessTree(process);
            throw new WorkspaceSearchTerminationException(
                "Search process tree termination could not be initiated.",
                exception);
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(_limits.ProcessTerminationGrace)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is TimeoutException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            TryKillProcessTree(process);
            throw new WorkspaceSearchTerminationException(
                "Search process tree termination could not be confirmed within the bounded grace period.",
                exception);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // This is a final best-effort re-kill. The caller reports an explicit fail-closed result.
        }
    }

    private static async Task ObserveDrainTasksAsync(TimeSpan grace, params Task[] tasks)
    {
        var aggregate = Task.WhenAll(tasks);
        try
        {
            await aggregate.WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = aggregate.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception exception) when (
            exception is IOException or
            OperationCanceledException or
            ObjectDisposedException or
            DecoderFallbackException)
        {
            // Reader cancellation and process disposal close redirected streams. The originating
            // termination failure remains the authoritative error.
        }
    }

    private static WorkspaceSearchResult EmptySearch(bool usedFallback, string error) =>
        new([], usedFallback, false, null, 0, 0, 0, error);
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
    private readonly IChatImageStagingWriter _stagingWriter;

    public WorkspaceImageCreateTool(
        ChatStore store,
        ChatConversation conversation,
        ChatPersona persona,
        string workspaceRoot,
        ChatArtistPromptComposer composer,
        IImageGenerationClient imageClient)
        : this(
            store,
            conversation,
            persona,
            workspaceRoot,
            composer,
            imageClient,
            ChatImageStagingWriter.Instance)
    {
    }

    internal WorkspaceImageCreateTool(
        ChatStore store,
        ChatConversation conversation,
        ChatPersona persona,
        string workspaceRoot,
        ChatArtistPromptComposer composer,
        IImageGenerationClient imageClient,
        IChatImageStagingWriter stagingWriter)
    {
        _store = store;
        _conversation = conversation;
        _persona = persona;
        _workspaceBoundary = new WorkspacePathBoundary(workspaceRoot);
        _workspaceRoot = _workspaceBoundary.WorkspaceRoot;
        _composer = composer;
        _imageClient = imageClient;
        _stagingWriter = stagingWriter;
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

        if (!_workspaceBoundary.TryResolveContainedPath(
                Path.Combine("images", "prompts"),
                out _,
                out error))
        {
            return Refused(invocation, error);
        }

        var effectJournal = new ChatImageEffectJournal();
        var promptFileEffects = new List<(string EffectName, string Path, string RelativePath)>();
        var promptDirectoryEffects = new List<(string EffectName, string Path, string RelativePath)>();
        string? promptContextItemId = null;
        var composerModelId = ChatImageToolSupport.EmptyToNull(ChatToolInput.String(invocation.Input, "composerModel"))
            ?? GeminiModelId.Flash25;
        var styleRecipe = ChatToolInput.String(invocation.Input, "styleRecipe");
        var recentMessages = _store.GetRecentMessages(_conversation.ConversationId, 16);
        var contextItems = _store.GetContextItems(_conversation.ConversationId, 40);

        ChatArtistPromptComposition composition;
        effectJournal.ProviderDispatchAttempted("artist_prompt_composer", composerModelId);
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
        catch (ChatProviderResponseValidationException exception)
        {
            effectJournal.ProviderResponseReceived(
                "artist_prompt_composer",
                exception.ProviderName,
                exception.ModelId,
                exception.ProviderRequestId);
            return ChatImageEffectReceipts.Failure(
                invocation,
                "Image artist prompt provider returned an invalid bounded response.",
                effectJournal);
        }
        catch (OperationCanceledException exception) when (
            ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            effectJournal.ProviderDispatchFailed("artist_prompt_composer", exception, cancelled: true);
            return ChatImageEffectReceipts.Failure(
                invocation,
                "Image artist prompt provider dispatch was cancelled; its remote outcome is indeterminate.",
                effectJournal,
                cancelled: true);
        }
        catch (Exception exception) when (ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            effectJournal.ProviderDispatchFailed("artist_prompt_composer", exception, cancelled: false);
            return ChatImageEffectReceipts.Failure(
                invocation,
                $"Image artist prompt provider dispatch failed after it was attempted: {exception.GetType().Name}.",
                effectJournal);
        }

        effectJournal.ProviderResponseReceived(
            "artist_prompt_composer",
            composition.ProviderName,
            composition.ModelId,
            composition.ProviderRequestId);

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
            promptPlanPath = await SavePromptPlanAsync(
                    promptPlanData,
                    effectJournal,
                    promptFileEffects,
                    promptDirectoryEffects,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            ChatImageToolSupport.CleanupLocalEffects(
                _store,
                promptContextItemId,
                "artist_prompt_context_item",
                promptFileEffects,
                promptDirectoryEffects,
                _workspaceBoundary,
                effectJournal);
            return ChatImageEffectReceipts.Failure(
                invocation,
                "Image prompt-plan persistence was cancelled after provider dispatch.",
                effectJournal,
                cancelled: true);
        }
        catch (Exception exception) when (ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            ChatImageToolSupport.CleanupLocalEffects(
                _store,
                promptContextItemId,
                "artist_prompt_context_item",
                promptFileEffects,
                promptDirectoryEffects,
                _workspaceBoundary,
                effectJournal);
            return ChatImageEffectReceipts.Failure(
                invocation,
                $"Image prompt-plan persistence failed after provider dispatch: {exception.GetType().Name}.",
                effectJournal);
        }

        promptPlanData["promptPlanPath"] = promptPlanPath;
        ChatContextItem promptItem;
        promptContextItemId = _store.NewContextItemId();
        effectJournal.MutationAttempted("artist_prompt_context_item", "persist artist prompt context item");
        try
        {
            promptItem = _store.AddImageContextItem(
                promptContextItemId,
                _conversation.ConversationId,
                "image_prompt",
                composition.Plan.FinalPrompt,
                ChatToolIds.WorkspaceImageCreate,
                JsonSerializer.Serialize(promptPlanData, JsonOptions.Create()));
            effectJournal.MutationCompleted(
                "artist_prompt_context_item",
                "artist prompt context item persisted");
        }
        catch (Exception exception) when (ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            effectJournal.MutationFailed(
                "artist_prompt_context_item",
                exception.GetType().Name,
                outcomeIndeterminate: true);
            ChatImageToolSupport.CleanupLocalEffects(
                _store,
                promptContextItemId,
                "artist_prompt_context_item",
                promptFileEffects,
                promptDirectoryEffects,
                _workspaceBoundary,
                effectJournal);
            return ChatImageEffectReceipts.Failure(
                invocation,
                $"Image prompt context persistence failed after provider dispatch: {exception.GetType().Name}.",
                effectJournal);
        }

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
                    _stagingWriter,
                    effectJournal,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ChatImageEffectException exception)
        {
            ChatImageToolSupport.CleanupLocalEffects(
                _store,
                promptContextItemId,
                "artist_prompt_context_item",
                promptFileEffects,
                promptDirectoryEffects,
                _workspaceBoundary,
                effectJournal);
            return ChatImageEffectReceipts.Failure(
                invocation,
                exception.Message,
                exception.Journal,
                exception.Cancelled);
        }
        catch (OperationCanceledException exception) when (
            ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            ChatImageToolSupport.CleanupLocalEffects(
                _store,
                promptContextItemId,
                "artist_prompt_context_item",
                promptFileEffects,
                promptDirectoryEffects,
                _workspaceBoundary,
                effectJournal);
            return ChatImageEffectReceipts.Failure(
                invocation,
                "Image generation was cancelled after prompt composition and local mutations.",
                effectJournal,
                cancelled: true);
        }
        catch (Exception exception) when (ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            ChatImageToolSupport.CleanupLocalEffects(
                _store,
                promptContextItemId,
                "artist_prompt_context_item",
                promptFileEffects,
                promptDirectoryEffects,
                _workspaceBoundary,
                effectJournal);
            return ChatImageEffectReceipts.Failure(
                invocation,
                $"Image creation failed after prompt composition: {exception.GetType().Name}.",
                effectJournal);
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
        ChatImageEffectJournal effectJournal,
        ICollection<(string EffectName, string Path, string RelativePath)> fileEffects,
        ICollection<(string EffectName, string Path, string RelativePath)> directoryEffects,
        CancellationToken cancellationToken)
    {
        var imagesRelativePath = "images";
        if (!ChatImageToolSupport.TryPrepareOwnedDirectory(
                _workspaceBoundary,
                imagesRelativePath,
                "artist_prompt_images_directory",
                directoryEffects,
                effectJournal,
                out _,
                out var error))
        {
            throw new InvalidOperationException(error);
        }

        var promptsRelativePath = Path.Combine("images", "prompts");
        if (!ChatImageToolSupport.TryPrepareOwnedDirectory(
                _workspaceBoundary,
                promptsRelativePath,
                "artist_prompt_directory",
                directoryEffects,
                effectJournal,
                out _,
                out error))
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

        var stagingRelativePath = Path.Combine(
            promptsRelativePath,
            $".agentica-staging-{Guid.NewGuid():N}");
        if (!ChatImageToolSupport.TryPrepareOwnedDirectory(
                _workspaceBoundary,
                stagingRelativePath,
                "artist_prompt_staging_directory",
                directoryEffects,
                effectJournal,
                out var stagingDirectory,
                out error))
        {
            throw new InvalidOperationException(error);
        }

        var stagedRelativePath = Path.Combine(stagingRelativePath, $"{baseName}.artist.json");
        if (!_workspaceBoundary.TryResolveNewFile(stagedRelativePath, out var stagedPath, out error))
        {
            throw new InvalidOperationException(error);
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(promptPlanData, JsonOptions.Create());
        const string stagedPromptPlanEffectName = "staged_artist_prompt_plan_file";
        const string promptPlanEffectName = "artist_prompt_plan_file";
        fileEffects.Add((stagedPromptPlanEffectName, stagedPath, stagedRelativePath));
        await ChatImageToolSupport.WriteStagedFileAsync(
                _workspaceBoundary,
                _stagingWriter,
                stagedPromptPlanEffectName,
                stagedPath,
                stagedRelativePath,
                content,
                effectJournal,
                cancellationToken)
            .ConfigureAwait(false);
        ChatImageToolSupport.PublishStagedFile(
            _workspaceBoundary,
            new ChatImagePublishFile(
                stagedPromptPlanEffectName,
                stagedPath,
                stagedRelativePath,
                promptPlanEffectName,
                path,
                relativePath),
            fileEffects,
            effectJournal);
        ChatImageToolSupport.RemoveStagingDirectory(
            _workspaceBoundary,
            stagingDirectory,
            stagingRelativePath,
            "artist_prompt_staging_directory",
            effectJournal);
        return path;
    }
}

internal sealed class WorkspaceImageGenerateTool : ITool
{
    private readonly ChatStore _store;
    private readonly ChatConversation _conversation;
    private readonly WorkspacePathBoundary _workspaceBoundary;
    private readonly IImageGenerationClient _imageClient;
    private readonly IChatImageStagingWriter _stagingWriter;

    public WorkspaceImageGenerateTool(
        ChatStore store,
        ChatConversation conversation,
        string workspaceRoot,
        IImageGenerationClient imageClient)
        : this(
            store,
            conversation,
            workspaceRoot,
            imageClient,
            ChatImageStagingWriter.Instance)
    {
    }

    internal WorkspaceImageGenerateTool(
        ChatStore store,
        ChatConversation conversation,
        string workspaceRoot,
        IImageGenerationClient imageClient,
        IChatImageStagingWriter stagingWriter)
    {
        _store = store;
        _conversation = conversation;
        _workspaceBoundary = new WorkspacePathBoundary(workspaceRoot);
        _imageClient = imageClient;
        _stagingWriter = stagingWriter;
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

        var effectJournal = new ChatImageEffectJournal();
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
                    _stagingWriter,
                    effectJournal,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ChatImageEffectException exception)
        {
            return ChatImageEffectReceipts.Failure(
                invocation,
                exception.Message,
                exception.Journal,
                exception.Cancelled);
        }
        catch (InvalidOperationException exception) when (!effectJournal.EffectsStarted)
        {
            return Refused(invocation, exception.Message);
        }
        catch (OperationCanceledException exception) when (
            effectJournal.EffectsStarted &&
            ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            return ChatImageEffectReceipts.Failure(
                invocation,
                "Image generation was cancelled after an effect began; final effect state is indeterminate.",
                effectJournal,
                cancelled: true);
        }
        catch (Exception exception) when (
            effectJournal.EffectsStarted && ChatImageToolSupport.IsRecoverableFailure(exception))
        {
            return ChatImageEffectReceipts.Failure(
                invocation,
                $"Image generation failed after an effect began: {exception.GetType().Name}.",
                effectJournal);
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
        => Refused(invocation, message, code: null, reason: null);

    public static ToolResult Refused(
        ToolInvocation invocation,
        string message,
        string? code,
        string? reason,
        string? resource = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["refusal"] = message
        };
        if (!string.IsNullOrWhiteSpace(code))
        {
            data["code"] = code;
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            data["reason"] = reason;
        }

        if (!string.IsNullOrWhiteSpace(resource))
        {
            data["resource"] = resource;
        }

        var receipt = Receipt(
            invocation,
            ReceiptStatus.Refused,
            message,
            data);
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
