using Agentica.Artifacts;
using Agentica.Continuity;
using Agentica.Observations;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;
using Agentica.Tools;

namespace Agentica.Execution;

internal sealed class PlanningRequestFactory
{
    private readonly ToolCatalog _toolCatalog;
    private readonly ExecutionPolicy _policy;
    private readonly IPlanningFrameProjector? _frameProjector;
    private readonly IGoalSpineCompiler _goalSpineCompiler;

    public PlanningRequestFactory(
        ToolCatalog toolCatalog,
        ExecutionPolicy policy,
        IPlanningFrameProjector? frameProjector = null,
        IGoalSpineCompiler? goalSpineCompiler = null)
    {
        _toolCatalog = toolCatalog;
        _policy = policy;
        _frameProjector = frameProjector;
        _goalSpineCompiler = goalSpineCompiler ?? new DefaultGoalSpineCompiler();
    }

    public PlanningRequest Create(RunRequest request, AgenticaRun run)
    {
        var context = _policy.EffectivePlanningContext;
        var observations = Limit(run.Observations, context.MaxRecentObservations);
        var receipts = Limit(run.Receipts, context.MaxRecentReceipts);
        var executionContext = CreateExecutionContext(run);
        var toolSurface = CreateToolSurfaceSnapshot(run, observations, receipts, executionContext, context);
        var requestContext = new PlanningRequest(
            request,
            _toolCatalog.Descriptors,
            observations,
            receipts)
        {
            ExecutionContext = executionContext,
            ToolSurface = toolSurface
        };

        var projectedFrames = _frameProjector?.Project(new PlanningFrameProjectionRequest(
            RunId: run.RunId,
            AttemptNumber: run.AttemptNumber,
            Request: request,
            ExecutionContext: executionContext,
            ToolDescriptors: _toolCatalog.Descriptors,
            Observations: observations,
            Receipts: receipts,
            ToolSurface: toolSurface)) ?? [];
        var goalSpineFrame = CreateGoalSpineFrame(
            request,
            run,
            executionContext,
            toolSurface);
        var contextFrames = projectedFrames
            .Concat([goalSpineFrame])
            .ToArray();

        return requestContext with
        {
            ContextFrames = contextFrames
        };
    }

    private ToolSurfaceSnapshot CreateToolSurfaceSnapshot(
        AgenticaRun run,
        IReadOnlyList<Observation> observations,
        IReadOnlyList<Receipt> receipts,
        PlanningExecutionContext executionContext,
        PlanningContextOptions context) =>
        new(
            AgenticaIds.New("surface"),
            _toolCatalog.ManifestHash,
            DateTimeOffset.UtcNow,
            _toolCatalog.Descriptors,
            executionContext,
            observations
                .Select(observation => new EvidenceRef("observation", observation.ObservationId))
                .ToArray(),
            receipts
                .Select(receipt => new EvidenceRef("receipt", receipt.ReceiptId))
                .ToArray(),
            CreatePolicySummary(run, context));

    private PlanningFrame CreateGoalSpineFrame(
        RunRequest request,
        AgenticaRun run,
        PlanningExecutionContext executionContext,
        ToolSurfaceSnapshot toolSurface)
    {
        var spine = _goalSpineCompiler.CompileInitial(request);
        var updateContext = new GoalSpineUpdateContext(
            run.RunId,
            run.AttemptNumber,
            request.Context ?? new Dictionary<string, object?>(StringComparer.Ordinal),
            executionContext);

        foreach (var receipt in run.Receipts)
        {
            spine = _goalSpineCompiler.UpdateFromReceipt(spine, receipt, updateContext).Spine;
        }

        foreach (var refinement in run.PlanRefinements)
        {
            spine = _goalSpineCompiler.UpdateFromRefinement(spine, refinement, updateContext).Spine;
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["goalSpine"] = spine,
            ["proofBoundary"] =
                "GoalSpine shapes continuity only; receipts, observations, artifacts, host checks, and verifiers prove reality.",
            ["plannerUse"] =
                "Use GoalSpine to preserve run-level continuity and divergence pressure. Do not use it as completion evidence."
        };

        return new PlanningFrame(
            FrameId: AgenticaIds.New("frame"),
            Kind: "agentica.goal_spine",
            Version: "1.0",
            CreatedAt: DateTimeOffset.UtcNow,
            Payload: payload,
            EvidenceRefs: spine.EvidenceRefs)
        {
            ToolSurfaceId = toolSurface.SurfaceId
        };
    }

