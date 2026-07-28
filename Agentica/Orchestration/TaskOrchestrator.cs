using Agentica;
using Agentica.Artifacts;
using Agentica.Execution;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Execution;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Validation;

namespace Agentica.Orchestration;

public sealed class TaskOrchestrator
{
    private const int MaximumRuns = 64;
    private const int MaximumRefinements = 64;
    private const int MaximumGraphMutationsPerRefinement = 64;
    private const int MaximumDiagnostics = 128;
    private const int MaximumDiagnosticCharacters = 1_024;
    private const string InvalidRequestObjective = "Invalid large-task request.";

    private readonly ITaskPlanner _taskPlanner;
    private readonly IRunExecutor _runExecutor;
    private readonly ITaskAcceptanceEvaluator _acceptanceEvaluator;
    private readonly IWorkContextCompiler _contextCompiler;
    private readonly Func<IReadOnlyDictionary<string, object?>> _hostStateProjection;
    private readonly OrchestrationPolicy _policy;

    public TaskOrchestrator(
        ITaskPlanner taskPlanner,
        IRunExecutor runExecutor,
        ITaskAcceptanceEvaluator acceptanceEvaluator,
        IWorkContextCompiler contextCompiler,
        Func<IReadOnlyDictionary<string, object?>> hostStateProjection,
        OrchestrationPolicy? policy = null)
    {
        _taskPlanner = taskPlanner;
        _runExecutor = runExecutor;
        _acceptanceEvaluator = acceptanceEvaluator;
        _contextCompiler = contextCompiler;
        _hostStateProjection = hostStateProjection;
        _policy = policy ?? new OrchestrationPolicy();
        ValidatePolicy(_policy);
    }

    public async Task<OrchestrationOutcomeEnvelope> RunAsync(
        LargeTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            request = OrchestrationRecordSnapshot.Request(request);
        }
        catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
        {
            var fallbackRequest = new LargeTaskRequest(
                InvalidRequestObjective,
                request.Origin,
                new Dictionary<string, object?>(StringComparer.Ordinal));
            var invalidState = new OrchestrationState(
                AgenticaIds.New("orchestration"),
                EmptyContext(fallbackRequest.Objective, fallbackRequest.Context))
            {
                Status = OrchestrationStatus.PlanInvalid,
                StopReason = OrchestrationStopReason.PlanInvalid
            };
            return Envelope(
                fallbackRequest,
                null,
                invalidState,
                [],
                null,
                [$"Large task request could not be safely snapshotted ({exception.GetType().Name})."]);
        }

        var orchestrationId = AgenticaIds.New("orchestration");
        var state = new OrchestrationState(
            orchestrationId,
            EmptyContext(request.Objective, new Dictionary<string, object?>(StringComparer.Ordinal)))
        {
            Status = OrchestrationStatus.Running
        };
        var outcomes = new List<Agentica.Outcomes.OutcomeEnvelope>();
        var diagnostics = new List<string>();
        TaskGraphPlan? plan = null;
        DefinitionOfDoneResult? definitionOfDone = null;
        var activeBoundary = "orchestration initialization";

