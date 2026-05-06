# Agentica

Agentica is a compact .NET package for bounded agentic workflow planning and execution.

It is designed to become the reusable reason-and-execute runtime behind applications that need an agent to inspect available tools and state, form a plan, execute tool steps under contract, observe results, refine the plan, and return a receipt-backed outcome.

Agentica is package-first and in-process-first. It is not MCP-first, storage-first, microservice-first, or project-sprawl-first.

## Current Status

Agentica now has the first runtime, client, and harness slices needed to test the core idea end-to-end.

```text
RunRequest
  -> deterministic or LLM-backed WorkflowPlan
  -> plan validation
  -> query/read tool steps
  -> Observation
  -> explicit PlanRefinement
  -> action/mutation tool steps
  -> Receipts
  -> OutcomeEnvelope
```

Implemented reality:

- `Agentica` is the central in-process runtime package.
- `Agentica.Clients` exists as the provider SDK isolation project.
- `Agentica.CLI` hosts deterministic proof runs, static quest runs, and MazeQuest runs.
- `Agentica.Tests` covers runtime contracts, validation, client mapping, retry behavior, and harness boundaries.
- Deterministic planning remains the regression baseline.
- Gemini-backed planning exists through `LlmWorkflowPlanner` and `GeminiLlmClient`.
- Tool input contracts, effect policy, completion evaluation, bounded continuation, blocked retries, provider-call retries, and planner context shaping are implemented.
- CLI run logging writes structured artifacts under `.agentica/runs`.
- MazeQuest exists as a host-owned reasoning harness, not Agentica runtime vocabulary.

Current reality gap:

The runtime and client contracts are in place, but real LLM MazeQuest traversal is still an active smoke-test target. The code can call Gemini, validate model plans, retry transient provider failures, and log the run. The remaining work is to improve planner prompting/strategy and harness pressure until the LLM reliably navigates the maze without host rescue or false success.

## The Goal

Most agentic systems eventually need the same inner loop:

1. Accept a bounded objective.
2. Inspect the available tools and state.
3. Build a plan.
4. Validate the plan before tool execution.
5. Run read/query tools to gather evidence.
6. Refine the plan when observations change what should happen next.
7. Run action/mutation tools only when they are valid and allowed.
8. Emit receipts for every tool invocation.
9. Stop truthfully when complete, blocked, failed, waiting, cancelled, or partially complete.
10. Return a machine-readable outcome envelope plus a narrative report grounded in evidence.

Agentica is meant to be that reusable runtime.

The long-term product is not merely a workflow engine. The product is an LLM-driven planner/executor that can reason over a bounded tool surface, query state, adapt from observations, and still remain inspectable, testable, and receipt-backed.

## Core Rule

```text
The model proposes.
Agentica validates.
Tools execute.
Receipts prove.
Outcome reports narrate, but never prove.
```

Execution truth comes from observations, artifacts, receipts, validation issues, and terminal state. It does not come from model narration.

## What Agentica Owns

Agentica owns the workflow proof envelope:

- `RunRequest`
- `AgenticaRun`
- `WorkflowPlan`
- `PlanStep`
- `ToolCatalog`
- `ToolDescriptor`
- `ToolInvocation`
- `ToolResult`
- `Observation`
- `Artifact`
- `Receipt`
- `ExecutionEvent`
- `AgenticaRunner`
- `ExecutionPolicy`
- `OutcomeEnvelope`
- `RunOutcome`
- `OutcomeReport`
- `ReceiptEnvelope`
- `DetailEnvelope`

Agentica owns:

- Request shape.
- Plan shape.
- Plan validation.
- Tool descriptor and invocation contracts.
- Step scheduling and execution loop.
- Observation, artifact, and receipt normalization.
- Plan refinement records.
- Stop reasons and terminal outcomes.
- Outcome envelope and generated outcome report.
- Operator-visible event stream.

## What The Host Owns

The host application owns:

- Domain state.
- Domain vocabulary.
- Tool implementations.
- Persistence.
- Approval behavior.
- Product policy.
- Presentation and final voice.
- Quotas, usage accounting, metrics, and rate-limit economics.
- Transport, auth, deployment, queues, and storage.

