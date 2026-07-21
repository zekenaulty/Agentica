# Codex Goal: Agentica.Clients LLM Planning Slice

> Lifecycle: **Implemented** · Completion: **100%** · Canonical status: [Agentica Product Status And Goal Xref](Agentica.ProductStatus.md)

## Mission

Implement the first `Agentica.Clients` slice.

This slice adds the provider/model call surface for Agentica and implements a Google Gemini-backed planner path with Gemini 2.5 thinking support.

The target is not to turn Agentica into an LLM wrapper. The target is to bridge LLM inference into Agentica planning contracts:

- `WorkflowPlan`
- refined `WorkflowPlan` driven by observations
- optionally `OutcomeReport` later

The runtime still validates all plans, tool effects, tool ids, mutation boundaries, receipts, and terminal outcomes.

Core rule:

```text
The model proposes.
Agentica validates.
Tools execute.
Receipts prove.
Outcome reports narrate but never prove.
```

## Architecture Boundary

```text
Agentica
  owns runtime contracts:
  RunRequest, WorkflowPlan, PlanRefinement, ToolCatalog,
  Observation, Receipt, OutcomeEnvelope, IWorkflowPlanner, IOutcomeReporter

Agentica.Clients
  owns model/provider call surface:
  ILlmClient, LlmRequest, LlmResponse, thinking options,
  Gemini adapter, Gemini-backed planner/reporter implementations
```

The LLM reasons and plans over Agentica surfaces. `Agentica.Clients` provides the bridge between model inference and Agentica tracking/execution.

The LLM is not "chatting" with the app. It receives an Agentica-shaped problem surface: objective, tool descriptors, observations, receipts, and expected output schema. It returns structured runtime proposals.

## Non-Negotiable Constraints

1. Keep the solution small.
2. Add `Agentica.Clients` only if it does not already exist.
3. Do not create `Agentica.Core`, `Agentica.Abstractions`, `Agentica.Runtime`, `Agentica.Storage`, `Agentica.Persistence`, `Agentica.Tools`, or other project-per-concern layers.
4. `Agentica` must not reference `Agentica.Clients`.
5. `Agentica.Clients` may reference `Agentica`.
6. `Agentica.Lab` may reference `Agentica.Clients` for boundary-host wiring.
7. Provider SDK types must not leak into `Agentica`.
8. Provider SDK types must not appear in `WorkflowPlan`, `PlanStep`, `PlanRefinement`, `OutcomeEnvelope`, `Receipt`, `Observation`, or `ToolDescriptor`.
9. Do not implement MCP.
10. Do not implement storage, queues, auth, deployment, vector memory, adaptive learning, multi-agent orchestration, or a microservice host.
11. Do not import product-specific vocabulary from BookForge, Nanda, MazeQuest, authoring, scenes, books, branches, files, or workspaces into Agentica contracts.

## Official Gemini Assumptions

Verify against official docs/package metadata before coding.

Expected baseline:

- Prefer Google's official `Google.GenAI` package.
- Prefer Gemini Developer API for the first slice.
- Default model: `gemini-2.5-flash`.
- Allow model override.
- Gemini 2.5 thinking uses `thinkingBudget`, not Gemini 3 `thinkingLevel`.
- Support dynamic thinking with `thinkingBudget = -1`.
- Support explicit thinking budget with a positive integer.
- Support thinking off where the model supports it.
- Support thought summaries when requested with `includeThoughts`.

Thought summaries are diagnostics. They are not proof. They are not raw private reasoning.

## Required Project Shape

```text
Agentica.slnx
  Agentica/
  Agentica.Lab/
  Agentica.Clients/
  Agentica.Tests/
```

Do not add additional runtime projects.

## Required Agentica.Clients Concepts

Suggested folders:

```text
Agentica.Clients/
  Llm/
    ILlmClient.cs
    LlmRequest.cs
    LlmResponse.cs
    LlmMessage.cs
    LlmMessageRole.cs
    LlmGenerationOptions.cs
    LlmThinkingOptions.cs
    LlmStructuredOutputOptions.cs
    LlmThoughtSummary.cs
    LlmUsage.cs
    LlmFinishReason.cs
    LlmClientException.cs

  Gemini/
    GeminiLlmClient.cs
    GeminiClientOptions.cs
    GeminiModelId.cs
    GeminiResponseMapper.cs

  Planning/
    LlmWorkflowPlanner.cs
    LlmWorkflowPlannerOptions.cs
    WorkflowPlanPromptBuilder.cs
    WorkflowPlanJsonContract.cs
    PlanRefinementJsonContract.cs
```

Keep the first-slice contracts small, serializable, testable, and obvious.

## Provider-Neutral Call Surface

Create an `ILlmClient` that can do a single generation request:

```csharp
public interface ILlmClient
{
    Task<LlmResponse> GenerateAsync(
        LlmRequest request,
        CancellationToken cancellationToken);
}
```

It should support:

- model id
- system/developer instructions
- user/request content
- structured output request
- temperature
- max output tokens
- thinking options
- response text
- structured JSON text
- thought summaries if available
- usage metadata if available
- provider/model metadata
- finish reason if available

Do not introduce streaming in this slice.

## Gemini Adapter

