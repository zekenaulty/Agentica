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

            currentRequest = CreateBlockedRetryRequest(originalRequest, envelope, attemptNumber + 1);
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
                    .CreatePlanAsync(CreatePlanningRequest(request, run), ct)
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

            var validationIssues = ValidatePlan(currentPlan);
            if (validationIssues.Count > 0)
            {
                return Finish(run, RunOutcomeStatus.PlanInvalid, StopReason.PlanInvalid, validationIssues);
            }

            var executedSteps = new HashSet<string>(StringComparer.Ordinal);
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

                var nextStep = currentPlan.Steps.FirstOrDefault(step => !executedSteps.Contains(step.StepId));
                if (nextStep is null)
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
                            .CreatePlanAsync(CreatePlanningRequest(request, run), ct)
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

                    validationIssues = ValidatePlan(currentPlan);
                    if (validationIssues.Count > 0)
                    {
                        return Finish(run, RunOutcomeStatus.PlanInvalid, StopReason.PlanInvalid, validationIssues);
                    }

                    continue;
                }

                var registration = _toolCatalog.Resolve(nextStep.ToolId);
                if (registration is null)
                {
                    return Finish(
                        run,
                        RunOutcomeStatus.Blocked,
                        StopReason.UnknownTool,
                        blockers: [$"Unknown tool '{nextStep.ToolId}'."]);
                }

                Emit(
                    run,
                    ExecutionEventType.StepStarted,
                    ("step", nextStep.StepId),
                    ("tool", nextStep.ToolId));

                var result = await registration.Tool.ExecuteAsync(
                    new ToolInvocation(run.RunId, nextStep.StepId, nextStep.ToolId, nextStep.Input),
                    ct).ConfigureAwait(false);

                executedSteps.Add(nextStep.StepId);
                run.CompletedSteps.Add(nextStep.StepId);
                run.Receipts.Add(result.Receipt);

                if (result.Observation is not null)
                {
                    run.Observations.Add(result.Observation);
                    Emit(
                        run,
                        ExecutionEventType.ObservationMade,
                        ("observation", result.Observation.ObservationId),
                        ("step", nextStep.StepId));
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

                if (result.Receipt.Status is ReceiptStatus.WaitingForApproval)
                {
                    return Finish(
                        run,
                        RunOutcomeStatus.WaitingForApproval,
                        StopReason.WaitingForApproval,
                        blockers: [result.Receipt.Message]);
                }

                if (result.Receipt.Status is ReceiptStatus.Failed or ReceiptStatus.TimedOut or ReceiptStatus.Cancelled)
                {
                    return Finish(run, RunOutcomeStatus.Failed, StopReason.ToolFailure);
                }

                if (result.Observation is { } observation && ShouldRefineAfterToolResult(nextStep, result))
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
                            .RefinePlanAsync(CreatePlanningRequest(request, run), observation, ct)
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
                        Reason: result.Receipt.Status is ReceiptStatus.Unavailable or ReceiptStatus.Refused
                            ? "blocker"
                            : "observation",
                        Evidence:
                        [
                            new EvidenceRef("observation", observation.ObservationId),
                            new EvidenceRef("receipt", result.Receipt.ReceiptId)
                        ]));

                    currentPlan = refinedPlan;
                    run.PlanVersions.Add(currentPlan);
                    refinementCount++;

                    Emit(
                        run,
                        ExecutionEventType.PlanRefined,
                        ("plan", currentPlan.PlanId),
                        ("reason", run.PlanRefinements[^1].Reason));

                    validationIssues = ValidatePlan(currentPlan);
                    if (validationIssues.Count > 0)
                    {
                        return Finish(run, RunOutcomeStatus.PlanInvalid, StopReason.PlanInvalid, validationIssues);
                    }

                    continue;
                }

                if (result.Receipt.Status is ReceiptStatus.Unavailable or ReceiptStatus.Refused)
                {
                    return Finish(
                        run,
                        RunOutcomeStatus.Blocked,
                        StopReason.ToolUnavailable,
                        blockers: [result.Receipt.Message]);
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

    public IReadOnlyList<ValidationIssue> ValidatePlan(WorkflowPlan plan)
    {
        var issues = new List<ValidationIssue>();

        if (plan.Steps.Count == 0)
        {
            issues.Add(new ValidationIssue(
                "plan.steps.required",
                $"Plan '{plan.PlanId}' must include at least one step."));
        }

        foreach (var step in plan.Steps)
        {
            var registration = _toolCatalog.Resolve(step.ToolId);
            if (registration is null)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.unknown_tool",
                    $"Step '{step.StepId}' references unknown tool '{step.ToolId}'.",
                    step.StepId));
                continue;
            }

            if (registration.Descriptor.Kind != step.Kind)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.kind_mismatch",
                    $"Step '{step.StepId}' kind '{step.Kind}' does not match tool kind '{registration.Descriptor.Kind}'.",
                    step.StepId));
            }

            if (registration.Descriptor.Effect != step.Effect)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.effect_mismatch",
                    $"Step '{step.StepId}' effect '{step.Effect}' does not match tool effect '{registration.Descriptor.Effect}'.",
                    step.StepId));
            }

            if (!_policy.EffectiveEffectPolicy.Allows(registration.Descriptor.Effect))
            {
                issues.Add(new ValidationIssue(
                    "plan.step.effect_not_allowed",
                    $"Step '{step.StepId}' references tool effect '{registration.Descriptor.Effect}' which is not allowed by policy.",
                    step.StepId));
            }

            if (step.Effect != ToolEffect.ReadOnly && step.Kind != ToolKind.Action)
            {
                issues.Add(new ValidationIssue(
                    "plan.step.mutation_hidden",
                    $"Step '{step.StepId}' has mutation effect but is not an action step.",
                    step.StepId));
            }

            issues.AddRange(ToolInputValidator.Validate(step, registration.Descriptor.InputSchema));
        }

        return issues;
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

    private PlanningRequest CreatePlanningRequest(RunRequest request, AgenticaRun run)
    {
        var context = _policy.EffectivePlanningContext;
        return new PlanningRequest(
            request,
            _toolCatalog.Descriptors,
            Limit(run.Observations, context.MaxRecentObservations),
            Limit(run.Receipts, context.MaxRecentReceipts));
    }

    private static IReadOnlyList<T> Limit<T>(IReadOnlyList<T> items, int? maxItems) =>
        maxItems switch
        {
            null => items,
            <= 0 => [],
            _ => items.TakeLast(maxItems.Value).ToArray()
        };

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

    private RunRequest CreateBlockedRetryRequest(
        RunRequest originalRequest,
        OutcomeEnvelope blockedEnvelope,
        int nextAttemptNumber)
    {
        var context = originalRequest.Context is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(originalRequest.Context, StringComparer.Ordinal);

        context.Remove("agentica.retry");
        context["agentica.retry"] = CreateBlockedRetryContext(blockedEnvelope, nextAttemptNumber);

        return originalRequest with
        {
            Origin = RequestOrigin.Agent,
            Context = context
        };
    }

    private IReadOnlyDictionary<string, object?> CreateBlockedRetryContext(
        OutcomeEnvelope blockedEnvelope,
        int nextAttemptNumber)
    {
        var context = _policy.EffectivePlanningContext;
        var previousAttempt = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runId"] = blockedEnvelope.Outcome.RunId,
            ["status"] = blockedEnvelope.Outcome.Status.ToString(),
            ["stopReason"] = blockedEnvelope.Outcome.StopReason.ToString(),
            ["blockers"] = blockedEnvelope.Outcome.Blockers,
            ["completedSteps"] = blockedEnvelope.Outcome.CompletedSteps,
            ["completionEvidence"] = blockedEnvelope.Outcome.CompletionEvidence
        };

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["attemptNumber"] = nextAttemptNumber,
            ["previousAttemptNumber"] = nextAttemptNumber - 1,
            ["maxBlockedRetries"] = _policy.MaxBlockedRetries,
            ["remainingBlockedRetries"] = Math.Max(0, _policy.MaxBlockedRetries - (nextAttemptNumber - 1)),
            ["instruction"] = "The previous Agentica run ended blocked. Use the supplied status, blockers, recent observations, recent receipts, validation issues, and available tools to plan a bounded strategy to unblock or resume. Do not claim success; Agentica will validate and execute any proposed plan.",
            ["previousAttempt"] = previousAttempt,
            ["recentReceipts"] = Limit(blockedEnvelope.Receipts.Items, context.MaxRecentReceipts)
                .Select(CreateReceiptContext)
                .ToArray(),
            ["recentObservations"] = Limit(blockedEnvelope.Details.Observations, context.MaxRecentObservations)
                .Select(CreateObservationContext)
                .ToArray(),
            ["validationIssues"] = blockedEnvelope.Details.ValidationIssues
                .Select(issue => new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = issue.Code,
                    ["message"] = issue.Message,
                    ["stepId"] = issue.StepId
                })
                .ToArray()
        };
    }

    private static IReadOnlyDictionary<string, object?> CreateReceiptContext(Receipt receipt) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["receiptId"] = receipt.ReceiptId,
            ["stepId"] = receipt.StepId,
            ["toolId"] = receipt.ToolId,
            ["status"] = receipt.Status.ToString(),
            ["message"] = receipt.Message,
            ["data"] = receipt.Data
        };

    private static IReadOnlyDictionary<string, object?> CreateObservationContext(Observation observation) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["observationId"] = observation.ObservationId,
            ["stepId"] = observation.StepId,
            ["kind"] = observation.Kind.ToString(),
            ["summary"] = observation.Summary,
            ["data"] = observation.Data,
            ["evidence"] = observation.Evidence
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
}