Agentica should not import product vocabulary such as books, scenes, branches, workspaces, games, authoring, or any specific host domain. Host systems can provide those as tools and state references.

## Runtime Shape

The intended runtime shape is:

```text
Host Application
  owns domain, state, policy, approvals, tools, presentation

Agentica Runtime
  owns request, plan, validation, execution loop,
  observations, refinements, receipts, events, outcome envelope

Tool Layer
  local tools
  host tools
  query/read tools
  action/mutation tools
  MCP tools through adapters later

Optional Boundary Adapters
  CLI host now
  LLM provider clients soon
  MCP host/client adapters later
  event/run recorder later
```

## Solution Shape

The solution intentionally stays small:

```text
Agentica.slnx
  Agentica/          planner/executor runtime package
  Agentica.Clients/  LLM provider SDK isolation
  Agentica.CLI/      console proof host and host-owned harnesses
  Agentica.Tests/    boundary and behavior tests
```

Do not create runtime project bloat such as:

```text
Agentica.Core
Agentica.Abstractions
Agentica.Runtime
Agentica.Storage
Agentica.Persistence
Agentica.Tools
Agentica.Events
```

Use folders and namespaces inside `Agentica` to carry scope and meaning.

## Current Console Proof

Run:

```powershell
dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts"
```

Expected event story:

```text
run.created
request.accepted
plan.created
step.started
observation.made
receipt.emitted
plan.refined
step.started
receipt.emitted
outcome.reported
run.succeeded
```

After the event stream, the CLI prints a serialized `OutcomeEnvelope` JSON object.

That envelope contains:

- `outcome`: terminal status, stop reason, completed steps, blockers, and completion evidence.
- `report`: deterministic narrative report with evidence-grounded claims.
- `receipts`: all tool receipts.
- `details`: request, plan versions, refinements, observations, artifacts, events, and validation issues.

## Build And Test

Prerequisite:

- .NET 10 SDK

Build:

```powershell
dotnet build Agentica.slnx
```

Test:

```powershell
dotnet test Agentica.slnx
```

Run proof slice:

```powershell
dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts"
```

Run the current Gemini MazeQuest smoke profile manually:

```powershell
dotnet run --project Agentica.CLI -- mazequest run --planner gemini --planning-mode stepwise --watch --narrator deterministic --turn-json --type unlock --seed 173 --width 7 --height 7 --timeout-seconds 1200 --thinking-budget off --max-blocked-retries 2 --log-run --log-dir .agentica\runs
```

Visual Studio launch profiles are configured to run the same Gemini MazeQuest smoke with logging. Docker mounts `/app/.agentica/runs` back to the host `.agentica/runs` folder.

## Current Test Coverage

The current test suite proves:

- A request from origin `User` is valid.
- A request from origin `Agent` is valid.
- A plan can execute a query/read tool before an action tool.
- Unknown tools fail before execution.
- A query observation can trigger explicit plan refinement.
- A mutation-capable tool cannot execute without a matching descriptor.
- Every executed tool invocation emits a receipt.
- A run can stop blocked without inventing success.
- Outcome report claims cite evidence.
- A downstream system can consume the `OutcomeEnvelope` JSON without parsing console text.
- Tool input schemas reject missing, unknown, invalid enum, invalid type, and out-of-range values before execution.
- Effect policy blocks destructive or disallowed tools before execution.
- Completion evidence gates prevent plan exhaustion from becoming false success.
- Bounded continuations can request more plan slices when completion is not proven.
- Blocked retries create fresh `RequestOrigin.Agent` attempts with immediate previous-blocker context.
- `Agentica.Clients` maps model JSON into Agentica plans without provider SDK types leaking into runtime contracts.
- `RetryingLlmClient` retries transient provider failures, respects caller cancellation, and surfaces attempt-count metadata on exhaustion.
- MazeQuest host scenarios exercise fog-of-war state, legal actions, objective artifacts, logging, and watch output without adding maze vocabulary to `Agentica`.

