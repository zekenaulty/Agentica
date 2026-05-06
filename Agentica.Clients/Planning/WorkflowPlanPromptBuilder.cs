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
        Produce the next safe plan slice, not a speculative long workflow.
        """;

    private static string BuildInitialPrompt(PlanningRequest request) =>
        $$"""
        Create an initial Agentica workflow plan.
        Create only the next safe step or small safe slice.
        If a read/query tool can establish state before action, choose the read/query tool first.

        Objective:
        {{request.Request.Objective}}

        Origin:
        {{request.Request.Origin}}

        Request context:
        {{Serialize(request.Request.Context ?? new Dictionary<string, object?>())}}

        Tool catalog:
        {{Serialize(request.ToolDescriptors)}}

        Existing observations:
        {{Serialize(request.Observations)}}

        Existing receipts:
        {{Serialize(request.Receipts)}}

        Required JSON shape:
        {
          "planId": "plan_001",
          "description": "short operator-readable description",
          "steps": [
            {
              "stepId": "step_001",
              "toolId": "one supplied tool id",
              "kind": "Query|Action|PlannerAssist|Validation|Synthesis",
              "effect": "ReadOnly|WritesLocalState|ExternalSideEffect|Destructive|Unknown",
              "input": {},
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
        This refinement is an auditable thinking/planning turn. Do not include hidden chain-of-thought.
        Use a concise reason code, not prose.

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
            "planId": "plan_002",
            "description": "short operator-readable description",
            "steps": [
              {
                "stepId": "step_002",
                "toolId": "one supplied tool id",
                "kind": "Query|Action|PlannerAssist|Validation|Synthesis",
                "effect": "ReadOnly|WritesLocalState|ExternalSideEffect|Destructive|Unknown",
                "input": {},
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
