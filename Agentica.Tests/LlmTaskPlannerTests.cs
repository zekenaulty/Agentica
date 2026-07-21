using Agentica.Artifacts;
using Agentica.Clients.Llm;
using Agentica.Clients.Orchestration;
using Agentica.Events;
using Agentica.Execution;
using Agentica.Observations;
using Agentica.Orchestration;
using Agentica.Orchestration.Acceptance;
using Agentica.Orchestration.Context;
using Agentica.Orchestration.Execution;
using Agentica.Orchestration.Planning;
using Agentica.Outcomes;
using Agentica.Planning;
using Agentica.Requests;
using Agentica.Tools;

namespace Agentica.Tests;

public sealed class LlmTaskPlannerTests
{
    [Fact]
    public async Task Llm_task_planner_composes_through_orchestrator_and_in_process_agentica_runner()
    {
        var client = new FakeLlmClient(
            """
            {
              "planId": "task_plan_composition",
              "objective": "Complete a bounded Agentica task.",
              "tasks": [
                {
                  "taskId": "direct_agentica_run",
                  "objective": "Create a two-step workflow that queries state and then acts.",
                  "dependsOn": [],
                  "optional": false,
                  "priority": 1,
                  "maxRuns": 1,
                  "contextProjection": {},
                  "acceptanceRequirements": [
                    {
                      "kind": "OutcomeStatus",
                      "requiredOutcomeStatus": "Succeeded"
                    },
                    {
                      "kind": "Artifact",
                      "artifactKind": "action_result"
                    }
                  ]
                }
              ],
              "definitionOfDone": [
                {
                  "kind": "OutcomeStatus",
                  "requiredOutcomeStatus": "Succeeded"
                }
              ]
            }
            """);
        var taskPlanner = new LlmTaskPlanner(client);
        var eventSink = new InMemoryEventSink();
        var runExecutor = new InProcessAgenticaRunExecutor(
            _ => new DeterministicWorkflowPlanner(),
            _ => DemoTools.CreateCatalog(),
            eventSink,
            new DeterministicOutcomeReporter(),
            _ => PlanExhaustionCompletionEvaluator.Instance,
            _ => new ExecutionPolicy(MaxSteps: 10, MaxRefinements: 2));
        var orchestrator = new TaskOrchestrator(
            taskPlanner,
            runExecutor,
            new EvidenceTaskAcceptanceEvaluator(),
            new DeterministicWorkContextCompiler(),
            () => new Dictionary<string, object?>());

        var outcome = await orchestrator.RunAsync(new LargeTaskRequest(
            "Complete a bounded Agentica task.",
            RequestOrigin.User,
            new Dictionary<string, object?>()));

        Assert.Equal(OrchestrationStatus.Succeeded, outcome.Status);
        Assert.Single(outcome.RunOutcomes);
        Assert.Equal(RunOutcomeStatus.Succeeded, outcome.RunOutcomes[0].Outcome.Status);
        Assert.Contains(outcome.RunOutcomes[0].Details.Artifacts, artifact => artifact.Kind == "action_result");
        Assert.Equal(["direct_agentica_run"], outcome.State.CompletedTaskIds);
        Assert.Contains(eventSink.Events, item => item.Type == "run.succeeded");
    }

