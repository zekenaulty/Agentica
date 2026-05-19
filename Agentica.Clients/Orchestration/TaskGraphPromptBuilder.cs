using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Clients.Llm;
using Agentica.Orchestration.Planning;

namespace Agentica.Clients.Orchestration;

public static class TaskGraphPromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    static TaskGraphPromptBuilder()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static LlmRequest BuildInitialPlanRequest(
        TaskPlanningRequest request,
        LlmTaskPlannerOptions options) =>
        new(
            ModelId: options.ModelId,
            Messages:
            [
                new LlmMessage(LlmMessageRole.System, SystemInstruction),
                new LlmMessage(LlmMessageRole.User, BuildInitialPrompt(request))
            ],
            GenerationOptions: options.GenerationOptions,
            StructuredOutput: new LlmStructuredOutputOptions());

    public static LlmRequest BuildRefinementRequest(
        TaskRefinementRequest request,
        LlmTaskPlannerOptions options) =>
        new(
            ModelId: options.ModelId,
            Messages:
            [
                new LlmMessage(LlmMessageRole.System, SystemInstruction),
                new LlmMessage(LlmMessageRole.User, BuildRefinementPrompt(request))
            ],
            GenerationOptions: options.GenerationOptions,
            StructuredOutput: new LlmStructuredOutputOptions());

    private const string SystemInstruction =
        """
        You are the task-level planning layer for Agentica.Orchestration.
        Return JSON only. Do not wrap JSON in markdown.
        Plan task objectives, not tool calls.
        Never name Agentica tool ids, PlanSteps, or tool inputs.
        The orchestrator assigns one bounded task at a time to AgenticaRunner.
        AgenticaRunner handles tool-level planning and execution.
        Use a single-node task graph when the objective is small enough for one bounded Agentica run.
        Use a multi-node task graph when the objective needs decomposition, dependencies, acceptance gates, or adaptation.
        The graph must be acyclic. Dependencies must reference known task ids.
        Acceptance criteria must be evidence-checkable through outcome status, artifacts, receipts, or host state.
        Valid acceptance requirement kind values are exactly: OutcomeStatus, Artifact, Receipt, HostState.
        Do not invent requirement kinds such as ObjectiveVerifier, Verifier, ObjectiveStatus, Completion, or PhaseReport.
        Do not treat report prose, confidence, hypotheses, or hidden reasoning as proof.
        Refinements must be explicit graph mutations.
        Do not create recursive orchestration layers. Decomposition is bounded by the supplied policy.
        """;

    private static string BuildInitialPrompt(TaskPlanningRequest request) =>
        $$"""
        Create an Agentica.Orchestration task graph.
        Decide complexity by returning either one task or multiple tasks.
        Keep tasks coarse enough that each task is a bounded Agentica run.
        Do not include tool calls or step-by-step tool instructions.

        Objective:
        {{request.Request.Objective}}

        Origin:
        {{request.Request.Origin}}

        Request context:
        {{Serialize(request.Request.Context)}}

        Policy:
        {{Serialize(request.Policy)}}

        Required JSON shape:
        {
          "planId": "task_plan_001",
          "objective": "the larger objective",
          "tasks": [
            {
              "taskId": "task_001",
              "objective": "bounded task objective for AgenticaRunner",
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
        """;

    private static string BuildRefinementPrompt(TaskRefinementRequest request) =>
        $$"""
        Refine the current Agentica.Orchestration task graph.
        The latest Agentica run produced an OutcomeEnvelope, and task acceptance requires graph changes or blocking.
        Return explicit graph mutations only. Do not return a replacement full graph.
        Do not rewrite completed tasks.
        Do not add tool calls or PlanSteps.

        Original objective:
        {{request.Request.Objective}}

        Policy:
        {{Serialize(request.Policy)}}

        Current task graph:
        {{Serialize(request.CurrentPlan)}}

        Orchestration state:
        {{Serialize(new
        {
            request.State.OrchestrationId,
            request.State.ActiveTaskId,
            request.State.CompletedTaskIds,
            request.State.BlockedTaskIds,
            request.State.RefinementCount
        })}}

        Active task:
        {{Serialize(request.ActiveTask)}}

        Latest task acceptance:
        {{Serialize(request.Acceptance)}}

        Working context:
        {{Serialize(request.WorkingContext)}}

        Latest run outcome:
        {{Serialize(new
        {
            request.LatestOutcome.Outcome,
            request.LatestOutcome.Receipts,
            request.LatestOutcome.Details.Artifacts,
            request.LatestOutcome.Details.Observations,
            request.LatestOutcome.Details.ValidationIssues
        })}}

        Required JSON shape:
        {
          "reason": "short reason code or concise explanation",
          "mutations": [
            {
              "kind": "AddTask|ReplaceTask|RemoveTask|AddDependency|RemoveDependency|ReorderPriority|MarkTaskBlocked|MarkTaskAccepted|ReviseAcceptanceCriteria|ReviseDefinitionOfDone",
              "taskId": "existing_or_new_task_id",
              "task": null,
              "dependencyTaskId": null,
              "priority": null,
              "acceptanceRequirements": null,
              "definitionOfDone": null
            }
          ],
          "blockers": [],
          "requiresUserInput": false
        }
        """;

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);
}
