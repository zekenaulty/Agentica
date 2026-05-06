using System.Text.Json;
using System.Text.Json.Serialization;
using Agentica.Clients.Llm;
using Agentica.Observations;
using Agentica.Planning;

namespace Agentica.Clients.Planning;

public static class WorkflowPlanPromptBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    static WorkflowPlanPromptBuilder()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static LlmRequest BuildInitialPlanRequest(
        PlanningRequest request,
        LlmPlannerOptions options)
    {
        return new LlmRequest(
            ModelId: options.ModelId,
            Messages:
            [
                new LlmMessage(LlmMessageRole.System, SystemInstruction),
                new LlmMessage(LlmMessageRole.User, BuildInitialPrompt(request))
            ],
            GenerationOptions: options.GenerationOptions,
            StructuredOutput: new LlmStructuredOutputOptions());
    }

    public static LlmRequest BuildRefinementRequest(
        PlanningRequest request,
        Observation observation,
        LlmPlannerOptions options)
    {
        return new LlmRequest(
            ModelId: options.ModelId,
            Messages:
            [
                new LlmMessage(LlmMessageRole.System, SystemInstruction),
                new LlmMessage(LlmMessageRole.User, BuildRefinementPrompt(request, observation))
            ],
            GenerationOptions: options.GenerationOptions,
            StructuredOutput: new LlmStructuredOutputOptions());
    }

    private const string SystemInstruction =
        """
        You are the planning layer for Agentica.
        Return JSON only. Do not wrap JSON in markdown.
        Use only the supplied tool ids.
        The model proposes plans; Agentica validates and executes them.
        Never claim success, invent receipts, or describe a tool result that has not happened.
        Mutation-capable steps must be Action steps and must declare the matching tool effect.
        Prefer query/read tools before mutation-capable tools when state or preconditions are unknown.
        Use batchId only for independent ReadOnly Query steps that can safely run together.
        Never batch mutation-capable steps.
        Use dependsOn only for execution ordering.
        A dependsOn value must reference either an earlier step in the same submitted plan slice or a step id listed in executionContext.completedStepIds.
        Do not put receipt ids, observation ids, artifact ids, plan ids, or unknown prior-step guesses in dependsOn.
        Use existing receipts, observations, artifacts, and request context as evidence/context; they are not automatically dependencies.
        Produce the next safe plan slice, not a speculative long workflow.
        A safe slice may contain 2-3 dependent steps when preconditions are already established by request context or prior receipts.
        If preconditions are uncertain, emit one query/read step or one blocker-handling step.
        If request context includes campaign.progress, treat it as scoped carry-forward evidence, not as proof of new tool results.
        """;

    private static string BuildInitialPrompt(PlanningRequest request) =>
        $$"""
        Create an initial Agentica workflow plan.
        Create only the next safe step or small safe slice.
        Prefer 2-3 steps only when each later step is clearly dependent on earlier steps or on already-proven context.
        Use dependsOn only when execution ordering requires it under the system dependency rule.
        Keep mutation-capable steps sequential and unbatched.
        If a read/query tool can establish state before action, choose the read/query tool first.

        Objective:
        {{request.Request.Objective}}

        Origin:
        {{request.Request.Origin}}

        Request context:
        {{Serialize(request.Request.Context ?? new Dictionary<string, object?>())}}

        Execution context:
        {{Serialize(request.ExecutionContext)}}

        Tool catalog:
        {{Serialize(request.ToolDescriptors)}}

        Existing observations:
        {{Serialize(request.Observations)}}

        Existing receipts:
        {{Serialize(request.Receipts)}}

        Required JSON shape:
        {
          "planId": "unique plan id for this plan slice",
          "description": "short operator-readable description",
          "steps": [
            {
              "stepId": "step_001",
              "toolId": "one supplied tool id",
              "kind": "Query|Action|PlannerAssist|Validation|Synthesis",
              "effect": "ReadOnly|WritesLocalState|ExternalSideEffect|Destructive|Unknown",
              "input": {},
              "dependsOn": [],
              "batchId": null,
              "reason": "why this step belongs in the plan"
            }
          ],
          "completionCondition": "what must be true before the run can finish"
        }
        """;

    private static string BuildRefinementPrompt(PlanningRequest request, Observation observation) =>
        $$"""
        Refine the Agentica workflow plan using the new observation.
        Create only the next safe step or small safe slice justified by the observation.
        Prefer 2-3 steps only when the observation or request context proves the needed preconditions.
        Use dependsOn only for valid sequential execution dependencies and batchId only for independent read-only query steps.
        Keep mutation-capable steps sequential and unbatched.
        This refinement is an auditable thinking/planning turn. Do not include hidden chain-of-thought.
        Use a concise reason code, not prose.
        Use observation only when no more specific reason code fits. Prefer the most specific accurate reason.

        Allowed refinement reason codes:
        - observation: ordinary update from new evidence
        - blocked: a receipt/refusal/blocker must be handled
        - ambiguous_action: legal options exist but no action is clearly dominant
        - low_confidence: current state is insufficient to safely act
        - conflicting_signals: observations or tool guidance conflict
        - completion_check: verify whether the task is already complete or can complete now
        - continue: completion is not proven and another bounded slice is needed
        - resource_risk: health, energy, cost, or risk makes blind action unsafe
        - retry_unblock: a retry attempt is trying to clear the previous blocker

        Objective:
        {{request.Request.Objective}}

        Origin:
        {{request.Request.Origin}}

        Request context:
        {{Serialize(request.Request.Context ?? new Dictionary<string, object?>())}}

        Execution context:
        {{Serialize(request.ExecutionContext)}}

        Tool catalog:
        {{Serialize(request.ToolDescriptors)}}

        New observation:
        {{Serialize(observation)}}

        Existing observations:
        {{Serialize(request.Observations)}}

        Existing receipts:
        {{Serialize(request.Receipts)}}

        Required JSON shape:
        {
          "fromPlanId": "previous plan id if known",
          "reason": "observation|blocked|ambiguous_action|low_confidence|conflicting_signals|completion_check|continue|resource_risk|retry_unblock",
          "evidence": [
            {
              "kind": "observation",
              "refId": "{{observation.ObservationId}}"
            }
          ],
          "refinedPlan": {
            "planId": "unique plan id for this refined plan slice",
            "description": "short operator-readable description",
            "steps": [
              {
                "stepId": "step_002",
                "toolId": "one supplied tool id",
                "kind": "Query|Action|PlannerAssist|Validation|Synthesis",
                "effect": "ReadOnly|WritesLocalState|ExternalSideEffect|Destructive|Unknown",
                "input": {},
                "dependsOn": [],
                "batchId": null,
                "reason": "why this step follows the observation"
              }
            ],
            "completionCondition": "what must be true before the run can finish"
          }
        }
        """;

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);
}
