using Agentica;
using Agentica.Observations;
using Agentica.Planning;

internal sealed class ChatPlanningFrameProjector : IPlanningFrameProjector
{
    public IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["host"] = "Agentica.Lab.Chat",
            ["persona"] = request.Request.Context?.GetValueOrDefault("persona"),
            ["chat"] = request.Request.Context?.GetValueOrDefault("chat"),
            ["workspace"] = request.Request.Context?.GetValueOrDefault("workspace"),
            ["requiredFinalArtifactKind"] = ChatArtifactKinds.Response,
            ["requiredFinalTool"] = ChatToolIds.ResponseEmit,
            ["plannerUse"] =
                "This is a chat-host turn. Use the active persona for the final response. Use tools only when needed for current context, workspace facts, or files. External image generation is quarantined until scoped transmission approvals exist. Emit chat.response.emit when ready to answer; receipts and artifacts remain the proof boundary.",
            ["toolGuidance"] = new[]
            {
                "Use chat.context.read when the supplied context window is insufficient.",
                "Use chat.context.append_note only when the user asks to remember or preserve a note.",
                "Use chat.memory.summarize when the user asks to summarize or preserve durable conversation state; do not create summaries every turn.",
                "Use workspace.file.search before workspace.file.read when the exact file is unknown.",
                "Do not select workspace.image.create or workspace.image.generate; both are quarantined pending scoped external-transmission approvals. Explain the limitation when the user requests image generation.",
                "Use chat.response.emit as the final step once the answer is ready."
            }
        };

        return
        [
            new PlanningFrame(
                AgenticaIds.New("frame"),
                "agentica.chat_host",
                "1.0",
                DateTimeOffset.UtcNow,
                payload,
                Array.Empty<EvidenceRef>())
            {
                ToolSurfaceId = request.ToolSurface?.SurfaceId
            }
        ];
    }
}
