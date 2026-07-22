extern alias AgenticaLab;

using Agentica.Tools;
using LabChatConversation = AgenticaLab::ChatConversation;
using LabChatPersona = AgenticaLab::ChatPersona;
using LabChatStore = AgenticaLab::ChatStore;
using LabChatToolIds = AgenticaLab::ChatToolIds;
using LabChatTools = AgenticaLab::ChatTools;

namespace Agentica.Tests;

public sealed class ChatToolSecurityTests
{
    [Fact]
    public void Image_tools_are_quarantined_as_approval_required_external_side_effects()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "agentica-chat-security-test");
        var now = DateTimeOffset.UtcNow;
        var catalog = LabChatTools.CreateCatalog(
            new LabChatStore(Path.Combine(workspaceRoot, "chat.db")),
            new LabChatConversation("conversation_test", "Test", "plain", workspaceRoot, now, now),
            new LabChatPersona("plain", "Plain", "Be concise.", "Plain"),
            workspaceRoot);

        AssertImageTool(catalog, LabChatToolIds.WorkspaceImageCreate);
        AssertImageTool(catalog, LabChatToolIds.WorkspaceImageGenerate);
    }

    private static void AssertImageTool(ToolCatalog catalog, string toolId)
    {
        var descriptor = Assert.Single(catalog.Descriptors, descriptor => descriptor.ToolId == toolId);

        Assert.Equal(ToolKind.Action, descriptor.Kind);
        Assert.Equal(ToolEffect.ExternalSideEffect, descriptor.Effect);
        Assert.True(descriptor.RequiresApproval);
    }
}
