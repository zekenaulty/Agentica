using Agentica.Artifacts;
using Agentica.Events;
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
    public void Graph_validator_accepts_the_maximum_bounded_dependency_chain_without_recursion()
    {
        var tasks = Enumerable.Range(0, 4_096)
            .Select(index => Task(
                $"task_{index}",
                dependsOn: index == 0 ? [] : [$"task_{index - 1}"]))
            .ToArray();

        TaskGraphValidator.Validate(Plan(tasks));
    }

    [Fact]
    public void Orchestrator_rejects_policy_values_outside_the_bounded_runtime_contract()
    {
        static TaskOrchestrator Create(OrchestrationPolicy policy) =>
            new(
                new ScriptedTaskPlanner(Plan([Task("work")])),
                new ScriptedRunExecutor([]),
                new EvidenceTaskAcceptanceEvaluator(),
                new DeterministicWorkContextCompiler(),
                () => new Dictionary<string, object?>(),
                policy);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create(new OrchestrationPolicy(MaxRuns: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create(new OrchestrationPolicy(MaxRuns: 65)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create(new OrchestrationPolicy(MaxRefinements: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create(new OrchestrationPolicy(MaxRefinements: 65)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create(new OrchestrationPolicy(MaxGraphMutationsPerRefinement: -1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Create(new OrchestrationPolicy(MaxGraphMutationsPerRefinement: 65)));
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
    public async Task Orchestrator_detaches_request_plan_state_and_child_proof_while_the_run_is_active()
    {
        var requestValues = new List<string> { "request-before" };
        var requestContext = new Dictionary<string, object?>
        {
            ["requestValues"] = requestValues
        };
        var taskValues = new List<string> { "task-before" };
        var taskContext = new Dictionary<string, object?>
        {
            ["taskValues"] = taskValues
        };
        var task = Task("work") with { ContextProjection = taskContext };
        var tasks = new List<TaskNode> { task };
        var plan = Plan(tasks);
        var receiptValues = new List<string> { "receipt-before" };
        var receiptData = new Dictionary<string, object?>
        {
            ["receiptValues"] = receiptValues
        };
        var childReceipts = new List<Receipt>
        {
            Receipt("run_work") with { Data = receiptData }
        };
        var child = Envelope("run_work", RunOutcomeStatus.Succeeded) with
        {
            Receipts = new ReceiptEnvelope(childReceipts)
        };
        var executor = new CoordinatedRunExecutor();
        var evaluator = new CoordinatedAcceptanceEvaluator();
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(plan),
            executor,
            evaluator);

        var running = orchestrator.RunAsync(new LargeTaskRequest(
            "Keep caller mutations outside the run.",
            RequestOrigin.User,
            requestContext));
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        requestValues.Add("request-after");
        requestContext["lateRequestKey"] = true;
        taskValues.Add("task-after");
        taskContext["lateTaskKey"] = true;
        tasks.Clear();
        executor.Complete(child);

        await evaluator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        receiptValues.Add("receipt-after");
        receiptData["lateReceiptKey"] = true;
        childReceipts.Clear();
        evaluator.Complete(new TaskAcceptanceResult(
            TaskAcceptanceStatus.Accepted,
            [],
            [new EvidenceRef("artifact", "artifact_run_work")]));

        var outcome = await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.DoesNotContain("forged-by-evaluator", outcome.State.CompletedTaskIds);
        Assert.Equal(["work"], outcome.State.CompletedTaskIds);
        Assert.Equal(["work"], outcome.FinalPlan!.Tasks.Select(item => item.TaskId));
        Assert.Equal(
            ["request-before"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                executor.Request!.Context!["requestValues"]));
        Assert.False(executor.Request.Context.ContainsKey("lateRequestKey"));
        Assert.Equal(
            ["task-before"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                executor.Request.Context["taskValues"]));
        Assert.False(executor.Request.Context.ContainsKey("lateTaskKey"));
        Assert.Equal(
            ["receipt-before"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                evaluator.Outcome!.Receipts.Items[0].Data["receiptValues"]));
        Assert.False(evaluator.Outcome.Receipts.Items[0].Data.ContainsKey("lateReceiptKey"));
        Assert.Single(outcome.RunOutcomes[0].Receipts.Items);
    }

    [Fact]
    public async Task Returned_orchestration_envelope_is_deeply_detached_and_read_only()
    {
        var taskValues = new List<string> { "before" };
        var context = new Dictionary<string, object?>
        {
            ["taskValues"] = taskValues
        };
        var tasks = new List<TaskNode>
        {
            Task("work") with { ContextProjection = context }
        };
        var child = Envelope("run_work", RunOutcomeStatus.Succeeded);
        var outcome = await CreateOrchestrator(
                new ScriptedTaskPlanner(Plan(tasks)),
                new ScriptedRunExecutor([child]),
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Return immutable proof."));

        taskValues.Add("after");
        context["late"] = true;
        tasks.Clear();

        Assert.Equal(["work"], outcome.FinalPlan!.Tasks.Select(item => item.TaskId));
        Assert.Equal(
            ["before"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                outcome.FinalPlan.Tasks[0].ContextProjection["taskValues"]));
        Assert.False(outcome.FinalPlan.Tasks[0].ContextProjection.ContainsKey("late"));

        AssertReadOnly(outcome.FinalPlan.Tasks, Task("late"));
        AssertReadOnly(outcome.FinalPlan.DefinitionOfDone,
            new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Failed));
        AssertReadOnly(outcome.State.CompletedTaskIds, "late");
        AssertReadOnly(outcome.State.RunRefs,
            new RunRef("late", "run_late", RunOutcomeStatus.Succeeded, []));
        AssertReadOnlyDictionary(outcome.State.TaskRunCounts, "late", 1);
        AssertReadOnly(outcome.WorkingContext.CompletedTaskIds, "late");
        AssertReadOnlyDictionary(outcome.WorkingContext.HostStateProjection, "late", true);
        AssertReadOnly(outcome.RunOutcomes, Envelope("run_late", RunOutcomeStatus.Succeeded));
        AssertReadOnly(outcome.RunOutcomes[0].Receipts.Items, Receipt("run_late"));
        AssertReadOnlyDictionary(outcome.RunOutcomes[0].Receipts.Items[0].Data, "late", true);
        AssertReadOnly(outcome.EvidenceRefs, new EvidenceRef("artifact", "late"));
        AssertReadOnly(outcome.Diagnostics, "late");
        AssertReadOnly(outcome.DefinitionOfDone!.Reasons, "late");
        AssertReadOnly(outcome.DefinitionOfDone.EvidenceRefs, new EvidenceRef("artifact", "late"));
    }

    [Fact]
    public async Task Cyclic_and_oversized_request_data_fail_closed_before_planning()
    {
        var planner = new CountingTaskPlanner(Plan([Task("work")]));
        var executor = new ScriptedRunExecutor([]);
        var orchestrator = CreateOrchestrator(
            planner,
            executor,
            new EvidenceTaskAcceptanceEvaluator());
        var cyclicContext = new Dictionary<string, object?>();
        cyclicContext["self"] = cyclicContext;
        var oversizedValues = Enumerable.Range(0, 16_385).ToList();

        var cyclic = await orchestrator.RunAsync(new LargeTaskRequest(
            "Reject a cycle.",
            RequestOrigin.User,
            cyclicContext));
        var oversized = await orchestrator.RunAsync(new LargeTaskRequest(
            "Reject oversized data.",
            RequestOrigin.User,
            new Dictionary<string, object?> { ["values"] = oversizedValues }));

        Assert.Equal(OrchestrationStatus.PlanInvalid, cyclic.Status);
        Assert.Equal(OrchestrationStatus.PlanInvalid, oversized.Status);
        Assert.Null(cyclic.FinalPlan);
        Assert.Null(oversized.FinalPlan);
        Assert.Equal(0, planner.CreateCalls);
        Assert.Empty(executor.Requests);
        Assert.Contains(cyclic.Diagnostics, item =>
            item.Contains("safely snapshotted", StringComparison.Ordinal));
        Assert.Contains(oversized.Diagnostics, item =>
            item.Contains("safely snapshotted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oversized_objective_is_normalized_to_a_bounded_redacted_terminal_envelope()
    {
        var planner = new CountingTaskPlanner(Plan([Task("work")]));
        var executor = new ScriptedRunExecutor([]);

        var outcome = await CreateOrchestrator(
                planner,
                executor,
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(new LargeTaskRequest(
                new string('x', 1_100_000),
                RequestOrigin.User,
                new Dictionary<string, object?>()));

        Assert.Equal(OrchestrationStatus.PlanInvalid, outcome.Status);
        Assert.Equal("Invalid large-task request.", outcome.Objective);
        Assert.Equal(0, planner.CreateCalls);
        Assert.Empty(executor.Requests);
        Assert.All(outcome.Diagnostics, diagnostic => Assert.True(diagnostic.Length <= 1_024));
        Assert.DoesNotContain(outcome.Diagnostics, diagnostic =>
            diagnostic.Contains(new string('x', 128), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Oversized_planner_exception_message_is_reduced_to_type_only_diagnostics()
    {
        var outcome = await CreateOrchestrator(
                new ThrowingTaskPlanner(new InvalidOperationException(new string('x', 1_100_000))),
                new ScriptedRunExecutor([]),
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Normalize a hostile planner exception."));

        Assert.Equal(OrchestrationStatus.PlanInvalid, outcome.Status);
        Assert.Contains(outcome.Diagnostics, diagnostic =>
            diagnostic.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        Assert.All(outcome.Diagnostics, diagnostic => Assert.True(diagnostic.Length <= 1_024));
        Assert.DoesNotContain(outcome.Diagnostics, diagnostic =>
            diagnostic.Contains(new string('x', 128), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cyclic_planner_task_data_is_normalized_as_an_invalid_plan()
    {
        var taskContext = new Dictionary<string, object?>();
        taskContext["self"] = taskContext;
        var planner = new CountingTaskPlanner(Plan(
        [
            Task("work") with { ContextProjection = taskContext }
        ]));
        var executor = new ScriptedRunExecutor([]);

        var outcome = await CreateOrchestrator(
                planner,
                executor,
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Reject a cyclic task projection."));

        Assert.Equal(OrchestrationStatus.PlanInvalid, outcome.Status);
        Assert.Null(outcome.FinalPlan);
        Assert.Equal(1, planner.CreateCalls);
        Assert.Empty(executor.Requests);
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("initial task planning failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Plan_snapshot_enforces_aggregate_node_and_byte_budgets_across_task_contexts()
    {
        var nodeTasks = Enumerable.Range(0, 64)
            .Select(index => Task($"node_{index}") with
            {
                ContextProjection = new Dictionary<string, object?>
                {
                    ["values"] = Enumerable.Range(0, 256).ToArray()
                }
            })
            .ToArray();
        var byteTasks = Enumerable.Range(0, 8)
            .Select(index => Task($"bytes_{index}") with
            {
                ContextProjection = new Dictionary<string, object?>
                {
                    ["payload"] = new string((char)('a' + index), 140_000)
                }
            })
            .ToArray();
        var nodeExecutor = new ScriptedRunExecutor([]);
        var byteExecutor = new ScriptedRunExecutor([]);

        var nodeOutcome = await CreateOrchestrator(
                new CountingTaskPlanner(Plan(nodeTasks)),
                nodeExecutor,
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Bound aggregate context nodes."));
        var byteOutcome = await CreateOrchestrator(
                new CountingTaskPlanner(Plan(byteTasks)),
                byteExecutor,
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Bound aggregate context bytes."));

        Assert.Equal(OrchestrationStatus.PlanInvalid, nodeOutcome.Status);
        Assert.Equal(OrchestrationStatus.PlanInvalid, byteOutcome.Status);
        Assert.Empty(nodeExecutor.Requests);
        Assert.Empty(byteExecutor.Requests);
        Assert.Contains(nodeOutcome.Diagnostics, item =>
            item.Contains("initial task planning failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(byteOutcome.Diagnostics, item =>
            item.Contains("initial task planning failed", StringComparison.OrdinalIgnoreCase));
        Assert.All(nodeOutcome.Diagnostics, diagnostic => Assert.True(diagnostic.Length <= 1_024));
        Assert.All(byteOutcome.Diagnostics, diagnostic => Assert.True(diagnostic.Length <= 1_024));
    }

    [Fact]
    public async Task Dishonest_task_count_cannot_drive_unbounded_orchestration_enumeration()
    {
        var tasks = new DishonestReadOnlyList<TaskNode>(
            reportedCount: 1,
            yieldedCount: 20_000,
            index => Task($"task_{index}"));
        var executor = new ScriptedRunExecutor([]);

        var outcome = await CreateOrchestrator(
                new ScriptedTaskPlanner(Plan(tasks)),
                executor,
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Bound dishonest task enumeration."));

        Assert.Equal(OrchestrationStatus.PlanInvalid, outcome.Status);
        Assert.Empty(executor.Requests);
        Assert.InRange(tasks.EnumerationCount, 1, 16_385);
    }

    [Fact]
    public async Task Child_outcome_snapshot_retains_complete_proof_above_one_megabyte()
    {
        var receipts = Enumerable.Range(0, 8)
            .Select(index => Receipt("run_work") with
            {
                ReceiptId = $"receipt_{index}",
                Data = new Dictionary<string, object?>
                {
                    ["payload"] = new string((char)('a' + index), 140_000)
                }
            })
            .ToArray();
        var child = Envelope("run_work", RunOutcomeStatus.Succeeded) with
        {
            Receipts = new ReceiptEnvelope(receipts)
        };

        var outcome = await CreateOrchestrator(
                new ScriptedTaskPlanner(Plan([Task("work")])),
                new ScriptedRunExecutor([child]),
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Bound aggregate child proof."));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        var retained = Assert.Single(outcome.RunOutcomes);
        Assert.Equal(8, retained.Receipts.Items.Count);
        Assert.Equal(
            8 * 140_000,
            retained.Receipts.Items.Sum(receipt =>
                Assert.IsType<string>(receipt.Data["payload"]).Length));
        Assert.NotSame(child, retained);
    }

    [Fact]
    public async Task Oversized_child_proof_fails_closed_but_retains_an_indeterminate_dispatch_record()
    {
        var receipts = Enumerable.Range(0, 120)
            .Select(index => Receipt("run_oversized") with
            {
                ReceiptId = $"receipt_{index}",
                Data = new Dictionary<string, object?>
                {
                    ["payload"] = new string((char)('a' + index % 26), 140_000)
                }
            })
            .ToArray();
        var child = Envelope("run_oversized", RunOutcomeStatus.Succeeded) with
        {
            Receipts = new ReceiptEnvelope(receipts)
        };

        var outcome = await CreateOrchestrator(
                new ScriptedTaskPlanner(Plan([Task("work")])),
                new ScriptedRunExecutor([child]),
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Fail closed on oversized returned proof."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Failed, outcome.StopReason);
        var retained = Assert.Single(outcome.RunOutcomes);
        Assert.Equal(RunOutcomeStatus.PartiallyComplete, retained.Outcome.Status);
        Assert.Equal(StopReason.Partial, retained.Outcome.StopReason);
        Assert.StartsWith("child_dispatch_", retained.Outcome.RunId, StringComparison.Ordinal);
        var receipt = Assert.Single(retained.Receipts.Items);
        Assert.Equal(ReceiptStatus.Partial, receipt.Status);
        Assert.Equal(true, receipt.Data["returnedOutcomeReceived"]);
        Assert.Equal(true, receipt.Data["effectMayHaveOccurred"]);
        Assert.Equal("run_oversized", receipt.Data["returnedRunId"]);
        Assert.Equal("unavailable", receipt.Data["proofStatus"]);
        Assert.Contains(retained.Details.ValidationIssues, issue =>
            issue.Code == "orchestration.child.proof.unavailable");
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("child run execution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Oversized_child_event_json_fails_closed_without_erasing_the_dispatch()
    {
        using var oversized = JsonDocument.Parse($"\"{new string('x', 1_100_000)}\"");
        var child = Envelope("run_event_oversized", RunOutcomeStatus.Succeeded);
        child = child with
        {
            Details = child.Details with
            {
                Events =
                [
                    new ExecutionEvent(
                        "event_oversized",
                        "child.returned",
                        DateTimeOffset.UtcNow,
                        new Dictionary<string, string>(StringComparer.Ordinal))
                    {
                        Payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["oversized"] = oversized
                        }
                    }
                ]
            }
        };

        var outcome = await CreateOrchestrator(
                new ScriptedTaskPlanner(Plan([Task("work")])),
                new ScriptedRunExecutor([child]),
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Bound child event JSON."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        var retained = Assert.Single(outcome.RunOutcomes);
        Assert.Equal(RunOutcomeStatus.PartiallyComplete, retained.Outcome.Status);
        var receipt = Assert.Single(retained.Receipts.Items);
        Assert.Equal(true, receipt.Data["returnedOutcomeReceived"]);
        Assert.Equal("run_event_oversized", receipt.Data["returnedRunId"]);
        Assert.Equal(true, receipt.Data["effectMayHaveOccurred"]);
    }

    [Fact]
    public async Task Child_event_proof_preserves_exact_json_number_after_source_disposal()
    {
        const string rawNumber = "0.100000000000000000000000000006";
        OrchestrationOutcomeEnvelope outcome;

        using (var number = JsonDocument.Parse(rawNumber))
        {
            var child = Envelope("run_event_number", RunOutcomeStatus.Succeeded);
            child = child with
            {
                Details = child.Details with
                {
                    Events =
                    [
                        new ExecutionEvent(
                            "event_number",
                            "child.returned",
                            DateTimeOffset.UtcNow,
                            new Dictionary<string, string>(StringComparer.Ordinal))
                        {
                            Payload = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["value"] = number.RootElement
                            }
                        }
                    ]
                }
            };

            outcome = await CreateOrchestrator(
                    new ScriptedTaskPlanner(Plan([Task("work")])),
                    new ScriptedRunExecutor([child]),
                    new EvidenceTaskAcceptanceEvaluator())
                .RunAsync(Request("Retain exact child event proof."));
        }

        var executionEvent = Assert.Single(Assert.Single(outcome.RunOutcomes).Details.Events);
        Assert.Equal(
            rawNumber,
            Assert.IsType<JsonElement>(executionEvent.Payload["value"]).GetRawText());
        using var serialized = JsonDocument.Parse(JsonSerializer.Serialize(executionEvent.Payload));
        Assert.Equal(rawNumber, serialized.RootElement.GetProperty("value").GetRawText());
    }

    [Fact]
    public async Task Final_envelope_retains_each_individually_bounded_child_proof_above_one_megabyte_total()
    {
        const int segmentLength = 200_000;
        const int payloadLength = segmentLength * 3;
        var first = Envelope("run_first", RunOutcomeStatus.Succeeded) with
        {
            Receipts = new ReceiptEnvelope(
            [
                Receipt("run_first") with
                {
                    Data = new Dictionary<string, object?>
                    {
                        ["payload1"] = new string('a', segmentLength),
                        ["payload2"] = new string('b', segmentLength),
                        ["payload3"] = new string('c', segmentLength)
                    }
                }
            ])
        };
        var second = Envelope("run_second", RunOutcomeStatus.Succeeded) with
        {
            Receipts = new ReceiptEnvelope(
            [
                Receipt("run_second") with
                {
                    Data = new Dictionary<string, object?>
                    {
                        ["payload1"] = new string('d', segmentLength),
                        ["payload2"] = new string('e', segmentLength),
                        ["payload3"] = new string('f', segmentLength)
                    }
                }
            ])
        };
        var plan = Plan(
        [
            Task("first"),
            Task("second", dependsOn: ["first"])
        ]);

        var outcome = await CreateOrchestrator(
                new ScriptedTaskPlanner(plan),
                new ScriptedRunExecutor([first, second]),
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Retain all bounded child proof."));

        Assert.True(
            outcome.Status == OrchestrationStatus.Succeeded,
            $"Expected success, but got {outcome.Status}/{outcome.StopReason}: {string.Join(" | ", outcome.Diagnostics)}");
        Assert.Equal(2, outcome.RunOutcomes.Count);
        Assert.Equal(
            payloadLength,
            outcome.RunOutcomes[0].Receipts.Items[0].Data.Values
                .Cast<string>()
                .Sum(value => value.Length));
        Assert.Equal(
            payloadLength,
            outcome.RunOutcomes[1].Receipts.Items[0].Data.Values
                .Cast<string>()
                .Sum(value => value.Length));
        Assert.NotSame(first, outcome.RunOutcomes[0]);
        Assert.NotSame(second, outcome.RunOutcomes[1]);
        AssertReadOnly(outcome.RunOutcomes, Envelope("run_late", RunOutcomeStatus.Succeeded));
    }

    [Fact]
    public void Mutation_applier_snapshots_refinement_values_and_returns_an_immutable_plan()
    {
        var originalTasks = new List<TaskNode> { Task("first") };
        var plan = Plan(originalTasks);
        var addedValues = new List<string> { "before" };
        var addedContext = new Dictionary<string, object?>
        {
            ["values"] = addedValues
        };
        var added = Task("second", dependsOn: ["first"]) with
        {
            ContextProjection = addedContext
        };
        var mutations = new List<TaskGraphMutation>
        {
            new(TaskGraphMutationKind.AddTask, "second", Task: added)
        };
        var refinement = new TaskGraphRefinement("add second", mutations, [], false);

        var result = TaskGraphMutationApplier.Apply(plan, refinement);

        originalTasks.Clear();
        addedValues.Add("after");
        addedContext["late"] = true;
        mutations.Clear();

        Assert.Equal(["first", "second"], result.Tasks.Select(item => item.TaskId));
        Assert.Equal(
            ["before"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(
                result.Tasks[1].ContextProjection["values"]));
        Assert.False(result.Tasks[1].ContextProjection.ContainsKey("late"));
        AssertReadOnly(result.Tasks, Task("late"));
        AssertReadOnly(result.Tasks[1].DependsOn, "late");
        AssertReadOnlyDictionary(result.Tasks[1].ContextProjection, "late", true);

        var cyclicContext = new Dictionary<string, object?>();
        cyclicContext["self"] = cyclicContext;
        var cyclicRefinement = new TaskGraphRefinement(
            "reject cycle",
            [
                new TaskGraphMutation(
                    TaskGraphMutationKind.AddTask,
                    "cyclic",
                    Task: Task("cyclic") with { ContextProjection = cyclicContext })
            ],
            [],
            false);
        Assert.Throws<TaskGraphValidationException>(() =>
            TaskGraphMutationApplier.Apply(Plan([Task("base")]), cyclicRefinement));
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
    public async Task Orchestrator_rejects_duplicate_child_run_ids_and_preserves_both_outcomes()
    {
        var task = Task("work", maxRuns: 2);
        var plan = Plan([task]) with
        {
            DefinitionOfDone =
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "test.artifact")
            ]
        };
        var refinement = Refinement(new TaskGraphMutation(
            TaskGraphMutationKind.ReorderPriority,
            task.TaskId,
            Priority: 2));
        var first = Envelope("run_reused", RunOutcomeStatus.Succeeded);
        var secondWithEvidence = Envelope("run_reused", RunOutcomeStatus.Succeeded);
        var second = secondWithEvidence with
        {
            Details = secondWithEvidence.Details with { Artifacts = [] }
        };
        var evaluations = 0;
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
            ++evaluations == 1
                ? new TaskAcceptanceResult(
                    TaskAcceptanceStatus.PartiallyAccepted,
                    ["The first child result is not acceptable."],
                    [])
                : new TaskAcceptanceResult(
                    TaskAcceptanceStatus.Accepted,
                    [],
                    [new EvidenceRef("run", "run_reused")]));
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(plan, [refinement]),
            new ScriptedRunExecutor([first, second]),
            evaluator);

        var outcome = await orchestrator.RunAsync(Request("Reject ambiguous child proof."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.ChildRunFailed, outcome.StopReason);
        Assert.Null(outcome.State.ActiveTaskId);
        Assert.Empty(outcome.State.CompletedTaskIds);
        Assert.Equal(2, outcome.RunOutcomes.Count);
        Assert.NotSame(first, outcome.RunOutcomes[0]);
        Assert.NotSame(second, outcome.RunOutcomes[1]);
        Assert.Equal(1, evaluations);
        Assert.Contains(outcome.Diagnostics, diagnostic =>
            diagnostic.Contains("reused run id 'run_reused'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Orchestrator_rejects_empty_child_run_id_and_preserves_the_outcome()
    {
        var child = Envelope(string.Empty, RunOutcomeStatus.Succeeded);
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(Plan([Task("work")])),
            new ScriptedRunExecutor([child]),
            new EvidenceTaskAcceptanceEvaluator());

        var outcome = await orchestrator.RunAsync(Request("Reject empty child identity."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.ChildRunFailed, outcome.StopReason);
        Assert.Single(outcome.RunOutcomes);
        Assert.NotSame(child, outcome.RunOutcomes[0]);
        Assert.Contains(outcome.Diagnostics, diagnostic =>
            diagnostic.Contains("empty run id", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Orchestrator_rejects_run_id_reused_across_nested_prior_attempt_trees()
    {
        var task = Task("work", maxRuns: 2);
        var plan = Plan([task]);
        var refinement = Refinement(new TaskGraphMutation(
            TaskGraphMutationKind.ReorderPriority,
            task.TaskId,
            Priority: 2));
        var firstPrior = Envelope("run_first_prior", RunOutcomeStatus.Succeeded) with
        {
            PriorAttempts = [Envelope("run_shared", RunOutcomeStatus.Succeeded)]
        };
        var first = Envelope("run_first", RunOutcomeStatus.Succeeded) with
        {
            PriorAttempts = [firstPrior]
        };
        var secondPrior = Envelope("run_second_prior", RunOutcomeStatus.Succeeded) with
        {
            PriorAttempts = [Envelope("run_shared", RunOutcomeStatus.Succeeded)]
        };
        var second = Envelope("run_second", RunOutcomeStatus.Succeeded) with
        {
            PriorAttempts = [secondPrior]
        };
        var evaluations = 0;
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
        {
            evaluations++;
            return new TaskAcceptanceResult(
                TaskAcceptanceStatus.PartiallyAccepted,
                ["The child result requires another run."],
                []);
        });
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(plan, [refinement]),
            new ScriptedRunExecutor([first, second]),
            evaluator);

        var outcome = await orchestrator.RunAsync(Request("Reject ambiguous retry proof."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.ChildRunFailed, outcome.StopReason);
        Assert.Null(outcome.State.ActiveTaskId);
        Assert.Empty(outcome.State.CompletedTaskIds);
        Assert.Equal(2, outcome.RunOutcomes.Count);
        Assert.NotSame(first, outcome.RunOutcomes[0]);
        Assert.NotSame(second, outcome.RunOutcomes[1]);
        Assert.Equal(1, evaluations);
        Assert.Contains(outcome.Diagnostics, diagnostic =>
            diagnostic.Contains("prior attempt 1 prior attempt 1 reused run id 'run_shared'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Orchestrator_rejects_empty_run_id_in_nested_prior_attempt_tree()
    {
        var prior = Envelope("run_prior", RunOutcomeStatus.Succeeded) with
        {
            PriorAttempts = [Envelope(" ", RunOutcomeStatus.Succeeded)]
        };
        var child = Envelope("run_child", RunOutcomeStatus.Succeeded) with
        {
            PriorAttempts = [prior]
        };
        var evaluations = 0;
        var evaluator = new ScriptedAcceptanceEvaluator(_ =>
        {
            evaluations++;
            return new TaskAcceptanceResult(TaskAcceptanceStatus.Accepted, [], []);
        });
        var orchestrator = CreateOrchestrator(
            new ScriptedTaskPlanner(Plan([Task("work")])),
            new ScriptedRunExecutor([child]),
            evaluator);

        var outcome = await orchestrator.RunAsync(Request("Reject empty retry identity."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.Equal(OrchestrationStopReason.ChildRunFailed, outcome.StopReason);
        Assert.Null(outcome.State.ActiveTaskId);
        Assert.Empty(outcome.State.CompletedTaskIds);
        Assert.Single(outcome.RunOutcomes);
        Assert.NotSame(child, outcome.RunOutcomes[0]);
        Assert.Equal(0, evaluations);
        Assert.Contains(outcome.Diagnostics, diagnostic =>
            diagnostic.Contains("prior attempt 1 prior attempt 1 has an empty run id", StringComparison.Ordinal));
    }

    [Fact]
    public void Definition_of_done_rejects_an_accepted_run_id_that_resolves_to_multiple_child_outcomes()
    {
        var task = Task("work");
        var plan = Plan([task]) with
        {
            DefinitionOfDone =
            [
                new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.Artifact, ArtifactKind: "test.artifact")
            ]
        };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        state.CompletedTaskIds.Add(task.TaskId);
        state.RunRefs.Add(new RunRef(task.TaskId, "run_reused", RunOutcomeStatus.Succeeded, []));

        var result = DefinitionOfDoneEvaluator.Evaluate(
            plan,
            state,
            [
                Envelope("run_reused", RunOutcomeStatus.Succeeded),
                Envelope("run_reused", RunOutcomeStatus.Succeeded)
            ],
            new Dictionary<string, object?>());

        Assert.False(result.Satisfied);
        Assert.Empty(result.EvidenceRefs);
        Assert.Contains(result.Reasons, reason =>
            reason.Contains("resolves to 2 child outcomes", StringComparison.Ordinal));
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
        Assert.NotSame(invalidPlan, outcome.FinalPlan);
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
        Assert.NotSame(plan, outcome.FinalPlan);
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
        Assert.NotSame(plan, outcome.FinalPlan);
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
        Assert.NotSame(plan, outcome.FinalPlan);
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
        Assert.NotSame(plan, outcome.FinalPlan);
        Assert.Equal(2, outcome.RunOutcomes.Count);
        Assert.Equal("run_first", outcome.RunOutcomes[0].Outcome.RunId);
        Assert.Equal(RunOutcomeStatus.PartiallyComplete, outcome.RunOutcomes[1].Outcome.Status);
        Assert.Equal(false, Assert.Single(outcome.RunOutcomes[1].Receipts.Items)
            .Data["returnedOutcomeReceived"]);
        Assert.Contains(first.TaskId, outcome.State.CompletedTaskIds);
        Assert.Equal("run_first", Assert.Single(outcome.State.RunRefs).RunId);
        Assert.Contains(outcome.Diagnostics, item =>
            item.Contains("child run execution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Child_failure_record_reuses_the_pre_dispatch_request_snapshot()
    {
        var executor = new RequestMutatingThrowingRunExecutor();
        var outcome = await CreateOrchestrator(
                new ScriptedTaskPlanner(Plan([Task("work")])),
                executor,
                new EvidenceTaskAcceptanceEvaluator())
            .RunAsync(Request("Retain the actual dispatched request."));

        Assert.Equal(OrchestrationStatus.Failed, outcome.Status);
        Assert.True(executor.MutationAttempted);
        Assert.True(executor.OuterMutationBlocked);
        Assert.True(executor.NestedMutationSucceeded);
        var childDispatch = Assert.Single(outcome.RunOutcomes);
        var receipt = Assert.Single(childDispatch.Receipts.Items);
        var context = childDispatch.Details.Request.Context!;
        Assert.Equal("work", context["orchestration.taskId"]);
        Assert.Equal(
            receipt.Data["childDispatchId"],
            context["orchestration.childDispatchId"]);
        Assert.False(context.ContainsKey("attacker.added"));
        Assert.NotEqual("attacker_dispatch", context["orchestration.childDispatchId"]);
        var workingContext = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            context["orchestration.workingContext"]);
        var hostState = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            workingContext["HostStateProjection"]);
        Assert.False(hostState.ContainsKey("attacker.nested"));
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
    public async Task Definition_of_done_uses_a_detached_host_state_snapshot_instead_of_custom_lookup_behavior()
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

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Equal("run_work", Assert.Single(outcome.RunOutcomes).Outcome.RunId);
        Assert.Contains(task.TaskId, outcome.State.CompletedTaskIds);
        Assert.Equal("run_work", Assert.Single(outcome.State.RunRefs).RunId);
        Assert.True(outcome.DefinitionOfDone?.Satisfied);
        Assert.DoesNotContain(outcome.Diagnostics, item =>
            item.Contains("definition-of-done", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Orchestrator_reuses_the_exact_definition_of_done_host_snapshot_for_final_context()
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
            return new Dictionary<string, object?>
            {
                ["hostReady"] = projectionCalls != 4
            };
        }

        var orchestrator = new TaskOrchestrator(
            new ScriptedTaskPlanner(plan),
            new ScriptedRunExecutor([Envelope("run_work", RunOutcomeStatus.Succeeded)]),
            new EvidenceTaskAcceptanceEvaluator(),
            new DeterministicWorkContextCompiler(),
            ProjectHostState);

        var outcome = await orchestrator.RunAsync(Request("Retain the checked final state."));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Equal(OrchestrationStopReason.Complete, outcome.StopReason);
        Assert.True(outcome.DefinitionOfDone?.Satisfied);
        Assert.Equal("run_work", Assert.Single(outcome.RunOutcomes).Outcome.RunId);
        Assert.Contains(task.TaskId, outcome.State.CompletedTaskIds);
        Assert.Equal("run_work", Assert.Single(outcome.State.RunRefs).RunId);
        Assert.Equal(3, projectionCalls);
        Assert.True(Assert.IsType<bool>(outcome.WorkingContext.HostStateProjection["hostReady"]));
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
        var childDispatch = Assert.Single(outcome.RunOutcomes);
        Assert.Equal(RunOutcomeStatus.PartiallyComplete, childDispatch.Outcome.Status);
        Assert.Equal(false, Assert.Single(childDispatch.Receipts.Items)
            .Data["returnedOutcomeReceived"]);
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
    public void Definition_of_done_rejects_json_number_that_only_matches_after_rounding()
    {
        using var document = JsonDocument.Parse("0.100000000000000000000000000006");
        var task = Task("exact-numeric-host-state");
        var plan = Plan([task]) with
        {
            DefinitionOfDone =
            [
                new TaskAcceptanceRequirement(
                    TaskAcceptanceRequirementKind.HostState,
                    HostStateKey: "value",
                    HostStateValue: 0.1d)
            ]
        };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        state.CompletedTaskIds.Add(task.TaskId);
        state.RunRefs.Add(new RunRef(task.TaskId, "run_exact_numeric_host", RunOutcomeStatus.Succeeded, []));

        var result = DefinitionOfDoneEvaluator.Evaluate(
            plan,
            state,
            [Envelope("run_exact_numeric_host", RunOutcomeStatus.Succeeded)],
            new Dictionary<string, object?> { ["value"] = document.RootElement });

        Assert.False(result.Satisfied);
        Assert.Contains(result.Reasons, reason => reason.Contains("did not satisfy", StringComparison.Ordinal));
    }

    [Fact]
    public void Definition_of_done_accepts_identical_exact_json_number_tokens()
    {
        const string rawNumber = "0.100000000000000000000000000006";
        using var expected = JsonDocument.Parse(rawNumber);
        using var actual = JsonDocument.Parse(rawNumber);
        var task = Task("matching-exact-numeric-host-state");
        var plan = Plan([task]) with
        {
            DefinitionOfDone =
            [
                new TaskAcceptanceRequirement(
                    TaskAcceptanceRequirementKind.HostState,
                    HostStateKey: "value",
                    HostStateValue: expected.RootElement)
            ]
        };
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("test", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));
        state.CompletedTaskIds.Add(task.TaskId);
        state.RunRefs.Add(new RunRef(task.TaskId, "run_matching_numeric_host", RunOutcomeStatus.Succeeded, []));

        var result = DefinitionOfDoneEvaluator.Evaluate(
            plan,
            state,
            [Envelope("run_matching_numeric_host", RunOutcomeStatus.Succeeded)],
            new Dictionary<string, object?> { ["value"] = actual.RootElement });

        Assert.True(result.Satisfied);
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

    private static void AssertReadOnly<T>(IReadOnlyList<T> values, T addedValue)
    {
        var list = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(addedValue));
    }

    private static void AssertReadOnlyDictionary<TKey, TValue>(
        IReadOnlyDictionary<TKey, TValue> values,
        TKey addedKey,
        TValue addedValue)
        where TKey : notnull
    {
        var dictionary = Assert.IsAssignableFrom<IDictionary<TKey, TValue>>(values);
        Assert.True(dictionary.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => dictionary.Add(addedKey, addedValue));
    }

    private sealed class CountingTaskPlanner : ITaskPlanner
    {
        private readonly TaskGraphPlan _plan;

        public CountingTaskPlanner(TaskGraphPlan plan)
        {
            _plan = plan;
        }

        public int CreateCalls { get; private set; }

        public Task<TaskGraphPlan> CreatePlanAsync(
            TaskPlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return System.Threading.Tasks.Task.FromResult(_plan);
        }

        public Task<TaskGraphRefinement> RefinePlanAsync(
            TaskRefinementRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refinement was not expected.");
    }

    private sealed class CoordinatedRunExecutor : IRunExecutor
    {
        private readonly TaskCompletionSource<OutcomeEnvelope> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RunRequest? Request { get; private set; }

        public Task<OutcomeEnvelope> RunAsync(
            RunRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            Entered.TrySetResult();
            return _completion.Task;
        }

        public void Complete(OutcomeEnvelope outcome) => _completion.TrySetResult(outcome);
    }

    private sealed class CoordinatedAcceptanceEvaluator : ITaskAcceptanceEvaluator
    {
        private readonly TaskCompletionSource<TaskAcceptanceResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OutcomeEnvelope? Outcome { get; private set; }

        public Task<TaskAcceptanceResult> EvaluateAsync(
            TaskNode task,
            OutcomeEnvelope outcome,
            TaskAcceptanceContext context,
            CancellationToken cancellationToken = default)
        {
            Outcome = outcome;
            context.State.CompletedTaskIds.Add("forged-by-evaluator");
            Entered.TrySetResult();
            return _completion.Task;
        }

        public void Complete(TaskAcceptanceResult result) => _completion.TrySetResult(result);
    }

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

    private sealed class RequestMutatingThrowingRunExecutor : IRunExecutor
    {
        public bool MutationAttempted { get; private set; }

        public bool OuterMutationBlocked { get; private set; }

        public bool NestedMutationSucceeded { get; private set; }

        public Task<OutcomeEnvelope> RunAsync(
            RunRequest request,
            CancellationToken cancellationToken = default)
        {
            var context = (IDictionary<string, object?>)request.Context!;
            MutationAttempted = true;
            try
            {
                context["orchestration.taskId"] = "attacker_task";
                context["orchestration.childDispatchId"] = "attacker_dispatch";
                context["attacker.added"] = true;
            }
            catch (NotSupportedException)
            {
                OuterMutationBlocked = true;
            }

            var workingContext = (WorkContextSnapshot)context["orchestration.workingContext"]!;
            if (workingContext.HostStateProjection is IDictionary<string, object?> hostState)
            {
                hostState["attacker.nested"] = true;
                NestedMutationSucceeded = true;
            }

            return System.Threading.Tasks.Task.FromException<OutcomeEnvelope>(
                new InvalidOperationException("Mutated child request then failed."));
        }
    }

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