## Outcome Report Discipline

The `OutcomeReport` exists because downstream systems often need a narrative report they can display, summarize, restate, or transform into another voice.

The report is not proof.

Every important report claim should cite one or more evidence refs:

- Receipt refs.
- Observation refs.
- Artifact refs.
- Validation issue refs.
- Stop reason refs.

If the receipts and details do not prove a claim, the report must not say it happened.

## Planning Lessons

Agentica is informed by earlier local planning and agentic-system work, especially Nanda and BookForge.

The most important lesson is that planning needs visible shape:

- Scope.
- Steps.
- Decisions.
- Risks.
- Validation.
- Artifacts.
- Receipts.
- A compiled review projection.

Agentica harvests that discipline into runtime objects:

```text
Nanda plan folder       -> AgenticaRun scope
plan.md                 -> RunRequest + WorkflowPlan summary
steps/index.md          -> ordered PlanStep set
step folder             -> PlanStep plus step-local artifacts/receipts
decisions/              -> PlanRefinement + continuation/stop rationale
risks/                  -> ValidationIssue + Blocker + StopReason
validation/             -> plan validation + completion conditions
artifacts/              -> Artifact + Receipt + EvidenceRef
compiled plan           -> OutcomeEnvelope + DetailEnvelope review projection
stage transition        -> RunOutcomeStatus transition
```

Agentica does not clone the markdown folder workflow at runtime. The runtime uses compact structured objects.

## LLM Cutover Plan

The deterministic planner is permanent as a regression fixture, but it is not the product.

The LLM will provide reasoning and planning over Agentica's tool and evidence surfaces. `Agentica.Clients` is the bridge between LLM inference and Agentica tracking/execution: it calls models, supplies Agentica-shaped tool/problem surfaces, requests structured planning output, and maps provider responses back into `WorkflowPlan`, `PlanRefinement`, and `OutcomeReport` contracts.

The runtime still owns validation, execution, receipts, observations, and terminal truth.

LLM planning entered after fail-closed validation was proven:

1. Deterministic envelope proof: implemented.
2. Fail-closed plan validation: implemented.
3. `LlmWorkflowPlanner` behind `IWorkflowPlanner`: implemented.
4. LLM observation-driven refinement: implemented at the planner interface and prompt/mapping layer.
5. LLM outcome reporting: not yet implemented as the primary outcome reporter.

The cutover rule:

```text
Add LLM planning as soon as Agentica can reject invalid model output before any mutation-capable tool runs.
```

The runtime package does not depend on provider SDK types. Provider-specific clients belong behind adapters in `Agentica.Clients`.

### Planning Modes

`ExecutionPolicy.PlanningMode` controls refinement cadence:

- `Stepwise`: refine after every observation.
- `QueryAndBlockerDriven`: refine after query observations or blocker/refusal observations.
- `BlockerDriven`: refine only after blocker/refusal observations.
- `PlanOnly`: execute the current plan without refinement.

This is a hook for future planner styles. Planning mode does not define success; receipts, artifacts, validation issues, blockers, and stop reasons still define truth.

### Runtime Safety Gates

Agentica now exposes generic harness seams without importing host vocabulary:

- `ToolInputSchema` on `ToolDescriptor` describes required inputs, allowed values, examples, and compact type/range constraints.
- `AgenticaRunner.ValidatePlan` rejects malformed planned inputs before tool execution.
- `ToolEffectPolicy` blocks disallowed effects such as `Destructive` unless the host explicitly permits them.
- `ICompletionEvaluator` lets hosts require evidence, such as a completion artifact, before a run can succeed.
- `MaxPlanContinuations` allows bounded continuation planning when a plan is exhausted but completion evidence is missing.
- `MaxBlockedRetries` allows bounded retry attempts after a blocked outcome. Retry attempts are fresh Agentica run attempts with `RequestOrigin.Agent` and an `agentica.retry` context describing the immediately previous blocker.
- `PlanningContextOptions` lets hosts bound recent observations and receipts passed back to planners while preserving full run details in the envelope.
- `PlanRefinementReasons` keeps extra thinking/planning turns auditable as normal refinement reasons such as `ambiguous_action`, `blocked`, `low_confidence`, `conflicting_signals`, `completion_check`, `continue`, `resource_risk`, or `retry_unblock`.