        try
        {
            activeBoundary = "initial host-state projection";
            var initialHostState = ProjectHostState();
            state.WorkingContext = EmptyContext(request.Objective, initialHostState);

            try
            {
                activeBoundary = "initial task planning";
                var planned = await _taskPlanner.CreatePlanAsync(
                    new TaskPlanningRequest(request, _policy),
                    cancellationToken).ConfigureAwait(false);
                plan = planned is null ? null : OrchestrationRecordSnapshot.Plan(planned);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException &&
                RuntimeExceptionBoundary.IsRecoverable(exception))
            {
                ApplyPlannerFailure(state, exception);
                AddDiagnostic(
                    diagnostics,
                    $"Initial task planning failed ({exception.GetType().Name}).");
                return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
            }

            if (plan is null)
            {
                state.Status = OrchestrationStatus.PlanInvalid;
                state.StopReason = OrchestrationStopReason.PlanInvalid;
                AddDiagnostic(diagnostics, "Initial task planner returned no task graph.");
                return Envelope(request, null, state, outcomes, definitionOfDone, diagnostics);
            }

            try
            {
                activeBoundary = "initial task-graph validation";
                TaskGraphValidator.Validate(plan);
            }
            catch (TaskGraphValidationException exception)
            {
                state.Status = OrchestrationStatus.PlanInvalid;
                state.StopReason = OrchestrationStopReason.PlanInvalid;
                AddDiagnostic(diagnostics, $"Initial task graph is invalid: {exception.Message}");
                return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
            }

            activeBoundary = "initial work-context compilation";
            state.WorkingContext = CompileContext(new WorkContextCompilationRequest(
                plan,
                state,
                null,
                null,
                null,
                null,
                initialHostState));

            for (var runIndex = 0; runIndex < _policy.MaxRuns; runIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TaskGraph.RequiredTasksComplete(plan, state))
                {
                    activeBoundary = "definition-of-done host-state projection";
                    var hostState = ProjectHostState();
                    activeBoundary = "definition-of-done evaluation";
                    definitionOfDone = DefinitionOfDoneEvaluator.Evaluate(
                        plan,
                        state,
                        outcomes,
                        hostState);
                    if (definitionOfDone.Satisfied)
                    {
                        activeBoundary = "final work-context compilation";
                        Complete(state, plan, outcomes, hostState);
                        return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                    }
                }

                var available = TaskGraph.AvailableTasks(plan, state);
                var exhausted = available
                    .Where(task => RunCount(state, task.TaskId) >= task.MaxRuns)
                    .ToArray();
                foreach (var exhaustedTask in exhausted)
                {
                    if (!state.BlockedTaskIds.Contains(exhaustedTask.TaskId, StringComparer.Ordinal))
                    {
                        state.BlockedTaskIds.Add(exhaustedTask.TaskId);
                    }
                }

                var executable = available
                    .Where(task => RunCount(state, task.TaskId) < task.MaxRuns)
                    .ToArray();
                state.AvailableTaskIds.Clear();
                state.AvailableTaskIds.AddRange(executable.Select(task => task.TaskId));

                var task = executable.FirstOrDefault(task => !task.Optional) ?? executable.FirstOrDefault();
                if (task is null)
                {
                    var requiredTasksComplete = TaskGraph.RequiredTasksComplete(plan, state);
                    var reasons = requiredTasksComplete && definitionOfDone is not null
                        ? definitionOfDone.Reasons
                        : exhausted.Length > 0
                            ? exhausted
                                .Select(item => $"Task '{item.TaskId}' reached its maxRuns budget of {item.MaxRuns}.")
                                .ToArray()
                            : ["No available task can advance the orchestration."];
                    state.Status = OrchestrationStatus.Blocked;
                    state.StopReason = requiredTasksComplete && definitionOfDone is not null
                        ? OrchestrationStopReason.DefinitionOfDoneNotSatisfied
                        : exhausted.Length > 0
                            ? OrchestrationStopReason.MaxRunsReached
                            : OrchestrationStopReason.Blocked;
                    activeBoundary = "final host-state projection";
                    var finalHostState = ProjectHostState();
                    activeBoundary = "final work-context compilation";
                    state.WorkingContext = CompileContext(new WorkContextCompilationRequest(
                        plan,
                        state,
                        null,
                        outcomes.LastOrDefault(),
                        new TaskAcceptanceResult(TaskAcceptanceStatus.Blocked, reasons, definitionOfDone?.EvidenceRefs ?? []),
                        state.WorkingContext,
                        finalHostState));
                    return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                }

                state.ActiveTaskId = task.TaskId;
                state.TaskRunCounts[task.TaskId] = RunCount(state, task.TaskId) + 1;
                var childDispatchId = AgenticaIds.New("child_dispatch");
                var runRequest = ExecutionRecordSnapshot.Request(
                    BuildRunRequest(request, task, state, childDispatchId));
                var childOutcomeIndex = outcomes.Count;
                var pendingChildDispatch = ChildDispatchProofUnavailable(
                    runRequest,
                    task,
                    childDispatchId,
                    returnedOutcomeReceived: false,
                    failureType: null);
                outcomes.Add(pendingChildDispatch);
                activeBoundary = "child run execution";
                Agentica.Outcomes.OutcomeEnvelope? returnedOutcome = null;
                Agentica.Outcomes.OutcomeEnvelope outcome;
                try
                {
                    returnedOutcome = await _runExecutor.RunAsync(runRequest, cancellationToken).ConfigureAwait(false);
                    if (returnedOutcome is null)
                    {
                        throw new InvalidOperationException("The child run executor returned no outcome envelope.");
                    }

                    outcome = OrchestrationRecordSnapshot.Outcome(returnedOutcome);
                    outcomes[childOutcomeIndex] = outcome;
                }
                catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
                {
                    // The dispatch itself has crossed the effect boundary even when its
                    // returned proof is missing, malformed, or larger than the bounded
                    // orchestration contract. Retain an immutable indeterminate record
                    // before normalizing the orchestration failure; never erase the call.
                    outcomes[childOutcomeIndex] = ChildDispatchProofUnavailable(
                        pendingChildDispatch.Details.Request,
                        task,
                        childDispatchId,
                        returnedOutcome is not null,
                        exception.GetType().Name,
                        BoundedReturnedRunId(returnedOutcome));
                    throw;
                }

                var childRunIdentityFailure = ChildRunIdentityFailure(outcomes);
                if (childRunIdentityFailure is not null)
                {
                    state.Status = OrchestrationStatus.Failed;
                    state.StopReason = OrchestrationStopReason.ChildRunFailed;
                    state.ActiveTaskId = null;
                    AddDiagnostic(diagnostics, childRunIdentityFailure);
                    return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                }

                activeBoundary = "acceptance host-state projection";
                var acceptanceHostState = ProjectHostState();
                var acceptanceContext = OrchestrationRecordSnapshot.AcceptanceContext(new TaskAcceptanceContext(
                    plan,
                    state,
                    state.WorkingContext,
                    acceptanceHostState));
                activeBoundary = "task acceptance evaluation";
                var returnedAcceptance = await _acceptanceEvaluator.EvaluateAsync(
                    task,
                    outcome,
                    acceptanceContext,
                    cancellationToken).ConfigureAwait(false);
                if (returnedAcceptance is null)
                {
                    throw new InvalidOperationException("The task acceptance evaluator returned no result.");
                }

                var acceptance = OrchestrationRecordSnapshot.Acceptance(returnedAcceptance);

                activeBoundary = "declared acceptance evaluation";
                acceptance = OrchestrationRecordSnapshot.Acceptance(
                    await EnforceDeclaredAcceptanceAsync(
                        task,
                        outcome,
                        acceptanceContext,
                        acceptance,
                        cancellationToken).ConfigureAwait(false));

                if (acceptance.Status == TaskAcceptanceStatus.Accepted)
                {
                    state.CompletedTaskIds.Add(task.TaskId);
                    state.RunRefs.Add(new RunRef(task.TaskId, outcome.Outcome.RunId, outcome.Outcome.Status, acceptance.EvidenceRefs));
                    activeBoundary = "work-context compilation";
                    state.WorkingContext = CompileContext(new WorkContextCompilationRequest(
                        plan,
                        state,
                        task,
                        outcome,
                        acceptance,
                        state.WorkingContext,
                        acceptanceContext.HostState));

                    if (!acceptance.RequiresGraphRefinement)
                    {
                        continue;
                    }
                }
                else
                {
                    activeBoundary = "work-context compilation";
                    state.WorkingContext = CompileContext(new WorkContextCompilationRequest(
                        plan,
                        state,
                        task,
                        outcome,
                        acceptance,
                        state.WorkingContext,
                        acceptanceContext.HostState));
                }

                if (!acceptance.ShouldRefine)
                {
                    state.BlockedTaskIds.Add(task.TaskId);
                    ApplyFinalStateFromAcceptance(state, acceptance, outcome);
                    return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                }

                if (state.RefinementCount >= _policy.MaxRefinements)
                {
                    ApplyFinalStateFromRefinementBudget(state, outcome);
                    return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                }

                TaskGraphRefinement refinement;
                try
                {
                    activeBoundary = "task-graph refinement";
                    var returnedRefinement = await _taskPlanner.RefinePlanAsync(
                        OrchestrationRecordSnapshot.RefinementRequest(new TaskRefinementRequest(
                            request,
                            plan,
                            state,
                            task,
                            outcome,
                            acceptance,
                            state.WorkingContext,
                            _policy)),
                        cancellationToken).ConfigureAwait(false);
                    refinement = returnedRefinement is null
                        ? null!
                        : OrchestrationRecordSnapshot.Refinement(returnedRefinement);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException &&
                    RuntimeExceptionBoundary.IsRecoverable(exception))
                {
                    ApplyPlannerFailure(state, exception);
                    AddDiagnostic(
                        diagnostics,
                        $"Task graph refinement failed after run '{outcome.Outcome.RunId}' ({exception.GetType().Name}).");
                    return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                }

                state.RefinementCount++;

                if (refinement is null || refinement.Mutations is null)
                {
                    state.Status = OrchestrationStatus.PlanInvalid;
                    state.StopReason = OrchestrationStopReason.PlanInvalid;
                    AddDiagnostic(
                        diagnostics,
                        "Task planner returned an invalid null refinement or mutation list.");
                    return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                }

                if (refinement.RequiresUserInput || refinement.Mutations.Count == 0)
                {
                    state.BlockedTaskIds.Add(task.TaskId);
                    state.Status = OrchestrationStatus.Blocked;
                    state.StopReason = OrchestrationStopReason.Blocked;
                    AddDiagnostics(diagnostics, refinement.Blockers);
                    return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                }

                if (refinement.Mutations.Count > _policy.MaxGraphMutationsPerRefinement)
                {
                    state.Status = OrchestrationStatus.PlanInvalid;
                    state.StopReason = OrchestrationStopReason.PlanInvalid;
                    AddDiagnostic(
                        diagnostics,
                        $"Refinement proposed {refinement.Mutations.Count} mutations; policy permits {_policy.MaxGraphMutationsPerRefinement}.");
                    return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                }

                var previousPlan = plan;
                try
                {
                    activeBoundary = "task-graph mutation";
                    var candidate = TaskGraphMutationApplier.Apply(previousPlan, refinement);
                    TaskGraphValidator.Validate(candidate, state, previousPlan);
                    plan = candidate;
                    ResetRunCountsForReplacedTasks(state, refinement);
                }
                catch (TaskGraphValidationException exception)
                {
                    state.Status = OrchestrationStatus.PlanInvalid;
                    state.StopReason = OrchestrationStopReason.PlanInvalid;
                    AddDiagnostic(diagnostics, $"Task graph mutation failed: {exception.Message}");
                    return Envelope(request, previousPlan, state, outcomes, definitionOfDone, diagnostics);
                }
            }

