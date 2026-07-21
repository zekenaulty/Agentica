using Agentica.Artifacts;
using Agentica.Observations;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Execution;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;
using System.Text.Json;

namespace Agentica.Orchestration.Tests;

public sealed class TaskOrchestratorTests
{
    [Fact]
    public async Task Orchestrator_executes_single_node_pass_through_graph()
    {
        var task = Task("direct", "Do the direct task.");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor([Envelope("run_direct", RunOutcomeStatus.Succeeded)]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(TaskAcceptanceStatus.Accepted, [], [new EvidenceRef("artifact", "artifact_run_direct")]));
        var orchestrator = CreateOrchestrator(planner, executor, evaluator);

        var outcome = await orchestrator.RunAsync(Request("Do a small thing."));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Complete, outcome.StopReason);
        Assert.Equal(["direct"], outcome.State.CompletedTaskIds);
        Assert.Single(executor.Requests);
        Assert.Equal("Do the direct task.", executor.Requests[0].Objective);
        Assert.NotNull(executor.Requests[0].Context);
        Assert.True(executor.Requests[0].Context!.ContainsKey("orchestration.workingContext"));
    }

    [Fact]
    public void Graph_validator_rejects_cycles_and_dangling_dependencies()
    {
        var cyclic = Plan(
        [
            Task("a", dependsOn: ["b"]),
            Task("b", dependsOn: ["a"])
        ]);
        var dangling = Plan([Task("a", dependsOn: ["missing"])]);

        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(cyclic));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(dangling));
    }

    [Fact]
    public void Graph_validator_requires_nonempty_semantically_valid_acceptance_and_definition_of_done()
    {
        var emptyAcceptance = Plan([Task("empty") with { AcceptanceRequirements = [] }]);
        var nullOutcomeStatus = Plan(
        [
            Task("invalid") with
            {
                AcceptanceRequirements =
                [
                    new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus)
                ]
            }
        ]);
        var emptyDefinitionOfDone = Plan([Task("valid")]) with { DefinitionOfDone = [] };

        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(emptyAcceptance));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(nullOutcomeStatus));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(emptyDefinitionOfDone));
    }

    [Fact]
    public async Task Failed_child_with_empty_acceptance_is_never_accepted()
    {
        var task = Task("failed") with { AcceptanceRequirements = [] };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));

        var result = await new EvidenceTaskAcceptanceEvaluator().EvaluateAsync(
            task,
            Envelope("run_failed", RunOutcomeStatus.Failed),
            new TaskAcceptanceContext(Plan([Task("declared")]), state, state.WorkingContext, new Dictionary<string, object?>()));

        Assert.NotEqual(TaskAcceptanceStatus.Accepted, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("at least one requirement", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Orchestrator_enforces_declared_acceptance_against_a_permissive_custom_evaluator()
    {
        var task = Task("failed");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor([Envelope("run_failed", RunOutcomeStatus.Failed)]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.Accepted,
                [],
                [new EvidenceRef("artifact", "artifact_run_failed")]));

        var outcome = await CreateOrchestrator(planner, executor, evaluator)
            .RunAsync(Request("Do not accept a failed child."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.ChildRunFailed, outcome.StopReason);
        Assert.Empty(outcome.State.CompletedTaskIds);
        Assert.Single(outcome.RunOutcomes);
    }

    [Fact]
    public async Task Orchestrator_rejects_unresolved_acceptance_evidence()
    {
        var task = Task("forged");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor([Envelope("run_forged", RunOutcomeStatus.Succeeded)]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.Accepted,
                [],
                [new EvidenceRef("artifact", "artifact_that_does_not_exist")]));

        var outcome = await CreateOrchestrator(planner, executor, evaluator)
            .RunAsync(Request("Reject forged proof."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Empty(outcome.State.CompletedTaskIds);
        Assert.Contains(outcome.WorkingContext.KnownBlockers, reason =>
            reason.Contains("does not resolve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Child_claimed_completion_and_nested_evidence_do_not_make_forged_refs_self_resolving()
    {
        var task = Task("forged_edges");
        var child = Envelope("run_forged_edges", RunOutcomeStatus.Succeeded);
        var completionForgery = new EvidenceRef("artifact", "forged_completion_artifact");
        var nestedForgery = new EvidenceRef("receipt", "forged_nested_receipt");
        child = child with
        {
            Outcome = child.Outcome with { CompletionEvidence = [completionForgery] },
            Details = child.Details with
            {
                Artifacts =
                [
                    child.Details.Artifacts[0] with { Evidence = [nestedForgery] }
                ]
            }
        };
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.Accepted,
                [],
                [completionForgery, nestedForgery]));
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(Plan([task])),
            new ScriptedRunExecutor([child]),
            evaluator);

        var outcome = await orchestrator.RunAsync(Request("Reject self-attested proof edges."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Empty(outcome.State.CompletedTaskIds);
        Assert.Contains(outcome.WorkingContext.KnownBlockers, reason =>
            reason.Contains("forged_completion_artifact", StringComparison.Ordinal));
        Assert.Contains(outcome.WorkingContext.KnownBlockers, reason =>
            reason.Contains("forged_nested_receipt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Orchestrator_blocks_when_required_tasks_complete_but_definition_of_done_is_unmet()
    {
        var task = Task("work");
        var plan = Plan([task]) with
        {
            DefinitionOfDone =
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "missing.proof")
            ]
        };
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ScriptedRunExecutor([Envelope("run_work", RunOutcomeStatus.Succeeded)]),
            new EvidenceTaskAcceptanceEvaluator());

        var outcome = await orchestrator.RunAsync(Request("Require global proof."));

        Assert.Equal(OrchestrationStatus.Blocked, outcome.Status);
        Assert.Equal(OrchestrationStopReason.DefinitionOfDoneNotSatisfied, outcome.StopReason);
        Assert.NotNull(outcome.DefinitionOfDone);
        Assert.False(outcome.DefinitionOfDone.Satisfied);
        Assert.Contains(outcome.DefinitionOfDone.Reasons, reason => reason.Contains("missing.proof", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Orchestrator_succeeds_only_with_resolved_definition_of_done_evidence()
    {
        var task = Task("work");
        var plan = Plan([task]) with
        {
            DefinitionOfDone =
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "test.artifact")
            ]
        };
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ScriptedRunExecutor([Envelope("run_work", RunOutcomeStatus.Succeeded)]),
            new EvidenceTaskAcceptanceEvaluator());

        var outcome = await orchestrator.RunAsync(Request("Require global proof."));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.True(outcome.DefinitionOfDone?.Satisfied);
        Assert.Contains(outcome.DefinitionOfDone!.EvidenceRefs, evidence =>
            evidence == new EvidenceRef("artifact", "artifact_run_work"));
        Assert.Contains(outcome.EvidenceRefs, evidence =>
            evidence == new EvidenceRef("artifact", "artifact_run_work"));
    }

    [Fact]
    public async Task All_optional_graph_does_not_succeed_without_running_a_task_that_satisfies_definition_of_done()
    {
        var optional = Task("optional") with { Optional = true };
        var executor = new ScriptedRunExecutor([Envelope("run_optional", RunOutcomeStatus.Succeeded)]);
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(Plan([optional])),
            executor,
            new EvidenceTaskAcceptanceEvaluator());

        var outcome = await orchestrator.RunAsync(Request("Avoid vacuous completion."));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Single(executor.Requests);
        Assert.Equal(["optional"], outcome.State.CompletedTaskIds);
        Assert.True(outcome.DefinitionOfDone?.Satisfied);
    }

    [Fact]
    public async Task Revised_definition_of_done_controls_the_final_completion_decision()
    {
        var task = Task("work");
        var initialPlan = Plan([task]) with
        {
            DefinitionOfDone =
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "missing.proof")
            ]
        };
        var refinement = new TaskGraphRefinement(
            "replace_unreachable_global_proof",
            [
                new TaskGraphMutation(
                    TaskGraphMutationKind.ReviseDefinitionOfDone,
                    initialPlan.PlanId,
                    DefinitionOfDone:
                    [
                        new TaskAcceptanceRequirement(
                            TaskAcceptanceRequirementKind.OutcomeStatus,
                            RunOutcomeStatus.Succeeded)
                    ])
            ],
            [],
            RequiresUserInput: false);
        var planner = new ScriptedTaskPlanner(initialPlan, [refinement]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.Accepted,
                [],
                [new EvidenceRef("artifact", "artifact_run_work")],
                RequiresGraphRefinement: true));
        var orchestrator = CreateOrchestrator(
            planner,
            new ScriptedRunExecutor([Envelope("run_work", RunOutcomeStatus.Succeeded)]),
            evaluator);

        var outcome = await orchestrator.RunAsync(Request("Refine global proof."));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Equal(1, planner.RefineCalls);
        Assert.Equal(
            TaskAcceptanceRequirementKind.OutcomeStatus,
            Assert.Single(outcome.FinalPlan!.DefinitionOfDone).Kind);
        Assert.True(outcome.DefinitionOfDone?.Satisfied);
    }

    [Fact]
    public async Task Orchestrator_refines_graph_when_successful_run_invalidates_plan()
    {
        var inspect = Task("inspect", "Inspect the model.");
        var implement = Task("implement", "Implement persistence.", dependsOn: ["inspect"]);
        var initialPlan = Plan([inspect, implement]);
        var design = Task("design_attempts", "Design execution attempt model.", dependsOn: ["inspect"], priority: 2);
        var revisedImplement = implement with
        {
            DependsOn = ["inspect", "design_attempts"],
            Priority = 3,
            Objective = "Implement persistence after execution attempts are modeled."
        };
        var refinement = new TaskGraphRefinement(
            "Execution attempts must be modeled first.",
            [
                new TaskGraphMutation(TaskGraphMutationKind.AddTask, design.TaskId, Task: design),
                new TaskGraphMutation(TaskGraphMutationKind.ReplaceTask, implement.TaskId, Task: revisedImplement)
            ],
            [],
            RequiresUserInput: false);
        var planner = new ScriptedTaskPlanner(initialPlan, [refinement]);
        var executor = new ScriptedRunExecutor(
        [
            Envelope("run_inspect", RunOutcomeStatus.Succeeded),
            Envelope("run_implement_invalidated", RunOutcomeStatus.Succeeded),
            Envelope("run_design", RunOutcomeStatus.Succeeded),
            Envelope("run_implement", RunOutcomeStatus.Succeeded)
        ]);
        var invalidatedImplement = false;
        var evaluator = new ScriptedAcceptanceEvaluator(task =>
        {
            if (task.TaskId == "implement" && !invalidatedImplement)
            {
                invalidatedImplement = true;
                return new TaskAcceptanceResult(
                    TaskAcceptanceStatus.InvalidatedPlan,
                    ["Persistence schema depends on execution attempt modeling."],
                    [new EvidenceRef("artifact", "artifact_run_implement_invalidated")]);
            }

            return new TaskAcceptanceResult(
                TaskAcceptanceStatus.Accepted,
                [],
                [new EvidenceRef(
                    "artifact",
                    task.TaskId switch
                    {
                        "inspect" => "artifact_run_inspect",
                        "design_attempts" => "artifact_run_design",
                        _ => "artifact_run_implement"
                    })]);
        });
        var orchestrator = CreateOrchestrator(planner, executor, evaluator);

        var outcome = await orchestrator.RunAsync(Request("Build durable run persistence."));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Equal(1, planner.RefineCalls);
        Assert.Equal(["inspect", "design_attempts", "implement"], outcome.State.CompletedTaskIds);
        Assert.Equal(["Inspect the model.", "Implement persistence.", "Design execution attempt model.", "Implement persistence after execution attempts are modeled."],
            executor.Requests.Select(request => request.Objective));
        Assert.Contains(outcome.WorkingContext.PlanImpacts, impact =>
            impact.Kind == PlanImpactKind.NewDependencyDiscovered &&
            impact.Summary.Contains("execution attempt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_blocks_task_after_per_task_max_runs()
    {
        var task = Task("retry", "Attempt bounded work.", maxRuns: 1);
        var followUp = Task("follow_up", "Follow up after retry succeeds.", dependsOn: ["retry"]);
        var refinement = new TaskGraphRefinement(
            "The first attempt produced partial evidence but still needs another run.",
            [new TaskGraphMutation(TaskGraphMutationKind.AddTask, followUp.TaskId, Task: followUp)],
            [],
            RequiresUserInput: false);
        var planner = new ScriptedTaskPlanner(Plan([task]), [refinement]);
        var executor = new ScriptedRunExecutor([Envelope("run_retry_first", RunOutcomeStatus.Succeeded)]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                ["More evidence is needed."],
                [new EvidenceRef("artifact", "artifact_run_retry_first")]));
        var orchestrator = CreateOrchestrator(planner, executor, evaluator);

        var outcome = await orchestrator.RunAsync(Request("Respect per-task run budget."));

        Assert.Equal(OrchestrationStatus.Blocked, outcome.Status);
        Assert.Equal(OrchestrationStopReason.MaxRunsReached, outcome.StopReason);
        Assert.Equal(1, planner.RefineCalls);
        Assert.Single(executor.Requests);
        Assert.Equal(1, outcome.State.TaskRunCounts["retry"]);
        Assert.Contains("retry", outcome.State.BlockedTaskIds);
        Assert.Contains(outcome.WorkingContext.KnownBlockers, blocker =>
            blocker.Contains("maxRuns", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_preserves_child_plan_invalid_when_refinement_budget_is_exhausted()
    {
        var task = Task("phase", "Run a child phase.");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor(
        [
            Envelope("run_phase_invalid", RunOutcomeStatus.PlanInvalid, StopReason.PlanInvalid)
        ]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                ["Child run produced an invalid plan and would need repair."],
                [],
                RequiresGraphRefinement: true));
        var orchestrator = new TaskOrchestrator(
            planner,
            executor,
            evaluator,
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>(),
            new OrchestrationPolicy(MaxRuns: 1, MaxRefinements: 0));

        var outcome = await orchestrator.RunAsync(Request("Preserve child plan-invalid failure."));

        Assert.Equal(OrchestrationStatus.PlanInvalid, outcome.Status);
        Assert.Equal(OrchestrationStopReason.PlanInvalid, outcome.StopReason);
        Assert.NotEqual(OrchestrationStopReason.MaxRefinementsReached, outcome.StopReason);
        Assert.Single(outcome.RunOutcomes);
        Assert.Equal(RunOutcomeStatus.PlanInvalid, outcome.RunOutcomes[0].Outcome.Status);
    }

    [Fact]
    public async Task Orchestrator_preserves_terminal_loss_child_stop_reason()
    {
        var task = Task("phase", "Run a terminal child phase.");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor(
        [
            Envelope("run_terminal_loss", RunOutcomeStatus.Failed, StopReason.TerminalLoss)
        ]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.Rejected,
                ["Child run reached terminal loss."],
                []));
        var orchestrator = CreateOrchestrator(planner, executor, evaluator);

        var outcome = await orchestrator.RunAsync(Request("Preserve terminal loss."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.TerminalLoss, outcome.StopReason);
    }

    [Fact]
    public async Task Orchestrator_preserves_child_planner_unavailable_when_refinement_budget_is_exhausted()
    {
        var task = Task("phase", "Run a child phase.");
        var planner = new ScriptedTaskPlanner(Plan([task]));
        var executor = new ScriptedRunExecutor(
        [
            Envelope("run_planner_unavailable", RunOutcomeStatus.Blocked, StopReason.PlannerUnavailable)
        ]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                ["Child planner was unavailable and would need retry."],
                [],
                RequiresGraphRefinement: true));
        var orchestrator = new TaskOrchestrator(
            planner,
            executor,
            evaluator,
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>(),
            new OrchestrationPolicy(MaxRuns: 1, MaxRefinements: 0));

        var outcome = await orchestrator.RunAsync(Request("Preserve child planner-unavailable failure."));

        Assert.Equal(OrchestrationStatus.Blocked, outcome.Status);
        Assert.Equal(OrchestrationStopReason.PlannerUnavailable, outcome.StopReason);
        Assert.NotEqual(OrchestrationStopReason.MaxRefinementsReached, outcome.StopReason);
    }

    [Fact]
    public void Mutation_applier_supports_the_complete_advertised_set()
    {
        var first = Task("first");
        var second = Task("second", dependsOn: ["first"], priority: 2);
        var added = Task("added", priority: 3);
        var replacement = second with { Objective = "Revised second task." };
        var revisedAcceptance = new TaskAcceptanceRequirement(
            TaskAcceptanceRequirementKind.Artifact,
            ArtifactKind: "test.artifact");
        var revisedDefinitionOfDone = new TaskAcceptanceRequirement(
            TaskAcceptanceRequirementKind.Receipt,
            ToolId: "tool.test");
        var plan = Plan([first, second]);
        var refinement = new TaskGraphRefinement(
            "exercise_supported_mutations",
            [
                new TaskGraphMutation(TaskGraphMutationKind.AddTask, added.TaskId, Task: added),
                new TaskGraphMutation(TaskGraphMutationKind.ReplaceTask, second.TaskId, Task: replacement),
                new TaskGraphMutation(TaskGraphMutationKind.AddDependency, added.TaskId, DependencyTaskId: second.TaskId),
                new TaskGraphMutation(TaskGraphMutationKind.RemoveDependency, added.TaskId, DependencyTaskId: second.TaskId),
                new TaskGraphMutation(TaskGraphMutationKind.ReorderPriority, second.TaskId, Priority: 4),
                new TaskGraphMutation(
                    TaskGraphMutationKind.ReviseAcceptanceCriteria,
                    second.TaskId,
                    AcceptanceRequirements: [revisedAcceptance]),
                new TaskGraphMutation(
                    TaskGraphMutationKind.ReviseDefinitionOfDone,
                    plan.PlanId,
                    DefinitionOfDone: [revisedDefinitionOfDone]),
                new TaskGraphMutation(TaskGraphMutationKind.RemoveTask, added.TaskId)
            ],
            [],
            RequiresUserInput: false);

        var result = TaskGraphMutationApplier.Apply(plan, refinement);
        TaskGraphValidator.Validate(result);

        Assert.Equal(["first", "second"], result.Tasks.Select(task => task.TaskId));
        Assert.Equal("Revised second task.", result.Tasks[1].Objective);
        Assert.Equal(4, result.Tasks[1].Priority);
        Assert.Equal(revisedAcceptance, Assert.Single(result.Tasks[1].AcceptanceRequirements));
        Assert.Equal(revisedDefinitionOfDone, Assert.Single(result.DefinitionOfDone));
    }

    [Fact]
    public void Mutation_applier_rejects_unknown_noop_and_mismatched_mutations_transactionally()
    {
        var first = Task("first");
        var second = Task("second", dependsOn: ["first"], priority: 2);
        var plan = Plan([first, second]);

        Assert.Throws<TaskGraphValidationException>(() => TaskGraphMutationApplier.Apply(
            plan,
            Refinement(new TaskGraphMutation(
                TaskGraphMutationKind.AddTask,
                "declared_id",
                Task: Task("payload_id")))));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphMutationApplier.Apply(
            plan,
            Refinement(new TaskGraphMutation(
                TaskGraphMutationKind.AddDependency,
                second.TaskId,
                DependencyTaskId: first.TaskId))));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphMutationApplier.Apply(
            plan,
            Refinement(new TaskGraphMutation(
                TaskGraphMutationKind.ReorderPriority,
                second.TaskId,
                Priority: second.Priority))));
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphMutationApplier.Apply(
            plan,
            Refinement(new TaskGraphMutation(TaskGraphMutationKind.RemoveTask, "unknown"))));

        var transactional = new TaskGraphRefinement(
            "later_mutation_fails",
            [
                new TaskGraphMutation(TaskGraphMutationKind.AddTask, "added", Task: Task("added")),
                new TaskGraphMutation(TaskGraphMutationKind.RemoveTask, "unknown")
            ],
            [],
            RequiresUserInput: false);
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphMutationApplier.Apply(plan, transactional));
        Assert.Equal(["first", "second"], plan.Tasks.Select(task => task.TaskId));

        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        state.CompletedTaskIds.Add(first.TaskId);
        var removedPendingTask = TaskGraphMutationApplier.Apply(
            plan,
            Refinement(new TaskGraphMutation(TaskGraphMutationKind.RemoveTask, second.TaskId)));
        TaskGraphValidator.Validate(removedPendingTask, state, plan);
        Assert.Equal(["first"], removedPendingTask.Tasks.Select(task => task.TaskId));
    }

    [Fact]
    public async Task Orchestrator_normalizes_initial_planner_failures()
    {
        var unavailable = CreateOrchestrator(
            new ThrowingTaskPlanner(new WorkflowPlannerException(
                WorkflowPlannerFailureKind.Unavailable,
                "task_planner.unavailable",
                "Provider unavailable.")),
            new ScriptedRunExecutor([]),
            new EvidenceTaskAcceptanceEvaluator());
        var invalid = CreateOrchestrator(
            new ThrowingTaskPlanner(new InvalidOperationException("Malformed planner payload.")),
            new ScriptedRunExecutor([]),
            new EvidenceTaskAcceptanceEvaluator());

        var unavailableOutcome = await unavailable.RunAsync(Request("Unavailable planner."));
        var invalidOutcome = await invalid.RunAsync(Request("Invalid planner output."));

        Assert.Equal(OrchestrationStatus.Blocked, unavailableOutcome.Status);
        Assert.Equal(OrchestrationStopReason.PlannerUnavailable, unavailableOutcome.StopReason);
        Assert.Null(unavailableOutcome.FinalPlan);
        Assert.Equal(OrchestrationStatus.PlanInvalid, invalidOutcome.Status);
        Assert.Equal(OrchestrationStopReason.PlanInvalid, invalidOutcome.StopReason);
        Assert.Null(invalidOutcome.FinalPlan);
        Assert.Empty(unavailableOutcome.RunOutcomes);
        Assert.Empty(invalidOutcome.RunOutcomes);
    }

    [Fact]
    public async Task Orchestrator_normalizes_an_initial_invalid_graph_without_starting_a_child_run()
    {
        var invalidPlan = Plan([Task("work")]) with { DefinitionOfDone = [] };
        var executor = new ScriptedRunExecutor([]);
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(invalidPlan),
            executor,
            new EvidenceTaskAcceptanceEvaluator());

        var outcome = await orchestrator.RunAsync(Request("Reject invalid graph."));

        Assert.Equal(OrchestrationStatus.PlanInvalid, outcome.Status);
        Assert.Equal(OrchestrationStopReason.PlanInvalid, outcome.StopReason);
        Assert.Same(invalidPlan, outcome.FinalPlan);
        Assert.Empty(outcome.RunOutcomes);
        Assert.Empty(executor.Requests);
        Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Contains("definition of done", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_normalizes_refinement_failure_and_preserves_child_proof_and_previous_plan()
    {
        var plan = Plan([Task("inspect")]);
        var planner = new ThrowingTaskPlanner(plan, new InvalidOperationException("Malformed refinement."));
        var executor = new ScriptedRunExecutor([Envelope("run_inspect", RunOutcomeStatus.Succeeded)]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                ["More work is required."],
                [new EvidenceRef("artifact", "artifact_run_inspect")],
                RequiresGraphRefinement: true));

        var outcome = await CreateOrchestrator(planner, executor, evaluator)
            .RunAsync(Request("Preserve prior proof."));

        Assert.Equal(OrchestrationStatus.PlanInvalid, outcome.Status);
        Assert.Equal(OrchestrationStopReason.PlanInvalid, outcome.StopReason);
        Assert.Same(plan, outcome.FinalPlan);
        Assert.Single(outcome.RunOutcomes);
        Assert.Equal("run_inspect", outcome.RunOutcomes[0].Outcome.RunId);
        Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Contains("refinement failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_rejects_invalid_mutation_and_preserves_previous_plan()
    {
        var plan = Plan([Task("inspect")]);
        var refinement = Refinement(new TaskGraphMutation(TaskGraphMutationKind.RemoveTask, "unknown"));
        var planner = new ScriptedTaskPlanner(plan, [refinement]);
        var executor = new ScriptedRunExecutor([Envelope("run_inspect", RunOutcomeStatus.Succeeded)]);
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                ["Refine."],
                [new EvidenceRef("artifact", "artifact_run_inspect")],
                RequiresGraphRefinement: true));

        var outcome = await CreateOrchestrator(planner, executor, evaluator)
            .RunAsync(Request("Reject invalid mutation."));

        Assert.Equal(OrchestrationStatus.PlanInvalid, outcome.Status);
        Assert.Same(plan, outcome.FinalPlan);
        Assert.Single(outcome.RunOutcomes);
        Assert.Equal(["inspect"], outcome.FinalPlan!.Tasks.Select(task => task.TaskId));
    }

    [Fact]
    public async Task Orchestrator_normalizes_cancellation()
    {
        var orchestrator = CreateOrchestrator(
            new ThrowingTaskPlanner(new OperationCanceledException("Cancelled by test.")),
            new ScriptedRunExecutor([]),
            new EvidenceTaskAcceptanceEvaluator());

        var outcome = await orchestrator.RunAsync(Request("Cancel safely."));

        Assert.Equal(OrchestrationStatus.Cancelled, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Cancelled, outcome.StopReason);
        Assert.Empty(outcome.RunOutcomes);
    }

    [Fact]
    public async Task Orchestrator_normalizes_initial_host_projection_failure()
    {
        var plan = Plan([Task("work")]);
        var orchestrator = new TaskOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ScriptedRunExecutor([]),
            new EvidenceTaskAcceptanceEvaluator(),
            new DeterministicWorkContextCompiler(),
            () => throw new InvalidOperationException("Host projection failed."));

        var outcome = await orchestrator.RunAsync(Request("Project host state."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Failed, outcome.StopReason);
        Assert.Null(outcome.FinalPlan);
        Assert.Empty(outcome.RunOutcomes);
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("initial host-state projection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_normalizes_initial_context_compiler_failure_and_preserves_plan()
    {
        var plan = Plan([Task("work")]);
        var orchestrator = new TaskOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ScriptedRunExecutor([]),
            new EvidenceTaskAcceptanceEvaluator(),
            new ThrowingWorkContextCompiler(throwOnCall: 1),
            () => new Dictionary<string, object?>());

        var outcome = await orchestrator.RunAsync(Request("Compile context."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Failed, outcome.StopReason);
        Assert.Same(plan, outcome.FinalPlan);
        Assert.Empty(outcome.RunOutcomes);
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("initial work-context compilation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_normalizes_child_executor_failure_and_preserves_prior_child_proof()
    {
        var first = Task("first");
        var second = Task("second", dependsOn: [first.TaskId]);
        var plan = Plan([first, second]);
        var executor = new ThrowOnSecondRunExecutor(Envelope("run_first", RunOutcomeStatus.Succeeded));
        var orchestrator = new TaskOrchestrator(
            new ScriptedTaskPlanner(plan),
            executor,
            new EvidenceTaskAcceptanceEvaluator(),
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>());

        var outcome = await orchestrator.RunAsync(Request("Preserve the first run."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Failed, outcome.StopReason);
        Assert.Same(plan, outcome.FinalPlan);
        Assert.Equal("run_first", Assert.Single(outcome.RunOutcomes).Outcome.RunId);
        Assert.Contains(first.TaskId, outcome.State.CompletedTaskIds);
        Assert.Equal("run_first", Assert.Single(outcome.State.RunRefs).RunId);
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("child run execution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_normalizes_acceptance_failure_and_keeps_the_child_envelope()
    {
        var plan = Plan([Task("work")]);
        var orchestrator = new TaskOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ScriptedRunExecutor([Envelope("run_work", RunOutcomeStatus.Succeeded)]),
            new ThrowingAcceptanceEvaluator(new InvalidOperationException("Acceptance failed.")),
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>());

        var outcome = await orchestrator.RunAsync(Request("Evaluate acceptance."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal("run_work", Assert.Single(outcome.RunOutcomes).Outcome.RunId);
        Assert.Empty(outcome.State.CompletedTaskIds);
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("task acceptance evaluation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_normalizes_definition_of_done_evaluation_failure_and_preserves_accepted_proof()
    {
        var task = Task("work");
        var plan = Plan([task]) with
        {
            DefinitionOfDone =
            [
                new TaskAcceptanceRequirement(
                    TaskAcceptanceRequirementKind.HostState,
                    HostStateKey: "hostReady",
                    HostStateValue: true)
            ]
        };
        var projectionCalls = 0;
        IReadOnlyDictionary<string, object?> ProjectHostState()
        {
            projectionCalls++;
            return projectionCalls == 3
                ? new ThrowingLookupDictionary("hostReady", true)
                : new Dictionary<string, object?> { ["hostReady"] = true };
        }

        var orchestrator = new TaskOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ScriptedRunExecutor([Envelope("run_work", RunOutcomeStatus.Succeeded)]),
            new EvidenceTaskAcceptanceEvaluator(),
            new DeterministicWorkContextCompiler(),
            ProjectHostState);

        var outcome = await orchestrator.RunAsync(Request("Evaluate definition of done."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal("run_work", Assert.Single(outcome.RunOutcomes).Outcome.RunId);
        Assert.Contains(task.TaskId, outcome.State.CompletedTaskIds);
        Assert.Equal("run_work", Assert.Single(outcome.State.RunRefs).RunId);
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("definition-of-done evaluation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_normalizes_final_projection_failure_without_relabeling_proof_as_success()
    {
        var task = Task("work");
        var plan = Plan([task]);
        var projectionCalls = 0;
        IReadOnlyDictionary<string, object?> ProjectHostState()
        {
            projectionCalls++;
            return projectionCalls == 4
                ? throw new InvalidOperationException("Final projection failed.")
                : new Dictionary<string, object?> { ["hostReady"] = true };
        }

        var orchestrator = new TaskOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ScriptedRunExecutor([Envelope("run_work", RunOutcomeStatus.Succeeded)]),
            new EvidenceTaskAcceptanceEvaluator(),
            new DeterministicWorkContextCompiler(),
            ProjectHostState);

        var outcome = await orchestrator.RunAsync(Request("Project final state."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Failed, outcome.StopReason);
        Assert.True(outcome.DefinitionOfDone?.Satisfied);
        Assert.Equal("run_work", Assert.Single(outcome.RunOutcomes).Outcome.RunId);
        Assert.Contains(task.TaskId, outcome.State.CompletedTaskIds);
        Assert.Equal("run_work", Assert.Single(outcome.State.RunRefs).RunId);
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("final host-state projection", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_normalizes_final_context_compilation_failure_and_preserves_definition_of_done()
    {
        var task = Task("work");
        var plan = Plan([task]);
        var orchestrator = new TaskOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ScriptedRunExecutor([Envelope("run_work", RunOutcomeStatus.Succeeded)]),
            new EvidenceTaskAcceptanceEvaluator(),
            new ThrowingWorkContextCompiler(throwOnCall: 3),
            () => new Dictionary<string, object?> { ["hostReady"] = true });

        var outcome = await orchestrator.RunAsync(Request("Compile final context."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Failed, outcome.StopReason);
        Assert.True(outcome.DefinitionOfDone?.Satisfied);
        Assert.Equal("run_work", Assert.Single(outcome.RunOutcomes).Outcome.RunId);
        Assert.Contains(task.TaskId, outcome.State.CompletedTaskIds);
        Assert.Equal("run_work", Assert.Single(outcome.State.RunRefs).RunId);
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("final work-context compilation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_does_not_relabel_child_cancellation_as_a_failure()
    {
        var plan = Plan([Task("work")]);
        var orchestrator = new TaskOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ThrowingRunExecutor(new OperationCanceledException("Child cancelled.")),
            new EvidenceTaskAcceptanceEvaluator(),
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>());

        var outcome = await orchestrator.RunAsync(Request("Cancel child."));

        Assert.Equal(OrchestrationStatus.Cancelled, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Cancelled, outcome.StopReason);
        Assert.DoesNotContain(outcome.Diagnostics, item =>
            item.Contains("child run execution failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Graph_validator_rejects_rewriting_completed_tasks()
    {
        var original = Plan([Task("done"), Task("next", dependsOn: ["done"])]);
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        state.CompletedTaskIds.Add("done");
        var semanticallyUnchanged = Plan([Task("done"), Task("next", dependsOn: ["done"])]);
        var rewritten = original with
        {
            Tasks =
            [
                Task("done", "A rewritten objective."),
                Task("next", dependsOn: ["done"])
            ]
        };

        TaskGraphValidator.Validate(semanticallyUnchanged, state, original);
        Assert.Throws<TaskGraphValidationException>(() => TaskGraphValidator.Validate(rewritten, state, original));
    }

    [Fact]
    public async Task Evidence_acceptance_evaluator_uses_receipts_artifacts_status_and_host_state()
    {
        var task = Task("accepted") with
        {
            AcceptanceRequirements =
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Succeeded),
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "test.artifact"),
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Receipt, ToolId: "tool.test"),
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.HostState, HostStateKey: "hostReady", HostStateValue: true)
            ]
        };
        var plan = Plan([task]);
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        var context = new TaskAcceptanceContext(
            plan,
            state,
            state.WorkingContext,
            new Dictionary<string, object?> { ["hostReady"] = true });

        var result = await new EvidenceTaskAcceptanceEvaluator().EvaluateAsync(
            task,
            Envelope("run_acceptance", RunOutcomeStatus.Succeeded),
            context);

        Assert.Equal(TaskAcceptanceStatus.Accepted, result.Status);
        Assert.Contains(result.EvidenceRefs, evidence => evidence.Kind == "artifact");
        Assert.Contains(result.EvidenceRefs, evidence => evidence.Kind == "receipt");
    }

    [Theory]
    [InlineData(true, "True")]
    [InlineData(1, "1")]
    public async Task Evidence_acceptance_rejects_host_values_that_only_match_after_string_conversion(
        object actual,
        string expected)
    {
        var task = Task("typed-host-state") with
        {
            AcceptanceRequirements =
            [
                new TaskAcceptanceRequirement(
                    TaskAcceptanceRequirementKind.HostState,
                    HostStateKey: "value",
                    HostStateValue: expected)
            ]
        };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));

        var result = await new EvidenceTaskAcceptanceEvaluator().EvaluateAsync(
            task,
            Envelope("run_typed_host", RunOutcomeStatus.Succeeded),
            new TaskAcceptanceContext(
                Plan([task]),
                state,
                state.WorkingContext,
                new Dictionary<string, object?> { ["value"] = actual }));

        Assert.NotEqual(TaskAcceptanceStatus.Accepted, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("did not satisfy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, "True")]
    [InlineData(1, "1")]
    public void Definition_of_done_rejects_host_values_that_only_match_after_string_conversion(
        object actual,
        string expected)
    {
        var task = Task("typed-host-state");
        var plan = Plan([task]) with
        {
            DefinitionOfDone =
            [
                new TaskAcceptanceRequirement(
                    TaskAcceptanceRequirementKind.HostState,
                    HostStateKey: "value",
                    HostStateValue: expected)
            ]
        };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        state.CompletedTaskIds.Add(task.TaskId);
        state.RunRefs.Add(new RunRef(task.TaskId, "run_typed_host", RunOutcomeStatus.Succeeded, []));

        var result = DefinitionOfDoneEvaluator.Evaluate(
            plan,
            state,
            [Envelope("run_typed_host", RunOutcomeStatus.Succeeded)],
            new Dictionary<string, object?> { ["value"] = actual });

        Assert.False(result.Satisfied);
        Assert.Contains(result.Reasons, reason => reason.Contains("did not satisfy", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evidence_acceptance_compares_common_json_and_generic_dictionary_values_structurally()
    {
        using var document = JsonDocument.Parse("""
            {
              "enabled": true,
              "items": [1, "one"],
              "labels": { "mode": "safe" }
            }
            """);
        var expected = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enabled"] = true,
            ["items"] = new object?[] { 1L, "one" },
            ["labels"] = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = "safe"
            }
        };
        var task = Task("json-host-state") with
        {
            AcceptanceRequirements =
            [
                new TaskAcceptanceRequirement(
                    TaskAcceptanceRequirementKind.HostState,
                    HostStateKey: "value",
                    HostStateValue: expected)
            ]
        };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));

        var result = await new EvidenceTaskAcceptanceEvaluator().EvaluateAsync(
            task,
            Envelope("run_json_host", RunOutcomeStatus.Succeeded),
            new TaskAcceptanceContext(
                Plan([task]),
                state,
                state.WorkingContext,
                new Dictionary<string, object?> { ["value"] = document }));

        Assert.Equal(TaskAcceptanceStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Evidence_acceptance_fails_closed_on_cyclic_host_values()
    {
        var cyclic = new Dictionary<string, object?>(StringComparer.Ordinal);
        cyclic["self"] = cyclic;
        var task = Task("cyclic-host-state") with
        {
            AcceptanceRequirements =
            [
                new TaskAcceptanceRequirement(
                    TaskAcceptanceRequirementKind.HostState,
                    HostStateKey: "value",
                    HostStateValue: cyclic)
            ]
        };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));

        var result = await new EvidenceTaskAcceptanceEvaluator().EvaluateAsync(
            task,
            Envelope("run_cyclic_host", RunOutcomeStatus.Succeeded),
            new TaskAcceptanceContext(
                Plan([task]),
                state,
                state.WorkingContext,
                new Dictionary<string, object?> { ["value"] = cyclic }));

        Assert.NotEqual(TaskAcceptanceStatus.Accepted, result.Status);
    }

    [Fact]
    public async Task Evidence_acceptance_evaluator_does_not_accept_report_prose_as_proof()
    {
        var task = Task("accepted") with
        {
            AcceptanceRequirements =
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "missing.kind")
            ]
        };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        var result = await new EvidenceTaskAcceptanceEvaluator().EvaluateAsync(
            task,
            Envelope("run_claims_success", RunOutcomeStatus.Succeeded) with
            {
                Report = new OutcomeReport("report_claims_success", "The missing.kind artifact exists and proves completion.", [])
            },
            new TaskAcceptanceContext(Plan([task]), state, state.WorkingContext, new Dictionary<string, object?>()));

        Assert.NotEqual(TaskAcceptanceStatus.Accepted, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("Missing artifact kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Work_context_compiler_keeps_report_claims_out_of_proven_facts_and_records_plan_impacts()
    {
        var task = Task("implement");
        var plan = Plan([task]);
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        var acceptance = new TaskAcceptanceResult(
            TaskAcceptanceStatus.InvalidatedPlan,
            ["Report prose says success, but evidence shows a new dependency is required."],
            [new EvidenceRef("artifact", "artifact_run_context")]);

        var context = new DeterministicWorkContextCompiler().Compile(new WorkContextCompilationRequest(
            plan,
            state,
            task,
            Envelope("run_context", RunOutcomeStatus.Succeeded) with
            {
                Report = new OutcomeReport("report_context", "Unsupported claim should not become a proven fact.", [])
            },
            acceptance,
            null,
            new Dictionary<string, object?>()));

        Assert.DoesNotContain(context.ProvenFacts, fact =>
            fact.Summary.Contains("Unsupported claim", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(context.PlanImpacts, impact =>
            impact.Kind == PlanImpactKind.NewDependencyDiscovered);
    }

    private static TaskOrchestrator CreateOrchestrator(
        ITaskPlanner planner,
        IRunExecutor executor,
        ITaskAcceptanceEvaluator evaluator) =>
        new(
            planner,
            executor,
            evaluator,
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?> { ["hostReady"] = true },
            new OrchestrationPolicy(MaxRuns: 8, MaxRefinements: 4, MaxGraphMutationsPerRefinement: 4));

    private static LargeTaskRequest Request(string objective) =>
        new(objective, RequestOrigin.User, new Dictionary<string, object?>());

    private static TaskGraphPlan Plan(IReadOnlyList<TaskNode> tasks) =>
        new(
            "plan_test",
            "Test objective.",
            tasks,
            [new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Succeeded)],
            DateTimeOffset.UtcNow);

    private static TaskGraphRefinement Refinement(TaskGraphMutation mutation) =>
        new("test_refinement", [mutation], [], RequiresUserInput: false);

    private static TaskNode Task(
        string taskId,
        string? objective = null,
        IReadOnlyList<string>? dependsOn = null,
        int priority = 1,
        int maxRuns = 1) =>
        new(
            taskId,
            objective ?? $"Objective for {taskId}.",
            dependsOn ?? [],
            Optional: false,
            priority,
            maxRuns,
            new Dictionary<string, object?>(),
            [new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Succeeded)]);

    private static OutcomeEnvelope Envelope(
        string runId,
        RunOutcomeStatus status,
        StopReason? stopReason = null) =>
        new(
            new RunOutcome(
                runId,
                status,
                stopReason ?? (status == RunOutcomeStatus.Succeeded ? StopReason.Complete : StopReason.ToolFailure),
                [],
                [],
                [new EvidenceRef("artifact", $"artifact_{runId}")]),
            new OutcomeReport($"report_{runId}", $"Report for {runId}.", []),
            new ReceiptEnvelope([Receipt(runId)]),
            new DetailEnvelope(
                new RunRequest($"Objective for {runId}."),
                [],
                [],
                [],
                [Artifact(runId)],
                [],
                [],
                []));

    private static Receipt Receipt(string runId) =>
        new(
            $"receipt_{runId}",
            "step_test",
            "tool.test",
            ReceiptStatus.Succeeded,
            "Receipt.",
            DateTimeOffset.UtcNow,
            new Dictionary<string, object?>());

    private static Artifact Artifact(string runId) =>
        new(
            $"artifact_{runId}",
            "test.artifact",
            new Dictionary<string, object?>(),
            []);

    private sealed class ScriptedTaskPlanner : ITaskPlanner
    {
        private readonly Queue<TaskGraphRefinement> _refinements;

        public ScriptedTaskPlanner(TaskGraphPlan plan, IReadOnlyList<TaskGraphRefinement>? refinements = null)
        {
            Plan = plan;
            _refinements = new Queue<TaskGraphRefinement>(refinements ?? []);
        }

        public TaskGraphPlan Plan { get; }

        public int RefineCalls { get; private set; }

        public Task<TaskGraphPlan> CreatePlanAsync(
            TaskPlanningRequest request,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(Plan);

        public Task<TaskGraphRefinement> RefinePlanAsync(
            TaskRefinementRequest request,
            CancellationToken cancellationToken = default)
        {
            RefineCalls++;
            return System.Threading.Tasks.Task.FromResult(_refinements.Dequeue());
        }
    }

    private sealed class ThrowingTaskPlanner : ITaskPlanner
    {
        private readonly TaskGraphPlan? _plan;
        private readonly Exception? _createException;
        private readonly Exception? _refineException;

        public ThrowingTaskPlanner(Exception createException)
        {
            _createException = createException;
        }

        public ThrowingTaskPlanner(TaskGraphPlan plan, Exception refineException)
        {
            _plan = plan;
            _refineException = refineException;
        }

        public Task<TaskGraphPlan> CreatePlanAsync(
            TaskPlanningRequest request,
            CancellationToken cancellationToken = default) =>
            _createException is not null
                ? System.Threading.Tasks.Task.FromException<TaskGraphPlan>(_createException)
                : System.Threading.Tasks.Task.FromResult(_plan!);

        public Task<TaskGraphRefinement> RefinePlanAsync(
            TaskRefinementRequest request,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromException<TaskGraphRefinement>(
                _refineException ?? new InvalidOperationException("No refinement was configured."));
    }

    private sealed class ScriptedRunExecutor : IRunExecutor
    {
        private readonly Queue<OutcomeEnvelope> _outcomes;

        public ScriptedRunExecutor(IReadOnlyList<OutcomeEnvelope> outcomes)
        {
            _outcomes = new Queue<OutcomeEnvelope>(outcomes);
        }

        public List<RunRequest> Requests { get; } = [];

        public Task<OutcomeEnvelope> RunAsync(
            RunRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return System.Threading.Tasks.Task.FromResult(_outcomes.Dequeue());
        }
    }

    private sealed class ThrowOnSecondRunExecutor : IRunExecutor
    {
        private readonly OutcomeEnvelope _firstOutcome;
        private int _calls;

        public ThrowOnSecondRunExecutor(OutcomeEnvelope firstOutcome)
        {
            _firstOutcome = firstOutcome;
        }

        public Task<OutcomeEnvelope> RunAsync(
            RunRequest request,
            CancellationToken cancellationToken = default)
        {
            _calls++;
            return _calls == 1
                ? System.Threading.Tasks.Task.FromResult(_firstOutcome)
                : System.Threading.Tasks.Task.FromException<OutcomeEnvelope>(
                    new InvalidOperationException("The second child executor call failed."));
        }
    }

    private sealed class ThrowingRunExecutor : IRunExecutor
    {
        private readonly Exception _exception;

        public ThrowingRunExecutor(Exception exception)
        {
            _exception = exception;
        }

        public Task<OutcomeEnvelope> RunAsync(
            RunRequest request,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromException<OutcomeEnvelope>(_exception);
    }

    private sealed class ThrowingAcceptanceEvaluator : ITaskAcceptanceEvaluator
    {
        private readonly Exception _exception;

        public ThrowingAcceptanceEvaluator(Exception exception)
        {
            _exception = exception;
        }

        public Task<TaskAcceptanceResult> EvaluateAsync(
            TaskNode task,
            OutcomeEnvelope outcome,
            TaskAcceptanceContext context,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromException<TaskAcceptanceResult>(_exception);
    }

    private sealed class ThrowingWorkContextCompiler : IWorkContextCompiler
    {
        private readonly DeterministicWorkContextCompiler _inner = new();
        private readonly int _throwOnCall;
        private int _calls;

        public ThrowingWorkContextCompiler(int throwOnCall)
        {
            _throwOnCall = throwOnCall;
        }

        public WorkContextSnapshot Compile(WorkContextCompilationRequest request)
        {
            _calls++;
            if (_calls == _throwOnCall)
            {
                throw new InvalidOperationException("Work-context compilation failed.");
            }

            return _inner.Compile(request);
        }
    }

    private sealed class ThrowingLookupDictionary : IReadOnlyDictionary<string, object?>
    {
        private readonly Dictionary<string, object?> _inner;
        private readonly string _throwingKey;

        public ThrowingLookupDictionary(string throwingKey, object? value)
        {
            _throwingKey = throwingKey;
            _inner = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [throwingKey] = value
            };
        }

        public object? this[string key] => _inner[key];

        public IEnumerable<string> Keys => _inner.Keys;

        public IEnumerable<object?> Values => _inner.Values;

        public int Count => _inner.Count;

        public bool ContainsKey(string key) => _inner.ContainsKey(key);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _inner.GetEnumerator();

        public bool TryGetValue(string key, out object? value)
        {
            if (string.Equals(key, _throwingKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Host-state lookup failed.");
            }

            return _inner.TryGetValue(key, out value);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ScriptedAcceptanceEvaluator : ITaskAcceptanceEvaluator
    {
        private readonly Func<TaskNode, TaskAcceptanceResult> _evaluate;

        public ScriptedAcceptanceEvaluator(Func<TaskNode, TaskAcceptanceResult> evaluate)
        {
            _evaluate = evaluate;
        }

        public Task<TaskAcceptanceResult> EvaluateAsync(
            TaskNode task,
            OutcomeEnvelope outcome,
            TaskAcceptanceContext context,
            CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.Task.FromResult(_evaluate(task));
    }
}
