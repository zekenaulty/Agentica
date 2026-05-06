namespace Agentica.Planning;

public sealed record PlanningExecutionContext(
    IReadOnlyList<string> CompletedStepIds,
    IReadOnlyList<CompletedStepContext> CompletedSteps,
    string? CurrentPlanId,
    int PlanVersionCount)
{
    public static PlanningExecutionContext Empty { get; } =
        new([], [], CurrentPlanId: null, PlanVersionCount: 0);
}

public sealed record CompletedStepContext(
    string StepId,
    string? ToolId,
    string? PlanId,
    int? PlanVersion,
    string? ReceiptId,
    string? ReceiptStatus,
    string? ObservationId,
    string? ArtifactId);
