using Agentica.Observations;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;

namespace Agentica.Orchestration;

public sealed record LargeTaskRequest(
    string Objective,
    Agentica.Requests.RequestOrigin Origin,
    IReadOnlyDictionary<string, object?> Context);

public sealed record OrchestrationPolicy(
    int MaxRuns = 16,
    int MaxRefinements = 8,
    int MaxGraphMutationsPerRefinement = 8);

public enum OrchestrationStatus
{
    Ready,
    Running,
    Succeeded,
    Blocked,
    Failed,
    Cancelled,
    PlanInvalid
}

public enum OrchestrationStopReason
{
    None,
    Complete,
    Blocked,
    Failed,
    ChildRunFailed,
    PlannerUnavailable,
    TerminalLoss,
    TerminalDraw,
    Timeout,
    Cancelled,
    PlanInvalid,
    MaxRunsReached,
    MaxRefinementsReached,
    DefinitionOfDoneNotSatisfied
}

public sealed class OrchestrationState
{
    public OrchestrationState(string orchestrationId, WorkContextSnapshot initialContext)
    {
        OrchestrationId = orchestrationId;
        WorkingContext = initialContext;
    }

    public string OrchestrationId { get; }

    public OrchestrationStatus Status { get; set; } = OrchestrationStatus.Ready;

    public OrchestrationStopReason StopReason { get; set; } = OrchestrationStopReason.None;

    public string? ActiveTaskId { get; set; }

    public List<string> CompletedTaskIds { get; } = [];

    public List<string> BlockedTaskIds { get; } = [];

    public List<string> AvailableTaskIds { get; } = [];

    public List<RunRef> RunRefs { get; } = [];

    public Dictionary<string, int> TaskRunCounts { get; } = new(StringComparer.Ordinal);

    public int RefinementCount { get; set; }

    public WorkContextSnapshot WorkingContext { get; set; }
}

public sealed record RunRef(
    string TaskId,
    string RunId,
    RunOutcomeStatus Status,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record DefinitionOfDoneResult(
    bool Satisfied,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record OrchestrationOutcomeEnvelope(
    string OrchestrationId,
    OrchestrationStatus Status,
    OrchestrationStopReason StopReason,
    string Objective,
    TaskGraphPlan? FinalPlan,
    OrchestrationState State,
    WorkContextSnapshot WorkingContext,
    IReadOnlyList<OutcomeEnvelope> RunOutcomes,
    IReadOnlyList<EvidenceRef> EvidenceRefs)
{
    public DefinitionOfDoneResult? DefinitionOfDone { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}
