internal static class ChatPaths
{
    public static string DefaultAppHome()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(Environment.CurrentDirectory, ".agentica");
        }

        return Path.GetFullPath(Path.Combine(localAppData, "Agentica"));
    }

    public static string NewConversationId() =>
        $"conv_{Guid.NewGuid():N}"[..17];

    public static string ConversationRoot(string appHome, string agentId, string conversationId) =>
        Path.Combine(
            Path.GetFullPath(appHome),
            "agents",
            SafeSegment(agentId),
            "conversations",
            SafeSegment(conversationId));

    public static string DatabasePath(string appHome, string agentId, string conversationId) =>
        Path.Combine(ConversationRoot(appHome, agentId, conversationId), "chat.sqlite");

    public static string WorkspaceRoot(string appHome, string agentId, string conversationId) =>
        Path.Combine(ConversationRoot(appHome, agentId, conversationId), "workspace");

    public static ChatLocatedConversation? FindConversation(string appHome, string conversationId)
    {
        var agentsRoot = Path.Combine(Path.GetFullPath(appHome), "agents");
        if (!Directory.Exists(agentsRoot))
        {
            return null;
        }

        foreach (var agentDirectory in Directory.EnumerateDirectories(agentsRoot))
        {
            var agentId = Path.GetFileName(agentDirectory);
            var conversationRoot = Path.Combine(agentDirectory, "conversations", SafeSegment(conversationId));
            var databasePath = Path.Combine(conversationRoot, "chat.sqlite");
            var conversation = TryReadConversation(databasePath, conversationId);
            if (conversation is null)
            {
                continue;
            }

            return new ChatLocatedConversation(
                Path.GetFullPath(appHome),
                agentId,
                conversationRoot,
                databasePath,
                conversation);
        }

        return null;
    }

    public static ChatLocatedConversation? FindLatestConversation(string appHome, string agentId)
    {
        var conversationsRoot = Path.Combine(
            Path.GetFullPath(appHome),
            "agents",
            SafeSegment(agentId),
            "conversations");
        if (!Directory.Exists(conversationsRoot))
        {
            return null;
        }

        ChatLocatedConversation? latest = null;
        foreach (var conversationRoot in Directory.EnumerateDirectories(conversationsRoot))
        {
            var databasePath = Path.Combine(conversationRoot, "chat.sqlite");
            var conversation = TryReadLatestConversation(databasePath);
            if (conversation is null)
            {
                continue;
            }

            if (latest is null || conversation.UpdatedAt > latest.Conversation.UpdatedAt)
            {
                latest = new ChatLocatedConversation(
                    Path.GetFullPath(appHome),
                    agentId,
                    conversationRoot,
                    databasePath,
                    conversation);
            }
        }

        return latest;
    }

    private static ChatConversation? TryReadConversation(string databasePath, string conversationId)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            return new ChatStore(databasePath).GetConversation(conversationId);
        }
        catch
        {
            return null;
        }
    }

    private static ChatConversation? TryReadLatestConversation(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return null;
        }

        try
        {
            return new ChatStore(databasePath).GetLatestConversation();
        }
        catch
        {
            return null;
        }
    }

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(
                invalid.Contains(character) ||
                character == Path.DirectorySeparatorChar ||
                character == Path.AltDirectorySeparatorChar
                    ? '_'
                    : character);
        }

        return builder.Length == 0 ? "default" : builder.ToString();
    }
}

internal sealed record ChatLocatedConversation(
    string AppHome,
    string AgentId,
    string ConversationRoot,
    string DatabasePath,
    ChatConversation Conversation);
