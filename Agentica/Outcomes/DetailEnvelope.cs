using Agentica.Artifacts;
using Agentica.Continuity;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Validation;

namespace Agentica.Outcomes;

public sealed record DetailEnvelope(
    RunRequest Request,
    IReadOnlyList<WorkflowPlan> PlanVersions,
    IReadOnlyList<PlanRefinement> PlanRefinements,
    IReadOnlyList<Observation> Observations,
    IReadOnlyList<Artifact> Artifacts,
    IReadOnlyList<ExecutionBatch> Batches,
    IReadOnlyList<ExecutionEvent> Events,
    IReadOnlyList<ValidationIssue> ValidationIssues)
{
    public IReadOnlyList<RunAttemptSummary> RunAttempts { get; init; } = [];

    public IReadOnlyList<ToolSurfaceSnapshot> ToolSurfaces { get; init; } = [];

    public IReadOnlyList<PlanningFrame> PlanningFrames { get; init; } = [];

    public EventDeliveryFailure? EventDeliveryFailure { get; init; }

    public BreadcrumbLedger Breadcrumbs { get; init; } = new([]);

    public DivergenceLedger Divergences { get; init; } = new([]);

    public ContinuitySummary Continuity { get; init; } =
        new(0, 0, 0, 0, 0, 1, false, []);
}
