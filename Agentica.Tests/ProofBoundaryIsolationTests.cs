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
        var allowedEffects = Assert.IsAssignableFrom<IList<string>>(
            surface.PolicySummary["allowedEffects"]);
        Assert.True(allowedEffects.IsReadOnly);
        Assert.DoesNotContain("late-effect", allowedEffects);
        var frame = Assert.Single(
            envelope.Details.PlanningFrames,
            item => item.Kind == "agentica.goal_spine");
        Assert.Contains("proofBoundary", frame.Payload);
        Assert.True(planner.AttemptedSurfaceMutation);
        Assert.True(planner.SurfaceMutationBlocked);
        Assert.True(planner.AttemptedNestedSurfaceMutation);
        Assert.True(planner.NestedSurfaceMutationBlocked);
        Assert.True(planner.AttemptedFrameMutation);
        Assert.True(planner.FrameMutationBlocked);
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
    public async Task Request_snapshot_enforces_one_aggregate_budget_before_planning()
    {
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
        var context = Enumerable.Range(0, 4).ToDictionary(
            index => $"value{index}",
            _ => (object?)new string('r', 210_000),
            StringComparer.Ordinal);

        var envelope = await runner.RunAsync(new RunRequest(
            new string('o', 210_000),
            Context: context));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Equal(StopReason.PlanInvalid, envelope.Outcome.StopReason);
        Assert.Equal(0, planner.CreatePlanCount);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Contains(
            envelope.Details.ValidationIssues,
            issue => issue.Code == "request.context.snapshot.invalid");
    }

    [Fact]
    public async Task Oversized_request_objective_returns_bounded_plan_invalid_proof()
    {
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

        var envelope = await runner.RunAsync(new RunRequest(new string('x', 300_000)));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Equal(StopReason.PlanInvalid, envelope.Outcome.StopReason);
        Assert.Equal("Request snapshot rejected.", envelope.Details.Request.Objective);
        Assert.Equal(0, planner.CreatePlanCount);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Contains(
            envelope.Details.ValidationIssues,
            issue => issue.Code == "request.context.snapshot.invalid");
    }

    [Fact]
    public async Task Initial_request_snapshot_failure_is_sticky_for_a_flaky_context()
    {
        var context = new FirstEnumerationThrowingContext();
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
            "Reject the first failed snapshot.",
            Context: context));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Equal("Request snapshot rejected.", envelope.Details.Request.Objective);
        Assert.Equal(1, context.EnumerationCount);
        Assert.Equal(0, planner.CreatePlanCount);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Plan_snapshot_enforces_one_aggregate_budget_across_step_inputs()
    {
        var largeValue = new string('p', 210_000);
        var steps = Enumerable.Range(0, 5)
            .Select(index => new PlanStep(
                $"step_{index}",
                "state.never",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["value"] = largeValue
                }))
            .ToArray();
        var planner = new StaticPlanner(new WorkflowPlan(
            "plan_aggregate_budget",
            1,
            steps,
            "Reject an aggregate-oversized plan."));
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
                MaxSteps: 8,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest("Reject oversized plan proof."));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Equal(StopReason.PlanInvalid, envelope.Outcome.StopReason);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Contains(
            envelope.Details.ValidationIssues,
            issue => issue.Code == "planner.create.failed");
    }

    [Fact]
    public async Task Oversized_planner_exception_message_returns_bounded_plan_invalid_proof()
    {
        var planner = new ThrowingPlanner(
            new InvalidOperationException(new string('e', 1_100_000)));
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

        var envelope = await runner.RunAsync(new RunRequest("Bound planner failure proof."));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        var issue = Assert.Single(
            envelope.Details.ValidationIssues,
            item => item.Code == "planner.create.failed");
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(issue.Message), 1, 8192);
        Assert.EndsWith("\u2026", issue.Message, StringComparison.Ordinal);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Dishonest_plan_step_count_cannot_drive_unbounded_enumeration()
    {
        var steps = new DishonestReadOnlyList<PlanStep>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => Step(
                $"step_{index}",
                "state.never",
                ToolKind.Query,
                ToolEffect.ReadOnly));
        var planner = new StaticPlanner(new WorkflowPlan(
            "plan_dishonest_steps",
            1,
            steps,
            "Reject dishonest enumeration."));
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
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest("Bound dishonest plan enumeration."));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.True(steps.EnumerationCount > steps.Count);
        Assert.InRange(steps.EnumerationCount, 1, 16_385);
    }

    [Fact]
    public async Task Plan_exceeding_remaining_step_budget_is_rejected_before_issue_fan_out()
    {
        var planner = new StaticPlanner(Plan(
            Step("step_1", "state.never", ToolKind.Query, ToolEffect.ReadOnly),
            Step("step_2", "state.never", ToolKind.Query, ToolEffect.ReadOnly),
            Step("step_3", "state.never", ToolKind.Query, ToolEffect.ReadOnly)));
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
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest("Reject over-budget plan."));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        var issue = Assert.Single(envelope.Details.ValidationIssues);
        Assert.Equal("plan.steps.limit", issue.Code);
        Assert.Equal(1, planner.CreatePlanCount);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Required_input_issue_fan_out_is_capped_and_marked_in_returned_proof()
    {
        var fields = Enumerable.Range(0, 2_000)
            .Select(index => new ToolInputField($"required_{index:D4}", Required: true))
            .ToArray();
        var tool = new StatusTool(ReceiptStatus.Succeeded);
        var registration = TestToolRegistration.Create(
            new ToolDescriptor(
                "state.required",
                "Required input fan-out",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                InputSchema: new ToolInputSchema(fields)),
            tool);
        var runner = Runner(
            new StaticPlanner(Plan(Step(
                "step_required",
                "state.required",
                ToolKind.Query,
                ToolEffect.ReadOnly))),
            ToolCatalog.Create(registration),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest("Bound validation proof."));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.InRange(envelope.Details.ValidationIssues.Count, 1, 1_024);
        Assert.Contains(
            envelope.Details.ValidationIssues,
            issue => issue.Code == "plan.validation.truncated");
        Assert.All(envelope.Details.ValidationIssues, issue =>
        {
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(issue.Code), 1, 512);
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(issue.Message), 1, 4 * 1024);
            if (issue.StepId is not null)
            {
                Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(issue.StepId), 1, 4 * 1024);
            }
        });
    }

    [Fact]
    public async Task Dishonest_report_evidence_count_falls_back_with_bounded_enumeration()
    {
        var evidence = new DishonestReadOnlyList<EvidenceRef>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => new EvidenceRef("receipt", $"fake_{index}"));
        var runner = Runner(
            new StaticPlanner(Plan(Step(
                "step_success",
                "state.success",
                ToolKind.Query,
                ToolEffect.ReadOnly))),
            ToolCatalog.Create(Registration(
                "state.success",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new StatusTool(ReceiptStatus.Succeeded))),
            new DishonestEvidenceReporter(evidence),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest("Bound dishonest report proof."));

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Contains("configured outcome reporter failed", envelope.Report.Summary);
        Assert.True(evidence.EnumerationCount > evidence.Count);
        Assert.InRange(evidence.EnumerationCount, 1, 16_385);
    }

    [Fact]
    public async Task Dishonest_completion_blocker_count_fails_closed_with_bounded_enumeration()
    {
        var blockers = new DishonestReadOnlyList<string>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => $"blocker_{index}");
        var tool = new StatusTool(ReceiptStatus.Succeeded);
        var runner = Runner(
            new StaticPlanner(Plan(Step(
                "step_success",
                "state.success",
                ToolKind.Query,
                ToolEffect.ReadOnly))),
            ToolCatalog.Create(Registration(
                "state.success",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                tool)),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly),
            new FixedCompletionEvaluator(new CompletionEvaluation(
                CompletionDecision.Complete,
                StopReason.Complete,
                blockers,
                [])));

        var envelope = await runner.RunAsync(new RunRequest("Bound completion blockers."));

        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        Assert.Equal(StopReason.CompletionEvaluationFailed, envelope.Outcome.StopReason);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.True(blockers.EnumerationCount > blockers.Count);
        Assert.InRange(blockers.EnumerationCount, 1, 16_385);
    }

    [Fact]
    public async Task Dishonest_completion_evidence_count_fails_closed_with_bounded_enumeration()
    {
        var evidence = new DishonestReadOnlyList<EvidenceRef>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => new EvidenceRef("receipt", $"fake_{index}"));
        var tool = new StatusTool(ReceiptStatus.Succeeded);
        var runner = Runner(
            new StaticPlanner(Plan(Step(
                "step_success",
                "state.success",
                ToolKind.Query,
                ToolEffect.ReadOnly))),
            ToolCatalog.Create(Registration(
                "state.success",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                tool)),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly),
            new FixedCompletionEvaluator(new CompletionEvaluation(
                CompletionDecision.Complete,
                StopReason.Complete,
                [],
                evidence)));

        var envelope = await runner.RunAsync(new RunRequest("Bound completion evidence."));

        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        Assert.Equal(StopReason.CompletionEvaluationFailed, envelope.Outcome.StopReason);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.True(evidence.EnumerationCount > evidence.Count);
        Assert.InRange(evidence.EnumerationCount, 1, 16_385);
    }

    [Fact]
    public async Task Completion_snapshot_enforces_one_aggregate_blocker_budget()
    {
        var blockers = Enumerable.Range(0, 5)
            .Select(_ => new string('c', 210_000))
            .ToArray();
        var tool = new StatusTool(ReceiptStatus.Succeeded);
        var runner = Runner(
            new StaticPlanner(Plan(Step(
                "step_success",
                "state.success",
                ToolKind.Query,
                ToolEffect.ReadOnly))),
            ToolCatalog.Create(Registration(
                "state.success",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                tool)),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly),
            new FixedCompletionEvaluator(new CompletionEvaluation(
                CompletionDecision.Complete,
                StopReason.Complete,
                blockers,
                [])));

        var envelope = await runner.RunAsync(new RunRequest("Bound aggregate completion proof."));

        Assert.Equal(RunOutcomeStatus.Failed, envelope.Outcome.Status);
        Assert.Equal(StopReason.CompletionEvaluationFailed, envelope.Outcome.StopReason);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.All(
            envelope.Outcome.Blockers,
            blocker => Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(blocker), 1, 8192));
    }

    [Fact]
    public async Task Dishonest_planning_frame_count_fails_before_planner_with_bounded_enumeration()
    {
        var frames = new DishonestReadOnlyList<PlanningFrame>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => new PlanningFrame(
                $"frame_{index}",
                "hostile.frame",
                "1.0",
                DateTimeOffset.UtcNow,
                new Dictionary<string, object?>(),
                []));
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
                PlanningMode: PlanningMode.PlanOnly),
            planningFrameProjector: new StaticPlanningFrameProjector(frames));

        var envelope = await runner.RunAsync(new RunRequest("Bound projected frames."));

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Equal(0, planner.CreatePlanCount);
        Assert.Equal(0, tool.ExecutionCount);
        Assert.True(frames.EnumerationCount > frames.Count);
        Assert.InRange(frames.EnumerationCount, 1, 16_385);
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

    [Fact]
    public async Task Returned_runtime_envelope_collection_shells_are_read_only()
    {
        var runner = Runner(
            new StaticPlanner(Plan(Step(
                "step_unavailable",
                "state.unavailable",
                ToolKind.Query,
                ToolEffect.ReadOnly))),
            ToolCatalog.Create(Registration(
                "state.unavailable",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new StatusTool(ReceiptStatus.Unavailable))),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 1));

        var envelope = await runner.RunAsync(new RunRequest("Return frozen proof shells."));

        Assert.Single(envelope.PriorAttempts);
        AssertReadOnly(envelope.PriorAttempts, envelope);
        AssertReadOnly(envelope.Outcome.CompletedSteps, "late");
        AssertReadOnly(envelope.Outcome.Blockers, "late");
        AssertReadOnly(envelope.Outcome.CompletionEvidence, new EvidenceRef("receipt", "late"));
        AssertReadOnly(envelope.Report.Claims, envelope.Report.Claims[0]);
        AssertReadOnly(envelope.Receipts.Items, envelope.Receipts.Items[0]);
        AssertReadOnly(envelope.Details.PlanVersions, envelope.Details.PlanVersions[0]);
        AssertReadOnly(envelope.Details.PlanVersions[0].Steps, envelope.Details.PlanVersions[0].Steps[0]);
        AssertReadOnly(envelope.Details.PlanRefinements, new PlanRefinement("a", "b", "late", []));
        AssertReadOnly(envelope.Details.Observations, new Observation(
            "late",
            "late",
            ObservationKind.ToolResult,
            "late",
            new Dictionary<string, object?>(),
            []));
        AssertReadOnly(envelope.Details.Artifacts, new Artifact(
            "late",
            "late",
            new Dictionary<string, object?>(),
            []));
        AssertReadOnly(envelope.Details.Batches, new ExecutionBatch(
            "late",
            ["late"],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow));
        AssertReadOnly(envelope.Details.Events, envelope.Details.Events[0]);
        AssertReadOnly(envelope.Details.ValidationIssues, new ValidationIssue("late", "late"));
        AssertReadOnly(envelope.Details.RunAttempts, envelope.Details.RunAttempts[0]);
        AssertReadOnly(envelope.Details.ToolSurfaces, envelope.Details.ToolSurfaces[0]);
        AssertReadOnly(envelope.Details.PlanningFrames, envelope.Details.PlanningFrames[0]);
        AssertReadOnly(envelope.Details.GrantConsumptions, new ToolGrantConsumption(
            "late",
            "late",
            "late",
            1,
            "late",
            "late",
            $"sha256-v1:{new string('0', 64)}",
            $"sha256-v1:{new string('0', 64)}",
            "late",
            DateTimeOffset.UtcNow.AddMinutes(1),
            Array.AsReadOnly(new[] { ToolDataBoundary.UserContent }),
            Array.AsReadOnly(new[] { ToolExternalOutputClassification.None }),
            DateTimeOffset.UtcNow));
        AssertReadOnly(envelope.Details.Breadcrumbs.Entries, envelope.Details.Breadcrumbs.Entries[0]);
        AssertReadOnly(envelope.Details.Divergences.Entries, envelope.Details.Divergences.Entries[0]);
        AssertReadOnly(envelope.Details.Continuity.RecommendationReasons, "late");
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

    [Fact]
    public void Public_plan_validation_rejects_every_blank_identity()
    {
        var catalog = ToolCatalog.Create(Registration(
            "state.identity",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            new StatusTool(ReceiptStatus.Succeeded)));
        var runner = Runner(
            new StaticPlanner(Plan(Step(
                "step_valid",
                "state.identity",
                ToolKind.Query,
                ToolEffect.ReadOnly))),
            catalog,
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 4, MaxRefinements: 0));
        var validStep = Step(
            "step_valid",
            "state.identity",
            ToolKind.Query,
            ToolEffect.ReadOnly);
        var candidates = new (WorkflowPlan Plan, string Code)[]
        {
            (new WorkflowPlan(" ", 1, [validStep], "Blank plan id."),
                "plan.identifier.required"),
            (new WorkflowPlan(
                    "plan_blank_step",
                    1,
                    [validStep with { StepId = "\t" }],
                    "Blank step id."),
                "plan.step.identifier.required"),
            (new WorkflowPlan(
                    "plan_blank_tool",
                    1,
                    [validStep with { ToolId = " " }],
                    "Blank tool id."),
                "plan.step.tool_identifier.required"),
            (new WorkflowPlan(
                    "plan_blank_dependency",
                    1,
                    [validStep with { DependsOn = [" "] }],
                    "Blank dependency id."),
                "plan.step.dependency.identifier.required"),
            (new WorkflowPlan(
                    "plan_blank_batch",
                    1,
                    [validStep with { BatchId = "\r\n" }],
                    "Blank batch id."),
                "plan.batch.identifier.required")
        };

        foreach (var candidate in candidates)
        {
            Assert.Contains(
                runner.ValidatePlan(candidate.Plan),
                issue => issue.Code == candidate.Code);
        }
    }

    [Fact]
    public void Public_plan_validation_bounds_dishonest_step_enumeration()
    {
        var steps = new DishonestReadOnlyList<PlanStep>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => Step(
                $"step_{index:D5}",
                "state.identity",
                ToolKind.Query,
                ToolEffect.ReadOnly));
        var runner = Runner(
            new StaticPlanner(Plan()),
            ToolCatalog.Create(Registration(
                "state.identity",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                new StatusTool(ReceiptStatus.Succeeded))),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(MaxSteps: 4, MaxRefinements: 0));

        var issues = runner.ValidatePlan(new WorkflowPlan(
            "plan_dishonest_public",
            1,
            steps,
            "Bound public plan input."));

        Assert.Contains(issues, issue => issue.Code == "plan.snapshot.invalid");
        Assert.InRange(steps.EnumerationCount, 1, 16_385);
    }

    [Fact]
    public async Task Manifest_recheck_budget_exhaustion_refuses_before_the_exhausted_tool_call()
    {
        var tool = new StatusTool(ReceiptStatus.Succeeded);
        var largeDescription = new string('d', 220_000);
        var schema = new ToolInputSchema(
            Enumerable.Range(0, 4)
                .Select(index => new ToolInputField(
                    $"field_{index}",
                    Description: largeDescription))
                .ToArray());
        var registration = TestToolRegistration.Create(
            new ToolDescriptor(
                "state.large_manifest",
                "Large manifest",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                InputSchema: schema),
            tool);
        var steps = Enumerable.Range(0, 24)
            .Select(index => Step(
                $"step_{index:D2}",
                "state.large_manifest",
                ToolKind.Query,
                ToolEffect.ReadOnly))
            .ToArray();
        var runner = Runner(
            new StaticPlanner(Plan(steps)),
            ToolCatalog.Create(registration),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 32,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest(
            "Exhaust manifest recheck work without repeating a tool call."));

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.InRange(tool.ExecutionCount, 1, steps.Length - 1);
        Assert.Equal(tool.ExecutionCount + 1, envelope.Receipts.Items.Count);
        Assert.Contains(
            envelope.Receipts.Items,
            receipt => receipt.Status == ReceiptStatus.Refused &&
                       receipt.Message.Contains(
                           "manifest-recheck work budget",
                           StringComparison.Ordinal));
    }

    [Fact]
    public async Task Runner_schema_validation_preserves_exact_noninteger_json_after_plan_snapshot()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            "0.100000000000000006000000000001");
        var tool = new StatusTool(ReceiptStatus.Succeeded);
        var registration = TestToolRegistration.Create(
            new ToolDescriptor(
                "state.exact_number",
                "Exact number",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                InputSchema: ToolInputSchema.Create(new ToolInputField(
                    "value",
                    ToolInputValueType.Number,
                    Required: true,
                    Maximum: 0.1d))),
            tool);
        var plan = Plan(new PlanStep(
            "step_exact_number",
            "state.exact_number",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            new Dictionary<string, object?>
            {
                ["value"] = document.RootElement.Clone()
            }));
        var runner = Runner(
            new StaticPlanner(plan),
            ToolCatalog.Create(registration),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest(
            "Reject an exactly out-of-range planner number."));

        Assert.True(
            envelope.Outcome.Status == RunOutcomeStatus.PlanInvalid,
            $"Expected PlanInvalid, got {envelope.Outcome.Status}/{envelope.Outcome.StopReason}: " +
            string.Join(" | ", envelope.Outcome.Blockers));
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Contains(
            envelope.Details.ValidationIssues,
            issue => issue.Code == "plan.step.input.range");
    }

    [Fact]
    public async Task Runner_integer_schema_accepts_exact_json_integer_beyond_uint64()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            "123456789012345678901234567890");
        var tool = new StatusTool(ReceiptStatus.Succeeded);
        var registration = TestToolRegistration.Create(
            new ToolDescriptor(
                "state.exact_integer",
                "Exact integer",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                InputSchema: ToolInputSchema.Create(new ToolInputField(
                    "value",
                    ToolInputValueType.Integer,
                    Required: true))),
            tool);
        var plan = Plan(new PlanStep(
            "step_exact_integer",
            "state.exact_integer",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            new Dictionary<string, object?>
            {
                ["value"] = document.RootElement.Clone()
            }));
        var runner = Runner(
            new StaticPlanner(plan),
            ToolCatalog.Create(registration),
            new DeterministicOutcomeReporter(),
            new ExecutionPolicy(
                MaxSteps: 2,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(new RunRequest(
            "Accept an exact JSON integer without narrowing it."));

        Assert.True(
            envelope.Outcome.Status == RunOutcomeStatus.Succeeded,
            $"Expected Succeeded, got {envelope.Outcome.Status}/{envelope.Outcome.StopReason}: " +
            string.Join(" | ", envelope.Outcome.Blockers));
        Assert.Equal(1, tool.ExecutionCount);
    }

    private static AgenticaRunner Runner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        IOutcomeReporter reporter,
        ExecutionPolicy policy,
        ICompletionEvaluator? completionEvaluator = null,
        IPlanningFrameProjector? planningFrameProjector = null) =>
        new(
            planner,
            catalog,
            new InMemoryEventSink(),
            reporter,
            policy,
            completionEvaluator ?? PlanExhaustionCompletionEvaluator.Instance,
            planningFrameProjector);

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
        var host = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(context["host"]);
        var settings = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(host["Settings"]);
        Assert.Equal("host-original", settings["mode"]);
        Assert.Equal(
            ["host-original"],
            Assert.IsAssignableFrom<IEnumerable<object?>>(host["Items"]));

        var hostDictionary = Assert.IsAssignableFrom<IDictionary<string, object?>>(host);
        Assert.True(hostDictionary.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => hostDictionary["late"] = "mutation");
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values, T addedValue)
    {
        var list = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(addedValue));
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

    private sealed class ThrowingPlanner(Exception exception) : IWorkflowPlanner
    {
        public int CreatePlanCount { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatePlanCount++;
            return Task.FromException<WorkflowPlan>(exception);
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

    private sealed class DishonestEvidenceReporter(
        IReadOnlyList<EvidenceRef> evidence) : IOutcomeReporter
    {
        public OutcomeReport BuildReport(
            AgenticaRun run,
            RunOutcomeStatus status,
            StopReason stopReason,
            IReadOnlyList<ValidationIssue> validationIssues,
            IReadOnlyList<string> blockers) =>
            new(
                "report_dishonest_evidence",
                "This report must be rejected.",
                [new ReportClaim("Dishonest evidence.", evidence)]);
    }

    private sealed class MutatingPlanningMetadataPlanner(WorkflowPlan plan) : IWorkflowPlanner
    {
        public bool AttemptedSurfaceMutation { get; private set; }

        public bool SurfaceMutationBlocked { get; private set; }

        public bool AttemptedNestedSurfaceMutation { get; private set; }

        public bool NestedSurfaceMutationBlocked { get; private set; }

        public bool AttemptedFrameMutation { get; private set; }

        public bool FrameMutationBlocked { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.ToolSurface?.PolicySummary is IDictionary<string, object?> policySummary)
            {
                AttemptedSurfaceMutation = true;
                try
                {
                    policySummary.Clear();
                }
                catch (NotSupportedException)
                {
                    SurfaceMutationBlocked = true;
                }

                if (policySummary.TryGetValue("allowedEffects", out var effects) &&
                    effects is IList<string> mutableEffects)
                {
                    AttemptedNestedSurfaceMutation = true;
                    try
                    {
                        mutableEffects.Add("late-effect");
                    }
                    catch (NotSupportedException)
                    {
                        NestedSurfaceMutationBlocked = true;
                    }
                }
            }

            var frame = request.ContextFrames.Single(item => item.Kind == "agentica.goal_spine");
            if (frame.Payload is IDictionary<string, object?> payload)
            {
                AttemptedFrameMutation = true;
                try
                {
                    payload.Clear();
                }
                catch (NotSupportedException)
                {
                    FrameMutationBlocked = true;
                }
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

    private sealed class DishonestReadOnlyList<T>(
        int reportedCount,
        int yieldedCount,
        Func<int, T> itemFactory) : IReadOnlyList<T>
    {
        public int Count => reportedCount;

        public int EnumerationCount { get; private set; }

        public T this[int index] => itemFactory(index);

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < yieldedCount; index++)
            {
                EnumerationCount++;
                yield return itemFactory(index);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

    private sealed class FirstEnumerationThrowingContext : IReadOnlyDictionary<string, object?>
    {
        private readonly IReadOnlyDictionary<string, object?> _values =
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["safeOnLaterRead"] = true
            };

        public int EnumerationCount { get; private set; }

        public int Count => _values.Count;

        public IEnumerable<string> Keys => _values.Keys;

        public IEnumerable<object?> Values => _values.Values;

        public object? this[string key] => _values[key];

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        public bool TryGetValue(string key, out object? value) =>
            _values.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            EnumerationCount++;
            if (EnumerationCount == 1)
            {
                throw new InvalidOperationException("The first context enumeration failed.");
            }

            return _values.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }

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

    private sealed class FixedCompletionEvaluator(
        CompletionEvaluation evaluation) : ICompletionEvaluator
    {
        public CompletionEvaluation Evaluate(CompletionContext context) => evaluation;
    }

    private sealed class StaticPlanningFrameProjector(
        IReadOnlyList<PlanningFrame> frames) : IPlanningFrameProjector
    {
        public IReadOnlyList<PlanningFrame> Project(PlanningFrameProjectionRequest request) => frames;
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
