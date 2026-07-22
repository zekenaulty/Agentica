using Agentica.Artifacts;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Runs;
using Agentica.Tools;
using Agentica.Validation;

namespace Agentica.Tests;

public sealed class ProofBoundaryIsolationTests
{
    [Fact]
    public async Task Mutating_outcome_reporter_cannot_erase_proof_or_authorize_mutation_retry()
    {
        var mutation = new StatusTool(ReceiptStatus.Succeeded);
        var unavailable = new StatusTool(ReceiptStatus.Unavailable);
        var planner = new StaticPlanner(Plan(
            Step("step_mutate", "state.mutate", ToolKind.Action, ToolEffect.WritesLocalState),
            Step("step_blocked", "state.read", ToolKind.Query, ToolEffect.ReadOnly)));
        var runner = Runner(
            planner,
            ToolCatalog.Create(
                Registration("state.mutate", ToolKind.Action, ToolEffect.WritesLocalState, mutation),
                Registration("state.read", ToolKind.Query, ToolEffect.ReadOnly, unavailable)),
            new MutatingOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 2,
                EffectPolicy: ToolEffectPolicy.AllowKnown));

        var envelope = await runner.RunAsync(new RunRequest("Preserve mutation proof."));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.ToolUnavailable, envelope.Outcome.StopReason);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Equal(1, mutation.ExecutionCount);
        Assert.Empty(envelope.PriorAttempts);
        Assert.Equal(["step_mutate", "step_blocked"], envelope.Outcome.CompletedSteps);
        Assert.Equal(2, envelope.Receipts.Items.Count);
        Assert.Equal(2, envelope.Details.PlanVersions[0].Steps.Count);
    }

    [Fact]
    public async Task Refinement_planner_cannot_mutate_authoritative_receipt_or_observation_views()
    {
        var planner = new MutatingRefinementPlanner(
            Plan(Step("step_observe", "state.observe", ToolKind.Query, ToolEffect.ReadOnly)),
            Plan(Step("step_finish", "state.finish", ToolKind.Query, ToolEffect.ReadOnly)));
        var runner = Runner(
            planner,
            ToolCatalog.Create(
                Registration(
                    "state.observe",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    new ObservationTool()),
                Registration(
                    "state.finish",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    new StatusTool(ReceiptStatus.Succeeded))),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 1,
                PlanningMode: PlanningMode.Stepwise));

        var envelope = await runner.RunAsync(new RunRequest("Keep planner views detached."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(2, envelope.Receipts.Items.Count);
        Assert.Single(envelope.Details.Observations);
        Assert.Equal(2, envelope.Outcome.CompletedSteps.Count);
        Assert.True(planner.AttemptedReceiptMutation);
        Assert.True(planner.AttemptedObservationMutation);
    }

    [Fact]
    public async Task Accepted_plan_and_step_input_are_detached_before_dispatch()
    {
        var sourceInput = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["value"] = "validated"
        };
        var sourceSteps = new List<PlanStep>
        {
            new(
                "step_execute",
                "state.execute",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                sourceInput)
        };
        var tool = new MutatingSourcePlanTool(() =>
        {
            sourceInput["value"] = "tampered";
            sourceSteps[0] = Step(
                "step_execute",
                "state.forged",
                ToolKind.Query,
                ToolEffect.ReadOnly);
        });
        var planner = new StaticPlanner(new WorkflowPlan(
            "plan_mutable_source",
            1,
            sourceSteps,
            "Mutable source plan."));
        var runner = Runner(
            planner,
            ToolCatalog.Create(Registration(
                "state.execute",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                tool)),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest("Freeze the accepted plan."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal("tampered", sourceInput["value"]);
        Assert.Equal("validated", tool.ObservedValue);
        var storedStep = Assert.Single(Assert.Single(envelope.Details.PlanVersions).Steps);
        Assert.Equal("state.execute", storedStep.ToolId);
        Assert.Equal("validated", storedStep.Input["value"]);
        Assert.Equal("state.execute", Assert.Single(envelope.Receipts.Items).ToolId);
    }

    [Fact]
    public async Task Planner_cannot_rewrite_authoritative_context_frames_or_tool_surfaces()
    {
        var planner = new MutatingPlanningMetadataPlanner(
            Plan(Step("step_read", "state.read", ToolKind.Query, ToolEffect.ReadOnly)));
        var runner = Runner(
            planner,
            ToolCatalog.Create(Registration(
                "state.read",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new StatusTool(ReceiptStatus.Succeeded))),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest("Keep planning metadata detached."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        var surface = Assert.Single(envelope.Details.ToolSurfaces);
        Assert.Equal("local", surface.PolicySummary["plannerBoundaryMode"]);
        var frame = Assert.Single(
            envelope.Details.PlanningFrames,
            item => item.Kind == "agentica.goal_spine");
        Assert.Contains("proofBoundary", frame.Payload);
        Assert.True(planner.AttemptedSurfaceMutation);
        Assert.True(planner.AttemptedFrameMutation);
    }

    [Fact]
    public async Task Nested_request_context_is_detached_for_retry_planner_and_returned_proof()
    {
        var callerItems = new List<object?> { "caller-original" };
        var callerNested = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["mode"] = "caller-original",
            ["items"] = callerItems
        };
        var callerHost = new MutableRequestHost(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "host-original"
            },
            ["host-original"]);
        var planner = new RequestMutatingRetryPlanner(
            Plan(Step("step_retry", "state.retry", ToolKind.Query, ToolEffect.ReadOnly)));
        var unavailable = new StatusTool(ReceiptStatus.Unavailable);
        var runner = Runner(
            planner,
            ToolCatalog.Create(Registration(
                "state.retry",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                unavailable)),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 1));
        var request = new RunRequest(
            "Retry without request aliases.",
            Context: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["nested"] = callerNested,
                ["host"] = callerHost
            });

        var runTask = runner.RunAsync(request);
        await planner.FirstRequestCaptured.Task;

        callerNested["mode"] = "caller-mutated";
        callerItems.Add("caller-mutated");
        callerHost.Settings["mode"] = "host-caller-mutated";
        callerHost.Items.Add("host-caller-mutated");
        planner.ReleaseFirstRequest.TrySetResult(true);

        var envelope = await runTask;

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(2, unavailable.ExecutionCount);
        Assert.Equal(2, planner.Views.Count);
        Assert.All(planner.Views, view =>
        {
            Assert.Equal("caller-original", view.NestedMode);
            Assert.Equal(["caller-original"], view.NestedItems);
            Assert.Equal("host-original", view.HostMode);
            Assert.Equal(["host-original"], view.HostItems);
        });
        Assert.True(planner.AttemptedNestedDictionaryMutation);
        Assert.True(planner.AttemptedNestedListMutation);

        Assert.Single(envelope.PriorAttempts);
        AssertRequestContextPreserved(envelope.PriorAttempts[0].Details.Request);
        AssertRequestContextPreserved(envelope.Details.Request);
        Assert.Contains("agentica.retry", envelope.Details.Request.Context!);
    }

    [Fact]
    public async Task Cyclic_request_context_fails_closed_before_planning()
    {
        var cycle = new Dictionary<string, object?>(StringComparer.Ordinal);
        cycle["self"] = cycle;
        var planner = new StaticPlanner(
            Plan(Step("step_never", "state.never", ToolKind.Query, ToolEffect.ReadOnly)));
        var tool = new StatusTool(ReceiptStatus.Succeeded);
        var runner = Runner(
            planner,
            ToolCatalog.Create(Registration(
                "state.never",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                tool)),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 1,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest(
            "Reject cyclic context.",
            Context: new Dictionary<string, object?> { ["cycle"] = cycle }));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Equal(StopReason.PlanInvalid, envelope.Outcome.StopReason);
        Assert.Equal(0, planner.CreatePlanCount);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Null(envelope.Details.Request.Context);
        Assert.Contains(
            envelope.Details.ValidationIssues,
            issue => issue.Code == "request.context.snapshot.invalid");
    }

    [Fact]
    public void Evidence_completion_evaluator_snapshots_caller_owned_requirements()
    {
        var requirements = new List<CompletionEvidenceRequirement>
        {
            CompletionEvidenceRequirement.ArtifactKind("required.artifact")
        };
        var evaluator = new EvidenceCompletionEvaluator(requirements, continueWhenMissing: false);

        requirements.Clear();

        var evaluation = evaluator.Evaluate(new CompletionContext(
            "run_completion_snapshot",
            1,
            [],
            [],
            [],
            []));
        Assert.Equal(CompletionDecision.Blocked, evaluation.Decision);
        Assert.Equal(StopReason.CompletionNotSatisfied, evaluation.StopReason);
    }

    [Theory]
    [InlineData(CompletionDecision.Blocked, RunOutcomeStatus.Blocked)]
    [InlineData(CompletionDecision.Partial, RunOutcomeStatus.PartiallyComplete)]
    public async Task Post_batch_terminal_completion_prevents_later_mutation(
        CompletionDecision decision,
        RunOutcomeStatus expectedStatus)
    {
        var read = new StatusTool(ReceiptStatus.Succeeded);
        var mutation = new StatusTool(ReceiptStatus.Succeeded);
        var runner = new AgenticaRunner(
            new StaticPlanner(Plan(
                Step("step_read", "state.read", ToolKind.Query, ToolEffect.ReadOnly),
                Step("step_mutate", "state.mutate", ToolKind.Action, ToolEffect.WritesLocalState))),
            ToolCatalog.Create(
                Registration("state.read", ToolKind.Query, ToolEffect.ReadOnly, read),
                Registration("state.mutate", ToolKind.Action, ToolEffect.WritesLocalState, mutation)),
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                EffectPolicy: ToolEffectPolicy.AllowKnown,
                EvaluateCompletionAfterEachBatch: true),
            new FixedTerminalCompletionEvaluator(decision));

        var envelope = await runner.RunAsync(new RunRequest("Stop before mutation."));

        Assert.Equal(expectedStatus, envelope.Outcome.Status);
        Assert.Equal(1, read.ExecutionCount);
        Assert.Equal(0, mutation.ExecutionCount);
        Assert.Equal(["step_read"], envelope.Outcome.CompletedSteps);
        Assert.Single(envelope.Receipts.Items);
    }

    private static AgenticaRunner Runner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        IOutcomeReporter reporter,
        ExecutionPolicy policy) =>
        new(
            planner,
            catalog,
            new InMemoryEventSink(),
            reporter,
            policy,
            PlanExhaustionCompletionEvaluator.Instance);

    private static WorkflowPlan Plan(params PlanStep[] steps) =>
        new("plan_proof_boundary", 1, steps, "Proof-boundary isolation plan.");

    private static PlanStep Step(
        string stepId,
        string toolId,
        ToolKind kind,
        ToolEffect effect) =>
        new(stepId, toolId, kind, effect, new Dictionary<string, object?>());

    private static ToolRegistration Registration(
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        ITool tool) =>
        TestToolRegistration.Create(
            new ToolDescriptor(toolId, toolId, kind, effect),
            tool);

    private static void AssertRequestContextPreserved(RunRequest request)
    {
        var context = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(request.Context);
        var nested = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(context["nested"]);
        Assert.Equal("caller-original", nested["mode"]);
        Assert.Equal(
            ["caller-original"],
            Assert.IsAssignableFrom<IEnumerable<object?>>(nested["items"]));
        var host = Assert.IsType<MutableRequestHost>(context["host"]);
        Assert.Equal("host-original", host.Settings["mode"]);
        Assert.Equal(["host-original"], host.Items);
    }

    private sealed class StaticPlanner(WorkflowPlan plan) : IWorkflowPlanner
    {
        public int CreatePlanCount { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatePlanCount++;
            return Task.FromResult(plan);
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class MutatingRefinementPlanner(
        WorkflowPlan initialPlan,
        WorkflowPlan refinedPlan) : IWorkflowPlanner
    {
        public bool AttemptedReceiptMutation { get; private set; }

        public bool AttemptedObservationMutation { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(initialPlan);

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default)
        {
            AttemptedReceiptMutation = TryClear(request.Receipts);
            AttemptedObservationMutation = TryClear(request.Observations);
            return Task.FromResult(refinedPlan);
        }

        private static bool TryClear<T>(IReadOnlyList<T> items)
        {
            if (items is not ICollection<T> collection)
            {
                return false;
            }

            try
            {
                collection.Clear();
            }
            catch (NotSupportedException)
            {
                // A read-only detached collection is the preferred representation.
            }

            return true;
        }
    }

    private sealed class MutatingOutcomeReporter : IOutcomeReporter
    {
        public OutcomeReport BuildReport(
            AgenticaRun run,
            RunOutcomeStatus status,
            StopReason stopReason,
            IReadOnlyList<ValidationIssue> validationIssues,
            IReadOnlyList<string> blockers)
        {
            run.CompletedSteps.Clear();
            run.PlanVersions.Clear();
            run.PlanRefinements.Clear();
            run.Receipts.Clear();
            run.Observations.Clear();
            run.Artifacts.Clear();
            run.Batches.Clear();
            run.ToolSurfaces.Clear();
            run.PlanningFrames.Clear();
            run.PlanToolSurfaceIds.Clear();
            run.PlanToolManifestHashes.Clear();
            run.ExposedBoundaries.Clear();
            return new OutcomeReport(
                "report_mutating_observer",
                "Reporter attempted to mutate its detached view.",
                [new ReportClaim("Detached observer.", [new EvidenceRef("stopReason", stopReason.ToString())])]);
        }
    }

    private sealed class MutatingPlanningMetadataPlanner(WorkflowPlan plan) : IWorkflowPlanner
    {
        public bool AttemptedSurfaceMutation { get; private set; }

        public bool AttemptedFrameMutation { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ToolSurface?.PolicySummary is IDictionary<string, object?> policySummary)
            {
                AttemptedSurfaceMutation = true;
                policySummary.Clear();
            }

            var frame = request.ContextFrames.Single(item => item.Kind == "agentica.goal_spine");
            if (frame.Payload is IDictionary<string, object?> payload)
            {
                AttemptedFrameMutation = true;
                payload.Clear();
            }

            return Task.FromResult(plan);
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class RequestMutatingRetryPlanner(WorkflowPlan plan) : IWorkflowPlanner
    {
        public TaskCompletionSource<bool> FirstRequestCaptured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseFirstRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<RequestContextView> Views { get; } = [];

        public bool AttemptedNestedDictionaryMutation { get; private set; }

        public bool AttemptedNestedListMutation { get; private set; }

        public async Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var context = request.Request.Context ??
                    throw new InvalidOperationException("Planner request context is required.");
                var nested = (IReadOnlyDictionary<string, object?>)context["nested"]!;
                var nestedItems = (IEnumerable<object?>)nested["items"]!;
                var host = (MutableRequestHost)context["host"]!;
                Views.Add(new RequestContextView(
                    Convert.ToString(nested["mode"])!,
                    nestedItems.Select(Convert.ToString).ToArray()!,
                    host.Settings["mode"],
                    host.Items.ToArray()));

                host.Settings["mode"] = "host-planner-mutated";
                host.Items.Add("host-planner-mutated");
                if (nested is IDictionary<string, object?> mutableNested)
                {
                    AttemptedNestedDictionaryMutation = true;
                    try
                    {
                        mutableNested["mode"] = "planner-mutated";
                    }
                    catch (NotSupportedException)
                    {
                    }
                }

                if (nested["items"] is IList<object?> mutableItems)
                {
                    AttemptedNestedListMutation = true;
                    try
                    {
                        mutableItems.Add("planner-mutated");
                    }
                    catch (NotSupportedException)
                    {
                    }
                }

                if (Views.Count == 1)
                {
                    FirstRequestCaptured.TrySetResult(true);
                    await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
                }

                return plan;
            }
            catch (Exception exception)
            {
                FirstRequestCaptured.TrySetException(exception);
                throw;
            }
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    public sealed record MutableRequestHost(
        Dictionary<string, string> Settings,
        List<string> Items);

    private sealed record RequestContextView(
        string NestedMode,
        IReadOnlyList<string> NestedItems,
        string HostMode,
        IReadOnlyList<string> HostItems);

    private sealed class StatusTool(ReceiptStatus status) : ITool
    {
        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(Receipt(invocation, status)));
        }
    }

    private sealed class ObservationTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded);
            return Task.FromResult(new ToolResult(
                receipt,
                new Observation(
                    "source_observation",
                    invocation.StepId,
                    ObservationKind.StateQuery,
                    "Observed state.",
                    new Dictionary<string, object?> { ["ready"] = true },
                    [new EvidenceRef("receipt", receipt.ReceiptId)])));
        }
    }

    private sealed class MutatingSourcePlanTool(Action mutateSourcePlan) : ITool
    {
        public string? ObservedValue { get; private set; }

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken cancellationToken)
        {
            mutateSourcePlan();
            ObservedValue = Convert.ToString(invocation.Input["value"]);
            return Task.FromResult(new ToolResult(Receipt(invocation, ReceiptStatus.Succeeded)));
        }
    }

    private sealed class FixedTerminalCompletionEvaluator(CompletionDecision decision) : ICompletionEvaluator
    {
        public CompletionEvaluation Evaluate(CompletionContext context) =>
            decision switch
            {
                CompletionDecision.Blocked => CompletionEvaluation.Blocked(
                    StopReason.CompletionNotSatisfied,
                    "Completion policy blocked further work."),
                CompletionDecision.Partial => CompletionEvaluation.Partial(
                    "Completion policy accepted only partial work."),
                _ => throw new InvalidOperationException("Test evaluator requires a blocked or partial decision.")
            };
    }

    private static Receipt Receipt(ToolInvocation invocation, ReceiptStatus status) =>
        new(
            "source_receipt",
            invocation.StepId,
            invocation.ToolId,
            status,
            status.ToString(),
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());
}
