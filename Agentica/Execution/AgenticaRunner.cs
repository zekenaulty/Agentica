using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;
using Agentica.Tools;
using Agentica.Validation;

namespace Agentica.Execution;

public sealed class AgenticaRunner
{
    private readonly IWorkflowPlanner _planner;
    private readonly ToolCatalog _toolCatalog;
    private readonly IEventSink _eventSink;
    private readonly IOutcomeReporter _outcomeReporter;
    private readonly ExecutionPolicy _policy;
    private readonly ICompletionEvaluator _completionEvaluator;
    private readonly PlanExecutionValidator _planValidator;
    private readonly PlanningRequestFactory _planningRequestFactory;
    private readonly BlockedRetryRequestFactory _blockedRetryRequestFactory;

    public AgenticaRunner(
        IWorkflowPlanner planner,
        ToolCatalog toolCatalog,
        IEventSink eventSink,
        IOutcomeReporter outcomeReporter,
        ExecutionPolicy? policy = null,
        ICompletionEvaluator? completionEvaluator = null)
    {
        _planner = planner;
        _toolCatalog = toolCatalog;
        _eventSink = eventSink;
        _outcomeReporter = outcomeReporter;
        _policy = policy ?? ExecutionPolicy.Default;
        _completionEvaluator = completionEvaluator ?? PlanExhaustionCompletionEvaluator.Instance;
        _planValidator = new PlanExecutionValidator(_toolCatalog, _policy);
        _planningRequestFactory = new PlanningRequestFactory(_toolCatalog, _policy);
        _blockedRetryRequestFactory = new BlockedRetryRequestFactory(_policy);
    }

    public async Task<OutcomeEnvelope> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        var originalRequest = request;
        var currentRequest = request;
        var attempts = new List<RunAttemptSummary>();
        var attemptNumber = 1;

