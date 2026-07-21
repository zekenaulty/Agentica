using Agentica.Artifacts;
using Agentica.Execution;
using Agentica.Events;
using Agentica.Observations;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class RuntimeHarnessGapTests
{
    [Fact]
    public async Task Tool_input_schema_rejects_missing_required_input_before_execution()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor(
                "maze.move",
                "Maze Move",
                ToolKind.Action,
                ToolEffect.WritesLocalState,
                InputSchema: ToolInputSchema.Create(new ToolInputField(
                    "direction",
                    Required: true,
                    AllowedValues: ["north", "south", "east", "west"]))),
            tool));
        var runner = CreateRunner(new StaticPlanner(Plan(Step("step_001", "maze.move", ToolKind.Action, ToolEffect.WritesLocalState))), catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.input.required");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Tool_input_schema_rejects_invalid_enum_extra_input_and_range_before_execution()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor(
                "maze.sense_objective",
                "Maze Sense Objective",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                InputSchema: new ToolInputSchema(
                [
                    new ToolInputField("objectiveId", Required: true, AllowedValues: ["sun_key", "sun_gate"]),
                    new ToolInputField("radius", ToolInputValueType.Integer, Required: true, Minimum: 1, Maximum: 4)
                ])),
            tool));
        var runner = CreateRunner(new StaticPlanner(Plan(Step(
            "step_001",
            "maze.sense_objective",
            ToolKind.Query,
            ToolEffect.ReadOnly,
            ("objectiveId", "exit"),
            ("radius", 9),
            ("path", "north,east,east")))), catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.input.enum");
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.input.range");
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.input.unknown");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Default_effect_policy_rejects_destructive_tool_effect_before_execution()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor("maze.burn_gate", "Burn Gate", ToolKind.Action, ToolEffect.Destructive),
            tool));
        var runner = CreateRunner(new StaticPlanner(Plan(Step(
            "step_001",
            "maze.burn_gate",
            ToolKind.Action,
            ToolEffect.Destructive))), catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.effect_not_allowed");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Approval_required_tool_is_default_denied_even_when_its_effect_is_allowed()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor(
                "workspace.image.generate",
                "Generate Workspace Image",
                ToolKind.Action,
                ToolEffect.ExternalSideEffect,
                RequiresApproval: true),
            tool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(Step(
                "step_001",
                "workspace.image.generate",
                ToolKind.Action,
                ToolEffect.ExternalSideEffect))),
            catalog,
            policy: new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                EffectPolicy: ToolEffectPolicy.AllowKnown));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "tool.security.grant_required");
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(envelope.Receipts.Items);
    }

    [Fact]
    public async Task Default_effect_policy_rejects_external_side_effect_before_execution()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor(
                "workspace.image.generate",
                "Generate Workspace Image",
                ToolKind.Action,
                ToolEffect.ExternalSideEffect),
            tool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(Step(
                "step_001",
                "workspace.image.generate",
                ToolKind.Action,
                ToolEffect.ExternalSideEffect))),
            catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.effect_not_allowed");
        Assert.Equal(0, tool.ExecutionCount);
        Assert.Empty(envelope.Receipts.Items);
    }

    [Fact]
    public void Tool_manifest_compilation_rejects_unknown_tool_effect_before_planning()
    {
        var tool = new CountingTool();
        var exception = Assert.Throws<ArgumentException>(() => ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor("maze.unknown", "Maze Unknown", ToolKind.Action, ToolEffect.Unknown),
            tool)));

        Assert.Contains("Effect cannot be Unknown", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Completion_evidence_gate_blocks_plan_exhaustion_without_required_artifact()
    {
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor("maze.move", "Maze Move", ToolKind.Action, ToolEffect.WritesLocalState),
            new CountingTool()));
        var runner = CreateRunner(
            new StaticPlanner(Plan(Step("step_001", "maze.move", ToolKind.Action, ToolEffect.WritesLocalState))),
            catalog,
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("maze.objective_completed", continueWhenMissing: false));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(StopReason.CompletionNotSatisfied, envelope.Outcome.StopReason);
        Assert.DoesNotContain("succeeded", envelope.Report.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Single(envelope.Receipts.Items);
    }

    [Fact]
    public async Task Plan_exhaustion_can_request_bounded_continuation_until_completion_evidence_exists()
    {
        var planner = new ContinuationPlanner();
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(
                new ToolDescriptor("maze.scan", "Maze Scan", ToolKind.Action, ToolEffect.WritesLocalState),
                new ObservationTool()),
            TestToolRegistration.Create(
                new ToolDescriptor("maze.complete_objective", "Maze Complete Objective", ToolKind.Action, ToolEffect.WritesLocalState),
                new ArtifactTool("maze.objective_completed")));
        var runner = CreateRunner(
            planner,
            catalog,
            policy: new ExecutionPolicy(
                MaxSteps: 5,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxPlanContinuations: 1,
                PlanningContext: new PlanningContextOptions(MaxRecentObservations: 1, MaxRecentReceipts: 1)),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("maze.objective_completed"));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(2, envelope.Details.PlanVersions.Count);
        Assert.Equal(2, planner.CreatePlanCount);
        Assert.Equal(1, planner.SecondRequestObservationCount);
        Assert.Equal(1, planner.SecondRequestReceiptCount);
        Assert.Contains(envelope.Details.Artifacts, artifact => artifact.Kind == "maze.objective_completed");
    }

    [Fact]
    public async Task Blocked_run_retries_with_blocked_context_and_can_succeed()
    {
        var planner = new RetryAwarePlanner();
        var blockedTool = new CountingBlockedTool("Initial blocker.");
        var completeTool = new ArtifactTool("maze.objective_completed");
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(
                new ToolDescriptor("maze.probe", "Maze Probe", ToolKind.Query, ToolEffect.ReadOnly),
                blockedTool),
            TestToolRegistration.Create(
                new ToolDescriptor("maze.complete_objective", "Maze Complete Objective", ToolKind.Action, ToolEffect.WritesLocalState),
                completeTool));
        var runner = CreateRunner(
            planner,
            catalog,
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("maze.objective_completed", continueWhenMissing: false));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(2, planner.CreatePlanCount);
        Assert.Equal(1, blockedTool.ExecutionCount);
        Assert.Equal(2, envelope.Details.RunAttempts.Count);
        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Details.RunAttempts[0].Status);
        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Details.RunAttempts[1].Status);
        Assert.Equal(RequestOrigin.Agent, envelope.Details.Request.Origin);

        var retryContext = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(planner.LastRetryContext);
        Assert.Equal(2, retryContext["attemptNumber"]);
        Assert.Equal(2, retryContext["maxBlockedRetries"]);

        var previousAttempt = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(retryContext["previousAttempt"]);
        Assert.Equal(nameof(RunOutcomeStatus.Blocked), previousAttempt["status"]);
        Assert.Equal(nameof(StopReason.ToolUnavailable), previousAttempt["stopReason"]);
        Assert.Contains("Initial blocker.", Assert.IsAssignableFrom<IReadOnlyList<string>>(previousAttempt["blockers"]));
    }

    [Fact]
    public async Task Blocked_retry_context_uses_the_latest_blocker_when_retry_blocks_again()
    {
        var planner = new AlwaysProbePlanner();
        var blockedTool = new SequenceBlockedTool("First blocker.", "Second blocker.", "Third blocker.");
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor("maze.probe", "Maze Probe", ToolKind.Query, ToolEffect.ReadOnly),
            blockedTool));
        var runner = CreateRunner(planner, catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Blocked, envelope.Outcome.Status);
        Assert.Equal(3, planner.CreatePlanCount);
        Assert.Equal(3, envelope.Details.RunAttempts.Count);

        var finalRetryContext = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(planner.RetryContexts[^1]);
        var previousAttempt = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(finalRetryContext["previousAttempt"]);
        var blockers = Assert.IsAssignableFrom<IReadOnlyList<string>>(previousAttempt["blockers"]);

        Assert.Contains("Second blocker.", blockers);
        Assert.DoesNotContain("First blocker.", blockers);
    }

    [Fact]
    public async Task Readonly_batch_steps_execute_in_parallel_and_record_batch_receipts()
    {
        var gate = new BatchGate(expectedCount: 2);
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(
                new ToolDescriptor("workbench.check_a", "Check A", ToolKind.Query, ToolEffect.ReadOnly),
                new GateTool(gate)),
            TestToolRegistration.Create(
                new ToolDescriptor("workbench.check_b", "Check B", ToolKind.Query, ToolEffect.ReadOnly),
                new GateTool(gate)));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_a", "workbench.check_a", ToolKind.Query, ToolEffect.ReadOnly) with { BatchId = "checks" },
                Step("step_b", "workbench.check_b", ToolKind.Query, ToolEffect.ReadOnly) with { BatchId = "checks" })),
            catalog,
            policy: new ExecutionPolicy(MaxSteps: 4, MaxRefinements: 0, PlanningMode: PlanningMode.PlanOnly));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(["step_a", "step_b"], envelope.Outcome.CompletedSteps);
        Assert.Equal(2, envelope.Receipts.Items.Count);
        var batch = Assert.Single(envelope.Details.Batches);
        Assert.Equal("checks", batch.BatchId);
        Assert.Equal(["step_a", "step_b"], batch.StepIds);
        Assert.Contains(envelope.Details.Events, item => item.Type == "batch.started");
        Assert.Contains(envelope.Details.Events, item => item.Type == "batch.completed");
    }

    [Fact]
    public async Task Plan_validation_rejects_unknown_and_forward_dependencies()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor("workbench.read", "Workbench Read", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_001", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly) with { DependsOn = ["missing_step"] },
                Step("step_002", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly) with { DependsOn = ["step_003"] },
                Step("step_003", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly))),
            catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.dependency.unknown");
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.dependency.order");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Plan_validation_rejects_mutation_steps_inside_batches()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor("workbench.patch", "Workbench Patch", ToolKind.Action, ToolEffect.WritesLocalState),
            tool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_patch", "workbench.patch", ToolKind.Action, ToolEffect.WritesLocalState) with { BatchId = "patches" })),
            catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.batch.readonly_only");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Plan_validation_rejects_batches_that_exceed_parallelism()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor("workbench.read", "Workbench Read", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_a", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly) with { BatchId = "reads" },
                Step("step_b", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly) with { BatchId = "reads" })),
            catalog,
            policy: new ExecutionPolicy(MaxSteps: 4, MaxParallelism: 1));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.batch.parallelism");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Plan_validation_rejects_batch_internal_dependencies()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor("workbench.read", "Workbench Read", ToolKind.Query, ToolEffect.ReadOnly),
            tool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_a", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly) with { BatchId = "reads" },
                Step("step_b", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly) with
                {
                    BatchId = "reads",
                    DependsOn = ["step_a"]
                })),
            catalog);

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.batch.internal_dependency");
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task Tool_cooldown_refuses_repeated_tool_before_plan_step_cooldown_expires()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor(
                "workbench.read",
                "Workbench Read",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                Cooldown: new ToolCooldownPolicy(PlanStepCount: 1)),
            tool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_a", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly),
                Step("step_b", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly),
                Step("step_c", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly))),
            catalog,
            policy: new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 0));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(
            [ReceiptStatus.Succeeded, ReceiptStatus.Refused],
            envelope.Receipts.Items.Select(receipt => receipt.Status).ToArray());
        var cooldownReceipt = envelope.Receipts.Items[1];
        Assert.True(cooldownReceipt.Data.ContainsKey("cooldown"));
        Assert.Contains(envelope.Details.Events, executionEvent =>
            executionEvent.Type == "receipt.emitted" &&
            executionEvent.Diagnostics?.Code == "tool.cooldown.active");
    }

    [Fact]
    public async Task Tool_cooldown_allows_repeated_tool_after_plan_step_cooldown_expires()
    {
        var cooledTool = new CountingTool();
        var otherTool = new CountingTool();
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(
                new ToolDescriptor(
                    "workbench.read",
                    "Workbench Read",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    Cooldown: new ToolCooldownPolicy(PlanStepCount: 1)),
                cooledTool),
            TestToolRegistration.Create(
                new ToolDescriptor("workbench.other", "Workbench Other", ToolKind.Query, ToolEffect.ReadOnly),
                otherTool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_a", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly),
                Step("step_b", "workbench.other", ToolKind.Query, ToolEffect.ReadOnly),
                Step("step_c", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly))),
            catalog,
            policy: new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 0));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(2, cooledTool.ExecutionCount);
        Assert.Equal(1, otherTool.ExecutionCount);
        Assert.All(envelope.Receipts.Items, receipt => Assert.Equal(ReceiptStatus.Succeeded, receipt.Status));
    }

    [Fact]
    public async Task Tool_cooldown_scope_can_include_selected_input_keys()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor(
                "workbench.lookup",
                "Workbench Lookup",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                Cooldown: new ToolCooldownPolicy(
                    PlanStepCount: 5,
                    ScopeInputKeys: ["query"])),
            tool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_a", "workbench.lookup", ToolKind.Query, ToolEffect.ReadOnly, ("query", "alpha")),
                Step("step_b", "workbench.lookup", ToolKind.Query, ToolEffect.ReadOnly, ("query", "beta")),
                Step("step_c", "workbench.lookup", ToolKind.Query, ToolEffect.ReadOnly, ("query", "alpha")))),
            catalog,
            policy: new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 0));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(2, tool.ExecutionCount);
        Assert.Equal(
            [ReceiptStatus.Succeeded, ReceiptStatus.Succeeded, ReceiptStatus.Refused],
            envelope.Receipts.Items.Select(receipt => receipt.Status).ToArray());
    }

    [Fact]
    public async Task Tool_cooldown_can_reset_after_successful_mutation()
    {
        var queryTool = new CountingTool();
        var actionTool = new CountingTool();
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(
                new ToolDescriptor(
                    "workbench.read",
                    "Workbench Read",
                    ToolKind.Query,
                    ToolEffect.ReadOnly,
                    Cooldown: new ToolCooldownPolicy(
                        PlanStepCount: 5,
                        ResetOnMutation: true)),
                queryTool),
            TestToolRegistration.Create(
                new ToolDescriptor("workbench.write", "Workbench Write", ToolKind.Action, ToolEffect.WritesLocalState),
                actionTool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_read_a", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly),
                Step("step_write", "workbench.write", ToolKind.Action, ToolEffect.WritesLocalState),
                Step("step_read_b", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly))),
            catalog,
            policy: new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 0));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(2, queryTool.ExecutionCount);
        Assert.Equal(1, actionTool.ExecutionCount);
        Assert.All(envelope.Receipts.Items, receipt => Assert.NotEqual(ReceiptStatus.Refused, receipt.Status));
    }

    [Fact]
    public async Task Tool_cooldown_refuses_duplicate_scope_inside_parallel_batch()
    {
        var tool = new CountingTool();
        var catalog = ToolCatalog.Create(TestToolRegistration.Create(
            new ToolDescriptor(
                "workbench.read",
                "Workbench Read",
                ToolKind.Query,
                ToolEffect.ReadOnly,
                Cooldown: new ToolCooldownPolicy(
                    PlanStepCount: 5,
                    ScopeInputKeys: ["topic"])),
            tool));
        var runner = CreateRunner(
            new StaticPlanner(Plan(
                Step("step_a", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly, ("topic", "maze")) with { BatchId = "reads" },
                Step("step_b", "workbench.read", ToolKind.Query, ToolEffect.ReadOnly, ("topic", "maze")) with { BatchId = "reads" })),
            catalog,
            policy: new ExecutionPolicy(
                MaxSteps: 4,
                MaxRefinements: 0,
                PlanningMode: PlanningMode.PlanOnly,
                MaxBlockedRetries: 0));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(1, tool.ExecutionCount);
        Assert.Equal(
            [ReceiptStatus.Succeeded, ReceiptStatus.Refused],
            envelope.Receipts.Items.Select(receipt => receipt.Status).ToArray());
    }

    [Fact]
    public async Task Refined_plan_can_depend_on_a_completed_prior_plan_step()
    {
        var planner = new DependencyRefinementPlanner(_ => Plan(
            Step("step_002", "hexquest.commit_patch", ToolKind.Action, ToolEffect.WritesLocalState) with
            {
                DependsOn = ["step_001"]
            }));
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(
                new ToolDescriptor("hexquest.validate_patch", "HexQuest Validate Patch", ToolKind.Query, ToolEffect.ReadOnly),
                new ObservationTool()),
            TestToolRegistration.Create(
                new ToolDescriptor("hexquest.commit_patch", "HexQuest Commit Patch", ToolKind.Action, ToolEffect.WritesLocalState),
                new ArtifactTool("hexquest.objective_completed")));
        var runner = CreateRunner(
            planner,
            catalog,
            policy: new ExecutionPolicy(MaxSteps: 4, MaxRefinements: 1, PlanningMode: PlanningMode.Stepwise),
            completionEvaluator: EvidenceCompletionEvaluator.ForArtifactKind("hexquest.objective_completed"));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.Succeeded, envelope.Outcome.Status);
        Assert.Equal(["step_001", "step_002"], envelope.Outcome.CompletedSteps);
        Assert.Equal(["step_001"], planner.RefinementCompletedStepIds);
        Assert.Equal(["step_001"], envelope.Details.PlanVersions[1].Steps[0].DependsOn);
    }

    [Fact]
    public async Task Refined_plan_rejects_dependency_that_is_not_in_current_plan_or_completed_steps()
    {
        var commitTool = new CountingTool();
        var planner = new DependencyRefinementPlanner(_ => Plan(
            Step("step_002", "hexquest.commit_patch", ToolKind.Action, ToolEffect.WritesLocalState) with
            {
                DependsOn = ["step_missing"]
            }));
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(
                new ToolDescriptor("hexquest.validate_patch", "HexQuest Validate Patch", ToolKind.Query, ToolEffect.ReadOnly),
                new ObservationTool()),
            TestToolRegistration.Create(
                new ToolDescriptor("hexquest.commit_patch", "HexQuest Commit Patch", ToolKind.Action, ToolEffect.WritesLocalState),
                commitTool));
        var runner = CreateRunner(
            planner,
            catalog,
            policy: new ExecutionPolicy(MaxSteps: 4, MaxRefinements: 1, PlanningMode: PlanningMode.Stepwise));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.dependency.unknown");
        Assert.Equal(0, commitTool.ExecutionCount);
    }

    [Fact]
    public async Task Refined_plan_rejects_reusing_a_completed_step_id()
    {
        var commitTool = new CountingTool();
        var planner = new DependencyRefinementPlanner(_ => Plan(
            Step("step_001", "hexquest.commit_patch", ToolKind.Action, ToolEffect.WritesLocalState)));
        var catalog = ToolCatalog.Create(
            TestToolRegistration.Create(
                new ToolDescriptor("hexquest.validate_patch", "HexQuest Validate Patch", ToolKind.Query, ToolEffect.ReadOnly),
                new ObservationTool()),
            TestToolRegistration.Create(
                new ToolDescriptor("hexquest.commit_patch", "HexQuest Commit Patch", ToolKind.Action, ToolEffect.WritesLocalState),
                commitTool));
        var runner = CreateRunner(
            planner,
            catalog,
            policy: new ExecutionPolicy(MaxSteps: 4, MaxRefinements: 1, PlanningMode: PlanningMode.Stepwise));

        var envelope = await runner.RunAsync(Request());

        Assert.Equal(RunOutcomeStatus.PlanInvalid, envelope.Outcome.Status);
        Assert.Contains(envelope.Details.ValidationIssues, issue => issue.Code == "plan.step.reused_completed_id");
        Assert.Equal(0, commitTool.ExecutionCount);
    }

    private static RunRequest Request() =>
        new("Navigate the host-provided test surface.", RequestOrigin.User);

    private static AgenticaRunner CreateRunner(
        IWorkflowPlanner planner,
        ToolCatalog catalog,
        ExecutionPolicy? policy = null,
        ICompletionEvaluator? completionEvaluator = null) =>
        new(
            planner,
            catalog,
            new InMemoryEventSink(),
            new DeterministicOutcomeReporter(),
            policy ?? new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 2),
            completionEvaluator ?? PlanExhaustionCompletionEvaluator.Instance);

    private static WorkflowPlan Plan(params PlanStep[] steps) =>
        new($"plan_{Guid.NewGuid():N}", 1, steps, "Runtime harness gap test plan.");

    private static PlanStep Step(
        string stepId,
        string toolId,
        ToolKind kind,
        ToolEffect effect,
        params (string Key, object? Value)[] input) =>
        new(
            stepId,
            toolId,
            kind,
            effect,
            input.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

    private sealed class StaticPlanner : IWorkflowPlanner
    {
        private readonly WorkflowPlan _plan;

        public StaticPlanner(WorkflowPlan plan)
        {
            _plan = plan;
        }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_plan);

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class ContinuationPlanner : IWorkflowPlanner
    {
        public int CreatePlanCount { get; private set; }

        public int SecondRequestObservationCount { get; private set; }

        public int SecondRequestReceiptCount { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatePlanCount++;
            if (CreatePlanCount == 1)
            {
                return Task.FromResult(Plan(
                    Step("step_001", "maze.scan", ToolKind.Action, ToolEffect.WritesLocalState),
                    Step("step_002", "maze.scan", ToolKind.Action, ToolEffect.WritesLocalState)));
            }

            SecondRequestObservationCount = request.Observations.Count;
            SecondRequestReceiptCount = request.Receipts.Count;
            return Task.FromResult(Plan(Step(
                "step_003",
                "maze.complete_objective",
                ToolKind.Action,
                ToolEffect.WritesLocalState)));
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class RetryAwarePlanner : IWorkflowPlanner
    {
        public int CreatePlanCount { get; private set; }

        public object? LastRetryContext { get; private set; }

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatePlanCount++;
            if (request.Request.Context?.TryGetValue("agentica.retry", out var retryContext) == true)
            {
                LastRetryContext = retryContext;
                return Task.FromResult(Plan(Step(
                    "step_retry_complete",
                    "maze.complete_objective",
                    ToolKind.Action,
                    ToolEffect.WritesLocalState)));
            }

            return Task.FromResult(Plan(Step(
                "step_initial_probe",
                "maze.probe",
                ToolKind.Query,
                ToolEffect.ReadOnly)));
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class AlwaysProbePlanner : IWorkflowPlanner
    {
        public int CreatePlanCount { get; private set; }

        public List<object?> RetryContexts { get; } = [];

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreatePlanCount++;
            if (request.Request.Context?.TryGetValue("agentica.retry", out var retryContext) == true)
            {
                RetryContexts.Add(retryContext);
            }

            return Task.FromResult(Plan(Step(
                $"step_probe_{CreatePlanCount}",
                "maze.probe",
                ToolKind.Query,
                ToolEffect.ReadOnly)));
        }

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement should not run.");
    }

    private sealed class DependencyRefinementPlanner : IWorkflowPlanner
    {
        private readonly Func<PlanningRequest, WorkflowPlan> _refinementFactory;

        public DependencyRefinementPlanner(Func<PlanningRequest, WorkflowPlan> refinementFactory)
        {
            _refinementFactory = refinementFactory;
        }

        public IReadOnlyList<string> RefinementCompletedStepIds { get; private set; } = [];

        public Task<WorkflowPlan> CreatePlanAsync(
            PlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Plan(Step("step_001", "hexquest.validate_patch", ToolKind.Query, ToolEffect.ReadOnly)));

        public Task<WorkflowPlan> RefinePlanAsync(
            PlanningRequest request,
            Observation observation,
            CancellationToken cancellationToken = default)
        {
            RefinementCompletedStepIds = request.ExecutionContext.CompletedStepIds;
            return Task.FromResult(_refinementFactory(request));
        }
    }

    private sealed class CountingTool : ITool
    {
        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(Receipt(invocation, ReceiptStatus.Succeeded, "Tool executed.")));
        }
    }

    private sealed class ObservationTool : ITool
    {
        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Observation tool executed.");
            return Task.FromResult(new ToolResult(
                receipt,
                new Observation(
                    AgenticaIds.New("observation"),
                    invocation.StepId,
                    ObservationKind.ToolResult,
                    "Observed host state.",
                    new Dictionary<string, object?>
                    {
                        ["step"] = invocation.StepId
                    },
                    [new EvidenceRef("receipt", receipt.ReceiptId)])));
        }
    }

    private sealed class ArtifactTool : ITool
    {
        private readonly string _artifactKind;

        public ArtifactTool(string artifactKind)
        {
            _artifactKind = artifactKind;
        }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var receipt = Receipt(invocation, ReceiptStatus.Succeeded, "Objective completed.");
            return Task.FromResult(new ToolResult(
                receipt,
                Artifact: new Artifact(
                    AgenticaIds.New("artifact"),
                    _artifactKind,
                    new Dictionary<string, object?>
                    {
                        ["completed"] = true
                    },
                    [new EvidenceRef("receipt", receipt.ReceiptId)])));
        }
    }

    private sealed class CountingBlockedTool : ITool
    {
        private readonly string _message;

        public CountingBlockedTool(string message)
        {
            _message = message;
        }

        public int ExecutionCount { get; private set; }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(new ToolResult(Receipt(invocation, ReceiptStatus.Unavailable, _message)));
        }
    }

    private sealed class SequenceBlockedTool : ITool
    {
        private readonly Queue<string> _messages;

        public SequenceBlockedTool(params string[] messages)
        {
            _messages = new Queue<string>(messages);
        }

        public Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var message = _messages.Count == 0 ? "Still blocked." : _messages.Dequeue();
            return Task.FromResult(new ToolResult(Receipt(invocation, ReceiptStatus.Unavailable, message)));
        }
    }

    private static Receipt Receipt(ToolInvocation invocation, ReceiptStatus status, string message) =>
        new(
            AgenticaIds.New("receipt"),
            invocation.StepId,
            invocation.ToolId,
            status,
            message,
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());

    private sealed class BatchGate
    {
        private readonly int _expectedCount;
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enteredCount;

        public BatchGate(int expectedCount)
        {
            _expectedCount = expectedCount;
        }

        public async Task<bool> EnterAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _enteredCount) >= _expectedCount)
            {
                _released.TrySetResult();
            }

            try
            {
                await _released.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }
    }

    private sealed class GateTool : ITool
    {
        private readonly BatchGate _gate;

        public GateTool(BatchGate gate)
        {
            _gate = gate;
        }

        public async Task<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
        {
            var parallel = await _gate.EnterAsync(cancellationToken).ConfigureAwait(false);
            return new ToolResult(Receipt(
                invocation,
                parallel ? ReceiptStatus.Succeeded : ReceiptStatus.Failed,
                parallel ? "Batch peer observed." : "Batch peer was not observed before timeout."));
        }
    }
}
