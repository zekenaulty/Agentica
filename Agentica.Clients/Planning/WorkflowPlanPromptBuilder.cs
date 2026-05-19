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

    public static LlmRequest BuildInitialPlanRepairRequest(
        LlmRequest originalRequest,
        string invalidResponse,
        string failureMessage,
        int attempt,
        LlmPlannerOptions options) =>
        BuildRepairRequest(
            originalRequest,
            invalidResponse,
            failureMessage,
            attempt,
            options,
            repairKind: "initial_plan",
            requiredTopLevelShape: "planId, description, steps, completionCondition");

    public static LlmRequest BuildRefinementRepairRequest(
        LlmRequest originalRequest,
        string invalidResponse,
        string failureMessage,
        int attempt,
        LlmPlannerOptions options) =>
        BuildRepairRequest(
            originalRequest,
            invalidResponse,
            failureMessage,
            attempt,
            options,
            repairKind: "refinement",
            requiredTopLevelShape: "fromPlanId, reason, evidence, refinedPlan");

    private const string SystemInstruction =
        """
        You are the planning layer for Agentica.
        Return JSON only. Do not wrap JSON in markdown.
        Use only the supplied tool ids.
        Request context keys, receipt kinds, artifact kinds, and agentica.* context entries are not tools unless their exact id appears in the supplied tool catalog.
        The model proposes plans; Agentica validates and executes them.
        Never claim success, invent receipts, or describe a tool result that has not happened.
        Mutation-capable steps must be Action steps and must declare the matching tool effect.
        Read-only queries are for missing public preconditions, not reassurance.
        If current public context is sufficient to choose a bounded action, prefer the action.
        Do not query merely to reconfirm unchanged state.
        Do not spend consecutive planning turns on read-only context unless the latest observation created a new uncertainty.
        Prefer a host-provided compound action over repeated equivalent single-action steps when the tool descriptor and public context say the compound action is bounded, validated, and uses already exposed state.
        A context-expansion slice may batch multiple independent Query steps with effect ReadOnly using the same batchId only when those tools answer genuinely missing independent public facts, up to the policy maxBatchSize/maxParallelism exposed in the tool surface.
        Use tool contextHint data to choose complementary query tools that can be batched together.
        If a tool descriptor includes cooldown data, treat that cooldown as part of the execution surface. Do not call the same cooldown scope again until the descriptor says it is available.
        A cooldown refusal means the previous receipt/observation is still the relevant public context; use existing public context, choose a different non-redundant information source, or take a bounded action that can change state.
        Current planning constraints are authoritative public context for remaining step budget, refinement budget, timeout pressure, cooldown posture, batching limits, and allowed effects.
        When current planning constraints show moderate, high, critical, or exhausted pressure, avoid context-only recursion; choose a bounded action, completion-check action, or explicit blocker unless one specific missing public precondition prevents action.
        Do not start a context-expansion batch if it cannot change the next action decision before budget or time runs out.
        Use batchId only for independent ReadOnly Query steps that can safely run together.
        Do not batch a query when its input depends on another query result in the same slice; use dependsOn and a later step instead.
        Never batch mutation-capable steps.
        Use dependsOn only for execution ordering.
        A dependsOn value must reference either an earlier step in the same submitted plan slice or a step id listed in executionContext.completedStepIds.
        Do not put receipt ids, observation ids, artifact ids, plan ids, or unknown prior-step guesses in dependsOn.
        Use existing receipts, observations, artifacts, and request context as evidence/context; they are not automatically dependencies.
        If projected context frames are present, treat them as host-compiled current public context for this planning turn.
        Prefer the newest projected context frame over stale request context when they describe the same public state.
        Projected context frames may include operator/cockpit policy hints, loop signals, risk posture, and tool affordance guidance; use them as public planning evidence.
        Produce the next bounded executable slice. Do not speculate beyond available public context, but do not delay action when a bounded action is available.
        A planning slice should normally either gather genuinely missing independent context or perform a bounded action.
        Avoid read-only slices when no new uncertainty has appeared since the last observation.
        A bounded executable slice may contain several independent read-only context queries in one batch, or 2-3 dependent steps when preconditions are already established by request context or prior receipts.
        If preconditions are uncertain, emit a read-only context-expansion batch only when complementary query tools can establish genuinely missing facts; otherwise emit one query/read step or one blocker-handling step.
        If request context includes campaign.progress, treat it as scoped carry-forward evidence, not as proof of new tool results.
        Every plan step must include public execution intent.
        Execution intent is not hidden chain-of-thought. It is an operator-facing explanation of action, public rationale, and expected outcome.
        Intent rationale must be justified from public request context, tool descriptors, observations, receipts, artifacts, validation issues, or host-provided public context.
        Do not include raw private reasoning, provider thought summaries, system/developer instructions, hidden oracle state, future receipts, secrets, or speculative facts as intent.
        Keep intent fields concise.
        """;

    private static string BuildInitialPrompt(PlanningRequest request) =>
        $$"""
        Create an initial Agentica workflow plan.
        Create only the next bounded executable step or small executable slice.
        Use a read-only context-expansion batch only when several independent query tools are needed to establish missing state, legality, risk, progress, or preconditions before action.
        Do not query for reassurance when the supplied public context already supports a bounded action.
        Prefer 2-3 dependent steps only when each later step is clearly dependent on earlier steps or on already-proven context.
        Use dependsOn only when execution ordering requires it under the system dependency rule.
        Keep mutation-capable steps sequential and unbatched.
        If multiple read/query tools can establish complementary facts before action, give them the same batchId only when they are independent Query + ReadOnly steps.
        If public context is sufficient to act, choose the bounded action instead of another read/query step.

        Objective:
        {{request.Request.Objective}}

        Origin:
        {{request.Request.Origin}}

        Request context:
        {{Serialize(request.Request.Context ?? new Dictionary<string, object?>())}}

        Projected context frames:
        {{Serialize(request.ContextFrames)}}

        Current planning constraints:
        {{Serialize(request.ToolSurface?.PolicySummary ?? new Dictionary<string, object?>())}}

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
              "reason": "compatibility fallback; concise rationale if intent is unavailable",
              "intent": {
                "action": "what Agentica is about to do",
                "rationale": "why this step is justified from public context/evidence",
                "expectedOutcome": "what receipt, observation, artifact, validation result, or decision this step should produce"
              }
            }
          ],
          "completionCondition": "what must be true before the run can finish"
        }
        """;

    private static string BuildRefinementPrompt(PlanningRequest request, Observation observation) =>
        $$"""
        Refine the Agentica workflow plan using the new observation.
        Create only the next bounded executable step or small executable slice justified by the observation.
        Prefer a read-only context-expansion batch only when the latest observation leaves several independent facts unknown.
        Do not spend this refinement on read-only context unless the latest observation created a new uncertainty or the current public context is insufficient for a bounded action.
        Prefer 2-3 dependent steps only when the observation or request context proves the needed preconditions.
        Use dependsOn only for valid sequential execution dependencies and batchId only for independent read-only query steps.
        Keep mutation-capable steps sequential and unbatched.
        This refinement is an auditable thinking/planning turn. Do not include hidden chain-of-thought.
        Use a concise reason code, not prose.
        Use observation only when no more specific reason code fits. Prefer the most specific accurate reason.
        For refined steps, intent.rationale must explicitly connect the new step to the latest public observation or receipt.
        Do not say "continue" or "handle observation" unless the rationale names the public blocker, uncertainty, or evidence that changed the plan.

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

        Projected context frames:
        {{Serialize(request.ContextFrames)}}

        Current planning constraints:
        {{Serialize(request.ToolSurface?.PolicySummary ?? new Dictionary<string, object?>())}}

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
                "reason": "compatibility fallback; concise rationale if intent is unavailable",
                "intent": {
                  "action": "what Agentica is about to do",
                  "rationale": "why this step follows from the latest public observation or receipt",
                  "expectedOutcome": "what receipt, observation, artifact, validation result, or decision this step should produce"
                }
              }
            ],
            "completionCondition": "what must be true before the run can finish"
          }
        }
        """;

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static LlmRequest BuildRepairRequest(
        LlmRequest originalRequest,
        string invalidResponse,
        string failureMessage,
        int attempt,
        LlmPlannerOptions options,
        string repairKind,
        string requiredTopLevelShape)
    {
        var messages = originalRequest.Messages
            .Concat(
            [
                new LlmMessage(
                    LlmMessageRole.Assistant,
                    TruncateForRepair(invalidResponse, options.MaxRepairPayloadCharacters)),
                new LlmMessage(
                    LlmMessageRole.User,
                    $$"""
                    Your previous Agentica planning response could not be parsed.

                    Repair attempt:
                    {{attempt}}

                    Repair kind:
                    {{repairKind}}

                    Parser/contract failure:
                    {{TruncateForRepair(failureMessage, options.MaxRepairPayloadCharacters)}}

                    Required top-level JSON fields:
                    {{requiredTopLevelShape}}

                    Return one replacement JSON object only.
                    Do not wrap it in markdown.
                    Do not explain the repair.
                    Do not add prose before or after the JSON.
                    Do not invent receipts, observations, artifacts, hidden state, or tool results.
                    Use only tool ids from the tool catalog in the previous user message.
                    Preserve the latest public observation and execution context from the previous user message.
                    Include concise public execution intent on every refined step.
                    """)
            ])
            .ToArray();

        var metadata = originalRequest.Metadata?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        metadata["agentica.planner.repairKind"] = repairKind;
        metadata["agentica.planner.repairAttempt"] = attempt.ToString();

        return originalRequest with
        {
            Messages = messages,
            Metadata = metadata
        };
    }

    private static string TruncateForRepair(string value, int maxCharacters)
    {
        if (maxCharacters <= 0 || value.Length <= maxCharacters)
        {
            return value;
        }

        return value[..maxCharacters] + "\n...[truncated]";
    }
}