        while (true)
        {
            var envelope = await RunAttemptAsync(currentRequest, cancellationToken).ConfigureAwait(false);
            attempts.Add(RunAttemptSummary.From(attemptNumber, envelope));

            if (envelope.Outcome.Status != RunOutcomeStatus.Blocked ||
                attemptNumber > _policy.MaxBlockedRetries)
            {
                return AttachAttemptSummaries(envelope, attempts);
            }

            currentRequest = _blockedRetryRequestFactory.Create(originalRequest, envelope, attemptNumber + 1);
            attemptNumber++;
        }
    }

    private async Task<OutcomeEnvelope> RunAttemptAsync(RunRequest request, CancellationToken cancellationToken)
    {
        using var timeoutCts = _policy.Timeout is { } timeout
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (timeoutCts is not null && _policy.Timeout is { } timeoutValue)
        {
            timeoutCts.CancelAfter(timeoutValue);
        }

        var ct = timeoutCts?.Token ?? cancellationToken;
        var run = new AgenticaRun(AgenticaIds.New("run"), request);

        Emit(run, ExecutionEventType.RunCreated, ("run", run.RunId));
        Emit(run, ExecutionEventType.RequestAccepted, ("origin", request.Origin.ToString()));

        if (!request.IsValid)
        {
            return Finish(
                run,
                RunOutcomeStatus.PlanInvalid,
                StopReason.PlanInvalid,
                validationIssues:
                [
                    new ValidationIssue("request.objective.required", "Run objective is required.")
                ]);
        }

        try
        {
            WorkflowPlan currentPlan;
            try
            {
                currentPlan = await _planner
                    .CreatePlanAsync(_planningRequestFactory.Create(request, run), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WorkflowPlannerException exception)
            {
                return FinishPlannerFailure(run, exception, "planner.create.failed");
            }
            catch (Exception exception)
            {
                return Finish(
                    run,
                    RunOutcomeStatus.PlanInvalid,
                    StopReason.PlanInvalid,
                    validationIssues:
                    [
                        new ValidationIssue(
                            "planner.create.failed",
                            $"Planner failed to create a valid plan: {exception.Message}")
                    ]);
            }

            run.PlanVersions.Add(currentPlan);
            EmitPlanCreated(run, currentPlan);

            var executedSteps = new HashSet<string>(StringComparer.Ordinal);
            var validationIssues = ValidatePlan(currentPlan, executedSteps);
            if (validationIssues.Count > 0)
            {
                return Finish(run, RunOutcomeStatus.PlanInvalid, StopReason.PlanInvalid, validationIssues);
            }

            var refinementCount = 0;
            var continuationCount = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (executedSteps.Count >= _policy.MaxSteps)
                {
                    return Finish(
                        run,
                        RunOutcomeStatus.Blocked,
                        StopReason.StepLimitReached,
                        blockers: ["Maximum step count reached."]);
                }

                var nextSteps = SelectNextSteps(currentPlan, executedSteps);
                if (nextSteps.Count == 0)
                {
                    var completion = _completionEvaluator.Evaluate(run);
                    if (completion.Decision == CompletionDecision.Complete)
                    {
                        return Finish(run, RunOutcomeStatus.Succeeded, completion.StopReason, blockers: completion.Blockers);
                    }

                    if (completion.Decision == CompletionDecision.Partial)
                    {
                        return Finish(run, RunOutcomeStatus.PartiallyComplete, completion.StopReason, blockers: completion.Blockers);
                    }

                    if (completion.Decision == CompletionDecision.Blocked)
                    {
                        return Finish(run, RunOutcomeStatus.Blocked, completion.StopReason, blockers: completion.Blockers);
                    }

                    if (continuationCount >= _policy.MaxPlanContinuations)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.Blocked,
                            StopReason.ContinuationLimitReached,
                            blockers: completion.Blockers.Count == 0
                                ? ["Completion was not satisfied and no plan continuations remain."]
                                : completion.Blockers);
                    }

                    try
                    {
                        currentPlan = await _planner
                            .CreatePlanAsync(_planningRequestFactory.Create(request, run), ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (WorkflowPlannerException exception)
                    {
                        return FinishPlannerFailure(run, exception, "planner.continue.failed");
                    }
                    catch (Exception exception)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.PlanInvalid,
                            StopReason.PlanInvalid,
                            validationIssues:
                            [
                                new ValidationIssue(
                                    "planner.continue.failed",
                                    $"Planner failed to continue the plan: {exception.Message}")
                            ]);
                    }

                    continuationCount++;
                    run.PlanVersions.Add(currentPlan);
                    EmitPlanCreated(run, currentPlan);

                    validationIssues = ValidatePlan(currentPlan, executedSteps);
                    if (validationIssues.Count > 0)
                    {
                        return Finish(run, RunOutcomeStatus.PlanInvalid, StopReason.PlanInvalid, validationIssues);
                    }

                    continue;
                }

                if (executedSteps.Count + nextSteps.Count > _policy.MaxSteps)
                {
                    return Finish(
                        run,
                        RunOutcomeStatus.Blocked,
                        StopReason.StepLimitReached,
                        blockers: ["Maximum step count would be exceeded by the next executable batch."]);
                }

                var executionResults = await ExecuteStepsAsync(run, nextSteps, ct).ConfigureAwait(false);

                foreach (var executionResult in executionResults)
                {
                    executedSteps.Add(executionResult.Step.StepId);
                    RecordToolResult(run, executionResult.Step, executionResult.Result);
                }

                var waitingResult = executionResults.FirstOrDefault(item =>
                    item.Result.Receipt.Status is ReceiptStatus.WaitingForApproval);
                if (waitingResult is not null)
                {
                    return Finish(
                        run,
                        RunOutcomeStatus.WaitingForApproval,
                        StopReason.WaitingForApproval,
                        blockers: [waitingResult.Result.Receipt.Message]);
                }

                if (executionResults.Any(item =>
                    item.Result.Receipt.Status is ReceiptStatus.Failed or ReceiptStatus.TimedOut or ReceiptStatus.Cancelled))
                {
                    return Finish(run, RunOutcomeStatus.Failed, StopReason.ToolFailure);
                }

                var refinementCandidate = executionResults.FirstOrDefault(item =>
                    item.Result.Observation is not null && ShouldRefineAfterToolResult(item.Step, item.Result));
                if (refinementCandidate is not null &&
                    refinementCandidate.Result.Observation is { } observation)
                {
                    if (refinementCount >= _policy.MaxRefinements)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.Blocked,
                            StopReason.RefinementLimitReached,
                            blockers: ["Maximum plan refinement count reached."]);
                    }

                    WorkflowPlan refinedPlan;
                    try
                    {
                        refinedPlan = await _planner
                            .RefinePlanAsync(_planningRequestFactory.Create(request, run), observation, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (WorkflowPlannerException exception)
                    {
                        return FinishPlannerFailure(run, exception, "planner.refine.failed");
                    }
                    catch (Exception exception)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.PlanInvalid,
                            StopReason.PlanInvalid,
                            validationIssues:
                            [
                                new ValidationIssue(
                                    "planner.refine.failed",
                                    $"Planner failed to refine the plan: {exception.Message}")
                            ]);
                    }

                    run.PlanRefinements.Add(new PlanRefinement(
                        FromPlanId: currentPlan.PlanId,
                        ToPlanId: refinedPlan.PlanId,
                        Reason: PlanRefinementReasons.Normalize(
                            refinedPlan.PlanningReason,
                            refinementCandidate.Result.Receipt.Status is ReceiptStatus.Unavailable or ReceiptStatus.Refused
                                ? PlanRefinementReasons.Blocked
                                : PlanRefinementReasons.Observation),
                        Evidence:
                        [
                            new EvidenceRef("observation", observation.ObservationId),
                            new EvidenceRef("receipt", refinementCandidate.Result.Receipt.ReceiptId)
                        ]));

                    currentPlan = refinedPlan;
                    run.PlanVersions.Add(currentPlan);
                    refinementCount++;

                    Emit(
                        run,
                        ExecutionEventType.PlanRefined,
                        ("plan", currentPlan.PlanId),
                        ("reason", run.PlanRefinements[^1].Reason));

                    validationIssues = ValidatePlan(currentPlan, executedSteps);
                    if (validationIssues.Count > 0)
                    {
                        return Finish(run, RunOutcomeStatus.PlanInvalid, StopReason.PlanInvalid, validationIssues);
                    }

                    continue;
                }

                var blockedResult = executionResults.FirstOrDefault(item =>
                    item.Result.Receipt.Status is ReceiptStatus.Unavailable or ReceiptStatus.Refused);
                if (blockedResult is not null)
                {
                    return Finish(
                        run,
                        RunOutcomeStatus.Blocked,
                        StopReason.ToolUnavailable,
                        blockers: [blockedResult.Result.Receipt.Message]);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Finish(run, RunOutcomeStatus.Cancelled, StopReason.Timeout, blockers: ["Run timed out."]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(run, RunOutcomeStatus.Cancelled, StopReason.Cancelled, blockers: ["Run was cancelled."]);
        }
    }

    public IReadOnlyList<ValidationIssue> ValidatePlan(WorkflowPlan plan) =>
        _planValidator.Validate(plan);

    private IReadOnlyList<ValidationIssue> ValidatePlan(
        WorkflowPlan plan,
        IReadOnlySet<string> completedStepIds) =>
        _planValidator.Validate(plan, completedStepIds);

    private IReadOnlyList<PlanStep> SelectNextSteps(
        WorkflowPlan plan,
        IReadOnlySet<string> executedSteps)
    {
        var nextIndex = plan.Steps
            .Select((step, index) => new { Step = step, Index = index })
            .FirstOrDefault(item => !executedSteps.Contains(item.Step.StepId));
        if (nextIndex is null)
        {
            return [];
        }

        var nextStep = nextIndex.Step;
        if (!DependenciesSatisfied(nextStep, executedSteps))
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(nextStep.BatchId))
        {
            return [nextStep];
        }

        var batchSteps = new List<PlanStep>();
        for (var index = nextIndex.Index; index < plan.Steps.Count; index++)
        {
            var candidate = plan.Steps[index];
            if (executedSteps.Contains(candidate.StepId) ||
                !string.Equals(candidate.BatchId, nextStep.BatchId, StringComparison.Ordinal) ||
                !DependenciesSatisfied(candidate, executedSteps))
            {
                break;
            }

            batchSteps.Add(candidate);
            if (batchSteps.Count >= Math.Min(_policy.MaxBatchSize, _policy.MaxParallelism))
            {
                break;
            }
        }

        return batchSteps.Count == 0 ? [nextStep] : batchSteps;
    }

    private static bool DependenciesSatisfied(PlanStep step, IReadOnlySet<string> executedSteps) =>
        step.DependsOn.All(executedSteps.Contains);

    private async Task<IReadOnlyList<StepExecutionResult>> ExecuteStepsAsync(
        AgenticaRun run,
        IReadOnlyList<PlanStep> steps,
        CancellationToken cancellationToken)
    {
        if (steps.Count == 1)
        {
            var step = steps[0];
            Emit(
                run,
                ExecutionEventType.StepStarted,
                ("step", step.StepId),
                ("tool", step.ToolId));

            return [await ExecuteStepAsync(run, step, cancellationToken).ConfigureAwait(false)];
        }

        var batchId = steps[0].BatchId ?? AgenticaIds.New("batch");
        var startedAt = DateTimeOffset.UtcNow;
        Emit(
            run,
            ExecutionEventType.BatchStarted,
            ("batch", batchId),
            ("steps", steps.Count.ToString()));

        foreach (var step in steps)
        {
            Emit(
                run,
                ExecutionEventType.StepStarted,
                ("step", step.StepId),
                ("tool", step.ToolId),
                ("batch", batchId));
        }

        var tasks = steps
            .Select(step => ExecuteStepAsync(run, step, cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var completedAt = DateTimeOffset.UtcNow;

        run.Batches.Add(new ExecutionBatch(
            batchId,
            steps.Select(step => step.StepId).ToArray(),
            startedAt,
            completedAt));

        Emit(
            run,
            ExecutionEventType.BatchCompleted,
            ("batch", batchId),
            ("steps", steps.Count.ToString()));

        return results;
    }

    private async Task<StepExecutionResult> ExecuteStepAsync(
        AgenticaRun run,
        PlanStep step,
        CancellationToken cancellationToken)
    {
        var registration = _toolCatalog.Resolve(step.ToolId);
        if (registration is null)
        {
            return new StepExecutionResult(
                step,
                new ToolResult(new Receipt(
                    AgenticaIds.New("receipt"),
                    step.StepId,
                    step.ToolId,
                    ReceiptStatus.Unavailable,
                    $"Unknown tool '{step.ToolId}'.",
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, object?>())));
        }

        try
        {
            var result = await registration.Tool.ExecuteAsync(
                new ToolInvocation(run.RunId, step.StepId, step.ToolId, step.Input),
                cancellationToken).ConfigureAwait(false);
            return new StepExecutionResult(step, result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new StepExecutionResult(
                step,
                new ToolResult(new Receipt(
                    AgenticaIds.New("receipt"),
                    step.StepId,
                    step.ToolId,
                    ReceiptStatus.Failed,
                    $"Tool '{step.ToolId}' failed: {exception.Message}",
                    DateTimeOffset.UtcNow,
                    new Dictionary<string, object?>
                    {
                        ["errorClass"] = exception.GetType().Name
                    })));
        }
    }

    private void RecordToolResult(AgenticaRun run, PlanStep step, ToolResult result)
    {
        run.CompletedSteps.Add(step.StepId);
        run.Receipts.Add(result.Receipt);

        if (result.Observation is not null)
        {
            run.Observations.Add(result.Observation);
            Emit(
                run,
                ExecutionEventType.ObservationMade,
                ("observation", result.Observation.ObservationId),
                ("step", step.StepId));
        }

        if (result.Artifact is not null)
        {
            run.Artifacts.Add(result.Artifact);
        }

        Emit(
            run,
            ExecutionEventType.ReceiptEmitted,
            ("receipt", result.Receipt.ReceiptId),
            ("status", result.Receipt.Status.ToString().ToLowerInvariant()));
    }

    private bool ShouldRefineAfterToolResult(PlanStep step, ToolResult result)
    {
        if (result.Observation is null)
        {
            return false;
        }

        var isBlocker = result.Receipt.Status is ReceiptStatus.Unavailable or ReceiptStatus.Refused;

        return _policy.PlanningMode switch
        {
            PlanningMode.Stepwise => true,
            PlanningMode.QueryAndBlockerDriven => step.Kind == ToolKind.Query || isBlocker,
            PlanningMode.BlockerDriven => isBlocker,
            PlanningMode.PlanOnly => false,
            _ => true
        };
    }

    private OutcomeEnvelope AttachAttemptSummaries(
        OutcomeEnvelope envelope,
        IReadOnlyList<RunAttemptSummary> attempts) =>
        envelope with
        {
            Details = envelope.Details with
            {
                RunAttempts = attempts.ToArray()
            }
        };

    private OutcomeEnvelope Finish(
        AgenticaRun run,
        RunOutcomeStatus status,
        StopReason stopReason,
        IReadOnlyList<ValidationIssue>? validationIssues = null,
        IReadOnlyList<string>? blockers = null)
    {
        validationIssues ??= [];
        blockers ??= [];
        run.Status = status;

        var completionEvidence = run.Receipts
            .Where(receipt => receipt.Status == ReceiptStatus.Succeeded)
            .Select(receipt => new EvidenceRef("receipt", receipt.ReceiptId))
            .Concat(run.Artifacts.Select(artifact => new EvidenceRef("artifact", artifact.ArtifactId)))
            .ToArray();

        var report = _outcomeReporter.BuildReport(run, status, stopReason, validationIssues, blockers);
        Emit(
            run,
            ExecutionEventType.OutcomeReported,
            ("report", report.ReportId),
            ("refs", report.Claims.Sum(claim => claim.Evidence.Count).ToString()));

        Emit(run, TerminalEventFor(status), ("run", run.RunId));

        return new OutcomeEnvelope(
            Outcome: new RunOutcome(
                RunId: run.RunId,
                Status: status,
                StopReason: stopReason,
                CompletedSteps: run.CompletedSteps.ToArray(),
                Blockers: blockers,
                CompletionEvidence: completionEvidence),
            Report: report,
            Receipts: new ReceiptEnvelope(run.Receipts.ToArray()),
            Details: new DetailEnvelope(
                Request: run.Request,
                PlanVersions: run.PlanVersions.ToArray(),
                PlanRefinements: run.PlanRefinements.ToArray(),
                Observations: run.Observations.ToArray(),
                Artifacts: run.Artifacts.ToArray(),
                Batches: run.Batches.ToArray(),
                Events: run.Events.ToArray(),
                ValidationIssues: validationIssues));
    }

    private void EmitPlanCreated(AgenticaRun run, WorkflowPlan plan)
    {
        Emit(
            run,
            ExecutionEventType.PlanCreated,
            ("plan", plan.PlanId),
            ("version", plan.Version.ToString()),
            ("steps", plan.Steps.Count.ToString()));
    }

    private void Emit(AgenticaRun run, ExecutionEventType type, params (string Key, string Value)[] data)
    {
        var executionEvent = new ExecutionEvent(
            EventId: AgenticaIds.New("event"),
            Type: type.WireName(),
            At: DateTimeOffset.UtcNow,
            Data: data.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

        run.Events.Add(executionEvent);
        _eventSink.Emit(executionEvent);
    }

    private static ExecutionEventType TerminalEventFor(RunOutcomeStatus status) =>
        status switch
        {
            RunOutcomeStatus.Succeeded => ExecutionEventType.RunSucceeded,
            RunOutcomeStatus.PlanInvalid => ExecutionEventType.RunFailed,
            RunOutcomeStatus.Blocked => ExecutionEventType.RunBlocked,
            RunOutcomeStatus.Failed => ExecutionEventType.RunFailed,
            _ => ExecutionEventType.RunStopped
        };

    private OutcomeEnvelope FinishPlannerFailure(
        AgenticaRun run,
        WorkflowPlannerException exception,
        string defaultCode)
    {
        if (exception.FailureKind == WorkflowPlannerFailureKind.Unavailable)
        {
            return Finish(
                run,
                RunOutcomeStatus.Blocked,
                StopReason.PlannerUnavailable,
                blockers: [exception.Message]);
        }

        return Finish(
            run,
            RunOutcomeStatus.PlanInvalid,
            StopReason.PlanInvalid,
            validationIssues:
            [
                new ValidationIssue(
                    string.IsNullOrWhiteSpace(exception.Code) ? defaultCode : exception.Code,
                    exception.Message)
            ]);
    }

    private sealed record StepExecutionResult(PlanStep Step, ToolResult Result);
}
