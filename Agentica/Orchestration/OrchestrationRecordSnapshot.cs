using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Continuity;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Validation;

namespace Agentica.Orchestration;

/// <summary>
/// Creates bounded, detached records at every orchestration extension boundary.
/// The mutable orchestration state machine remains private to <see cref="TaskOrchestrator"/>.
/// </summary>
internal static class OrchestrationRecordSnapshot
{
    private const int MaxDepth = 32;
    private const int MaxItems = 16_384;
    private const int MaxNodes = 16_384;
    private const int MaxBytes = 1_048_576;
    private const int MaxChildOutcomeNodes = MaxNodes * 16;
    private const int MaxChildOutcomeBytes = MaxBytes * 16;
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> SnapshotProperties = new();

    public static LargeTaskRequest Request(LargeTaskRequest source) =>
        Request(source, new SnapshotBudget(), 0);

    private static LargeTaskRequest Request(
        LargeTaskRequest source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Context);
        budget.Visit(depth);
        return new LargeTaskRequest(
            budget.Text(source.Objective, "large-task objective"),
            source.Origin,
            Structured(source.Context, budget, depth + 1));
    }

    public static TaskGraphPlan Plan(TaskGraphPlan source) =>
        Plan(source, new SnapshotBudget(), 0);

    private static TaskGraphPlan Plan(
        TaskGraphPlan source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        return new TaskGraphPlan(
            budget.Text(source.PlanId, "task-graph plan id"),
            budget.Text(source.Objective, "task-graph objective"),
            ReadOnly(source.Tasks, task => Task(task, budget, depth + 1), budget, depth + 1),
            ReadOnly(
                source.DefinitionOfDone,
                requirement => Requirement(requirement, budget, depth + 1),
                budget,
                depth + 1),
            source.CreatedAt);
    }

    public static TaskNode Task(TaskNode source)
    {
        var budget = new SnapshotBudget();
        return Task(source, budget, 0);
    }

    public static TaskGraphRefinement Refinement(TaskGraphRefinement source) =>
        Refinement(source, new SnapshotBudget(), 0);

    private static TaskGraphRefinement Refinement(
        TaskGraphRefinement source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        return new TaskGraphRefinement(
            budget.Text(source.Reason, "task-graph refinement reason"),
            ReadOnly(
                source.Mutations,
                mutation => Mutation(mutation, budget, depth + 1),
                budget,
                depth + 1),
            Strings(source.Blockers, budget, depth + 1),
            source.RequiresUserInput);
    }

    public static TaskAcceptanceResult Acceptance(TaskAcceptanceResult source) =>
        Acceptance(source, new SnapshotBudget(), 0);

    private static TaskAcceptanceResult Acceptance(
        TaskAcceptanceResult source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        return new TaskAcceptanceResult(
            source.Status,
            Strings(source.Reasons, budget, depth + 1),
            Evidence(source.EvidenceRefs, budget, depth + 1),
            source.RequiresGraphRefinement);
    }

    public static DefinitionOfDoneResult? DefinitionOfDone(DefinitionOfDoneResult? source) =>
        DefinitionOfDone(source, new SnapshotBudget(), 0);

    private static DefinitionOfDoneResult? DefinitionOfDone(
        DefinitionOfDoneResult? source,
        SnapshotBudget budget,
        int depth)
    {
        if (source is null)
        {
            return null;
        }

        budget.Visit(depth);
        return new DefinitionOfDoneResult(
            source.Satisfied,
            Strings(source.Reasons, budget, depth + 1),
            Evidence(source.EvidenceRefs, budget, depth + 1));
    }

    public static WorkContextSnapshot Context(WorkContextSnapshot source) =>
        Context(source, new SnapshotBudget(), 0);

    private static WorkContextSnapshot Context(
        WorkContextSnapshot source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        return new WorkContextSnapshot(
            budget.Text(source.Objective, "work-context objective"),
            budget.OptionalText(source.ActiveTaskId, "work-context active task id"),
            Strings(source.CompletedTaskIds, budget, depth + 1),
            ReadOnly(
                source.ProvenFacts,
                fact =>
                {
                    ArgumentNullException.ThrowIfNull(fact);
                    return new ProvenFact(
                        budget.Text(fact.FactId, "proven-fact id"),
                        budget.Text(fact.Summary, "proven-fact summary"),
                        Evidence(fact.EvidenceRefs, budget, depth + 2));
                },
                budget,
                depth + 1),
            Strings(source.OpenQuestions, budget, depth + 1),
            Strings(source.Hypotheses, budget, depth + 1),
            Strings(source.KnownBlockers, budget, depth + 1),
            ReadOnly(
                source.PlanImpacts,
                impact =>
                {
                    ArgumentNullException.ThrowIfNull(impact);
                    return new PlanImpact(
                        impact.Kind,
                        budget.Text(impact.Summary, "plan-impact summary"),
                        Evidence(impact.EvidenceRefs, budget, depth + 2));
                },
                budget,
                depth + 1),
            Evidence(source.EvidenceRefs, budget, depth + 1),
            Structured(source.HostStateProjection, budget, depth + 1),
            source.UpdatedAt);
    }

    public static TaskAcceptanceContext AcceptanceContext(TaskAcceptanceContext source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var budget = new SnapshotBudget();
        budget.Visit(0);
        return new TaskAcceptanceContext(
            Plan(source.Plan, budget, 1),
            StateView(source.State, budget, 1),
            Context(source.WorkingContext, budget, 1),
            Structured(source.HostState, budget, 1));
    }

    public static TaskRefinementRequest RefinementRequest(TaskRefinementRequest source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // A refinement boundary carries one already-bounded child envelope in
        // addition to the ordinary orchestration metadata. Reusing the metadata
        // budget here would reject valid proof solely because its aggregate size
        // exceeds one megabyte after the child has actually run.
        var budget = BoundaryWithChildBudget();
        budget.Visit(0);
        return new TaskRefinementRequest(
            Request(source.Request, budget, 1),
            Plan(source.CurrentPlan, budget, 1),
            StateView(source.State, budget, 1),
            Task(source.ActiveTask, budget, 1),
            Outcome(
                source.LatestOutcome,
                budget,
                new HashSet<object>(ReferenceEqualityComparer.Instance),
                1),
            Acceptance(source.Acceptance, budget, 1),
            Context(source.WorkingContext, budget, 1),
            SnapshotRecord(source.Policy with { }, budget, 1));
    }

    public static WorkContextCompilationRequest CompilationRequest(WorkContextCompilationRequest source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // The compiler must see the same complete child proof retained by the
        // orchestrator. Keep the boundary bounded, but reserve the independently
        // bounded child allowance rather than silently dropping a real outcome.
        var budget = BoundaryWithChildBudget();
        budget.Visit(0);
        return new WorkContextCompilationRequest(
            Plan(source.Plan, budget, 1),
            StateView(source.State, budget, 1),
            source.ActiveTask is null ? null : Task(source.ActiveTask, budget, 1),
            source.LatestOutcome is null
                ? null
                : Outcome(
                    source.LatestOutcome,
                    budget,
                    new HashSet<object>(ReferenceEqualityComparer.Instance),
                    1),
            source.LatestAcceptance is null ? null : Acceptance(source.LatestAcceptance, budget, 1),
            source.Previous is null ? null : Context(source.Previous, budget, 1),
            Structured(source.HostState, budget, 1));
    }

    public static OrchestrationState StateView(OrchestrationState source) =>
        StateView(source, new SnapshotBudget(), 0);

    private static OrchestrationState StateView(
        OrchestrationState source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        var context = Context(source.WorkingContext, budget, depth + 1);
        var snapshot = new OrchestrationState(
            budget.Text(source.OrchestrationId, "orchestration id"),
            context)
        {
            Status = source.Status,
            StopReason = source.StopReason,
            ActiveTaskId = budget.OptionalText(source.ActiveTaskId, "active task id"),
            RefinementCount = source.RefinementCount,
            WorkingContext = context
        };
        snapshot.CompletedTaskIds.AddRange(Strings(source.CompletedTaskIds, budget, depth + 1));
        snapshot.BlockedTaskIds.AddRange(Strings(source.BlockedTaskIds, budget, depth + 1));
        snapshot.AvailableTaskIds.AddRange(Strings(source.AvailableTaskIds, budget, depth + 1));
        snapshot.RunRefs.AddRange(ReadOnly(
            source.RunRefs,
            run => RunReference(run, budget, depth + 1),
            budget,
            depth + 1));
        foreach (var pair in source.TaskRunCounts)
        {
            budget.Visit(depth + 1);
            snapshot.TaskRunCounts.Add(
                budget.Text(pair.Key, "task run-count id"),
                pair.Value);
        }

        return snapshot;
    }

    public static OrchestrationStateSnapshot State(OrchestrationState source) =>
        State(source, new SnapshotBudget(), 0);

    private static OrchestrationStateSnapshot State(
        OrchestrationState source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        var taskRunCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in source.TaskRunCounts)
        {
            budget.Visit(depth + 1);
            taskRunCounts.Add(budget.Text(pair.Key, "task run-count id"), pair.Value);
        }

        return new OrchestrationStateSnapshot(
            budget.Text(source.OrchestrationId, "orchestration id"),
            source.Status,
            source.StopReason,
            budget.OptionalText(source.ActiveTaskId, "active task id"),
            Strings(source.CompletedTaskIds, budget, depth + 1),
            Strings(source.BlockedTaskIds, budget, depth + 1),
            Strings(source.AvailableTaskIds, budget, depth + 1),
            ReadOnly(
                source.RunRefs,
                run => RunReference(run, budget, depth + 1),
                budget,
                depth + 1),
            new ReadOnlyDictionary<string, int>(taskRunCounts),
            source.RefinementCount,
            Context(source.WorkingContext, budget, depth + 1));
    }

    public static OutcomeEnvelope Outcome(OutcomeEnvelope source) =>
        Outcome(
            source,
            new SnapshotBudget(MaxChildOutcomeBytes, MaxChildOutcomeNodes),
            new HashSet<object>(ReferenceEqualityComparer.Instance),
            0);

    private static SnapshotBudget BoundaryWithChildBudget() =>
        new(
            checked(MaxChildOutcomeBytes + MaxBytes),
            checked(MaxChildOutcomeNodes + MaxNodes));

    public static OrchestrationOutcomeEnvelope Envelope(
        LargeTaskRequest request,
        TaskGraphPlan? plan,
        OrchestrationState state,
        IReadOnlyList<OutcomeEnvelope> outcomes,
        DefinitionOfDoneResult? definitionOfDone,
        IReadOnlyList<string> diagnostics,
        int maximumRuns)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRuns, 1);
        // Derived terminal evidence can reference proof from every bounded child. Keep
        // that metadata budget linear in the validated run cap while retaining a fixed
        // two-record allowance for the request and terminal result itself.
        var budget = new SnapshotBudget(
            checked(MaxBytes * (maximumRuns + 2)),
            checked(MaxNodes * (maximumRuns + 2)));
        budget.Visit(0);
        var planSnapshot = plan is null ? null : Plan(plan);
        var stateSnapshot = State(
            state,
            new SnapshotBudget(MaxBytes * 2, MaxNodes * 2),
            0);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count > maximumRuns || outcomes.Any(outcome => outcome is null))
        {
            throw new InvalidOperationException(
                $"Orchestration outcomes must contain at most {maximumRuns} frozen child envelopes.");
        }

        // Child outcomes are frozen and bounded exactly once when they cross the executor
        // boundary. Retaining those immutable snapshots avoids both mutable aliases and a
        // terminal re-snapshot budget that could discard proof after multiple real runs.
        var outcomeSnapshots = Array.AsReadOnly(outcomes.ToArray());
        var evidence = state.WorkingContext.EvidenceRefs
            .Concat(definitionOfDone?.EvidenceRefs ?? [])
            .Distinct()
            .ToArray();
        return new OrchestrationOutcomeEnvelope(
            budget.Text(state.OrchestrationId, "orchestration id"),
            state.Status,
            state.StopReason,
            budget.Text(request.Objective, "large-task objective"),
            planSnapshot,
            stateSnapshot,
            stateSnapshot.WorkingContext,
            outcomeSnapshots,
            Evidence(evidence, budget, 1))
        {
            DefinitionOfDone = DefinitionOfDone(definitionOfDone, budget, 1),
            Diagnostics = Strings(diagnostics, budget, 1)
        };
    }

    private static TaskNode Task(TaskNode source, SnapshotBudget budget, int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        return new TaskNode(
            budget.Text(source.TaskId, "task id"),
            budget.Text(source.Objective, "task objective"),
            Strings(source.DependsOn, budget, depth + 1),
            source.Optional,
            source.Priority,
            source.MaxRuns,
            Structured(source.ContextProjection, budget, depth + 1),
            ReadOnly(
                source.AcceptanceRequirements,
                requirement => Requirement(requirement, budget, depth + 1),
                budget,
                depth + 1));
    }

    private static TaskAcceptanceRequirement Requirement(
        TaskAcceptanceRequirement source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        return new TaskAcceptanceRequirement(
            source.Kind,
            source.RequiredOutcomeStatus,
            budget.OptionalText(source.ArtifactKind, "acceptance artifact kind"),
            budget.OptionalText(source.ToolId, "acceptance tool id"),
            budget.OptionalText(source.HostStateKey, "acceptance host-state key"),
            StructuredValue(source.HostStateValue, budget, depth + 1));
    }

    private static TaskGraphMutation Mutation(
        TaskGraphMutation source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        return new TaskGraphMutation(
            source.Kind,
            budget.Text(source.TaskId, "mutation task id"),
            source.Task is null ? null : Task(source.Task, budget, depth + 1),
            budget.OptionalText(source.DependencyTaskId, "mutation dependency task id"),
            source.Priority,
            source.AcceptanceRequirements is null
                ? null
                : ReadOnly(
                    source.AcceptanceRequirements,
                    requirement => Requirement(requirement, budget, depth + 1),
                    budget,
                    depth + 1),
            source.DefinitionOfDone is null
                ? null
                : ReadOnly(
                    source.DefinitionOfDone,
                    requirement => Requirement(requirement, budget, depth + 1),
                    budget,
                    depth + 1));
    }

    private static RunRef RunReference(
        RunRef source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        return new RunRef(
            budget.Text(source.TaskId, "run-reference task id"),
            budget.Text(source.RunId, "run-reference run id"),
            source.Status,
            Evidence(source.EvidenceRefs, budget, depth + 1));
    }

    private static OutcomeEnvelope Outcome(
        OutcomeEnvelope source,
        SnapshotBudget budget,
        ISet<object> active,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        budget.Visit(depth);
        if (!active.Add(source))
        {
            throw new InvalidOperationException("Orchestration outcome proof contains a reference cycle.");
        }

        try
        {
            ArgumentNullException.ThrowIfNull(source.Outcome);
            ArgumentNullException.ThrowIfNull(source.Report);
            ArgumentNullException.ThrowIfNull(source.Receipts);
            ArgumentNullException.ThrowIfNull(source.Details);
            var outcome = new RunOutcome(
                budget.Text(source.Outcome.RunId, "child run id"),
                source.Outcome.Status,
                source.Outcome.StopReason,
                Strings(source.Outcome.CompletedSteps, budget, depth + 1),
                Strings(source.Outcome.Blockers, budget, depth + 1),
                Evidence(source.Outcome.CompletionEvidence, budget, depth + 1));
            var receipts = new ReceiptEnvelope(ReadOnly(
                source.Receipts.Items,
                receipt => SnapshotRecord(
                    ExecutionRecordSnapshot.Receipt(receipt),
                    budget,
                    depth + 2),
                budget,
                depth + 1));
            var details = Detail(source.Details, budget, active, depth + 1);
            return new OutcomeEnvelope(
                outcome,
                SnapshotRecord(
                    ExecutionRecordSnapshot.Report(source.Report),
                    budget,
                    depth + 1),
                receipts,
                details)
            {
                PriorAttempts = ReadOnly(
                    source.PriorAttempts,
                    attempt => Outcome(attempt, budget, active, depth + 1),
                    budget,
                    depth + 1)
            };
        }
        finally
        {
            active.Remove(source);
        }
    }

    private static DetailEnvelope Detail(
        DetailEnvelope source,
        SnapshotBudget budget,
        ISet<object> active,
        int depth)
    {
        budget.Visit(depth);
        return new DetailEnvelope(
            SnapshotRecord(ExecutionRecordSnapshot.ProofRequest(source.Request), budget, depth + 1),
            ReadOnly(
                source.PlanVersions,
                plan => SnapshotRecord(ExecutionRecordSnapshot.Plan(plan), budget, depth + 2),
                budget,
                depth + 1),
            ReadOnly(
                source.PlanRefinements,
                refinement => new PlanRefinement(
                    budget.Text(refinement.FromPlanId, "plan-refinement source id"),
                    budget.Text(refinement.ToPlanId, "plan-refinement target id"),
                    budget.Text(refinement.Reason, "plan-refinement reason"),
                    Evidence(refinement.Evidence, budget, depth + 1)),
                budget,
                depth + 1),
            ReadOnly(
                source.Observations,
                observation => SnapshotRecord(
                    ExecutionRecordSnapshot.Observation(observation),
                    budget,
                    depth + 2),
                budget,
                depth + 1),
            ReadOnly(
                source.Artifacts,
                artifact => SnapshotRecord(
                    ExecutionRecordSnapshot.Artifact(artifact),
                    budget,
                    depth + 2),
                budget,
                depth + 1),
            ReadOnly(
                source.Batches,
                batch => new ExecutionBatch(
                    budget.Text(batch.BatchId, "execution batch id"),
                    Strings(batch.StepIds, budget, depth + 1),
                    batch.StartedAt,
                    batch.CompletedAt),
                budget,
                depth + 1),
            ReadOnly(
                source.Events,
                executionEvent => SnapshotRecord(
                    ExecutionEventSnapshot.Clone(executionEvent),
                    budget,
                    depth + 2),
                budget,
                depth + 1),
            ReadOnly(
                source.ValidationIssues,
                issue => SnapshotRecord(issue with { }, budget, depth + 2),
                budget,
                depth + 1))
        {
            RunAttempts = ReadOnly(
                source.RunAttempts,
                attempt => new RunAttemptSummary(
                    attempt.AttemptNumber,
                    budget.Text(attempt.RunId, "run-attempt id"),
                    attempt.Status,
                    attempt.StopReason,
                    Strings(attempt.CompletedSteps, budget, depth + 1),
                    Strings(attempt.Blockers, budget, depth + 1)),
                budget,
                depth + 1),
            ToolSurfaces = ReadOnly(
                source.ToolSurfaces,
                surface => SnapshotRecord(
                    ExecutionRecordSnapshot.ToolSurface(surface),
                    budget,
                    depth + 2),
                budget,
                depth + 1),
            PlanningFrames = ReadOnly(
                source.PlanningFrames,
                frame => SnapshotRecord(
                    ExecutionRecordSnapshot.PlanningFrame(frame),
                    budget,
                    depth + 2),
                budget,
                depth + 1),
            GrantConsumptions = ReadOnly(
                source.GrantConsumptions,
                consumption => SnapshotRecord(
                    ExecutionRecordSnapshot.GrantConsumption(consumption),
                    budget,
                    depth + 2),
                budget,
                depth + 1),
            EventDeliveryFailure = source.EventDeliveryFailure is null
                ? null
                : SnapshotRecord(source.EventDeliveryFailure with { }, budget, depth + 1),
            Breadcrumbs = new BreadcrumbLedger(ReadOnly(
                source.Breadcrumbs.Entries,
                entry => new BreadcrumbEntry(
                    budget.Text(entry.EntryId, "breadcrumb id"),
                    budget.Text(entry.RunId, "breadcrumb run id"),
                    entry.Sequence,
                    entry.Kind,
                    budget.Text(entry.Summary, "breadcrumb summary"),
                    budget.OptionalText(entry.StepId, "breadcrumb step id"),
                    budget.OptionalText(entry.ToolId, "breadcrumb tool id"),
                    budget.OptionalText(entry.ReceiptId, "breadcrumb receipt id"),
                    budget.OptionalText(entry.ObservationId, "breadcrumb observation id"),
                    budget.OptionalText(entry.PlanId, "breadcrumb plan id"),
                    budget.OptionalText(entry.PhaseId, "breadcrumb phase id"),
                    entry.At,
                    Evidence(entry.EvidenceRefs, budget, depth + 1)),
                budget,
                depth + 1)),
            Divergences = new DivergenceLedger(ReadOnly(
                source.Divergences.Entries,
                entry => new DivergenceEntry(
                    budget.Text(entry.DivergenceId, "divergence id"),
                    budget.Text(entry.RunId, "divergence run id"),
                    entry.Sequence,
                    budget.Text(entry.Expected, "divergence expected value"),
                    budget.Text(entry.Actual, "divergence actual value"),
                    entry.Severity,
                    budget.Text(entry.Interpretation, "divergence interpretation"),
                    budget.Text(entry.RecommendedAdjustment, "divergence recommendation"),
                    entry.At,
                    Evidence(entry.EvidenceRefs, budget, depth + 1)),
                budget,
                depth + 1)),
            Continuity = source.Continuity with
            {
                RecommendationReasons = Strings(source.Continuity.RecommendationReasons, budget, depth + 1)
            }
        };
    }

    private static IReadOnlyDictionary<string, object?> Structured(
        IReadOnlyDictionary<string, object?> source,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        var snapshot = ToolResultNormalizer.SnapshotStructuredData(source);
        budget.ChargeSnapshot(snapshot, depth);
        return snapshot;
    }

    private static object? StructuredValue(
        object? source,
        SnapshotBudget budget,
        int depth)
    {
        var wrapper = ToolResultNormalizer.SnapshotStructuredData(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["value"] = source
            });
        var snapshot = wrapper["value"];
        budget.ChargeSnapshot(snapshot, depth);
        return snapshot;
    }

    private static T SnapshotRecord<T>(T snapshot, SnapshotBudget budget, int depth)
    {
        budget.ChargeSnapshot(snapshot, depth);
        return snapshot;
    }

    private static IReadOnlyList<EvidenceRef> Evidence(
        IReadOnlyList<EvidenceRef> source,
        SnapshotBudget budget,
        int depth) =>
        ReadOnly(
            source,
            item =>
            {
                ArgumentNullException.ThrowIfNull(item);
                return new EvidenceRef(
                    budget.Text(item.Kind, "evidence kind"),
                    budget.Text(item.RefId, "evidence reference id"));
            },
            budget,
            depth);

    private static IReadOnlyList<string> Strings(
        IReadOnlyList<string> source,
        SnapshotBudget budget,
        int depth) =>
        ReadOnly(
            source,
            item => budget.Text(
                item ?? throw new InvalidOperationException(
                    "Orchestration string collections cannot contain null entries."),
                "orchestration string"),
            budget,
            depth);

    private static IReadOnlyList<TResult> ReadOnly<TSource, TResult>(
        IReadOnlyList<TSource> source,
        Func<TSource, TResult> snapshot,
        SnapshotBudget budget,
        int depth)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(snapshot);
        budget.Visit(depth);
        var items = new List<TResult>();
        foreach (var item in source)
        {
            if (items.Count >= MaxItems)
            {
                throw new InvalidOperationException(
                    $"Orchestration records cannot contain more than {MaxItems} collection items.");
            }

            budget.Visit(depth + 1);
            items.Add(snapshot(item));
        }

        return items.AsReadOnly();
    }

    private sealed class SnapshotBudget
    {
        private readonly int _maximumNodes;
        private readonly int _maximumBytes;
        private int _remainingNodes;
        private int _remainingBytes;

        public SnapshotBudget(int maximumBytes = MaxBytes, int maximumNodes = MaxNodes)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumNodes, 1);
            _maximumBytes = maximumBytes;
            _maximumNodes = maximumNodes;
            _remainingBytes = maximumBytes;
            _remainingNodes = maximumNodes;
        }

        public void Visit(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new InvalidOperationException(
                    $"Orchestration records cannot exceed a depth of {MaxDepth}.");
            }

            if (_remainingNodes-- <= 0)
            {
                throw new InvalidOperationException(
                    $"Orchestration records cannot contain more than {_maximumNodes} nodes.");
            }
        }

        public string Text(string value, string description)
        {
            ArgumentNullException.ThrowIfNull(value);
            ConsumeBytes(Encoding.UTF8.GetByteCount(value), description);
            return value;
        }

        public string? OptionalText(string? value, string description) =>
            value is null ? null : Text(value, description);

        public void ChargeSnapshot(object? value, int depth) =>
            ChargeSnapshot(
                value,
                depth,
                new HashSet<object>(ReferenceEqualityComparer.Instance));

        private void ChargeSnapshot(
            object? value,
            int depth,
            ISet<object> active)
        {
            Visit(depth);
            if (value is null)
            {
                ConsumeBytes(4, "null value");
                return;
            }

            if (value is string text)
            {
                Text(text, "snapshot string");
                return;
            }

            if (value is JsonElement element)
            {
                ChargeJson(element, depth, active);
                return;
            }

            if (value is JsonDocument document)
            {
                ChargeJson(document.RootElement, depth, active);
                return;
            }

            if (IsScalar(value))
            {
                ConsumeBytes(
                    Encoding.UTF8.GetByteCount(ScalarText(value)),
                    "snapshot scalar");
                return;
            }

            var trackReference = !value.GetType().IsValueType;
            if (trackReference && !active.Add(value))
            {
                throw new InvalidOperationException("Orchestration snapshot contains a reference cycle.");
            }

            try
            {
                if (value is IDictionary dictionary)
                {
                    var count = 0;
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (count++ >= MaxItems)
                        {
                            throw new InvalidOperationException(
                                $"Orchestration records cannot contain more than {MaxItems} dictionary entries.");
                        }

                        ChargeSnapshot(entry.Key, depth + 1, active);
                        ChargeSnapshot(entry.Value, depth + 1, active);
                    }

                    return;
                }

                if (value is IEnumerable sequence)
                {
                    var count = 0;
                    foreach (var item in sequence)
                    {
                        if (count++ >= MaxItems)
                        {
                            throw new InvalidOperationException(
                                $"Orchestration records cannot contain more than {MaxItems} collection items.");
                        }

                        ChargeSnapshot(item, depth + 1, active);
                    }

                    return;
                }

                var properties = SnapshotProperties.GetOrAdd(
                    value.GetType(),
                    static type => type
                        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
                        .ToArray());
                foreach (var property in properties)
                {
                    ChargeSnapshot(property.GetValue(value), depth + 1, active);
                }
            }
            finally
            {
                if (trackReference)
                {
                    active.Remove(value);
                }
            }
        }

        private void ChargeJson(JsonElement element, int depth, ISet<object> active)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Text(property.Name, "JSON property name");
                        ChargeSnapshot(property.Value, depth + 1, active);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        ChargeSnapshot(item, depth + 1, active);
                    }

                    break;
                case JsonValueKind.String:
                    Text(element.GetString() ?? string.Empty, "JSON string");
                    break;
                case JsonValueKind.Number:
                    Text(element.GetRawText(), "JSON number");
                    break;
                case JsonValueKind.True:
                    ConsumeBytes(4, "JSON Boolean");
                    break;
                case JsonValueKind.False:
                    ConsumeBytes(5, "JSON Boolean");
                    break;
                case JsonValueKind.Null:
                    ConsumeBytes(4, "JSON null");
                    break;
                default:
                    throw new InvalidOperationException("Orchestration snapshot contains undefined JSON data.");
            }
        }

        private void ConsumeBytes(int byteCount, string description)
        {
            if (byteCount < 0 || byteCount > _remainingBytes)
            {
                throw new InvalidOperationException(
                    "Orchestration " + description +
                    $" exceeds the aggregate maximum of {_maximumBytes} UTF-8 bytes.");
            }

            _remainingBytes -= byteCount;
        }

        private static bool IsScalar(object value) =>
            value is char or bool or
                byte or sbyte or short or ushort or int or uint or long or ulong or
                float or double or decimal or
                DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly or
                Guid or Uri or Version or Enum;

        private static string ScalarText(object value) =>
            value switch
            {
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
                _ => value.ToString() ?? string.Empty
            };
    }
}
