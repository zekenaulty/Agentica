internal static class ChatToolIds
{
    public const string ContextRead = "chat.context.read";
    public const string ContextAppendNote = "chat.context.append_note";
    public const string MemoryList = "chat.memory.list";
    public const string MemorySummarize = "chat.memory.summarize";
    public const string WorkspaceFileRead = "workspace.file.read";
    public const string WorkspaceFileSearch = "workspace.file.search";
    public const string WorkspaceImageCreate = "workspace.image.create";
    public const string WorkspaceImageGenerate = "workspace.image.generate";
    public const string ResponseEmit = "chat.response.emit";
}

internal static class ChatArtifactKinds
{
    public const string Response = "chat_response";
    public const string ContextItem = "chat_context_item";
    public const string FileRead = "workspace_file_read";
    public const string FileSearch = "workspace_file_search";
    public const string WorkspaceImage = "workspace_image";
}
