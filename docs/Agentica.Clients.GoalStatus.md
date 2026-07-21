# Agentica.Clients Goal Status

> Lifecycle: **Historical** · Completion: **95%** · Canonical status: [Agentica Product Status And Goal Xref](Agentica.ProductStatus.md)
>
> Rename note: `Agentica.CLI` was renamed to `Agentica.Lab`; legacy names below are retained as historical evidence.

Live status log for `docs/CodexGoal.Agentica.Clients.LlmPlanning.md`.

## Milestone 0: Orientation And Baseline

Status: complete

Baseline project shape:

```text
Agentica.slnx
  Agentica/        runtime package
  Agentica.CLI/    console host
  Agentica.Tests/  test project
```

Alignment docs read:

- `README.md`
- `Agentica.ReadMe.md`
- `docs/Agentica.ObjectGraph.md`
- `docs/Agentica.Design.Decisions.md`
- `docs/Agentica.GoalStatus.md`

Baseline verification:

```text
dotnet build Agentica.slnx
Build succeeded.
0 Warning(s)
0 Error(s)

dotnet test Agentica.slnx
Passed! - Failed: 0, Passed: 10, Skipped: 0, Total: 10
```

Current slice state:

- Deterministic proof slice exists.
- `Agentica` owns runtime contracts and execution.
- `Agentica.Clients` does not exist yet.
- No LLM provider adapter exists yet.
- No MCP, storage, queue, auth, deployment, vector memory, or real LLM implementation exists.

## Milestone 1: Agentica.Clients Project

Status: complete

Implemented:

- Added `Agentica.Clients`.
- Added `Agentica.Clients` to `Agentica.slnx`.
- Added reference direction: `Agentica.Clients -> Agentica`.
- Added host/test references: `Agentica.CLI -> Agentica.Clients`, `Agentica.Tests -> Agentica.Clients`.
- Added `Google.GenAI` package version `1.6.1` only to `Agentica.Clients`.

Boundary rule preserved:

- `Agentica` does not reference `Agentica.Clients`.
- No `Agentica.Core`, `Agentica.Runtime`, `Agentica.Abstractions`, `Agentica.Storage`, `Agentica.Tools`, or similar project split was added.

## Milestone 2: Provider-Neutral LLM Contracts

Status: complete

Added `Agentica.Clients.Llm`:

- `ILlmClient`
- `LlmRequest`
- `LlmResponse`
- `LlmMessage`
- `LlmMessageRole`
- `LlmGenerationOptions`
- `LlmThinkingOptions`
- `LlmStructuredOutputOptions`
- `LlmThoughtSummary`
- `LlmUsage`
- `LlmFinishReason`
- `LlmClientException`

Design notes:

- Provider-neutral contracts do not expose Google SDK types.
- Thought summaries are represented as diagnostic model output, not proof.

## Milestone 3: Gemini Adapter

Status: complete with external provider availability caveat

Added `Agentica.Clients.Gemini`:

- `GeminiLlmClient`
- `GeminiClientOptions`
- `GeminiModelId`
- `GeminiResponseMapper`
- `GeminiThinkingOptionsMapper`
- `GeminiThinkingConfigSnapshot`

Implemented:

- Gemini Developer API key discovery from explicit options, `GEMINI_API_KEY`, then `GOOGLE_API_KEY`.
- Vertex-ready option shape through `Google.GenAI.Client`.
- `gemini-2.5-flash` default model id.
- Configurable model id.
- Gemini 2.5 `thinkingBudget` mapping, including dynamic `-1`, off `0`, and explicit positive budgets.
- `includeThoughts` mapping.
- Thought-summary extraction from thought parts when returned.
- Usage metadata mapping, including thinking token count when exposed.
- Safe missing-key failure without printing secrets.

## Milestone 4: LLM Workflow Planner

Status: complete

Added `Agentica.Clients.Planning`:

- `LlmWorkflowPlanner`
- `LlmPlannerOptions`
- `WorkflowPlanPromptBuilder`
- `WorkflowPlanJsonContract`
- `PlanRefinementJsonContract`
- `LlmPlannerException`

Runtime seam correction:

- Changed `IWorkflowPlanner` to async methods:
  - `CreatePlanAsync`
  - `RefinePlanAsync`

Reason:

- Real model inference is async. Blocking inside the runtime would hide latency and cancellation behavior at the wrong layer.

Implemented behavior:

- LLM planner builds prompts from `RunRequest`, tool descriptors, observations, and receipts.
- Model output maps into Agentica `WorkflowPlan`.
- Refinement output maps into an explicit refined `WorkflowPlan`.
- The planner never executes tools, emits receipts, or marks success.
- Runtime validation still gates unknown tools, kind/effect mismatch, and hidden mutation.

Additional runtime behavior:

- Added `WorkflowPlannerException` and `WorkflowPlannerFailureKind`.
- Provider/model availability failures map to `RunOutcomeStatus.Blocked` with `StopReason.PlannerUnavailable`.
- Invalid planner output still maps to plan-invalid behavior before tool execution.

## Milestone 5: CLI Integration

Status: complete

Deterministic default remains:

```powershell
dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts"
```

Added model-backed flags:

```powershell
--planner deterministic|gemini
--model <model-id>
--thinking-budget dynamic|off|<tokens>
--include-thoughts
```

Credential behavior:

- If Gemini is requested and no Gemini/Google API key or Vertex mode is configured, the CLI exits safely with:

```text
Gemini planner requested, but no Gemini API key was configured. Set GEMINI_API_KEY or GOOGLE_API_KEY.
```

Local override behavior:

