using Agentica;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Execution;
using Agentica.Orchestration.Planning;
using Agentica.Requests;

namespace Agentica.Orchestration;

public sealed class TaskOrchestrator
{
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
    }

    public async Task<OrchestrationOutcomeEnvelope> RunAsync(
        LargeTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var plan = await _taskPlanner.CreatePlanAsync(
            new TaskPlanningRequest(request, _policy),
            cancellationToken).ConfigureAwait(false);
        TaskGraphValidator.Validate(plan);

        var orchestrationId = AgenticaIds.New("orchestration");
        var state = new OrchestrationState(orchestrationId, EmptyContext(plan, _hostStateProjection()))
        {
            Status = OrchestrationStatus.Running
        };
        state.WorkingContext = _contextCompiler.Compile(new WorkContextCompilationRequest(
            plan,
            state,
            null,
            null,
            null,
            null,
            _hostStateProjection()));
        var outcomes = new List<Agentica.Outcomes.OutcomeEnvelope>();

        for (var runIndex = 0; runIndex < _policy.MaxRuns; runIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TaskGraph.RequiredTasksComplete(plan, state))
            {
                state.Status = OrchestrationStatus.Succeeded;
                state.StopReason = OrchestrationStopReason.Complete;
                state.ActiveTaskId = null;
                state.WorkingContext = _contextCompiler.Compile(new WorkContextCompilationRequest(
                    plan,
                    state,
                    null,
                    outcomes.LastOrDefault(),
                    null,
                    state.WorkingContext,
                    _hostStateProjection()));
                return Envelope(request, plan, state, outcomes);
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
                var reasons = exhausted.Length > 0
                    ? exhausted
                        .Select(task => $"Task '{task.TaskId}' reached its maxRuns budget of {task.MaxRuns}.")
                        .ToArray()
                    : ["No available task can advance the orchestration."];
                state.Status = OrchestrationStatus.Blocked;
                state.StopReason = exhausted.Length > 0
                    ? OrchestrationStopReason.MaxRunsReached
                    : OrchestrationStopReason.Blocked;
                state.WorkingContext = _contextCompiler.Compile(new WorkContextCompilationRequest(
                    plan,
                    state,
                    null,
                    outcomes.LastOrDefault(),
                    new TaskAcceptanceResult(TaskAcceptanceStatus.Blocked, reasons, []),
                    state.WorkingContext,
                    _hostStateProjection()));
                return Envelope(request, plan, state, outcomes);
            }

            state.ActiveTaskId = task.TaskId;
            state.TaskRunCounts[task.TaskId] = RunCount(state, task.TaskId) + 1;
            var runRequest = BuildRunRequest(request, task, state);
            var outcome = await _runExecutor.RunAsync(runRequest, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);

            var acceptance = await _acceptanceEvaluator.EvaluateAsync(
                task,
                outcome,
                new TaskAcceptanceContext(plan, state, state.WorkingContext, _hostStateProjection()),
                cancellationToken).ConfigureAwait(false);

            if (acceptance.Status == TaskAcceptanceStatus.Accepted)
            {
                state.CompletedTaskIds.Add(task.TaskId);
                state.RunRefs.Add(new RunRef(task.TaskId, outcome.Outcome.RunId, outcome.Outcome.Status, acceptance.EvidenceRefs));
                state.WorkingContext = _contextCompiler.Compile(new WorkContextCompilationRequest(
                    plan,
                    state,
                    task,
                    outcome,
                    acceptance,
                    state.WorkingContext,
                    _hostStateProjection()));

                if (!acceptance.RequiresGraphRefinement)
                {
                    continue;
                }
            }
            else
            {
                state.WorkingContext = _contextCompiler.Compile(new WorkContextCompilationRequest(
                    plan,
                    state,
                    task,
                    outcome,
                    acceptance,
                    state.WorkingContext,
                    _hostStateProjection()));
            }

            if (!acceptance.ShouldRefine)
            {
                state.BlockedTaskIds.Add(task.TaskId);
                ApplyFinalStateFromAcceptance(state, acceptance, outcome);
                return Envelope(request, plan, state, outcomes);
            }

            if (state.RefinementCount >= _policy.MaxRefinements)
            {
                ApplyFinalStateFromRefinementBudget(state, outcome);
                return Envelope(request, plan, state, outcomes);
            }

            var refinement = await _taskPlanner.RefinePlanAsync(
                new TaskRefinementRequest(
                    request,
                    plan,
                    state,
                    task,
                    outcome,
                    acceptance,
                    state.WorkingContext,
                    _policy),
                cancellationToken).ConfigureAwait(false);
            state.RefinementCount++;

            if (refinement.RequiresUserInput || refinement.Mutations.Count == 0)
            {
                state.BlockedTaskIds.Add(task.TaskId);
                state.Status = OrchestrationStatus.Blocked;
                state.StopReason = OrchestrationStopReason.Blocked;
                return Envelope(request, plan, state, outcomes);
            }

            if (refinement.Mutations.Count > _policy.MaxGraphMutationsPerRefinement)
            {
                state.Status = OrchestrationStatus.PlanInvalid;
                state.StopReason = OrchestrationStopReason.PlanInvalid;
                return Envelope(request, plan, state, outcomes);
            }

            var previousPlan = plan;
            plan = TaskGraphMutationApplier.Apply(plan, refinement);
            TaskGraphValidator.Validate(plan, state, previousPlan);
            ResetRunCountsForReplacedTasks(state, refinement);
        }

        state.Status = OrchestrationStatus.Blocked;
        state.StopReason = OrchestrationStopReason.MaxRunsReached;
        return Envelope(request, plan, state, outcomes);
    }

    private static RunRequest BuildRunRequest(
        LargeTaskRequest request,
        TaskNode task,
        OrchestrationState state)
    {
        var context = new Dictionary<string, object?>(request.Context, StringComparer.Ordinal);
        foreach (var pair in task.ContextProjection)
        {
            context[pair.Key] = pair.Value;
        }

        context["orchestration.id"] = state.OrchestrationId;
        context["orchestration.taskId"] = task.TaskId;
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

    private static int RunCount(OrchestrationState state, string taskId) =>
        state.TaskRunCounts.TryGetValue(taskId, out var count) ? count : 0;

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
        TaskGraphPlan plan,
        IReadOnlyDictionary<string, object?> hostState) =>
        new(
            plan.Objective,
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

    private static OrchestrationOutcomeEnvelope Envelope(
        LargeTaskRequest request,
        TaskGraphPlan plan,
        OrchestrationState state,
        IReadOnlyList<Agentica.Outcomes.OutcomeEnvelope> outcomes) =>
        new(
            state.OrchestrationId,
            state.Status,
            state.StopReason,
            request.Objective,
            plan,
            state,
            state.WorkingContext,
            outcomes,
            state.WorkingContext.EvidenceRefs);
}
