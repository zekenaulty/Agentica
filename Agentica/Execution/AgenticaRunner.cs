using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Agentica.Artifacts;
using Agentica.Continuity;
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
    private readonly BlockedRetryEvaluator _blockedRetryEvaluator;
    private readonly IUserFacingReasonProjector _userFacingReasonProjector;

    public AgenticaRunner(
        IWorkflowPlanner planner,
        ToolCatalog toolCatalog,
        IEventSink eventSink,
        IOutcomeReporter outcomeReporter,
        ExecutionPolicy? policy,
        ICompletionEvaluator completionEvaluator,
        IPlanningFrameProjector? planningFrameProjector = null,
        IUserFacingReasonProjector? userFacingReasonProjector = null)
    {
        ArgumentNullException.ThrowIfNull(completionEvaluator);

        _planner = planner;
        _toolCatalog = toolCatalog;
        _eventSink = eventSink;
        _outcomeReporter = outcomeReporter;
        _policy = policy ?? ExecutionPolicy.Default;
        _completionEvaluator = completionEvaluator;
        _planValidator = new PlanExecutionValidator(_toolCatalog, _policy);
        _planningRequestFactory = new PlanningRequestFactory(_toolCatalog, _policy, planningFrameProjector);
        _blockedRetryRequestFactory = new BlockedRetryRequestFactory(_policy);
        _blockedRetryEvaluator = new BlockedRetryEvaluator(_toolCatalog, _policy.EffectiveBlockedRetries);
        _userFacingReasonProjector = userFacingReasonProjector ?? DefaultUserFacingReasonProjector.Instance;
    }

    public async Task<OutcomeEnvelope> RunAsync(RunRequest request, CancellationToken cancellationToken = default)
    {
        var originalRequest = request;
        var currentRequest = request;
        var attempts = new List<RunAttemptSummary>();
        var attemptEnvelopes = new List<OutcomeEnvelope>();
        var attemptNumber = 1;

        while (true)
        {
            var envelope = await RunAttemptAsync(currentRequest, attemptNumber, cancellationToken).ConfigureAwait(false);
            attempts.Add(RunAttemptSummary.From(attemptNumber, envelope));
            attemptEnvelopes.Add(envelope);

            if (attemptNumber > _policy.MaxBlockedRetries ||
                !_blockedRetryEvaluator.CanRetry(attemptEnvelopes))
            {
                return AttachAttemptSummaries(envelope, attempts, attemptEnvelopes);
            }

            currentRequest = _blockedRetryRequestFactory.Create(originalRequest, envelope, attemptNumber + 1);
            attemptNumber++;
        }
    }

    private async Task<OutcomeEnvelope> RunAttemptAsync(
        RunRequest request,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = _policy.Timeout is { } timeout
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (timeoutCts is not null && _policy.Timeout is { } timeoutValue)
        {
            timeoutCts.CancelAfter(timeoutValue);
        }

        var ct = timeoutCts?.Token ?? cancellationToken;
        var run = new AgenticaRun(AgenticaIds.New("run"), request, attemptNumber);
        run.ExposedBoundaries.UnionWith(_policy.EffectiveSecurityPolicy.InitialBoundaries);
        // Every planning request contains RunRequest.Objective. Treat it as user content
        // regardless of origin so an external planner cannot receive an unclassified prompt.
        run.ExposedBoundaries.Add(ToolDataBoundary.UserContent);
        var toolCooldowns = new Dictionary<string, ToolCooldownState>(StringComparer.Ordinal);
        var toolIdentityAliases = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        Emit(
            run,
            ExecutionEventType.RunCreated,
            [("run", run.RunId)],
            source: "Runner",
            context: Context(run),
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["origin"] = request.Origin.ToString(),
                ["hasContext"] = request.Context is not null && request.Context.Count > 0
            });
        Emit(
            run,
            ExecutionEventType.RequestAccepted,
            [("origin", request.Origin.ToString())],
            source: "Runner",
            context: Context(run),
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["origin"] = request.Origin.ToString(),
                ["hasContext"] = request.Context is not null && request.Context.Count > 0
            });

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

        if (PlannerSecurityBlockers(run) is { Count: > 0 } initialPlannerBlockers)
        {
            return Finish(
                run,
                RunOutcomeStatus.Blocked,
                StopReason.PlannerDataBoundaryDenied,
                blockers: initialPlannerBlockers);
        }

        try
        {
            WorkflowPlan currentPlan;
            PlanningRequest? initialPlanningRequest = null;
            try
            {
                initialPlanningRequest = CreatePlanningRequest(request, run);
                EmitPlanningStarted(run, PlanningCallKind.Creation, initialPlanningRequest);
                currentPlan = await _planner
                    .CreatePlanAsync(initialPlanningRequest, ct)
                    .ConfigureAwait(false);
                RegisterPlanToolSurface(run, currentPlan, initialPlanningRequest);
            }
            catch (OperationCanceledException exception)
            {
                EmitPlanningCancelled(
                    run,
                    PlanningCallKind.Creation,
                    cancellationSource: CancellationSource(ct, cancellationToken),
                    exception,
                    planningRequest: initialPlanningRequest);
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
                    ],
                    diagnostics: new ExecutionDiagnostics(
                        "planner.create.failed",
                        $"Planner failed to create a valid plan: {exception.Message}",
                        exception.GetType().Name));
            }

            run.PlanVersions.Add(currentPlan);
            EmitPlanCreated(run, currentPlan);

            var executedSteps = new HashSet<string>(StringComparer.Ordinal);
            var validationIssues = ValidatePlan(run, currentPlan, executedSteps);
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
                    var completion = EvaluateCompletion(run);
                    if (completion.Decision == CompletionDecision.Complete)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.Succeeded,
                            completion.StopReason,
                            blockers: completion.Blockers,
                            completionEvidence: completion.EvidenceRefs);
                    }

                    if (completion.Decision == CompletionDecision.Partial)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.PartiallyComplete,
                            completion.StopReason,
                            blockers: completion.Blockers,
                            completionEvidence: completion.EvidenceRefs);
                    }

                    if (completion.Decision == CompletionDecision.Blocked)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.Blocked,
                            completion.StopReason,
                            blockers: completion.Blockers,
                            completionEvidence: completion.EvidenceRefs);
                    }

                    if (completion.Decision == CompletionDecision.Failed)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.Failed,
                            completion.StopReason,
                            blockers: completion.Blockers,
                            completionEvidence: completion.EvidenceRefs);
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

                    PlanningRequest? continuationPlanningRequest = null;
                    if (PlannerSecurityBlockers(run) is { Count: > 0 } continuationPlannerBlockers)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.Blocked,
                            StopReason.PlannerDataBoundaryDenied,
                            blockers: continuationPlannerBlockers);
                    }

                    try
                    {
                        continuationPlanningRequest = CreatePlanningRequest(request, run);
                        EmitPlanningStarted(
                            run,
                            PlanningCallKind.Continuation,
                            continuationPlanningRequest,
                            currentPlan: currentPlan);
                        currentPlan = await _planner
                            .CreatePlanAsync(continuationPlanningRequest, ct)
                            .ConfigureAwait(false);
                        RegisterPlanToolSurface(run, currentPlan, continuationPlanningRequest);
                    }
                    catch (OperationCanceledException exception)
                    {
                        EmitPlanningCancelled(
                            run,
                            PlanningCallKind.Continuation,
                            cancellationSource: CancellationSource(ct, cancellationToken),
                            exception,
                            planningRequest: continuationPlanningRequest,
                            currentPlan: currentPlan);
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
                            ],
                            diagnostics: new ExecutionDiagnostics(
                                "planner.continue.failed",
                                $"Planner failed to continue the plan: {exception.Message}",
                                exception.GetType().Name));
                    }

                    continuationCount++;
                    run.PlanVersions.Add(currentPlan);
                    EmitPlanCreated(run, currentPlan);

                    validationIssues = ValidatePlan(run, currentPlan, executedSteps);
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

                var executionResults = await ExecuteStepsAsync(
                    run,
                    currentPlan,
                    nextSteps,
                    toolCooldowns,
                    toolIdentityAliases,
                    ct).ConfigureAwait(false);

                foreach (var executionResult in executionResults)
                {
                    executedSteps.Add(executionResult.Step.StepId);
                    RecordToolResult(
                        run,
                        executionResult.Step,
                        executionResult.Result,
                        executionResult.DurationMs,
                        executionResult.Diagnostics,
                        executionResult.ExposedBoundaries);
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

                var failedResult = executionResults.FirstOrDefault(item =>
                    item.Result.Receipt.Status is ReceiptStatus.Failed);
                if (failedResult is not null)
                {
                    return Finish(
                        run,
                        RunOutcomeStatus.Failed,
                        StopReason.ToolFailure,
                        diagnostics: failedResult.Diagnostics ?? DiagnosticsFromReceipt(failedResult.Result.Receipt));
                }

                var cancelledResult = executionResults.FirstOrDefault(item =>
                    item.Result.Receipt.Status is ReceiptStatus.Cancelled or ReceiptStatus.TimedOut);
                if (cancelledResult is not null)
                {
                    var timedOut = cancelledResult.Result.Receipt.Status == ReceiptStatus.TimedOut ||
                        (ct.IsCancellationRequested && !cancellationToken.IsCancellationRequested);
                    return Finish(
                        run,
                        RunOutcomeStatus.Cancelled,
                        timedOut ? StopReason.Timeout : StopReason.Cancelled,
                        blockers: [cancelledResult.Result.Receipt.Message],
                        diagnostics: cancelledResult.Diagnostics ?? DiagnosticsFromReceipt(cancelledResult.Result.Receipt));
                }

                var incompleteResult = executionResults.FirstOrDefault(item =>
                    item.Result.Receipt.Status is ReceiptStatus.Accepted or ReceiptStatus.Partial);
                if (incompleteResult is not null)
                {
                    return Finish(
                        run,
                        RunOutcomeStatus.PartiallyComplete,
                        StopReason.Partial,
                        blockers: [incompleteResult.Result.Receipt.Message],
                        diagnostics: incompleteResult.Diagnostics);
                }

                if (_policy.EvaluateCompletionAfterEachBatch)
                {
                    var postExecutionCompletion = EvaluateCompletion(run);
                    if (postExecutionCompletion.Decision == CompletionDecision.Complete)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.Succeeded,
                            postExecutionCompletion.StopReason,
                            blockers: postExecutionCompletion.Blockers,
                            completionEvidence: postExecutionCompletion.EvidenceRefs);
                    }

                    if (postExecutionCompletion.Decision == CompletionDecision.Failed)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.Failed,
                            postExecutionCompletion.StopReason,
                            blockers: postExecutionCompletion.Blockers,
                            completionEvidence: postExecutionCompletion.EvidenceRefs);
                    }
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
                    PlanningRequest? refinementPlanningRequest = null;
                    if (PlannerSecurityBlockers(run) is { Count: > 0 } refinementPlannerBlockers)
                    {
                        return Finish(
                            run,
                            RunOutcomeStatus.Blocked,
                            StopReason.PlannerDataBoundaryDenied,
                            blockers: refinementPlannerBlockers);
                    }

                    try
                    {
                        refinementPlanningRequest = CreatePlanningRequest(request, run);
                        var refinementEvidence = new EvidenceRef[]
                        {
                            new("observation", observation.ObservationId),
                            new("receipt", refinementCandidate.Result.Receipt.ReceiptId)
                        };
                        EmitPlanningStarted(
                            run,
                            PlanningCallKind.Refinement,
                            refinementPlanningRequest,
                            currentPlan: currentPlan,
                            observation: observation,
                            evidenceRefs: refinementEvidence);
                        refinedPlan = await _planner
                            .RefinePlanAsync(refinementPlanningRequest, observation, ct)
                            .ConfigureAwait(false);
                        RegisterPlanToolSurface(run, refinedPlan, refinementPlanningRequest);
                    }
                    catch (OperationCanceledException exception)
                    {
                        EmitPlanningCancelled(
                            run,
                            PlanningCallKind.Refinement,
                            cancellationSource: CancellationSource(ct, cancellationToken),
                            exception,
                            planningRequest: refinementPlanningRequest,
                            currentPlan: currentPlan,
                            observation: observation,
                            evidenceRefs:
                            [
                                new EvidenceRef("observation", observation.ObservationId),
                                new EvidenceRef("receipt", refinementCandidate.Result.Receipt.ReceiptId)
                            ]);
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
                            ],
                            diagnostics: new ExecutionDiagnostics(
                                "planner.refine.failed",
                                $"Planner failed to refine the plan: {exception.Message}",
                                exception.GetType().Name));
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

                    var refinement = run.PlanRefinements[^1];
                    Emit(
                        run,
                        ExecutionEventType.PlanRefined,
                        [("plan", currentPlan.PlanId), ("reason", refinement.Reason)],
                        source: "Planner",
                        context: Context(
                            run,
                            plan: currentPlan,
                            toolSurfaceId: ToolSurfaceIdFor(run, currentPlan),
                            fromPlanId: refinement.FromPlanId,
                            toPlanId: refinement.ToPlanId),
                        intent: IntentForRefinement(
                            refinement.Reason,
                            observation,
                            refinementCandidate.Result.Receipt,
                            currentPlan),
                        evidenceRefs: refinement.Evidence,
                        payload: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["reason"] = refinement.Reason,
                            ["fromPlanId"] = refinement.FromPlanId,
                            ["toPlanId"] = refinement.ToPlanId,
                            ["nextStepIntents"] = currentPlan.Steps
                                .Select(StepIntentPayload)
                                .ToArray()
                        });

                    validationIssues = ValidatePlan(run, currentPlan, executedSteps);
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
                        blockedResult.Result.Receipt.Status == ReceiptStatus.Refused
                            ? StopReason.ToolRefused
                            : StopReason.ToolUnavailable,
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

    private CompletionEvaluation EvaluateCompletion(AgenticaRun run)
    {
        try
        {
            var context = CompletionContext.From(run);
            var completion = _completionEvaluator.Evaluate(context);
            if (completion is null ||
                !Enum.IsDefined(completion.Decision) ||
                !Enum.IsDefined(completion.StopReason) ||
                completion.Blockers is null ||
                completion.EvidenceRefs is null ||
                completion.EvidenceRefs.Any(reference => !EvidenceResolves(context, reference)))
            {
                return CompletionEvaluation.Failed(
                    StopReason.CompletionEvaluationFailed,
                    "Completion policy returned an invalid evaluation.");
            }

            return completion;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CompletionEvaluation.Failed(
                StopReason.CompletionEvaluationFailed,
                $"Completion policy failed: {exception.GetType().Name}.");
        }
    }

    private static bool EvidenceResolves(CompletionContext context, EvidenceRef? reference)
    {
        if (reference is null ||
            string.IsNullOrWhiteSpace(reference.Kind) ||
            string.IsNullOrWhiteSpace(reference.RefId))
        {
            return false;
        }

        return reference.Kind switch
        {
            "receipt" => context.Receipts.Any(item =>
                string.Equals(item.ReceiptId, reference.RefId, StringComparison.Ordinal)),
            "observation" => context.Observations.Any(item =>
                string.Equals(item.ObservationId, reference.RefId, StringComparison.Ordinal)),
            "artifact" => context.Artifacts.Any(item =>
                string.Equals(item.ArtifactId, reference.RefId, StringComparison.Ordinal)),
            _ => false
        };
    }

    private IReadOnlyList<ValidationIssue> ValidatePlan(
        AgenticaRun run,
        WorkflowPlan plan,
        IReadOnlySet<string> completedStepIds) =>
        _planValidator.Validate(
            plan,
            completedStepIds,
            run.ExposedBoundaries,
            ToolManifestHashFor(run, plan) ?? string.Empty);

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
        WorkflowPlan plan,
        IReadOnlyList<PlanStep> steps,
        Dictionary<string, ToolCooldownState> toolCooldowns,
        ConcurrentDictionary<string, string> toolIdentityAliases,
        CancellationToken cancellationToken)
    {
        var toolSurfaceId = ToolSurfaceIdFor(run, plan);
        var manifestHash = ToolManifestHashFor(run, plan);
        if (steps.Count == 1)
        {
            var step = steps[0];
            Emit(
                run,
                ExecutionEventType.StepStarted,
                [("step", step.StepId), ("tool", step.ToolId)],
                source: "Runner",
                context: Context(run, plan, step, toolSurfaceId: toolSurfaceId),
                intent: IntentFor(step),
                payload: StepPayload(step));

            return
            [
                await ExecuteStepAsync(
                    run,
                    step,
                    manifestHash,
                    toolCooldowns,
                    toolIdentityAliases,
                    cancellationToken).ConfigureAwait(false)
            ];
        }

        var batchId = steps[0].BatchId ?? AgenticaIds.New("batch");
        var startedAt = DateTimeOffset.UtcNow;
        Emit(
            run,
            ExecutionEventType.BatchStarted,
            [("batch", batchId), ("steps", steps.Count.ToString())],
            source: "Runner",
            context: Context(run, plan, batchId: batchId, toolSurfaceId: toolSurfaceId),
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["stepIds"] = steps.Select(step => step.StepId).ToArray(),
                ["steps"] = steps.Count
            });

        foreach (var step in steps)
        {
            Emit(
                run,
                ExecutionEventType.StepStarted,
                [("step", step.StepId), ("tool", step.ToolId), ("batch", batchId)],
                source: "Runner",
                context: Context(run, plan, step, batchId, toolSurfaceId),
                intent: IntentFor(step),
                payload: StepPayload(step));
        }

        var tasks = steps
            .Select(step => ExecuteStepAsync(
                run,
                step,
                manifestHash,
                toolCooldowns,
                toolIdentityAliases,
                cancellationToken))
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
            [("batch", batchId), ("steps", steps.Count.ToString())],
            source: "Runner",
            context: Context(run, plan, batchId: batchId, toolSurfaceId: toolSurfaceId),
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["stepIds"] = steps.Select(step => step.StepId).ToArray(),
                ["steps"] = steps.Count,
                ["durationMs"] = Math.Max(0, (long)(completedAt - startedAt).TotalMilliseconds)
            });

        return results;
    }

    private async Task<StepExecutionResult> ExecuteStepAsync(
        AgenticaRun run,
        PlanStep step,
        string? expectedManifestHash,
        Dictionary<string, ToolCooldownState> toolCooldowns,
        ConcurrentDictionary<string, string> toolIdentityAliases,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(expectedManifestHash))
        {
            return CreateSecurityRefusal(
                step,
                stopwatch,
                "tool.security.manifest_unpinned",
                "Tool dispatch was refused because the plan is not pinned to a compiled manifest.");
        }

        CompiledToolManifest currentManifest;
        try
        {
            currentManifest = _toolCatalog.CompileCurrentManifest();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return CreateSecurityRefusal(
                step,
                stopwatch,
                "tool.security.registration_invalid",
                $"Tool dispatch was refused because the current registration surface is invalid: {exception.Message}",
                exception.GetType().Name);
        }

        if (!string.Equals(currentManifest.ManifestHash, expectedManifestHash, StringComparison.Ordinal))
        {
            return CreateSecurityRefusal(
                step,
                stopwatch,
                "tool.security.manifest_changed",
                "Tool dispatch was refused because the compiled registration manifest changed after planning.",
                data: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["expectedManifestHash"] = expectedManifestHash,
                    ["currentManifestHash"] = currentManifest.ManifestHash
                });
        }

        var registration = currentManifest.Resolve(step.ToolId);
        if (registration is null)
        {
            return CreateSecurityRefusal(
                step,
                stopwatch,
                "tool.security.tool_missing",
                $"Tool dispatch was refused because '{step.ToolId}' is absent from the checked manifest.");
        }

        var descriptor = registration.PlannerProjection;
        var security = registration.Security;
        if (descriptor.Kind != step.Kind || security.Effect != step.Effect)
        {
            return CreateSecurityRefusal(
                step,
                stopwatch,
                "tool.security.plan_registration_mismatch",
                "Tool dispatch was refused because the planned kind/effect does not match the checked registration.");
        }

        if (!_policy.EffectiveEffectPolicy.Allows(security.Effect))
        {
            return CreateSecurityRefusal(
                step,
                stopwatch,
                "tool.security.effect_not_allowed",
                $"Tool dispatch was refused because effect '{security.Effect}' is not allowed by policy.");
        }

        var plannerBoundaryViolations = ToolSecurityEvaluator.PlannerBoundaryViolations(
            _policy.EffectiveSecurityPolicy,
            security.ExposesToPlanner);
        if (plannerBoundaryViolations.Count > 0)
        {
            return CreateSecurityRefusal(
                step,
                stopwatch,
                "tool.security.planner_boundary_not_allowed",
                "Tool dispatch was refused because its result would taint a later external-planner call with " +
                $"disallowed boundaries: {string.Join(", ", plannerBoundaryViolations)}.");
        }

        var grant = ToolSecurityEvaluator.EvaluateDispatch(
            _policy.EffectiveSecurityPolicy,
            expectedManifestHash,
            registration,
            run.ExposedBoundaries,
            DateTimeOffset.UtcNow);
        if (!grant.Allowed)
        {
            return CreateSecurityRefusal(
                step,
                stopwatch,
                grant.Code ?? "tool.security.grant_required",
                grant.Message ?? $"Tool '{step.ToolId}' is not authorized for dispatch.");
        }

        var inputIssues = ToolInputValidator.Validate(step, descriptor.InputSchema);
        if (inputIssues.Count > 0)
        {
            return CreateSecurityRefusal(
                step,
                stopwatch,
                "tool.security.input_invalid_at_dispatch",
                "Tool dispatch was refused because input no longer validates against the checked registration.");
        }

        if (TryCreateCooldownResultOrReserve(run, step, descriptor, toolCooldowns) is { } cooldownResult)
        {
            stopwatch.Stop();
            return new StepExecutionResult(
                step,
                cooldownResult,
                new ExecutionDiagnostics(
                    "tool.cooldown.active",
                    cooldownResult.Receipt.Message,
                    FailureKind: ReceiptStatus.Refused.ToString()),
                stopwatch.ElapsedMilliseconds);
        }

        try
        {
            var invocation = new ToolInvocation(
                run.RunId,
                step.StepId,
                step.ToolId,
                ToolResultNormalizer.RestoreSourceIdentities(step.Input, toolIdentityAliases));
            var rawResult = await registration.Tool.ExecuteAsync(
                invocation,
                cancellationToken).ConfigureAwait(false);
            var normalization = ToolResultNormalizer.Normalize(invocation, rawResult);
            var result = normalization.Result;
            foreach (var alias in normalization.IdentityAliases)
            {
                toolIdentityAliases[alias.Key] = alias.Value;
            }

            UpdateCooldownReceipt(step, descriptor, result.Receipt, toolCooldowns);
            ResetCooldownsAfterMutation(descriptor, result, toolCooldowns);
            stopwatch.Stop();
            return new StepExecutionResult(
                step,
                result,
                normalization.Diagnostics,
                stopwatch.ElapsedMilliseconds,
                security.ExposesToPlanner);
        }
        catch (OperationCanceledException exception)
        {
            var receipt = new Receipt(
                AgenticaIds.New("receipt"),
                step.StepId,
                step.ToolId,
                ReceiptStatus.Cancelled,
                $"Tool '{step.ToolId}' was cancelled after dispatch.",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>
                {
                    ["errorClass"] = exception.GetType().Name,
                    ["cancellationRequested"] = cancellationToken.IsCancellationRequested
                });
            stopwatch.Stop();
            return new StepExecutionResult(
                step,
                new ToolResult(receipt),
                new ExecutionDiagnostics(
                    "tool.execution.cancelled",
                    receipt.Message,
                    exception.GetType().Name,
                    ReceiptStatus.Cancelled.ToString()),
                stopwatch.ElapsedMilliseconds,
                security.ExposesToPlanner);
        }
        catch (Exception exception)
        {
            var receipt = new Receipt(
                AgenticaIds.New("receipt"),
                step.StepId,
                step.ToolId,
                ReceiptStatus.Failed,
                $"Tool '{step.ToolId}' failed: {exception.Message}",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>
                {
                    ["errorClass"] = exception.GetType().Name
                });
            UpdateCooldownReceipt(step, descriptor, receipt, toolCooldowns);
            stopwatch.Stop();
            return new StepExecutionResult(
                step,
                new ToolResult(receipt),
                new ExecutionDiagnostics(
                    "tool.execution.failed",
                    $"Tool '{step.ToolId}' failed: {exception.Message}",
                    exception.GetType().Name,
                    ReceiptStatus.Failed.ToString()),
                stopwatch.ElapsedMilliseconds,
                security.ExposesToPlanner);
        }
    }

    private static StepExecutionResult CreateSecurityRefusal(
        PlanStep step,
        Stopwatch stopwatch,
        string code,
        string message,
        string? errorClass = null,
        IReadOnlyDictionary<string, object?>? data = null)
    {
        stopwatch.Stop();
        var receiptData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["securityCode"] = code
        };
        if (data is not null)
        {
            foreach (var item in data)
            {
                receiptData[item.Key] = item.Value;
            }
        }

        var receipt = new Receipt(
            AgenticaIds.New("receipt"),
            step.StepId,
            step.ToolId,
            ReceiptStatus.Refused,
            message,
            DateTimeOffset.UtcNow,
            receiptData);
        return new StepExecutionResult(
            step,
            new ToolResult(receipt),
            new ExecutionDiagnostics(code, message, errorClass, ReceiptStatus.Refused.ToString()),
            stopwatch.ElapsedMilliseconds);
    }

    private static ToolResult? TryCreateCooldownResultOrReserve(
        AgenticaRun run,
        PlanStep step,
        ToolDescriptor descriptor,
        Dictionary<string, ToolCooldownState> toolCooldowns)
    {
        if (descriptor.Cooldown is not { IsActive: true } cooldown)
        {
            return null;
        }

        var key = CooldownKey(step, cooldown);
        var now = DateTimeOffset.UtcNow;
        ToolCooldownState? activeState = null;
        int? remainingPlanSteps = null;
        TimeSpan? remainingDuration = null;

        lock (toolCooldowns)
        {
            if (toolCooldowns.TryGetValue(key, out var state) &&
                IsCooldownActive(
                    cooldown,
                    state,
                    run.CompletedSteps.Count,
                    now,
                    out remainingPlanSteps,
                    out remainingDuration))
            {
                activeState = state;
            }
            else
            {
                toolCooldowns.Remove(key);
                var availableAfterCompletedStepCount = cooldown.PlanStepCount is > 0
                    ? run.CompletedSteps.Count + 1 + cooldown.PlanStepCount.Value
                    : (int?)null;
                var availableAt = cooldown.Duration is { Ticks: > 0 } duration
                    ? now.Add(duration)
                    : (DateTimeOffset?)null;

                toolCooldowns[key] = new ToolCooldownState(
                    step.ToolId,
                    key,
                    step.StepId,
                    ReceiptId: null,
                    availableAfterCompletedStepCount,
                    availableAt,
                    cooldown.ResetOnMutation);
            }
        }

        if (activeState is null)
        {
            return null;
        }

        return CreateCooldownResult(step, cooldown, key, activeState, now, remainingPlanSteps, remainingDuration);
    }

    private static void UpdateCooldownReceipt(
        PlanStep step,
        ToolDescriptor descriptor,
        Receipt receipt,
        Dictionary<string, ToolCooldownState> toolCooldowns)
    {
        if (descriptor.Cooldown is not { IsActive: true } cooldown)
        {
            return;
        }

        var key = CooldownKey(step, cooldown);
        lock (toolCooldowns)
        {
            if (toolCooldowns.TryGetValue(key, out var state) &&
                string.Equals(state.StepId, step.StepId, StringComparison.Ordinal))
            {
                if (receipt.Status == ReceiptStatus.Succeeded)
                {
                    toolCooldowns[key] = state with { ReceiptId = receipt.ReceiptId };
                }
                else
                {
                    toolCooldowns.Remove(key);
                }
            }
        }
    }

    private static ToolResult CreateCooldownResult(
        PlanStep step,
        ToolCooldownPolicy cooldown,
        string key,
        ToolCooldownState state,
        DateTimeOffset now,
        int? remainingPlanSteps,
        TimeSpan? remainingDuration)
    {
        var reason = string.IsNullOrWhiteSpace(cooldown.Reason)
            ? "The host marked this tool as temporarily stale after recent use."
            : cooldown.Reason;
        var message = $"Tool '{step.ToolId}' is on cooldown. Use recent receipts/observations or choose another available action.";
        var cooldownData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["toolId"] = step.ToolId,
            ["cooldownKey"] = key,
            ["reason"] = reason,
            ["lastStepId"] = state.StepId,
            ["lastReceiptId"] = state.ReceiptId,
            ["planStepCount"] = cooldown.PlanStepCount,
            ["remainingPlanSteps"] = remainingPlanSteps,
            ["durationMs"] = cooldown.Duration is { } duration
                ? Math.Max(0, (long)duration.TotalMilliseconds)
                : null,
            ["remainingDurationMs"] = remainingDuration is { } remaining
                ? Math.Max(0, (long)Math.Ceiling(remaining.TotalMilliseconds))
                : null,
            ["resetOnMutation"] = cooldown.ResetOnMutation
        };
        var receipt = new Receipt(
            AgenticaIds.New("receipt"),
            step.StepId,
            step.ToolId,
            ReceiptStatus.Refused,
            message,
            now,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cooldown"] = cooldownData
            });

        return new ToolResult(
            receipt,
            new Observation(
                AgenticaIds.New("observation"),
                step.StepId,
                ObservationKind.Validation,
                $"Tool '{step.ToolId}' cooldown is active.",
                cooldownData,
                [new EvidenceRef("receipt", receipt.ReceiptId)]));
    }

    private static void ResetCooldownsAfterMutation(
        ToolDescriptor descriptor,
        ToolResult result,
        Dictionary<string, ToolCooldownState> toolCooldowns)
    {
        if (descriptor.Effect == ToolEffect.ReadOnly ||
            result.Receipt.Status != ReceiptStatus.Succeeded)
        {
            return;
        }

        lock (toolCooldowns)
        {
            foreach (var key in toolCooldowns
                .Where(pair => pair.Value.ResetOnMutation)
                .Select(pair => pair.Key)
                .ToArray())
            {
                toolCooldowns.Remove(key);
            }
        }
    }

    private static bool IsCooldownActive(
        ToolCooldownPolicy cooldown,
        ToolCooldownState state,
        int completedStepCount,
        DateTimeOffset now,
        out int? remainingPlanSteps,
        out TimeSpan? remainingDuration)
    {
        var hasStepCooldown = cooldown.PlanStepCount is > 0 &&
            state.AvailableAfterCompletedStepCount is not null;
        var hasDurationCooldown = cooldown.Duration is { Ticks: > 0 } &&
            state.AvailableAt is not null;
        var stepActive = hasStepCooldown &&
            completedStepCount < state.AvailableAfterCompletedStepCount!.Value;
        var durationActive = hasDurationCooldown &&
            now < state.AvailableAt!.Value;

        remainingPlanSteps = stepActive
            ? state.AvailableAfterCompletedStepCount!.Value - completedStepCount
            : null;
        remainingDuration = durationActive
            ? state.AvailableAt!.Value - now
            : null;

        if (hasStepCooldown && hasDurationCooldown)
        {
            return stepActive && durationActive;
        }

        return stepActive || durationActive;
    }

    private static string CooldownKey(PlanStep step, ToolCooldownPolicy cooldown)
    {
        if (cooldown.ScopeInputKeys is not { Count: > 0 } scopeKeys)
        {
            return step.ToolId;
        }

        var scoped = scopeKeys
            .Select(key => step.Input.TryGetValue(key, out var value)
                ? $"{key}={CooldownValue(value)}"
                : $"{key}=<missing>");
        return $"{step.ToolId}|{string.Join("|", scoped)}";
    }

    private static string CooldownValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return text;
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception exception) when (exception is NotSupportedException or JsonException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private sealed record ToolCooldownState(
        string ToolId,
        string Key,
        string StepId,
        string? ReceiptId,
        int? AvailableAfterCompletedStepCount,
        DateTimeOffset? AvailableAt,
        bool ResetOnMutation);

    private void RecordToolResult(
        AgenticaRun run,
        PlanStep step,
        ToolResult result,
        long durationMs,
        ExecutionDiagnostics? diagnostics,
        IReadOnlySet<ToolDataBoundary>? exposedBoundaries)
    {
        run.CompletedSteps.Add(step.StepId);
        run.Receipts.Add(result.Receipt);
        if (exposedBoundaries is not null)
        {
            run.ExposedBoundaries.UnionWith(exposedBoundaries);
        }

        if (result.Observation is not null)
        {
            run.Observations.Add(result.Observation);
            Emit(
                run,
                ExecutionEventType.ObservationMade,
                [("observation", result.Observation.ObservationId), ("step", step.StepId)],
                source: $"Tool:{step.ToolId}",
                context: Context(
                    run,
                    stepId: step.StepId,
                    toolId: step.ToolId,
                    observationId: result.Observation.ObservationId),
                evidenceRefs: [new EvidenceRef("observation", result.Observation.ObservationId)],
                payload: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = result.Observation.Kind.ToString(),
                    ["summary"] = result.Observation.Summary
                });
        }

        if (result.Artifact is not null)
        {
            run.Artifacts.Add(result.Artifact);
        }

        var evidenceRefs = new List<EvidenceRef>
        {
            new("receipt", result.Receipt.ReceiptId)
        };
        if (result.Observation is not null)
        {
            evidenceRefs.Add(new EvidenceRef("observation", result.Observation.ObservationId));
        }

        if (result.Artifact is not null)
        {
            evidenceRefs.Add(new EvidenceRef("artifact", result.Artifact.ArtifactId));
        }

        Emit(
            run,
            ExecutionEventType.ReceiptEmitted,
            [("receipt", result.Receipt.ReceiptId), ("status", result.Receipt.Status.ToString().ToLowerInvariant())],
            source: $"Tool:{step.ToolId}",
            context: Context(
                run,
                stepId: step.StepId,
                toolId: step.ToolId,
                receiptId: result.Receipt.ReceiptId,
                observationId: result.Observation?.ObservationId,
                artifactId: result.Artifact?.ArtifactId),
            evidenceRefs: evidenceRefs,
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = result.Receipt.Status.ToString(),
                ["message"] = result.Receipt.Message,
                ["durationMs"] = durationMs
            },
            diagnostics: diagnostics ?? DiagnosticsFromReceipt(result.Receipt));
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
        IReadOnlyList<RunAttemptSummary> attempts,
        IReadOnlyList<OutcomeEnvelope> attemptEnvelopes) =>
        envelope with
        {
            PriorAttempts = attemptEnvelopes
                .Take(Math.Max(0, attemptEnvelopes.Count - 1))
                .Select(attempt => attempt with { PriorAttempts = [] })
                .ToArray(),
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
        IReadOnlyList<string>? blockers = null,
        ExecutionDiagnostics? diagnostics = null,
        IReadOnlyList<EvidenceRef>? completionEvidence = null)
    {
        validationIssues ??= [];
        blockers ??= [];
        run.Status = status;

        completionEvidence ??= [];

        OutcomeReport report;
        ExecutionDiagnostics? reportDiagnostics = null;
        try
        {
            report = _outcomeReporter.BuildReport(run, status, stopReason, validationIssues, blockers)
                ?? throw new InvalidOperationException("Outcome reporter returned no report.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            reportDiagnostics = new ExecutionDiagnostics(
                "outcome.reporter.failed",
                "Outcome reporter failed; the runtime emitted a canonical fallback report.",
                exception.GetType().Name,
                "OutcomeReportingFailure");
            report = new OutcomeReport(
                AgenticaIds.New("report"),
                $"The run stopped with status {status}. Stop reason: {stopReason}. The configured outcome reporter failed.",
                [
                    new ReportClaim(
                        "The runtime preserved the outcome after the configured reporter failed.",
                        [new EvidenceRef("stopReason", stopReason.ToString())])
                ]);
        }

        var effectiveDiagnostics = diagnostics ?? reportDiagnostics;
        var outcomeEvidence = completionEvidence
            .Concat(validationIssues.Select(issue => new EvidenceRef("validationIssue", issue.Code)))
            .ToArray();
        Emit(
            run,
            ExecutionEventType.OutcomeReported,
            [("report", report.ReportId), ("refs", report.Claims.Sum(claim => claim.Evidence.Count).ToString())],
            source: "OutcomeReporter",
            context: Context(run),
            evidenceRefs: outcomeEvidence,
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["reportId"] = report.ReportId,
                ["status"] = status.ToString(),
                ["stopReason"] = stopReason.ToString(),
                ["completedStepCount"] = run.CompletedSteps.Count,
                ["receiptCount"] = run.Receipts.Count,
                ["artifactCount"] = run.Artifacts.Count,
                ["blockers"] = blockers.ToArray(),
                ["validationIssues"] = validationIssues
                    .Select(issue => new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = issue.Code,
                        ["message"] = issue.Message,
                        ["stepId"] = issue.StepId
                    })
                    .ToArray()
            },
            diagnostics: effectiveDiagnostics ?? DiagnosticsFrom(validationIssues));

        Emit(
            run,
            TerminalEventFor(status),
            [("run", run.RunId)],
            source: "Runner",
            context: Context(run),
            evidenceRefs: outcomeEvidence,
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = status.ToString(),
                ["stopReason"] = stopReason.ToString(),
                ["blockers"] = blockers.ToArray(),
                ["completedStepCount"] = run.CompletedSteps.Count,
                ["receiptCount"] = run.Receipts.Count,
                ["artifactCount"] = run.Artifacts.Count
            },
            diagnostics: effectiveDiagnostics ?? DiagnosticsFrom(validationIssues));

        var continuityCompiler = new ContinuityLedgerCompiler();
        var breadcrumbs = continuityCompiler.CompileBreadcrumbLedger(run);
        var divergences = continuityCompiler.CompileDivergenceLedger(
            run,
            validationIssues,
            status,
            stopReason,
            blockers);
        var continuity = continuityCompiler.CompileSummary(
            run,
            breadcrumbs,
            divergences,
            validationIssues,
            status);

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
                Events: run.Events.ToList().AsReadOnly(),
                ValidationIssues: validationIssues)
            {
                ToolSurfaces = run.ToolSurfaces.ToArray(),
                PlanningFrames = run.PlanningFrames.ToArray(),
                EventDeliveryFailure = run.EventDeliveryFailure,
                Breadcrumbs = breadcrumbs,
                Divergences = divergences,
                Continuity = continuity
            });
    }

    private enum PlanningCallKind
    {
        Creation,
        Continuation,
        Refinement
    }

    private void EmitPlanningStarted(
        AgenticaRun run,
        PlanningCallKind kind,
        PlanningRequest planningRequest,
        WorkflowPlan? currentPlan = null,
        Observation? observation = null,
        IReadOnlyList<EvidenceRef>? evidenceRefs = null)
    {
        var toolSurfaceId = planningRequest.ToolSurface?.SurfaceId;
        var operation = PlanningOperationName(kind);

        Emit(
            run,
            PlanningStartedEventFor(kind),
            PlanningEventData(operation, toolSurfaceId, currentPlan, observation),
            source: "Planner",
            context: Context(
                run,
                currentPlan,
                toolSurfaceId: toolSurfaceId,
                observationId: observation?.ObservationId,
                fromPlanId: currentPlan?.PlanId),
            intent: new ExecutionIntent(
                $"Start planner {operation}.",
                PlanningStartedRationale(kind),
                PlanningStartedOutcome(kind)),
            evidenceRefs: evidenceRefs,
            payload: PlanningPayload(
                operation,
                "started",
                planningRequest,
                toolSurfaceId,
                currentPlan,
                observation));
    }

    private void EmitPlanningCancelled(
        AgenticaRun run,
        PlanningCallKind kind,
        string cancellationSource,
        OperationCanceledException exception,
        PlanningRequest? planningRequest = null,
        WorkflowPlan? currentPlan = null,
        Observation? observation = null,
        IReadOnlyList<EvidenceRef>? evidenceRefs = null)
    {
        var toolSurfaceId = planningRequest?.ToolSurface?.SurfaceId ??
            (currentPlan is null ? null : ToolSurfaceIdFor(run, currentPlan));
        var operation = PlanningOperationName(kind);
        var data = PlanningEventData(operation, toolSurfaceId, currentPlan, observation).ToList();
        data.Add(("cancellationSource", cancellationSource));

        Emit(
            run,
            PlanningCancelledEventFor(kind),
            data.ToArray(),
            source: "Planner",
            context: Context(
                run,
                currentPlan,
                toolSurfaceId: toolSurfaceId,
                observationId: observation?.ObservationId,
                fromPlanId: currentPlan?.PlanId),
            intent: new ExecutionIntent(
                $"Cancel planner {operation}.",
                $"Planner {operation} was cancelled by {cancellationSource}.",
                "No new plan slice was returned for this planner call."),
            evidenceRefs: evidenceRefs,
            payload: PlanningPayload(
                operation,
                "cancelled",
                planningRequest,
                toolSurfaceId,
                currentPlan,
                observation,
                cancellationSource),
            diagnostics: new ExecutionDiagnostics(
                $"planner.{operation}.cancelled",
                $"Planner {operation} was cancelled before a plan was returned.",
                exception.GetType().Name));
    }

    private void EmitPlanCreated(AgenticaRun run, WorkflowPlan plan)
    {
        var toolSurfaceId = ToolSurfaceIdFor(run, plan);
        Emit(
            run,
            ExecutionEventType.PlanCreated,
            [("plan", plan.PlanId), ("version", plan.Version.ToString()), ("steps", plan.Steps.Count.ToString())],
            source: "Planner",
            context: Context(run, plan, toolSurfaceId: toolSurfaceId),
            intent: new ExecutionIntent(
                "Create an executable plan slice.",
                string.IsNullOrWhiteSpace(plan.PlanningReason)
                    ? plan.Description
                    : plan.PlanningReason,
                "A validated plan slice that Agentica can execute against the visible tool surface."),
            payload: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["description"] = plan.Description,
                ["planningReason"] = plan.PlanningReason,
                ["stepCount"] = plan.Steps.Count,
                ["stepIds"] = plan.Steps.Select(step => step.StepId).ToArray(),
                ["toolIds"] = plan.Steps.Select(step => step.ToolId).Distinct(StringComparer.Ordinal).ToArray(),
                ["stepIntents"] = plan.Steps.Select(StepIntentPayload).ToArray()
            });
    }

    private static ExecutionEventType PlanningStartedEventFor(PlanningCallKind kind) =>
        kind switch
        {
            PlanningCallKind.Creation => ExecutionEventType.PlanCreationStarted,
            PlanningCallKind.Continuation => ExecutionEventType.PlanContinuationStarted,
            PlanningCallKind.Refinement => ExecutionEventType.PlanRefinementStarted,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static ExecutionEventType PlanningCancelledEventFor(PlanningCallKind kind) =>
        kind switch
        {
            PlanningCallKind.Creation => ExecutionEventType.PlanCreationCancelled,
            PlanningCallKind.Continuation => ExecutionEventType.PlanContinuationCancelled,
            PlanningCallKind.Refinement => ExecutionEventType.PlanRefinementCancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static string PlanningOperationName(PlanningCallKind kind) =>
        kind switch
        {
            PlanningCallKind.Creation => "creation",
            PlanningCallKind.Continuation => "continuation",
            PlanningCallKind.Refinement => "refinement",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static string PlanningStartedRationale(PlanningCallKind kind) =>
        kind switch
        {
            PlanningCallKind.Creation => "No executable plan has been returned yet.",
            PlanningCallKind.Continuation => "The current plan slice is exhausted before completion evidence is satisfied.",
            PlanningCallKind.Refinement => "New observation evidence requires the planner to adapt the current plan.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static string PlanningStartedOutcome(PlanningCallKind kind) =>
        kind switch
        {
            PlanningCallKind.Creation => "An initial validated plan slice.",
            PlanningCallKind.Continuation => "A validated continuation plan slice.",
            PlanningCallKind.Refinement => "A validated refined plan slice.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static (string Key, string Value)[] PlanningEventData(
        string operation,
        string? toolSurfaceId,
        WorkflowPlan? currentPlan,
        Observation? observation)
    {
        var data = new List<(string Key, string Value)>
        {
            ("operation", operation)
        };

        if (!string.IsNullOrWhiteSpace(toolSurfaceId))
        {
            data.Add(("toolSurface", toolSurfaceId));
        }

        if (currentPlan is not null)
        {
            data.Add(("fromPlan", currentPlan.PlanId));
        }

        if (observation is not null)
        {
            data.Add(("observation", observation.ObservationId));
        }

        return data.ToArray();
    }

    private static IReadOnlyDictionary<string, object?> PlanningPayload(
        string operation,
        string status,
        PlanningRequest? planningRequest,
        string? toolSurfaceId,
        WorkflowPlan? currentPlan,
        Observation? observation,
        string? cancellationSource = null)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operation"] = operation,
            ["status"] = status,
            ["toolSurfaceId"] = toolSurfaceId,
            ["fromPlanId"] = currentPlan?.PlanId,
            ["observationId"] = observation?.ObservationId,
            ["visibleToolCount"] = planningRequest?.ToolDescriptors.Count,
            ["observationCount"] = planningRequest?.Observations.Count,
            ["receiptCount"] = planningRequest?.Receipts.Count,
            ["contextFrameIds"] = planningRequest?.ContextFrames.Select(frame => frame.FrameId).ToArray()
        };

        if (!string.IsNullOrWhiteSpace(cancellationSource))
        {
            payload["cancellationSource"] = cancellationSource;
        }

        return payload;
    }

    private void Emit(AgenticaRun run, ExecutionEventType type, params (string Key, string Value)[] data)
    {
        EmitCore(run, type, data);
    }

    private void Emit(
        AgenticaRun run,
        ExecutionEventType type,
        (string Key, string Value)[] data,
        string? source = null,
        ExecutionEventContext? context = null,
        ExecutionIntent? intent = null,
        IReadOnlyList<EvidenceRef>? evidenceRefs = null,
        IReadOnlyDictionary<string, object?>? payload = null,
        ExecutionDiagnostics? diagnostics = null)
    {
        EmitCore(run, type, data, source, context, intent, evidenceRefs, payload, diagnostics);
    }

    private void EmitCore(
        AgenticaRun run,
        ExecutionEventType type,
        (string Key, string Value)[] data,
        string? source = null,
        ExecutionEventContext? context = null,
        ExecutionIntent? intent = null,
        IReadOnlyList<EvidenceRef>? evidenceRefs = null,
        IReadOnlyDictionary<string, object?>? payload = null,
        ExecutionDiagnostics? diagnostics = null)
    {
        var eventType = type.WireName();
        var eventId = AgenticaIds.New("event");
        var eventAt = DateTimeOffset.UtcNow;
        var eventSequence = run.NextEventSequence();
        ExecutionEvent executionEvent;
        try
        {
            var eventData = ExecutionEventSnapshot.Data(
                data.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            var eventPayload = ExecutionEventSnapshot.Payload(
                payload ?? new Dictionary<string, object?>(StringComparer.Ordinal));
            UserFacingReason? userFacingReason;
            try
            {
                userFacingReason = ExecutionEventSnapshot.Reason(
                    _userFacingReasonProjector.Project(new UserFacingReasonProjectionRequest(
                        EventType: eventType,
                        Source: source,
                        Context: ExecutionEventSnapshot.Context(context),
                        Intent: ExecutionEventSnapshot.Intent(intent),
                        Data: ExecutionEventSnapshot.Data(eventData),
                        Payload: ExecutionEventSnapshot.Payload(eventPayload),
                        Diagnostics: ExecutionEventSnapshot.Diagnostics(diagnostics))));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                userFacingReason = null;
                diagnostics ??= new ExecutionDiagnostics(
                    "event.reason_projection.failed",
                    "User-facing event reason projection failed; the canonical event was preserved.",
                    exception.GetType().Name,
                    "EventProjectionFailure");
            }

            executionEvent = ExecutionEventSnapshot.Create(
                eventId,
                eventType,
                eventAt,
                eventSequence,
                source,
                context,
                intent,
                userFacingReason,
                eventData,
                evidenceRefs ?? [],
                eventPayload,
                diagnostics);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            executionEvent = ExecutionEventSnapshot.CreateFailure(
                eventId,
                eventType,
                eventAt,
                eventSequence,
                source,
                context,
                evidenceRefs,
                diagnostics,
                exception);
        }

        lock (run.EventDeliveryGate)
        {
            // The run ledger is authoritative. Sink delivery is best-effort and cannot
            // invalidate business state or evidence that has already been recorded.
            run.AddEvent(executionEvent);

            if (run.EventDeliveryFailure is not null)
            {
                return;
            }

            try
            {
                _eventSink.Emit(ExecutionEventSnapshot.Clone(executionEvent));
            }
            catch (Exception exception)
            {
                run.EventDeliveryFailure = new EventDeliveryFailure(
                    EventId: executionEvent.EventId,
                    EventType: executionEvent.Type,
                    EventSequence: executionEvent.Sequence,
                    SinkType: _eventSink.GetType().FullName ?? _eventSink.GetType().Name,
                    ExceptionType: exception.GetType().FullName ?? exception.GetType().Name,
                    Message: "Event delivery failed.",
                    FailedAt: DateTimeOffset.UtcNow);
            }
        }
    }

    private PlanningRequest CreatePlanningRequest(RunRequest request, AgenticaRun run)
    {
        var planningRequest = _planningRequestFactory.Create(request, run);
        if (planningRequest.ToolSurface is { } toolSurface)
        {
            run.ToolSurfaces.Add(toolSurface);
        }

        run.PlanningFrames.AddRange(planningRequest.ContextFrames);

        return planningRequest;
    }

    private IReadOnlyList<string> PlannerSecurityBlockers(AgenticaRun run)
    {
        var securityPolicy = _policy.EffectiveSecurityPolicy;
        if (_planner is IExternalWorkflowPlanner && !securityPolicy.UsesExternalPlanner)
        {
            return
            [
                "The planner declares an external trust boundary, but no explicit external-planner boundary policy is configured."
            ];
        }

        var violations = ToolSecurityEvaluator.PlannerBoundaryViolations(
            securityPolicy,
            run.ExposedBoundaries);
        return violations.Count == 0
            ? []
            :
            [
                "Planner dispatch was denied because sticky run data exceeds the external-planner boundary policy: " +
                $"{string.Join(", ", violations)}."
            ];
    }

    private static void RegisterPlanToolSurface(
        AgenticaRun run,
        WorkflowPlan plan,
        PlanningRequest planningRequest)
    {
        if (planningRequest.ToolSurface is { } toolSurface)
        {
            run.PlanToolSurfaceIds[plan.PlanId] = toolSurface.SurfaceId;
            run.PlanToolManifestHashes[plan.PlanId] = toolSurface.ManifestHash;
        }
    }

    private static string? ToolSurfaceIdFor(AgenticaRun run, WorkflowPlan plan) =>
        run.PlanToolSurfaceIds.TryGetValue(plan.PlanId, out var toolSurfaceId)
            ? toolSurfaceId
            : null;

    private static string? ToolManifestHashFor(AgenticaRun run, WorkflowPlan plan) =>
        run.PlanToolManifestHashes.TryGetValue(plan.PlanId, out var manifestHash)
            ? manifestHash
            : null;

    private static ExecutionEventContext Context(
        AgenticaRun run,
        WorkflowPlan? plan = null,
        PlanStep? step = null,
        string? batchId = null,
        string? toolSurfaceId = null,
        string? stepId = null,
        string? toolId = null,
        string? receiptId = null,
        string? observationId = null,
        string? artifactId = null,
        string? fromPlanId = null,
        string? toPlanId = null) =>
        new(
            RunId: run.RunId,
            AttemptNumber: run.AttemptNumber,
            PlanId: plan?.PlanId,
            PlanVersion: plan?.Version,
            StepId: step?.StepId ?? stepId,
            BatchId: batchId ?? step?.BatchId,
            ToolId: step?.ToolId ?? toolId,
            ReceiptId: receiptId,
            ObservationId: observationId,
            ArtifactId: artifactId,
            ToolSurfaceId: toolSurfaceId,
            FromPlanId: fromPlanId,
            ToPlanId: toPlanId);

    private Dictionary<string, object?> StepPayload(PlanStep step)
    {
        var descriptor = _toolCatalog.Resolve(step.ToolId)?.Descriptor;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = step.Kind.ToString(),
            ["effect"] = step.Effect.ToString(),
            ["toolName"] = descriptor?.Name,
            ["requiresApproval"] = descriptor?.RequiresApproval,
            ["cooldown"] = descriptor?.Cooldown,
            ["inputKeys"] = step.Input.Keys.ToArray()
        };
    }

    private static ExecutionIntent? IntentFor(PlanStep step)
    {
        if (step.Intent is not null)
        {
            return step.Intent;
        }

        return string.IsNullOrWhiteSpace(step.Reason)
            ? null
            : new ExecutionIntent($"Invoke {step.ToolId}.", step.Reason);
    }

    private static ExecutionIntent IntentForRefinement(
        string refinementReason,
        Observation observation,
        Receipt receipt,
        WorkflowPlan refinedPlan)
    {
        var nextStep = refinedPlan.Steps.FirstOrDefault();
        var nextIntent = nextStep is null ? null : IntentFor(nextStep);
        var nextAction = nextIntent?.Action ?? $"Execute {nextStep?.ToolId ?? "the next validated step"}.";
        var rationaleParts = new[]
        {
            $"Refinement reason: {refinementReason}.",
            $"Latest observation: {observation.Summary}",
            $"Receipt: {receipt.Status} from {receipt.ToolId} - {receipt.Message}",
            nextIntent is null ? null : $"Next-step rationale: {nextIntent.Rationale}"
        }.Where(part => !string.IsNullOrWhiteSpace(part));

        return new ExecutionIntent(
            $"Refine the plan toward: {nextAction}",
            string.Join(" ", rationaleParts),
            nextIntent?.ExpectedOutcome ?? "A validated plan slice that responds to the latest evidence.");
    }

    private static IReadOnlyDictionary<string, object?> StepIntentPayload(PlanStep step)
    {
        var intent = IntentFor(step);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stepId"] = step.StepId,
            ["toolId"] = step.ToolId,
            ["action"] = intent?.Action,
            ["rationale"] = intent?.Rationale,
            ["expectedOutcome"] = intent?.ExpectedOutcome,
            ["reason"] = step.Reason
        };
    }

    private static string CancellationSource(CancellationToken effectiveToken, CancellationToken callerToken)
    {
        if (callerToken.IsCancellationRequested)
        {
            return "caller";
        }

        if (effectiveToken.IsCancellationRequested)
        {
            return "execution_policy_timeout";
        }

        return "planner";
    }

    private static ExecutionDiagnostics? DiagnosticsFrom(IReadOnlyList<ValidationIssue> validationIssues)
    {
        var issue = validationIssues.FirstOrDefault();
        return issue is null
            ? null
            : new ExecutionDiagnostics(issue.Code, issue.Message);
    }

    private static ExecutionDiagnostics? DiagnosticsFromReceipt(Receipt receipt)
    {
        var code = receipt.Status switch
        {
            ReceiptStatus.Failed => "tool.failed",
            ReceiptStatus.TimedOut => "tool.timed_out",
            ReceiptStatus.Cancelled => "tool.cancelled",
            _ => null
        };

        if (code is null)
        {
            return null;
        }

        var errorClass = receipt.Data.TryGetValue("errorClass", out var value)
            ? Convert.ToString(value)
            : null;

        return new ExecutionDiagnostics(
            code,
            receipt.Message,
            errorClass,
            receipt.Status.ToString());
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
        var code = string.IsNullOrWhiteSpace(exception.Code) ? defaultCode : exception.Code;
        var diagnostics = new ExecutionDiagnostics(
            Code: code,
            Message: exception.Message,
            ErrorClass: exception.GetType().Name,
            FailureKind: exception.FailureKind.ToString());

        if (exception.FailureKind == WorkflowPlannerFailureKind.Unavailable)
        {
            return Finish(
                run,
                RunOutcomeStatus.Blocked,
                StopReason.PlannerUnavailable,
                blockers: [exception.Message],
                diagnostics: diagnostics);
        }

        return Finish(
            run,
            RunOutcomeStatus.PlanInvalid,
            StopReason.PlanInvalid,
            validationIssues:
            [
                new ValidationIssue(code, exception.Message)
            ],
            diagnostics: diagnostics);
    }

    private sealed record StepExecutionResult(
        PlanStep Step,
        ToolResult Result,
        ExecutionDiagnostics? Diagnostics,
        long DurationMs,
        IReadOnlySet<ToolDataBoundary>? ExposedBoundaries = null);
}
