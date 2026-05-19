namespace Agentica.Events;

public sealed record ExecutionEventContext(
    string? RunId = null,
    int? AttemptNumber = null,
    string? PlanId = null,
    int? PlanVersion = null,
    string? StepId = null,
    string? BatchId = null,
    string? ToolId = null,
    string? ReceiptId = null,
    string? ObservationId = null,
    string? ArtifactId = null,
    string? ToolSurfaceId = null,
    string? FromPlanId = null,
    string? ToPlanId = null);
