using Agentica.Events;

namespace Agentica.Lab.Scenarios.MazeQuest;

public sealed class MazeQuestUserFacingReasonProjector : IUserFacingReasonProjector
{
    public static MazeQuestUserFacingReasonProjector Instance { get; } = new();

    private MazeQuestUserFacingReasonProjector()
    {
    }

    public UserFacingReason? Project(UserFacingReasonProjectionRequest request)
    {
        if (request.Context?.ToolId is not { } toolId)
        {
            return WithFallbackSource(DefaultUserFacingReasonProjector.Instance.Project(request));
        }

        var projected = request.EventType switch
        {
            "step.started" => StepStarted(toolId),
            "observation.made" => ObservationMade(request, toolId),
            "receipt.emitted" => ReceiptEmitted(request, toolId),
            _ => null
        };

        if (projected is not null)
        {
            return projected with { ProjectionSource = "mazequest.host" };
        }

        return WithFallbackSource(DefaultUserFacingReasonProjector.Instance.Project(request));
    }

    private static UserFacingReason? WithFallbackSource(UserFacingReason? reason) =>
        reason is null
            ? null
            : reason with { ProjectionSource = "mazequest.default_fallback" };

    private static UserFacingReason? StepStarted(string toolId) =>
        toolId switch
        {
            MazeQuestToolIds.GetState => Reason("Checking the current maze state.", "checking", "context"),
            MazeQuestToolIds.RenderMap => Reason("Refreshing the visible map.", "checking", "map"),
            MazeQuestToolIds.Scan => Reason("Scanning nearby cells.", "checking", "map"),
            MazeQuestToolIds.SenseObjective => Reason("Checking the objective direction.", "checking", "objective"),
            MazeQuestToolIds.EvaluateMoves => Reason("Checking nearby paths.", "checking", "movement"),
            MazeQuestToolIds.AnalyzeProgress => Reason("Checking recent progress.", "checking", "progress"),
            MazeQuestToolIds.EvaluateEscapeMoves => Reason("Looking for a better route.", "checking", "progress"),
            MazeQuestToolIds.Move => Reason("Moving one step.", "acting", "movement"),
            MazeQuestToolIds.MoveTo => Reason("Following a known route.", "acting", "movement"),
            MazeQuestToolIds.Take => Reason("Picking up the visible item.", "acting", "objective"),
            MazeQuestToolIds.Use => Reason("Using the visible target.", "acting", "objective"),
            MazeQuestToolIds.Rest => Reason("Recovering before moving on.", "acting", "resource"),
            MazeQuestToolIds.CompleteObjective => Reason("Checking whether the quest is complete.", "checking", "objective"),
            _ => null
        };

    private static UserFacingReason? ObservationMade(
        UserFacingReasonProjectionRequest request,
        string toolId)
    {
        var summary = PayloadString(request, "summary");
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        return toolId switch
        {
            MazeQuestToolIds.MoveTo => Reason("Moved through the known route.", "observed", "movement", summary),
            MazeQuestToolIds.Move => Reason(summary, "observed", "movement"),
            MazeQuestToolIds.Take => Reason(summary, "observed", "objective"),
            MazeQuestToolIds.Use => Reason(summary, "observed", "objective"),
            MazeQuestToolIds.EvaluateMoves => Reason("Nearby paths are updated.", "observed", "movement", summary),
            MazeQuestToolIds.SenseObjective => Reason("Objective direction is updated.", "observed", "objective", summary),
            _ => Reason(summary, "observed", "observation")
        };
    }

    private static UserFacingReason? ReceiptEmitted(
        UserFacingReasonProjectionRequest request,
        string toolId)
    {
        var message = PayloadString(request, "message");
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var status = PayloadString(request, "status")?.ToLowerInvariant();
        return toolId switch
        {
            MazeQuestToolIds.MoveTo => Reason("Known-route movement finished.", status, "movement", message),
            MazeQuestToolIds.EvaluateMoves => Reason("Nearby path check finished.", status, "movement", message),
            MazeQuestToolIds.SenseObjective => Reason("Objective check finished.", status, "objective", message),
            _ => Reason(message, status, "receipt")
        };
    }

    private static UserFacingReason Reason(
        string summary,
        string? status,
        string tag,
        string? detail = null) =>
        new(
            Summary: Compact(summary),
            Detail: string.IsNullOrWhiteSpace(detail) ? null : Compact(detail),
            Status: status)
        {
            Tags = [tag]
        };

    private static string? PayloadString(UserFacingReasonProjectionRequest request, string key) =>
        request.Payload.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string Compact(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 160 ? compact : compact[..157] + "...";
    }
}