    [Fact]
    public async Task Llm_task_planner_distinguishes_max_tokens_truncation_from_plain_invalid_json()
    {
        var planner = new LlmTaskPlanner(new FakeLlmClient("{not-json", LlmFinishReason.MaxTokens));

        var exception = await Assert.ThrowsAsync<LlmTaskPlannerException>(() =>
            planner.CreatePlanAsync(new TaskPlanningRequest(
                new LargeTaskRequest(
                    "Create a task graph.",
                    RequestOrigin.User,
                    new Dictionary<string, object?>()),
                new OrchestrationPolicy())));

        Assert.Contains("truncated", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MaxTokens", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Llm_task_planner_parses_initial_task_graph()
    {
        var client = new FakeLlmClient(
            """
            {
              "planId": "task_plan_test",
              "objective": "Build persistence.",
              "tasks": [
                {
                  "taskId": "inspect",
                  "objective": "Inspect existing persistence code.",
                  "dependsOn": [],
                  "optional": false,
                  "priority": 1,
                  "maxRuns": 1,
                  "contextProjection": {},
                  "acceptanceRequirements": [
                    {
                      "kind": "OutcomeStatus",
                      "requiredOutcomeStatus": "Succeeded"
                    }
                  ]
                }
              ],
              "definitionOfDone": [
                {
                  "kind": "OutcomeStatus",
                  "requiredOutcomeStatus": "Succeeded"
                }
              ]
            }
            """);
        var planner = new LlmTaskPlanner(client);

        var plan = await planner.CreatePlanAsync(new TaskPlanningRequest(
            new LargeTaskRequest("Build persistence.", RequestOrigin.User, new Dictionary<string, object?>()),
            new OrchestrationPolicy()));

        Assert.Equal("task_plan_test", plan.PlanId);
        Assert.Single(plan.Tasks);
        Assert.Equal("inspect", plan.Tasks[0].TaskId);
        Assert.Equal(RunOutcomeStatus.Succeeded, plan.Tasks[0].AcceptanceRequirements[0].RequiredOutcomeStatus);
        Assert.Contains("task graph", client.Requests[0].Messages[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Llm_task_planner_rejects_prohibited_acceptance_kind_aliases()
    {
        var client = new FakeLlmClient(
            """
            {
              "planId": "task_plan_test",
              "objective": "Run ChessQuest phase.",
              "tasks": [
                {
                  "taskId": "phase_001",
                  "objective": "Execute a bounded phase.",
                  "dependsOn": [],
                  "optional": false,
                  "priority": 1,
                  "maxRuns": 1,
                  "contextProjection": {},
                  "acceptanceRequirements": [
                    {
                      "kind": "ObjectiveVerifier",
                      "artifactKind": "chessquest.phase_report"
                    }
                  ]
                }
              ],
              "definitionOfDone": [
                {
                  "kind": "OutcomeStatus",
                  "requiredOutcomeStatus": "Succeeded"
                }
              ]
            }
            """);
        var planner = new LlmTaskPlanner(client);

        await Assert.ThrowsAsync<LlmTaskPlannerException>(() => planner.CreatePlanAsync(new TaskPlanningRequest(
            new LargeTaskRequest("Run ChessQuest phase.", RequestOrigin.User, new Dictionary<string, object?>()),
            new OrchestrationPolicy())));
    }

    [Fact]
    public async Task Llm_task_planner_rejects_empty_acceptance_and_definition_of_done()
    {
        var emptyAcceptance = new LlmTaskPlanner(new FakeLlmClient(
            """
            {
              "planId": "empty_acceptance",
              "objective": "Invalid proof contract.",
              "tasks": [
                {
                  "taskId": "task",
                  "objective": "Attempt work.",
                  "dependsOn": [],
                  "optional": false,
                  "priority": 1,
                  "maxRuns": 1,
                  "contextProjection": {},
                  "acceptanceRequirements": []
                }
              ],
              "definitionOfDone": [
                { "kind": "OutcomeStatus", "requiredOutcomeStatus": "Succeeded" }
              ]
            }
            """));
        var emptyDefinitionOfDone = new LlmTaskPlanner(new FakeLlmClient(
            """
            {
              "planId": "empty_dod",
              "objective": "Invalid proof contract.",
              "tasks": [
                {
                  "taskId": "task",
                  "objective": "Attempt work.",
                  "dependsOn": [],
                  "optional": false,
                  "priority": 1,
                  "maxRuns": 1,
                  "contextProjection": {},
                  "acceptanceRequirements": [
                    { "kind": "OutcomeStatus", "requiredOutcomeStatus": "Succeeded" }
                  ]
                }
              ],
              "definitionOfDone": []
            }
            """));
        var request = new TaskPlanningRequest(
            new LargeTaskRequest("Invalid proof contract.", RequestOrigin.User, new Dictionary<string, object?>()),
            new OrchestrationPolicy());

        await Assert.ThrowsAsync<LlmTaskPlannerException>(() => emptyAcceptance.CreatePlanAsync(request));
        await Assert.ThrowsAsync<LlmTaskPlannerException>(() => emptyDefinitionOfDone.CreatePlanAsync(request));
    }

    [Fact]
    public async Task Llm_task_planner_rejects_semantically_invalid_task_graph()
    {
        var client = new FakeLlmClient(
            """
            {
              "planId": "task_plan_invalid",
              "objective": "Invalid graph.",
              "tasks": [
                {
                  "taskId": "a",
                  "objective": "Task A.",
                  "dependsOn": ["b"],
                  "optional": false,
                  "priority": 1,
                  "maxRuns": 1,
                  "contextProjection": {},
                  "acceptanceRequirements": [
                    {
                      "kind": "OutcomeStatus",
                      "requiredOutcomeStatus": "Succeeded"
                    }
                  ]
                },
                {
                  "taskId": "b",
                  "objective": "Task B.",
                  "dependsOn": ["a"],
                  "optional": false,
                  "priority": 2,
                  "maxRuns": 1,
                  "contextProjection": {},
                  "acceptanceRequirements": [
                    {
                      "kind": "OutcomeStatus",
                      "requiredOutcomeStatus": "Succeeded"
                    }
                  ]
                }
              ],
              "definitionOfDone": [
                {
                  "kind": "OutcomeStatus",
                  "requiredOutcomeStatus": "Succeeded"
                }
              ]
            }
            """);
        var planner = new LlmTaskPlanner(client);

        await Assert.ThrowsAsync<TaskGraphValidationException>(() => planner.CreatePlanAsync(new TaskPlanningRequest(
            new LargeTaskRequest("Invalid graph.", RequestOrigin.User, new Dictionary<string, object?>()),
            new OrchestrationPolicy())));
    }


    [Fact]
    public async Task Llm_task_planner_parses_refinement_mutations()
    {
        var client = new FakeLlmClient(
            """
            {
              "reason": "new_dependency_discovered",
              "mutations": [
                {
                  "kind": "AddTask",
                  "taskId": "design_attempts",
                  "task": {
                    "taskId": "design_attempts",
                    "objective": "Design execution attempt model.",
                    "dependsOn": ["inspect"],
                    "optional": false,
                    "priority": 2,
                    "maxRuns": 1,
                    "contextProjection": {},
                    "acceptanceRequirements": [
                      {
                        "kind": "OutcomeStatus",
                        "requiredOutcomeStatus": "Succeeded"
                      }
                    ]
                  }
                }
              ],
              "blockers": [],
              "requiresUserInput": false
            }
            """);
        var planner = new LlmTaskPlanner(client);
        var task = Task("inspect");
        var plan = Plan([task]);
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("Build persistence.", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));

        var refinement = await planner.RefinePlanAsync(new TaskRefinementRequest(
            new LargeTaskRequest("Build persistence.", RequestOrigin.User, new Dictionary<string, object?>()),
            plan,
            state,
            task,
            Envelope("run_test", RunOutcomeStatus.Succeeded),
            new TaskAcceptanceResult(TaskAcceptanceStatus.InvalidatedPlan, ["Need attempts."], []),
            state.WorkingContext,
            new OrchestrationPolicy()));

        Assert.Equal("new_dependency_discovered", refinement.Reason);
        Assert.Single(refinement.Mutations);
        Assert.Equal(TaskGraphMutationKind.AddTask, refinement.Mutations[0].Kind);
        Assert.Equal("design_attempts", refinement.Mutations[0].Task?.TaskId);
    }

    [Fact]
    public async Task Llm_task_planner_rejects_removed_proof_authority_mutations()
    {
        var planner = new LlmTaskPlanner(new FakeLlmClient(
            """
            {
              "reason": "model_claims_acceptance",
              "mutations": [
                {
                  "kind": "MarkTaskAccepted",
                  "taskId": "inspect"
                }
              ],
              "blockers": [],
              "requiresUserInput": false
            }
            """));
        var task = Task("inspect");
        var plan = Plan([task]);
        var state = new OrchestrationState(
            "orchestration_test",
            new WorkContextSnapshot("Build persistence.", null, [], [], [], [], [], [], [], new Dictionary<string, object?>(), DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<LlmTaskPlannerException>(() => planner.RefinePlanAsync(new TaskRefinementRequest(
            new LargeTaskRequest("Build persistence.", RequestOrigin.User, new Dictionary<string, object?>()),
            plan,
            state,
            task,
            Envelope("run_test", RunOutcomeStatus.Succeeded),
            new TaskAcceptanceResult(TaskAcceptanceStatus.InvalidatedPlan, ["Need repair."], []),
            state.WorkingContext,
            new OrchestrationPolicy())));
    }

    private static TaskGraphPlan Plan(IReadOnlyList<TaskNode> tasks) =>
        new(
            "plan_test",
            "Build persistence.",
            tasks,
            [new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Succeeded)],
            DateTimeOffset.UtcNow);

    private static TaskNode Task(string taskId) =>
        new(
            taskId,
            $"Objective for {taskId}.",
            [],
            Optional: false,
            Priority: 1,
            MaxRuns: 1,
            new Dictionary<string, object?>(),
            [new TaskAcceptanceRequirement(TaskAcceptanceRequirementKind.OutcomeStatus, RunOutcomeStatus.Succeeded)]);

    private static OutcomeEnvelope Envelope(string runId, RunOutcomeStatus status) =>
        new(
            new RunOutcome(
                runId,
                status,
                status == RunOutcomeStatus.Succeeded ? StopReason.Complete : StopReason.ToolFailure,
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

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly string _json;
        private readonly LlmFinishReason _finishReason;

        public FakeLlmClient(
            string json,
            LlmFinishReason finishReason = LlmFinishReason.Stop)
        {
            _json = json;
            _finishReason = finishReason;
        }

        public List<LlmRequest> Requests { get; } = [];

        public Task<LlmResponse> GenerateAsync(
            LlmRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return System.Threading.Tasks.Task.FromResult(new LlmResponse(
                "fake",
                request.ModelId,
                _json,
                StructuredJson: _json,
                FinishReason: _finishReason));
        }
    }
}