            if (TaskGraph.RequiredTasksComplete(plan, state))
            {
                activeBoundary = "definition-of-done host-state projection";
                var hostState = ProjectHostState();
                activeBoundary = "definition-of-done evaluation";
                definitionOfDone = DefinitionOfDoneEvaluator.Evaluate(
                    plan,
                    state,
                    outcomes,
                    hostState);
                if (definitionOfDone.Satisfied)
                {
                    activeBoundary = "final work-context compilation";
                    Complete(state, plan, outcomes, hostState);
                    return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
                }

                state.Status = OrchestrationStatus.Blocked;
                state.StopReason = OrchestrationStopReason.DefinitionOfDoneNotSatisfied;
                return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
            }

            state.Status = OrchestrationStatus.Blocked;
            state.StopReason = OrchestrationStopReason.MaxRunsReached;
            return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
        }
        catch (OperationCanceledException exception) when (
            RuntimeExceptionBoundary.IsRecoverable(exception))
        {
            state.Status = OrchestrationStatus.Cancelled;
            state.StopReason = OrchestrationStopReason.Cancelled;
            state.ActiveTaskId = null;
            AddDiagnostic(
                diagnostics,
                $"Orchestration cancelled ({exception.GetType().Name}).");
            return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
        }
        catch (Exception exception) when (RuntimeExceptionBoundary.IsRecoverable(exception))
        {
            state.Status = OrchestrationStatus.Failed;
            state.StopReason = OrchestrationStopReason.Failed;
            state.ActiveTaskId = null;
            AddDiagnostic(
                diagnostics,
                $"{activeBoundary} failed ({exception.GetType().Name}).");
            return Envelope(request, plan, state, outcomes, definitionOfDone, diagnostics);
        }
    }

    private static RunRequest BuildRunRequest(
        LargeTaskRequest request,
        TaskNode task,
        OrchestrationState state,
        string childDispatchId)
    {
        var context = new Dictionary<string, object?>(request.Context, StringComparer.Ordinal);
        foreach (var pair in task.ContextProjection)
        {
            context[pair.Key] = pair.Value;
        }

        context["orchestration.id"] = state.OrchestrationId;
        context["orchestration.taskId"] = task.TaskId;
        context["orchestration.childDispatchId"] = childDispatchId;
        context["orchestration.workingContext"] = state.WorkingContext;
        context["orchestration.completedTaskIds"] = state.CompletedTaskIds.ToArray();
        context["orchestration.taskRunCount"] = RunCount(state, task.TaskId);
        context["orchestration.taskMaxRuns"] = task.MaxRuns;
        context["orchestration.taskRunCounts"] = state.TaskRunCounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);

        return new RunRequest(task.Objective, request.Origin, context);
    }

    private static Agentica.Outcomes.OutcomeEnvelope ChildDispatchProofUnavailable(
        RunRequest request,
        TaskNode task,
        string childDispatchId,
        bool returnedOutcomeReceived,
        string? failureType,
        string? returnedRunId = null)
    {
        var phase = failureType is null ? "pending" : "unavailable";
        var message = failureType is null
            ? "Child executor dispatch started; complete returned proof is pending."
            : $"Child executor dispatch did not yield retainable complete proof ({failureType}); effects may have occurred.";
        var envelope = new Agentica.Outcomes.OutcomeEnvelope(
            new RunOutcome(
                childDispatchId,
                RunOutcomeStatus.PartiallyComplete,
                StopReason.Partial,
                [],
                [message],
                []),
            new OutcomeReport(
                AgenticaIds.New("report"),
                message,
                []),
            new ReceiptEnvelope(
            [
                new Receipt(
                    AgenticaIds.New("receipt"),
                    task.TaskId,
                    "orchestration.child-executor",
                    ReceiptStatus.Partial,
                    message,
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["childDispatchId"] = childDispatchId,
                        ["taskId"] = task.TaskId,
                        ["proofStatus"] = phase,
                        ["returnedOutcomeReceived"] = returnedOutcomeReceived,
                        ["returnedRunId"] = returnedRunId,
                        ["effectMayHaveOccurred"] = true,
                        ["failureType"] = failureType
                    })
            ]),
            new DetailEnvelope(
                request,
                [],
                [],
                [],
                [],
                [],
                [],
                failureType is null
                    ? []
                    :
                    [
                        new ValidationIssue(
                            "orchestration.child.proof.unavailable",
                            message,
                            task.TaskId)
                    ]));
        return OrchestrationRecordSnapshot.Outcome(envelope);
    }

    private static string? BoundedReturnedRunId(Agentica.Outcomes.OutcomeEnvelope? outcome)
    {
        var runId = outcome?.Outcome?.RunId;
        return !string.IsNullOrWhiteSpace(runId) && runId.Length <= 256
            ? runId
            : null;
    }

    private static int RunCount(OrchestrationState state, string taskId) =>
        state.TaskRunCounts.TryGetValue(taskId, out var count) ? count : 0;

    private static string? ChildRunIdentityFailure(
        IReadOnlyList<Agentica.Outcomes.OutcomeEnvelope> outcomes)
    {
        var runIds = new HashSet<string>(StringComparer.Ordinal);
        for (var outcomeIndex = 0; outcomeIndex < outcomes.Count; outcomeIndex++)
        {
            var pending = new Stack<(Agentica.Outcomes.OutcomeEnvelope? Envelope, string Location)>();
            pending.Push((outcomes[outcomeIndex], $"Child outcome {outcomeIndex + 1}"));

            while (pending.Count > 0)
            {
                var (envelope, location) = pending.Pop();
                if (envelope is null || envelope.Outcome is null)
                {
                    return $"{location} is missing its outcome identity; child-run proof identities must be nonempty and globally unique across top-level outcomes and prior attempts.";
                }

                var runId = envelope.Outcome.RunId;
                if (string.IsNullOrWhiteSpace(runId))
                {
                    return $"{location} has an empty run id; child-run proof identities must be nonempty and globally unique across top-level outcomes and prior attempts.";
                }

                if (!runIds.Add(runId))
                {
                    return $"{location} reused run id '{runId}'; child-run proof identities must be nonempty and globally unique across top-level outcomes and prior attempts.";
                }

                if (envelope.PriorAttempts is null)
                {
                    return $"{location} has no prior-attempt collection; child-run proof identities cannot be validated.";
                }

                for (var priorIndex = envelope.PriorAttempts.Count - 1; priorIndex >= 0; priorIndex--)
                {
                    pending.Push((
                        envelope.PriorAttempts[priorIndex],
                        $"{location} prior attempt {priorIndex + 1}"));
                }
            }
        }

        return null;
    }

    private static async Task<TaskAcceptanceResult> EnforceDeclaredAcceptanceAsync(
        TaskNode task,
        Agentica.Outcomes.OutcomeEnvelope outcome,
        TaskAcceptanceContext context,
        TaskAcceptanceResult acceptance,
        CancellationToken cancellationToken)
    {
        if (acceptance.Status != TaskAcceptanceStatus.Accepted)
        {
            return acceptance;
        }

        var declared = await new EvidenceTaskAcceptanceEvaluator().EvaluateAsync(
            task,
            outcome,
            context,
            cancellationToken).ConfigureAwait(false);
        if (declared.Status != TaskAcceptanceStatus.Accepted)
        {
            return new TaskAcceptanceResult(
                TaskAcceptanceStatus.Rejected,
                ["The configured evaluator accepted the task, but its declared acceptance contract was not satisfied.", .. declared.Reasons],
                declared.EvidenceRefs);
        }

        var unresolvedDeclared = declared.EvidenceRefs
            .Where(evidenceRef => !AcceptanceEvidenceResolver.IsResolved(evidenceRef, outcome, context.HostState))
            .ToArray();
        if (unresolvedDeclared.Length > 0)
        {
            return new TaskAcceptanceResult(
                TaskAcceptanceStatus.Rejected,
                unresolvedDeclared
                    .Select(item =>
                        $"Declared acceptance evidence '{item.Kind}:{item.RefId}' does not resolve to a concrete child-envelope object or host-state key.")
                    .ToArray(),
                []);
        }

        var unresolved = acceptance.EvidenceRefs
            .Where(evidenceRef => !AcceptanceEvidenceResolver.IsResolved(evidenceRef, outcome, context.HostState))
            .ToArray();
        if (unresolved.Length > 0)
        {
            return new TaskAcceptanceResult(
                TaskAcceptanceStatus.Rejected,
                unresolved
                    .Select(item => $"Acceptance evidence '{item.Kind}:{item.RefId}' does not resolve in the child outcome or host snapshot.")
                    .ToArray(),
                declared.EvidenceRefs);
        }

        return acceptance with
        {
            EvidenceRefs = acceptance.EvidenceRefs
                .Concat(declared.EvidenceRefs)
                .Distinct()
                .ToArray()
        };
    }

    private void Complete(
        OrchestrationState state,
        TaskGraphPlan plan,
        IReadOnlyList<Agentica.Outcomes.OutcomeEnvelope> outcomes,
        IReadOnlyDictionary<string, object?> hostState)
    {
        var finalContext = CompileContext(new WorkContextCompilationRequest(
            plan,
            state,
            null,
            outcomes.LastOrDefault(),
            null,
            state.WorkingContext,
            hostState));

        state.Status = OrchestrationStatus.Succeeded;
        state.StopReason = OrchestrationStopReason.Complete;
        state.ActiveTaskId = null;
        state.WorkingContext = finalContext;
    }

    private IReadOnlyDictionary<string, object?> ProjectHostState() =>
        OrchestrationRecordSnapshot.Request(new LargeTaskRequest(
            "Host-state projection.",
            RequestOrigin.Agent,
            _hostStateProjection() ??
            throw new InvalidOperationException("The host-state projection returned no snapshot."))).Context;

    private WorkContextSnapshot CompileContext(WorkContextCompilationRequest request)
    {
        var compiled = _contextCompiler.Compile(
            OrchestrationRecordSnapshot.CompilationRequest(request)) ??
            throw new InvalidOperationException("The work-context compiler returned no snapshot.");
        return OrchestrationRecordSnapshot.Context(compiled);
    }

    private static void ApplyPlannerFailure(OrchestrationState state, Exception exception)
    {
        if (exception is WorkflowPlannerException
            {
                FailureKind: WorkflowPlannerFailureKind.Unavailable
            })
        {
            state.Status = OrchestrationStatus.Blocked;
            state.StopReason = OrchestrationStopReason.PlannerUnavailable;
            return;
        }

        state.Status = OrchestrationStatus.PlanInvalid;
        state.StopReason = OrchestrationStopReason.PlanInvalid;
    }

    private static void ApplyFinalStateFromAcceptance(
        OrchestrationState state,
        TaskAcceptanceResult acceptance,
        Agentica.Outcomes.OutcomeEnvelope outcome)
    {
        if (TryMapChildOutcome(outcome, out var status, out var stopReason))
        {
            state.Status = status;
            state.StopReason = stopReason;
            return;
        }

        state.Status = acceptance.Status == TaskAcceptanceStatus.Rejected
            ? OrchestrationStatus.Failed
            : OrchestrationStatus.Blocked;
        state.StopReason = acceptance.Status == TaskAcceptanceStatus.Rejected
            ? OrchestrationStopReason.Failed
            : OrchestrationStopReason.Blocked;
    }

    private static void ApplyFinalStateFromRefinementBudget(
        OrchestrationState state,
        Agentica.Outcomes.OutcomeEnvelope outcome)
    {
        if (TryMapChildOutcome(outcome, out var status, out var stopReason))
        {
            state.Status = status;
            state.StopReason = stopReason;
            return;
        }

        state.Status = OrchestrationStatus.Blocked;
        state.StopReason = OrchestrationStopReason.MaxRefinementsReached;
    }

    private static bool TryMapChildOutcome(
        Agentica.Outcomes.OutcomeEnvelope outcome,
        out OrchestrationStatus status,
        out OrchestrationStopReason stopReason)
    {
        switch (outcome.Outcome.StopReason)
        {
            case Agentica.Outcomes.StopReason.TerminalLoss:
                status = OrchestrationStatus.Failed;
                stopReason = OrchestrationStopReason.TerminalLoss;
                return true;
            case Agentica.Outcomes.StopReason.TerminalDraw:
                status = OrchestrationStatus.Failed;
                stopReason = OrchestrationStopReason.TerminalDraw;
                return true;
            case Agentica.Outcomes.StopReason.PlannerUnavailable:
                status = OrchestrationStatus.Blocked;
                stopReason = OrchestrationStopReason.PlannerUnavailable;
                return true;
            case Agentica.Outcomes.StopReason.PlanInvalid:
                status = OrchestrationStatus.PlanInvalid;
                stopReason = OrchestrationStopReason.PlanInvalid;
                return true;
            case Agentica.Outcomes.StopReason.Timeout:
                status = OrchestrationStatus.Cancelled;
                stopReason = OrchestrationStopReason.Timeout;
                return true;
            case Agentica.Outcomes.StopReason.Cancelled:
                status = OrchestrationStatus.Cancelled;
                stopReason = OrchestrationStopReason.Cancelled;
                return true;
        }

        switch (outcome.Outcome.Status)
        {
            case Agentica.Outcomes.RunOutcomeStatus.PlanInvalid:
                status = OrchestrationStatus.PlanInvalid;
                stopReason = OrchestrationStopReason.PlanInvalid;
                return true;
            case Agentica.Outcomes.RunOutcomeStatus.Failed:
                status = OrchestrationStatus.Failed;
                stopReason = OrchestrationStopReason.ChildRunFailed;
                return true;
            case Agentica.Outcomes.RunOutcomeStatus.Cancelled:
                status = OrchestrationStatus.Cancelled;
                stopReason = OrchestrationStopReason.Cancelled;
                return true;
        }

        status = OrchestrationStatus.Blocked;
        stopReason = OrchestrationStopReason.Blocked;
        return false;
    }

    private static void ResetRunCountsForReplacedTasks(
        OrchestrationState state,
        TaskGraphRefinement refinement)
    {
        foreach (var mutation in refinement.Mutations)
        {
            if (mutation.Kind is TaskGraphMutationKind.ReplaceTask or TaskGraphMutationKind.RemoveTask)
            {
                state.TaskRunCounts.Remove(mutation.TaskId);
            }
        }
    }

    private static WorkContextSnapshot EmptyContext(
        string objective,
        IReadOnlyDictionary<string, object?> hostState) =>
        new(
            objective,
            null,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            hostState,
            DateTimeOffset.UtcNow);

    private OrchestrationOutcomeEnvelope Envelope(
        LargeTaskRequest request,
        TaskGraphPlan? plan,
        OrchestrationState state,
        IReadOnlyList<Agentica.Outcomes.OutcomeEnvelope> outcomes,
        DefinitionOfDoneResult? definitionOfDone,
        IReadOnlyList<string> diagnostics) =>
        OrchestrationRecordSnapshot.Envelope(
            request,
            plan,
            state,
            outcomes,
            definitionOfDone,
            diagnostics,
            _policy.MaxRuns);

    private static void ValidatePolicy(OrchestrationPolicy policy)
    {
        if (policy.MaxRuns is < 1 or > MaximumRuns)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                $"Orchestration MaxRuns must be between 1 and {MaximumRuns}.");
        }

        if (policy.MaxRefinements is < 0 or > MaximumRefinements)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                $"Orchestration MaxRefinements must be between 0 and {MaximumRefinements}.");
        }

        if (policy.MaxGraphMutationsPerRefinement is < 0 or > MaximumGraphMutationsPerRefinement)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy),
                "Orchestration MaxGraphMutationsPerRefinement must be between " +
                $"0 and {MaximumGraphMutationsPerRefinement}.");
        }
    }

    private static void AddDiagnostics(List<string> diagnostics, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            AddDiagnostic(diagnostics, value);
        }
    }

    private static void AddDiagnostic(List<string> diagnostics, string? value)
    {
        if (diagnostics.Count >= MaximumDiagnostics)
        {
            return;
        }

        var diagnostic = string.IsNullOrWhiteSpace(value)
            ? "Orchestration boundary returned an empty diagnostic."
            : value;
        diagnostics.Add(diagnostic.Length <= MaximumDiagnosticCharacters
            ? diagnostic
            : diagnostic[..(MaximumDiagnosticCharacters - 1)] + "\u2026");
    }
}
