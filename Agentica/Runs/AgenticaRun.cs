using Agentica.Artifacts;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Runs;

public sealed class AgenticaRun
{
    private readonly List<ExecutionEvent> _events = [];

    internal object EventDeliveryGate { get; } = new();

    public AgenticaRun(string runId, RunRequest request, int attemptNumber = 1)
        : this(runId, request, attemptNumber, DateTimeOffset.UtcNow)
    {
    }

    internal AgenticaRun(
        string runId,
        RunRequest request,
        int attemptNumber,
        DateTimeOffset createdAt)
    {
        RunId = runId;
        Request = request;
        AttemptNumber = attemptNumber;
        CreatedAt = createdAt;
    }

    public string RunId { get; }

    public DateTimeOffset CreatedAt { get; }

    public int AttemptNumber { get; }

    public RunRequest Request { get; }

    public RunOutcomeStatus Status { get; set; } = RunOutcomeStatus.Created;

    public List<WorkflowPlan> PlanVersions { get; } = [];

    public List<PlanRefinement> PlanRefinements { get; } = [];

    public List<string> CompletedSteps { get; } = [];

    public List<Observation> Observations { get; } = [];

    public List<Artifact> Artifacts { get; } = [];

    public List<Receipt> Receipts { get; } = [];

    public List<ExecutionBatch> Batches { get; } = [];

    public IReadOnlyList<ExecutionEvent> Events => _events.AsReadOnly();

    internal void AddEvent(ExecutionEvent executionEvent) => _events.Add(executionEvent);

    public EventDeliveryFailure? EventDeliveryFailure { get; internal set; }

    public List<ToolSurfaceSnapshot> ToolSurfaces { get; } = [];

    public List<PlanningFrame> PlanningFrames { get; } = [];

    public Dictionary<string, string> PlanToolSurfaceIds { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> PlanToolManifestHashes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Sticky classifications for data that has entered planner-visible run state.
    /// Boundaries are never removed during an attempt.
    /// </summary>
    public HashSet<ToolDataBoundary> ExposedBoundaries { get; } = [];

    private long EventSequence { get; set; }

    public long NextEventSequence() => ++EventSequence;
}
