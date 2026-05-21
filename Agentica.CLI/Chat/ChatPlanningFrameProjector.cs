using Agentica;
using Agentica.Observations;
using Agentica.Planning;

internal sealed class ChatPlanningFrameProjector : IPlanningFrameProjector
{
    public IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["host"] = "Agentica.CLI.Chat",
            ["persona"] = request.Request.Context?.GetValueOrDefault("persona"),
            ["chat"] = request.Request.Context?.GetValueOrDefault("chat"),
            ["workspace"] = request.Request.Context?.GetValueOrDefault("workspace"),
            ["requiredFinalArtifactKind"] = ChatArtifactKinds.Response,
            ["requiredFinalTool"] = ChatToolIds.ResponseEmit,
            ["plannerUse"] =
                "This is a chat-host turn. Use the active persona for the final response. Use tools only when needed for current context, workspace facts, files, or image creation. Emit chat.response.emit when ready to answer; receipts and artifacts remain the proof boundary.",
            ["toolGuidance"] = new[]
            {
                "Use chat.context.read when the supplied context window is insufficient.",
                "Use chat.context.append_note only when the user asks to remember or preserve a note.",
                "Use chat.memory.summarize when the user asks to summarize or preserve durable conversation state; do not create summaries every turn.",
                "Use workspace.file.search before workspace.file.read when the exact file is unknown.",
                "Use workspace.image.create for image requests that should draw from recent chat, persona, style, setting, subject, or layered composition; include the saved image path in the final response.",
                "Use workspace.image.generate only when the user supplies an already-shaped final image prompt.",
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