These are Agentica concerns because every tool-using host needs them. Maze cells, fog of war, rooms, hazards, rewards, and quest objectives remain host/tool data.

### Local LLM Credentials

For local CLI development, put provider keys in a `.env` file at the solution root. The CLI loads this file before creating LLM clients and applies the values to the current process, overriding inherited user/process environment variables for that CLI run.

Start from [.env.example](.env.example):

```text
GEMINI_API_KEY=your-local-dev-key
```

`.env` is ignored by git and should not be committed. The runtime package does not load `.env` files; this is intentionally a CLI host concern.

### LLM Provider Retries

`Agentica.Clients` has a narrow retry layer around provider generation calls. The CLI wraps Gemini with `RetryingLlmClient` before constructing the LLM planner.

Defaults:

- `maxAttempts`: 3
- `baseDelay`: 500ms
- `maxDelay`: 3s
- `callTimeout`: 10 minutes for one `ILlmClient.GenerateAsync` call, including retries
- jitter enabled

Retried failures include provider-side cancellation when the caller token is not cancelled, HTTP 429, HTTP 500/502/503/504, temporary network/transport failures, and timeouts. Non-transient failures such as bad credentials, bad requests, safety refusals, deterministic plan validation failures, and host tool refusals are not retried.

All retry delays observe the caller cancellation token. If Ctrl+C, run timeout, or policy timeout cancels the token, retries stop immediately. The Visual Studio Gemini MazeQuest profile currently uses a 20-minute run timeout so one slow 10-minute provider call is not cut off before the client-level timeout can classify it.

## MCP Boundary

Agentica is not MCP-first.

There are two envelopes:

- MCP envelope: JSON-RPC transport, lifecycle, capability negotiation, and tool call wrapper.
- Agentica envelope: workflow execution proof, plan versions, receipts, observations, refinements, details, and report.

Agentica may later be exposed through MCP as a tool such as `agentica.run`.

Agentica may later consume MCP tools by adapting remote tool descriptors into `ToolDescriptor` objects and remote tool results into `ToolResult`, `Observation`, `Artifact`, and `Receipt` objects.

The runtime package must not depend on MCP SDK types.

## Code Reality Snapshot

The original goal was to build a compact planner/executor package that can reason over tool surfaces, query state, execute validated actions, adapt from observations, and return proof. The code now matches that direction more than the early plan, but it is still a first working shape rather than the final product.

What matches the original goals:

- Package-first shape is preserved. There is no `Agentica.Core`, `Agentica.Runtime`, `Agentica.Storage`, or project-per-concern spread.
- The justified extra project, `Agentica.Clients`, exists and isolates provider SDK dependencies from the runtime.
- Agentica owns contracts and execution truth, not host domain state or storage.
- The runtime validates tool ids, kinds, effects, policy, and input schemas before execution.
- Query tools, observations, explicit refinements, action tools, receipts, artifacts, and outcome envelopes are real code.
- Completion can be evidence-gated so success is not just "no more plan steps."
- Blocked runs can retry in a bounded way with explicit immediate-blocker context.
- LLM provider failures are retried at the client boundary before becoming runtime `PlannerUnavailable`.
- CLI logging creates inspectable run artifacts instead of relying on console scrollback.
- MazeQuest provides a higher-pressure host harness while keeping maze concepts outside the runtime.

Where the code is ahead of the original first slice:

- `Agentica.Clients` and Gemini planning are already present.
- Runtime input schemas, effect policy, evidence-gated completion, bounded continuation, blocked retry, and planner context shaping are implemented.
- Planner-provided step reasons and refinement reason codes are preserved in the normal plan/refinement envelope.
- CLI run logging and MazeQuest watch/turn envelopes are implemented.
- Provider-call retry and 10-minute LLM generation call timeouts are implemented.

Where the code is still behind the product goal:

