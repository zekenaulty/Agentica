using Agentica.Observations;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;

namespace Agentica.Orchestration.Context;

public sealed record ProvenFact(
    string FactId,
    string Summary,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public enum PlanImpactKind
{
    NewDependencyDiscovered,
    TaskTooBroad,
    TaskNoLongerNeeded,
    AcceptanceTooWeak,
    AcceptanceTooStrict,
    ExternalBlockerDiscovered,
    HostStateChanged,
    ObjectiveNarrowed,
    ObjectiveExpanded,
    ContradictoryEvidence
}

public sealed record PlanImpact(
    PlanImpactKind Kind,
    string Summary,
    IReadOnlyList<EvidenceRef> EvidenceRefs);

public sealed record WorkContextSnapshot(
    string Objective,
    string? ActiveTaskId,
    IReadOnlyList<string> CompletedTaskIds,
    IReadOnlyList<ProvenFact> ProvenFacts,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<string> KnownBlockers,
    IReadOnlyList<PlanImpact> PlanImpacts,
    IReadOnlyList<EvidenceRef> EvidenceRefs,
    IReadOnlyDictionary<string, object?> HostStateProjection,
    DateTimeOffset UpdatedAt);

public sealed record WorkContextCompilationRequest(
    TaskGraphPlan Plan,
    OrchestrationState State,
    TaskNode? ActiveTask,
    OutcomeEnvelope? LatestOutcome,
    TaskAcceptanceResult? LatestAcceptance,
    WorkContextSnapshot? Previous,
    IReadOnlyDictionary<string, object?> HostState);

public interface IWorkContextCompiler
{
    WorkContextSnapshot Compile(WorkContextCompilationRequest request);
}
