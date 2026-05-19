namespace Agentica.Events;

public sealed record UserFacingReasonProjectionRequest(
    string EventType,
    string? Source,
    ExecutionEventContext? Context,
    ExecutionIntent? Intent,
    IReadOnlyDictionary<string, string> Data,
    IReadOnlyDictionary<string, object?> Payload,
    ExecutionDiagnostics? Diagnostics);

public interface IUserFacingReasonProjector
{
    UserFacingReason? Project(UserFacingReasonProjectionRequest request);
}

public sealed class DefaultUserFacingReasonProjector : IUserFacingReasonProjector
{
    public static DefaultUserFacingReasonProjector Instance { get; } = new();

    private DefaultUserFacingReasonProjector()
    {
    }

    public UserFacingReason? Project(UserFacingReasonProjectionRequest request)
    {
        if (request.Diagnostics is not null)
        {
            return new UserFacingReason(
                Summary: "Something needs attention.",
                Detail: string.IsNullOrWhiteSpace(request.Diagnostics.Message)
                    ? null
                    : PublicMessage(request.Diagnostics.Message),
                Status: "attention")
            {
                Tags = ["attention", "diagnostic"]
            };
        }

        return request.EventType switch
        {
            "run.created" => Reason("Started the run.", "running", "run"),
            "request.accepted" => Reason("Accepted the request.", "running", "request"),
            "plan.creation.started" => Reason("Planning the first step.", "thinking", "planning"),
            "plan.continuation.started" => Reason("Planning the next step.", "thinking", "planning"),
            "plan.refinement.started" => Reason("Updating the plan with new information.", "thinking", "planning"),
            "plan.created" => Reason("Prepared the next step.", "ready", "planning"),
            "plan.refined" => Reason("Adjusted the next step.", "ready", "planning"),
            "batch.started" => Reason("Checking multiple pieces of context.", "checking", "context"),
            "batch.completed" => Reason("Finished checking context.", "ready", "context"),
            "step.started" => StepReason(request),
            "observation.made" => ObservationReason(request),
            "receipt.emitted" => ReceiptReason(request),
            "outcome.reported" => Reason("Prepared the run summary.", "reporting", "outcome"),
            "run.succeeded" => Reason("Completed the run.", "complete", "run"),
            "run.blocked" => Reason("The run is blocked.", "attention", "run"),
            "run.failed" => Reason("The run failed.", "attention", "run"),
            "run.stopped" => Reason("The run stopped.", "stopped", "run"),
            _ => null
        };
    }

    private static UserFacingReason StepReason(UserFacingReasonProjectionRequest request)
    {
        var kind = PayloadString(request, "kind");
        var effect = PayloadString(request, "effect");
        var toolName = PayloadString(request, "toolName") ?? request.Context?.ToolId ?? "tool";
        var isReadOnlyQuery = string.Equals(kind, "Query", StringComparison.Ordinal) &&
            string.Equals(effect, "ReadOnly", StringComparison.Ordinal);

        return new UserFacingReason(
            Summary: isReadOnlyQuery
                ? $"Checking {FriendlyToolName(toolName)}."
                : $"Using {FriendlyToolName(toolName)}.",
            Status: isReadOnlyQuery ? "checking" : "acting")
        {
            Tags = isReadOnlyQuery ? ["context", "tool"] : ["action", "tool"]
        };
    }

    private static UserFacingReason? ObservationReason(UserFacingReasonProjectionRequest request)
    {
        var summary = PayloadString(request, "summary");
        return string.IsNullOrWhiteSpace(summary)
            ? null
            : new UserFacingReason(
                Summary: PublicMessage(summary),
                Status: "observed")
            {
                Tags = ["observation"]
            };
    }

    private static UserFacingReason? ReceiptReason(UserFacingReasonProjectionRequest request)
    {
        var message = PayloadString(request, "message");
        var status = PayloadString(request, "status")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return new UserFacingReason(
            Summary: PublicMessage(message),
            Status: status)
        {
            Tags = ["receipt"]
        };
    }

    private static UserFacingReason Reason(string summary, string status, string tag) =>
        new(summary, Status: status)
        {
            Tags = [tag]
        };

    private static string? PayloadString(UserFacingReasonProjectionRequest request, string key) =>
        request.Payload.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string FriendlyToolName(string value)
    {
        var normalized = value.Contains('.', StringComparison.Ordinal)
            ? value[(value.LastIndexOf('.') + 1)..]
            : value;
        normalized = normalized.Replace('_', ' ');
        return normalized.Length == 0 ? value : normalized;
    }

    private static string PublicMessage(string value)
    {
        var compact = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 160 ? compact : compact[..157] + "...";
    }
}