    private IReadOnlyDictionary<string, object?> CreatePolicySummary(
        AgenticaRun run,
        PlanningContextOptions context)
    {
        var completedStepCount = run.CompletedSteps.Count;
        var remainingStepBudget = Math.Max(0, _policy.MaxSteps - completedStepCount);
        var refinementCount = run.PlanRefinements.Count;
        var remainingRefinementBudget = Math.Max(0, _policy.MaxRefinements - refinementCount);
        var continuationCount = Math.Max(0, run.PlanVersions.Count - refinementCount - 1);
        var remainingContinuationBudget = Math.Max(0, _policy.MaxPlanContinuations - continuationCount);
        var elapsed = DateTimeOffset.UtcNow - run.CreatedAt;
        var remainingTimeout = RemainingTimeout(elapsed);
        var timePressure = ClassifyTimePressure(_policy.Timeout, remainingTimeout);
        var runPressure = ClassifyRunPressure(_policy.MaxSteps, remainingStepBudget, timePressure);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["maxSteps"] = _policy.MaxSteps,
            ["completedStepCount"] = completedStepCount,
            ["remainingStepBudget"] = remainingStepBudget,
            ["maxRefinements"] = _policy.MaxRefinements,
            ["planRefinementCount"] = refinementCount,
            ["remainingRefinementBudget"] = remainingRefinementBudget,
            ["planningMode"] = _policy.PlanningMode.ToString(),
            ["maxPlanContinuations"] = _policy.MaxPlanContinuations,
            ["planContinuationCount"] = continuationCount,
            ["remainingPlanContinuationBudget"] = remainingContinuationBudget,
            ["maxBlockedRetries"] = _policy.MaxBlockedRetries,
            ["retryableStopReasons"] = _policy.EffectiveBlockedRetries.RetryableStopReasons
                .Select(reason => reason.ToString())
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToArray(),
            ["authorizedMutationRetryToolIds"] = _policy.EffectiveBlockedRetries.AuthorizedMutationToolIds
                .OrderBy(toolId => toolId, StringComparer.Ordinal)
                .ToArray(),
            ["maxBatchSize"] = _policy.MaxBatchSize,
            ["maxParallelism"] = _policy.MaxParallelism,
            ["allowReadOnlyParallelBatches"] = _policy.AllowReadOnlyParallelBatches,
            ["maxRecentObservations"] = context.MaxRecentObservations,
            ["maxRecentReceipts"] = context.MaxRecentReceipts,
            ["allowedEffects"] = _policy.EffectiveEffectPolicy.AllowedEffects
                .Select(effect => effect.ToString())
                .ToArray(),
            ["toolManifestHash"] = _toolCatalog.ManifestHash,
            ["plannerBoundaryMode"] = _policy.EffectiveSecurityPolicy.UsesExternalPlanner
                ? "external"
                : "local",
            ["initialDataBoundaries"] = _policy.EffectiveSecurityPolicy.InitialBoundaries
                .Select(boundary => boundary.ToString())
                .OrderBy(boundary => boundary, StringComparer.Ordinal)
                .ToArray(),
            ["exposedDataBoundaries"] = run.ExposedBoundaries
                .Select(boundary => boundary.ToString())
                .OrderBy(boundary => boundary, StringComparer.Ordinal)
                .ToArray(),
            ["externalPlannerAllowedBoundaries"] = _policy.EffectiveSecurityPolicy.ExternalPlannerAllowedBoundaries?
                .Select(boundary => boundary.ToString())
                .OrderBy(boundary => boundary, StringComparer.Ordinal)
                .ToArray(),
            ["executionGrantCount"] = _policy.EffectiveSecurityPolicy.ExecutionGrants.Count,
            ["elapsedMs"] = Milliseconds(elapsed),
            ["timeoutMs"] = _policy.Timeout is null ? null : Milliseconds(_policy.Timeout.Value),
            ["remainingTimeoutMs"] = remainingTimeout is null ? null : Milliseconds(remainingTimeout.Value),
            ["timePressure"] = timePressure,
            ["runPressure"] = runPressure,
            ["recommendedPlanningPosture"] = RecommendedPlanningPosture(runPressure),
            ["planningConstraints"] = PlanningConstraints(runPressure)
        };
    }

    private TimeSpan? RemainingTimeout(TimeSpan elapsed)
    {
        if (_policy.Timeout is not { } timeout)
        {
            return null;
        }

        var remaining = timeout - elapsed;
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }

    private static long Milliseconds(TimeSpan value) =>
        (long)Math.Max(0, value.TotalMilliseconds);

    private static string ClassifyTimePressure(TimeSpan? timeout, TimeSpan? remaining)
    {
        if (timeout is null || timeout.Value <= TimeSpan.Zero || remaining is null)
        {
            return "none";
        }

        if (remaining.Value <= TimeSpan.Zero)
        {
            return "expired";
        }

        var ratio = remaining.Value.TotalMilliseconds / Math.Max(1, timeout.Value.TotalMilliseconds);
        if (remaining.Value <= TimeSpan.FromSeconds(5) || ratio <= 0.05)
        {
            return "critical";
        }

        if (remaining.Value <= TimeSpan.FromSeconds(15) || ratio <= 0.15)
        {
            return "high";
        }

        if (remaining.Value <= TimeSpan.FromSeconds(30) || ratio <= 0.35)
        {
            return "moderate";
        }

        return "low";
    }

    private static string ClassifyRunPressure(int maxSteps, int remainingStepBudget, string timePressure)
    {
        var timeRank = PressureRank(timePressure);
        var stepRank = remainingStepBudget switch
        {
            <= 0 => 4,
            1 => 3,
            2 => 2,
            _ when maxSteps > 0 && remainingStepBudget <= Math.Max(3, maxSteps / 4) => 1,
            _ => 0
        };

        return PressureName(Math.Max(timeRank, stepRank));
    }

    private static int PressureRank(string pressure) =>
        pressure switch
        {
            "expired" => 4,
            "critical" => 3,
            "high" => 2,
            "moderate" => 1,
            _ => 0
        };

    private static string PressureName(int rank) =>
        rank switch
        {
            >= 4 => "exhausted",
            3 => "critical",
            2 => "high",
            1 => "moderate",
            _ => "low"
        };

    private static string RecommendedPlanningPosture(string runPressure) =>
        runPressure switch
        {
            "exhausted" =>
                "Do not request more context. The next response should be a terminal blocker or completion-directed slice if the runtime still asks for a plan.",
            "critical" =>
                "Use existing public context. Prefer one bounded action or completion-check action; avoid read-only context unless it is the only missing precondition that immediately changes the next action.",
            "high" =>
                "Prefer bounded action over context expansion. Use at most one non-redundant read-only query when a specific missing public precondition blocks action.",
            "moderate" =>
                "Avoid consecutive read-only slices. Batch only genuinely independent missing-precondition queries, otherwise act from current public context.",
            _ =>
                "Normal bounded planning. Read-only queries are for missing public preconditions, not reassurance."
        };

    private static string[] PlanningConstraints(string runPressure) =>
        runPressure switch
        {
            "exhausted" or "critical" =>
            [
                "Do not query merely to reconfirm unchanged public state.",
                "Use cooldown refusals as evidence that the previous public context is still current.",
                "Choose a bounded state-changing action, completion-check action, or explicit blocker over recursive context gathering."
            ],
            "high" =>
            [
                "Do not spend consecutive planning turns on read-only context without new uncertainty.",
                "Use existing observations and receipts after cooldown refusal.",
                "Prefer bounded action when current public context is sufficient."
            ],
            _ =>
            [
                "Read-only queries are for missing public preconditions, not reassurance.",
                "Use cooldown refusals as pressure to act or choose a different non-redundant source.",
                "Prefer bounded action when current public context is sufficient."
            ]
        };

    private static PlanningExecutionContext CreateExecutionContext(AgenticaRun run) =>
        new(
            run.CompletedSteps.ToArray(),
            run.CompletedSteps.Select(stepId => CreateCompletedStepContext(run, stepId)).ToArray(),
            run.PlanVersions.LastOrDefault()?.PlanId,
            run.PlanVersions.Count);

    private static CompletedStepContext CreateCompletedStepContext(AgenticaRun run, string stepId)
    {
        var planMatch = run.PlanVersions
            .SelectMany(plan => plan.Steps.Select(step => new { Plan = plan, Step = step }))
            .FirstOrDefault(item => string.Equals(item.Step.StepId, stepId, StringComparison.Ordinal));
        var receipt = run.Receipts.LastOrDefault(item => string.Equals(item.StepId, stepId, StringComparison.Ordinal));
        var observation = run.Observations.LastOrDefault(item => string.Equals(item.StepId, stepId, StringComparison.Ordinal));
        var artifact = receipt is null
            ? null
            : run.Artifacts.LastOrDefault(item => item.Evidence.Any(evidence =>
                string.Equals(evidence.Kind, "receipt", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(evidence.RefId, receipt.ReceiptId, StringComparison.Ordinal)));

        return new CompletedStepContext(
            stepId,
            planMatch?.Step.ToolId ?? receipt?.ToolId,
            planMatch?.Plan.PlanId,
            planMatch?.Plan.Version,
            receipt?.ReceiptId,
            receipt?.Status.ToString(),
            observation?.ObservationId,
            artifact?.ArtifactId);
    }

    internal static IReadOnlyList<T> Limit<T>(IReadOnlyList<T> items, int? maxItems) =>
        maxItems switch
        {
            null => items,
            <= 0 => [],
            _ => items.TakeLast(maxItems.Value).ToArray()
        };
}
