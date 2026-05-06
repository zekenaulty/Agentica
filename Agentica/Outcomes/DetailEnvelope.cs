using Agentica.Artifacts;
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
    IReadOnlyList<ExecutionEvent> Events,
    IReadOnlyList<ValidationIssue> ValidationIssues)
{
    public IReadOnlyList<RunAttemptSummary> RunAttempts { get; init; } = [];
}