Implement `GeminiLlmClient`.

It must:

1. Use the official `Google.GenAI` package if viable.
2. Read API key from explicit options first, then environment/config.
3. Never log or print the API key.
4. Support `gemini-2.5-flash` and configurable model ids.
5. Support `LlmThinkingOptions`.
6. Map thinking options to Gemini 2.5 `thinkingBudget`.
7. Support `includeThoughts`.
8. Capture thought summary parts when returned.
9. Capture output text.
10. Capture usage, including thinking token count if exposed by the SDK.
11. Fail with clear provider errors when credentials are missing.
12. Avoid provider SDK types in public Agentica runtime contracts.

## LLM Planner

Implement `LlmWorkflowPlanner` in `Agentica.Clients.Planning`.

It should implement Agentica's existing `IWorkflowPlanner`.

It must:

1. Accept an `ILlmClient`.
2. Accept planner options such as model id, thinking budget, include thoughts, and max output tokens.
3. Build a model prompt from `RunRequest`, `ToolCatalog` descriptors, observations, and receipts.
4. Ask the model for structured JSON only.
5. Convert structured JSON into Agentica runtime contracts.
6. Produce an initial `WorkflowPlan`.
7. Produce a refined `WorkflowPlan` after an `Observation`.
8. Never execute tools.
9. Never skip runtime validation.
10. Never invent receipts.
11. Never mark success.

The planner may choose and sequence tools. Legality is enforced by Agentica runtime validation.

## CLI Behavior

Deterministic default must still work:

```powershell
dotnet run --project Agentica.Lab -- run "Create a two-step workflow that queries state and then acts"
```

Add model-backed planning flags:

```powershell
dotnet run --project Agentica.Lab -- run "Create a two-step workflow that queries state and then acts" --planner gemini --model gemini-2.5-flash --thinking-budget 4096 --include-thoughts
```

Support dynamic thinking:

```powershell
dotnet run --project Agentica.Lab -- run "Create a two-step workflow that queries state and then acts" --planner gemini --model gemini-2.5-flash --thinking-budget dynamic --include-thoughts
```

If no Gemini API key is available, the Lab command must fail clearly and safely:

```text
Gemini planner requested, but no Gemini API key was configured.
```

No secret values should ever be printed.

## Tests

Use fast unit tests with fake clients wherever possible.

Add or update tests proving:

1. `Agentica` does not reference `Agentica.Clients`.
2. `Agentica.Clients` references `Agentica`.
3. `ILlmClient` can return a structured response without provider SDK types leaking.
4. `LlmWorkflowPlanner` maps valid JSON into a `WorkflowPlan`.
5. `LlmWorkflowPlanner` maps valid refinement JSON into an explicit refined plan.
6. Invalid planner JSON fails before tool execution.
7. Unknown tool ids from model output fail before execution.
8. Mutation-capable model-produced steps remain subject to runtime validation.
9. Deterministic planner path still passes.
10. Gemini thinking options map to Gemini 2.5 thinking budget settings.
11. Thought summaries are diagnostics, not proof.
12. Missing API key produces a clear provider configuration error.
13. Unit tests do not require network access.

Optional real Gemini integration test should skip unless credentials are present.

## Verification Commands

Run before completion:

```powershell
dotnet build Agentica.slnx
dotnet test Agentica.slnx
dotnet run --project Agentica.Lab -- run "Create a two-step workflow that queries state and then acts"
```

If Gemini credentials are present, also run:

```powershell
dotnet run --project Agentica.Lab -- run "Create a two-step workflow that queries state and then acts" --planner gemini --model gemini-2.5-flash --thinking-budget 4096 --include-thoughts
```

If credentials are not present, run the Gemini command and verify the error is clear and does not print secrets.

## Completion Condition

This goal is complete only when:

1. `dotnet build Agentica.slnx` passes.
2. `dotnet test Agentica.slnx` passes.
3. Deterministic CLI run still works.
4. `Agentica.Clients` exists and is the only new provider/runtime project.
5. `Agentica.Clients` references `Agentica`.
6. `Agentica` does not reference `Agentica.Clients`.
7. `Google.GenAI` or chosen minimal provider mechanism is isolated to `Agentica.Clients`.
8. Provider SDK types do not leak into Agentica runtime contracts.
9. `ILlmClient` exists and supports provider-neutral generation.
10. `LlmThinkingOptions` exists and can express Gemini 2.5 thinking budget and thought summary inclusion.
11. `GeminiLlmClient` supports `gemini-2.5-flash` and configurable model ids.
12. `LlmWorkflowPlanner` can produce a `WorkflowPlan` from model-style structured JSON.
13. `LlmWorkflowPlanner` can produce an explicit refined plan from observation-style structured JSON.
14. Invalid model JSON fails before execution.
15. Unknown model-produced tool ids fail before execution.
16. Mutation-capable model-produced steps remain subject to runtime validation.
17. Thought summaries are captured as diagnostics only.
18. Missing API credentials fail clearly and safely.
19. Unit tests cover planner mapping and adapter configuration without network.
20. `docs/Agentica.Clients.GoalStatus.md` contains milestone logs, steelman review, verification evidence, known limitations, and deferred work.