- The CLI loads `.env` from the solution root before checking credentials.
- `.env` values override inherited process/user environment variables for the CLI process.
- `.env` is ignored by git; `.env.example` is the committed template.

## Milestone 6: Boundary Tests

Status: complete

Automated tests now cover:

- `Agentica` does not reference `Agentica.Clients`.
- `Agentica.Clients` references `Agentica`.
- Provider-neutral LLM contracts do not expose Google SDK types.
- `LlmWorkflowPlanner` maps valid initial JSON into `WorkflowPlan`.
- `LlmWorkflowPlanner` maps valid refinement JSON into a refined plan.
- Invalid model JSON fails before tool execution.
- Unknown model-produced tool ids fail before execution.
- Mutation-capable model-produced steps remain subject to runtime validation.
- Provider unavailable blocks the run without inventing success.
- Gemini thinking options map to Gemini 2.5 thinking budget settings.
- Thought summaries are diagnostics, not proof.
- Missing API key produces a clear provider error without network access.
- Deterministic planner regression tests still pass.

Verification:

```text
dotnet test Agentica.slnx
Passed! - Failed: 0, Passed: 22, Skipped: 0, Total: 22
```

## Milestone 7: Steelman Review

Status: complete

Strongest argument that this drifted from package-first design:

- `Agentica.CLI` and `Agentica.Tests` now reference `Agentica.Clients`. This is acceptable host/test wiring, but the runtime package must remain clean. Test coverage now proves `Agentica` does not reference `Agentica.Clients`.

Strongest argument that the contracts are too abstract:

- `ILlmClient` is intentionally minimal, but it already has enough shape for model id, messages, structured output, generation options, thinking options, usage, finish reason, and thought summaries. Streaming and provider tools were intentionally deferred.

Strongest argument that the contracts are too thin:

- `LlmStructuredOutputOptions` currently carries MIME type and optional schema text. Gemini now maps valid schema text into provider-enforced `ResponseJsonSchema`; deeper cross-provider schema validation and richer schema generation remain future adapter work.

Provider leakage check:

- Google SDK types are isolated to `Agentica.Clients.Gemini`.
- No Google SDK type appears in Agentica runtime contracts.

Authority check:

- `LlmWorkflowPlanner` proposes plans only.
- Runtime validation controls legality.
- Tools execute only through `AgenticaRunner`.
- Receipts are emitted only by tools.

Truthfulness check:

- Provider high-demand failures stop as `Blocked` / `PlannerUnavailable`.
- Invalid model JSON stops before execution.
- Unknown model tools stop before execution.
- No success is synthesized from model narration.

Deferred:

- LLM-backed `IOutcomeReporter`.
- Strong cross-provider JSON Schema generation/normalization beyond the current Gemini schema mapping.
- Streaming.
- MCP host/client adapters.
- Recording/storage adapters.

## Milestone 8: Final Verification

Status: implemented; real Gemini smoke blocked by provider availability

Commands run:

```text
dotnet build Agentica.slnx
Build succeeded.
0 Warning(s)
0 Error(s)

dotnet test Agentica.slnx
Passed! - Failed: 0, Passed: 22, Skipped: 0, Total: 22

dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts"
Succeeded. Event stream and OutcomeEnvelope JSON printed.

dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts" --planner gemini --model gemini-2.5-flash --thinking-budget 4096 --include-thoughts
Reached Gemini provider. Provider returned high-demand/try-later error.
Run stopped as Blocked with StopReason PlannerUnavailable. No tool execution. No receipts. No fake success.

dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts" --planner gemini --model gemini-2.5-flash-lite --thinking-budget 1024 --include-thoughts
Reached Gemini provider path. Provider/request returned an error while sending the request.
Run stopped as Blocked with StopReason PlannerUnavailable. No tool execution. No receipts. No fake success.
```

Files changed or added in this slice:

- `Agentica/Planning/IWorkflowPlanner.cs`
- `Agentica/Planning/DeterministicWorkflowPlanner.cs`
- `Agentica/Planning/WorkflowPlannerException.cs`
- `Agentica/Planning/WorkflowPlannerFailureKind.cs`
- `Agentica/Execution/AgenticaRunner.cs`
- `Agentica/Outcomes/StopReason.cs`
- `Agentica.Clients/**`
- `Agentica.CLI/Program.cs`
- `Agentica.Tests/AgenticaRunnerTests.cs`
- `Agentica.Tests/AgenticaClientsTests.cs`
- `Agentica.slnx`
- `Agentica.CLI/Agentica.CLI.csproj`
- `Agentica.Tests/Agentica.Tests.csproj`
- `Agentica.ReadMe.md`
- `README.md`
- `docs/Agentica.ObjectGraph.md`
- `docs/Agentica.Design.Decisions.md`
- `docs/CodexGoal.Agentica.Clients.LlmPlanning.md`
- `docs/Agentica.Clients.GoalStatus.md`

Known limitations:

- Real Gemini planning did not complete due provider availability/request errors during verification.
- The adapter requests JSON via `ResponseMimeType = application/json` and maps optional schema text to Gemini `ResponseJsonSchema`; full cross-provider schema generation remains future work.
- `OutcomeReport` remains deterministic in this slice.
- No MCP, storage, queue, auth, deployment, vector memory, or multi-agent orchestration was added.

Completion evidence:

- Build passes.
- Tests pass.
- Deterministic CLI still works.
- `Agentica.Clients` is the only new provider/runtime project.
- Provider SDK is isolated to `Agentica.Clients`.
- LLM model output is mapped into Agentica contracts.
- Invalid or unavailable planner paths fail closed before tool execution.