- The LLM planner is present, but real MazeQuest solving is not yet proven as a stable green smoke test.
- LLM outcome reporting is not yet the primary report path; deterministic/host reporters still carry most proof reporting.
- Planning decisions are intentionally still `WorkflowPlan`/`PlanRefinement` shaped; richer model decisions such as "already complete", "cannot resume", or "ask for approval" are not first-class structured model outputs yet.
- Provider retry observability is currently response/exception metadata, not a runtime event stream.
- The run logger is a CLI adapter, not a general recorder API.
- MCP remains intentionally unimplemented.
- Storage, approvals, durable run replay, and host policy plugins are still adapter-level future work.

The current state is good enough to keep pressing on LLM behavior. The next meaningful proof is not more scaffolding; it is a logged Gemini MazeQuest run where the model uses the tool contracts, observations, retry context, and completion evidence gate to either finish honestly or stop with a useful blocker.

## Non-Goals

The current and near-term package is not:

- A storage framework.
- A database schema.
- A microservice template.
- An MCP server.
- An auth system.
- A queue engine.
- A vector memory system.
- A dashboard product.
- A domain-specific authoring or game workflow.
- A general autonomous agent with an unbounded loop.

Those can exist around Agentica later as adapters, hosts, or tools.

## Roadmap

### Slice 1: Executable Envelope Proof

Status: implemented.

- Deterministic plan.
- Query tool.
- Observation.
- Explicit refinement.
- Action tool.
- Receipts.
- Outcome envelope.
- Console event stream.
- Tests.

### Slice 2: Stronger Tool Contracts

Status: implemented for first harness needs.

- Add input contract objects.
- Validate tool input shape before execution.
- Gate tool effects through runtime policy.
- Add completion evaluator and bounded continuation hooks.
- Bound planner-visible observations and receipts.

### Slice 3: LLM Initial Planner

Status: implemented for first provider slice.

- `Agentica.Clients` exists.
- Gemini adapter exists.
- `LlmWorkflowPlanner` maps structured model output into `WorkflowPlan`.
- Runtime validates model-proposed plans before execution.
- Deterministic planner remains the regression baseline.

### Slice 4: LLM Refinement Loop

Status: partially implemented.

- Observations are fed back into `IWorkflowPlanner.RefinePlanAsync`.
- `LlmWorkflowPlanner` maps structured refinement output into a refined `WorkflowPlan`.
- Refinement, continuation, step, retry, and timeout limits are hard runtime policy.
- Rich model decisions such as stop/ask/already-complete/cannot-resume remain future work.

### Slice 5: LLM Outcome Reporter

Status: not yet primary.

- Deterministic and host-specific reporters exist.
- Report claims are evidence-grounded.
- LLM outcome reporting is still deferred until the run proof surfaces are more stable.

### Slice 6: Recording Adapter

Status: implemented as CLI logging only.

- `--log-run` writes structured run artifacts under `.agentica/runs`.
- MazeQuest logs include stage, public snapshot, event JSONL, turn JSONL, and outcome JSON.
- This is intentionally not `Agentica.Storage` and not yet a reusable recorder API.

### Slice 7: Boundary Adapters

Status: not started.

- MCP server adapter.
- MCP client/tool adapter.
- Host integration examples.

## Design Documents

Important supporting docs:

- [Agentica Object Graph](docs/Agentica.ObjectGraph.md)
- [Agentica Design Decisions](docs/Agentica.Design.Decisions.md)
- [Codex Goal: First Executable Slice](docs/CodexGoal.Agentica.FirstExecutableSlice.md)
- [Codex Goal: Runtime Harness Gaps](docs/CodexGoal.Agentica.RuntimeHarnessGaps.md)
- [Goal Status Log](docs/Agentica.GoalStatus.md)
- [Original Planning README](Agentica.ReadMe.md)

## License

This repository is source-available for conceptual reference only. It is not open source.

See [LICENSE](LICENSE).

In short: you may read the code to understand the ideas. You may not copy, redistribute, modify, incorporate, host, sell, or use the code in another project, product, service, or commercial/internal system without explicit written permission from the copyright holder.
