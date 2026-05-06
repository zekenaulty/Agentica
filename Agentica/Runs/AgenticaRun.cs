using Agentica.Artifacts;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;

namespace Agentica.Runs;

public sealed class AgenticaRun
{
    public AgenticaRun(string runId, RunRequest request)
    {
        RunId = runId;
        Request = request;
    }

    public string RunId { get; }

    public RunRequest Request { get; }

    public RunOutcomeStatus Status { get; set; } = RunOutcomeStatus.Created;

    public List<WorkflowPlan> PlanVersions { get; } = [];

    public List<PlanRefinement> PlanRefinements { get; } = [];

    public List<string> CompletedSteps { get; } = [];

    public List<Observation> Observations { get; } = [];

    public List<Artifact> Artifacts { get; } = [];

    public List<Receipt> Receipts { get; } = [];

    public List<ExecutionBatch> Batches { get; } = [];

    public List<ExecutionEvent> Events { get; } = [];
}
