# Agentica

Agentica is a compact .NET package for planning and executing bounded tool-using workflows.

The initial request often begins as a human prompt, as in Codex or Claude Code, but Agentica should not assume the requester is human. A run may be requested by a user, another agent, a scheduler, a host application, or a test harness. The package turns that request into a visible workflow plan, refines that plan through state/data queries when needed, executes approved tool steps, and stops only when the workflow reaches a truthful terminal outcome.

Agentica is not a storage framework. A database shim or run recorder may exist later, but persistence is an adapter concern. The core package is about the data shape and action shape of planning, tool use, observation, plan refinement, and execution.

Agentica is the reusable reason-and-execute workflow handler. It is the piece a host application can use when it needs an agentic system to inspect available state and tools, build a plan, execute the next bounded step, observe the result, and decide whether to continue, pause, repair, replan, or finish.

The BookForge/Nanda version of this looked like a choose-your-own-adventure system for an AI author because BookForge exposes legal next actions over a mostly linear engine. Agentica should generalize the handler behind that pattern. The host application owns the adventure, domain, data, policy, and tools. Agentica owns the reasoning and execution loop that navigates those tools under contract.

Build Agentica package-first. A microservice wrapper is a likely hosting mode later, but transport, auth, queues, storage, and deployment should not define the core runtime. The core must be usable in-process before it is hosted out-of-process.

## Core Shape

```text
RunRequest
  -> planner
  -> WorkflowPlan
  -> plan validation
  -> query/read tool steps
  -> observations and artifacts
  -> plan refinement when needed
  -> action tool steps
  -> receipts
  -> OutcomeEnvelope
  -> AgenticaRun outcome
```

Execution truth comes from observations, artifacts, receipts, and state transitions. It does not come from model narration.

## Final Outcome

The final result of a run is not just a status code. Agentica should return an `OutcomeEnvelope` that downstream systems can ingest, inspect, summarize, or restate in another voice.

The outcome envelope contains:

- `RunOutcome`: machine-readable terminal status and stop reason.
- `OutcomeReport`: an LLM-generated report describing what happened, what was completed, what remains blocked or uncertain, and what the next useful action is.
- `ReceiptEnvelope`: the receipts that prove what tools ran, refused, failed, paused, or produced results.
- `DetailEnvelope`: full structured details for plans, plan refinements, steps, observations, artifacts, events, and evidence refs.
- `CompletionEvidence`: the specific receipts and artifacts that justify success, partial success, or refusal.

The `OutcomeReport` is portable narrative output. It is meant for a host system, agent, UI, or another LLM to summarize, transform, or speak in a different voice. It is not execution proof. Every claim in the report should be grounded in receipts, observations, artifacts, or explicit stop reasons in the same envelope.

## Package Boundary

The central runtime package is [Agentica.csproj](Agentica/Agentica.csproj).

The solution should stay small:

```text
Agentica.slnx
  Agentica/                  planner/executor runtime package
  Agentica.CLI/              console host
  Agentica.Clients/          LLM provider SDK isolation
```

Do not create project-per-concern layers such as:

```text
Agentica.Core
Agentica.Abstractions
Agentica.Runtime
Agentica.Storage
Agentica.Persistence
Agentica.Tools
Agentica.Events
```

Use namespaces and folders inside `Agentica` instead.

## Local Development Secrets

For local CLI runs, use a `.env` file at the solution root:

```text
GEMINI_API_KEY=your-local-dev-key
```

The CLI loads `.env` before checking Gemini credentials and overrides inherited process/user environment variables for that run. This makes local key selection explicit without moving secret loading into the Agentica runtime package.

`.env` is ignored by git. Keep [.env.example](.env.example) as the committed template.

## LLM Provider Retries

`Agentica.Clients` retries transient provider-call failures before surfacing `PlannerUnavailable` to the Agentica runtime.

The default retry policy is intentionally small:

- max attempts: 3
- base delay: 500ms
- max delay: 3s
- generation call timeout: 10 minutes
- jitter: enabled

Retries are for provider/transient failures such as provider-side cancellation when the caller token is still active, HTTP 429, HTTP 500/502/503/504, temporary transport failures, and network timeouts.

Retries are not for runtime validation failures, bad tool plans, host tool refusals, bad credentials, bad requests, safety refusals, or caller cancellation. If the run timeout, policy timeout, or Ctrl+C cancellation token is cancelled, retry delays stop immediately and cancellation flows back to the runner.

The generation call timeout bounds one `ILlmClient.GenerateAsync` call, including retries. It is intentionally heavy-handed for early Gemini smoke testing and can be tuned later through configuration or environment variables.

The CLI wraps Gemini with this retry layer. If all attempts fail, `PlannerUnavailable` includes provider, model, attempt count, error kind, status code when known, and last error class without printing secrets.

## Planning Modes

Agentica exposes planning cadence through `ExecutionPolicy.PlanningMode`:

- `Stepwise`: refine after every observation.
- `QueryAndBlockerDriven`: refine after query observations or blocker/refusal observations.
- `BlockerDriven`: refine only after blocker/refusal observations.
- `PlanOnly`: execute the current plan without refinement.

This is intentionally a small hook. It lets hosts test stepwise LLM planning now without committing Agentica to one permanent loop shape.

## Runtime Safety Gates

Agentica exposes generic safety gates needed by richer host harnesses without importing host vocabulary:

- `ToolInputSchema` on `ToolDescriptor` describes required inputs, allowed values, examples, and compact type/range constraints.
- Plan validation rejects missing inputs, unknown inputs, invalid enum values, invalid types, and out-of-range values before execution.
- `ToolEffectPolicy` controls which `ToolEffect` values can execute under the current `ExecutionPolicy`.
- `ICompletionEvaluator` lets a host require proof, such as a completion artifact, before plan exhaustion can become success.
- `MaxPlanContinuations` lets the runner request bounded continuation plans when completion is not proven.
- `MaxBlockedRetries` lets the runner start bounded retry attempts when a run ends blocked. Each retry is a new Agentica run attempt with `RequestOrigin.Agent` and a fresh `agentica.retry` context describing the immediately previous blocker.
- `PlanningContextOptions` lets the planner see a bounded recent slice of observations and receipts while the outcome envelope keeps the full history.

Maze cells, fog of war, hazards, rewards, quest objectives, and board presentation remain host concerns.

## What Agentica Owns

Agentica owns:

- Request shape for starting work.
- Workflow plan shape.
- Tool catalog and tool contract shape.
- Query/read action shape.
- Mutation/action shape.
- Observation, artifact, and receipt shape.
- Outcome envelope, outcome report, and completion evidence shape.
- Plan validation.
- Step scheduling.
- Plan refinement gates.
- Execution loop policy.
- Run status and terminal outcome.
- Operator-visible event stream.

Agentica does not own:

- Domain-specific tool implementations.
- Product vocabulary such as books, scenes, branches, files, workspaces, or game structures.
- Provider SDK details.
- Prompt persona or final product voice.
- Database schema or durable storage implementation.
- Claims that an action succeeded without a receipt.
- Microservice transport, deployment, auth, queue infrastructure, or database ownership.

Host applications provide:

- Tool catalogs.
- Query/read tools.
- Action/mutation tools.
- State and data access.
- Domain policy and effect rules.
- Optional quota, usage, rate-limit, and metrics tools.
- Approval hooks.
- Optional recording or persistence adapters.

Agentica provides:

- Planning loop.
- Step execution loop.
- Observation handling.
- Plan refinement.
- Blocker handling.
- Receipt and evidence model.
- Final outcome envelope and report.

## Prior Stabs And Lessons

`cognition` is the clearest cautionary example. It contains useful planning ideas, but they are buried inside an application stack: API, jobs, clients, relational data, vector data, domains, document storage, workflow persistence, dashboards, migrations, and sandbox/foundry plans.

Preserve these ideas:

- Planner metadata and capability discovery.
- Plan/result objects with artifacts, diagnostics, and backlog-like follow-up work.
- Scope-aware execution context.
- Template validation before planner execution.
- Cancellation, hard stop limits, and visible events.
- Tool contracts with effect level, approval requirements, and audit tags.
- Human-gated tool execution as a policy shape, not as first-slice infrastructure.

Avoid importing these mistakes:

- Project-per-concern expansion before the runtime contract is proven.
- Runtime contracts depending directly on EF, Hangfire, Rebus, SignalR, dashboards, or a specific app domain.
- Planner abstractions living inside a client/provider package.
- Giant orchestration services and controllers that own workflow logic.
- Transcript persistence becoming the center of the design.
- Quota systems, token accounting, metrics dashboards, and rate-limit economics becoming core run-domain concepts.
- Tool foundry, sandboxing, dynamic tool publishing, and registry enablement before basic plan/execute/observe/report works.
- Product vocabulary leaking into the generic planner/executor package.

Agentica should take the contract shape from `cognition`, not the topology.

Quota and metric ideas are still valid, but they belong at the host boundary. Expose them through tools, plugins, policy hooks, event sinks, or recorders. Core Agentica only needs bounded execution guards such as max steps, max refinements, cancellation, and timeout.

## Primary Concepts

`RunRequest`
: The input that asks Agentica to do work. It may originate from a user, another agent, a scheduler, or a host system.

`AgenticaRun`
: The runtime record of one bounded workflow execution.

`WorkflowPlan`
: A structured plan describing query steps, action steps, dependencies, expected outputs, and completion conditions.

`PlanStep`
: One planned unit of work.

`ToolCatalog`
: The current set of tools the planner may use.

`ToolDescriptor`
: A tool's contract: id, kind, input shape, output shape, effect level, and approval requirements.

`ToolInvocation`
: One concrete call to a tool.

`Observation`
: Information returned from read/query tools and used to refine the plan.

`Artifact`
: Typed output that later steps can reference.

`Receipt`
: Proof that a tool executed, refused, failed, paused, or produced a result.

`OutcomeEnvelope`
: The complete terminal package returned by Agentica. It includes status, generated report, receipts, details, and evidence.

`OutcomeReport`
: LLM-generated report over the run outcome. It is designed for downstream systems to summarize, restate, or present in another voice.

`ReceiptEnvelope`
: Machine-readable receipt package for the run.

`PlanRefinement`
: An explicit plan update caused by observations, blockers, failures, or changed preconditions.

`RunOutcome`
: Machine-readable terminal result: succeeded, blocked, failed, waiting for approval, cancelled, or partially complete.

## Runtime Rules

1. Plan before executing mutation-capable tools.
2. Treat the plan as a contract that can only change through explicit refinement.
3. Query/read tools are first-class steps, not side effects hidden in prompts.
4. Unknown tools fail validation before execution.
5. Every tool invocation emits a receipt.
6. Every meaningful state change emits an event.
7. A model may propose; Agentica validates and executes.
8. A run may stop honestly as blocked, failed, waiting, or partial.
9. The final outcome includes both a generated report and machine-readable proof.
10. Choice, sequencing, and adaptation may be agentic; legality, effects, receipts, approval, and loop limits stay in runtime contracts.
11. Storage adapters may record the run, but they are not the runtime's center of gravity.

## First Console Slice

The first useful slice should not require a real LLM.

```powershell
dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts"
```

Expected shape:

```text
run.created        run_...
request.accepted   origin=user
plan.created       plan_001 steps=2
step.started       step_001 tool=query_state
observation.made   observation_001
receipt.emitted    receipt_001 status=succeeded
plan.refined       plan_002 reason=observation
step.started       step_002 tool=perform_action
receipt.emitted    receipt_002 status=succeeded
outcome.reported   report_001 refs=2
run.succeeded      run_...
```

The first slice proves the planner/executor envelope. It does not need intelligence yet.

The deterministic planner is a harness, not the destination. LLM planning should enter as soon as Agentica can reject invalid model output before any mutation-capable tool runs.

## Development Plan

### Slice 1: Deterministic Envelope Proof

- Replace placeholder anchors with `RunRequest` and `AgenticaRun`.
- Add `WorkflowPlan`, `PlanStep`, `ToolDescriptor`, `Observation`, `Artifact`, `Receipt`, `ExecutionEvent`, and `OutcomeEnvelope`.
- Add a deterministic planner that emits a query step, observes state, refines the plan, executes an action step, emits receipts, and returns an outcome report.
- Add a console event stream and JSON outcome envelope output.

### Slice 2: Tool Catalog And Fail-Closed Validation

- Add `ToolCatalog`.
- Validate every plan step against the available tools.
- Reject unknown tools, bad dependencies, and invalid effect levels before execution.
- Treat read/query tools and mutation/action tools differently.
- Prove malformed or unsafe plans stop as `PlanInvalid` before any mutation-capable tool runs.
- Add input schemas, effect policy, evidence-gated completion, bounded continuation, and planner context shaping for richer host harnesses.

### Slice 3: LLM Initial Planner

- Add an async `LlmWorkflowPlanner` behind `IWorkflowPlanner`.
- Add `Agentica.Clients` as the provider SDK isolation project.
- Keep provider SDK types out of Agentica runtime contracts.
- Convert model structured output into `WorkflowPlan`.
- Validate the model-proposed plan before execution.
- Keep `DeterministicPlanner` as the regression baseline.

### Slice 4: Observation-Driven LLM Refinement

- Let read/query tools produce `Observation` objects.
- Let the model propose `Continue`, `RefinePlan`, `StopBlocked`, `Ask`, or `Finish`.
- Convert valid model output into `PlanRefinement` or terminal decision records.
- Enforce hard refinement and step limits.
- Stop truthfully on blocker, refusal, failure, approval wait, or exhaustion.

### Slice 5: Outcome Envelope And LLM Report

- Add `OutcomeEnvelope`, `OutcomeReport`, `ReceiptEnvelope`, `DetailEnvelope`, and `CompletionEvidence`.
- Generate a deterministic report first if needed for tests.
- Add an LLM-backed report once receipts, observations, artifacts, blockers, and stop reason are available.
- Require report claims to cite receipts, observations, artifacts, or stop reasons.

### Slice 6: Recording Adapter

- Add a minimal event/run recorder only after the runtime and LLM planner shape are stable.
- Keep this as adapter plumbing, not `Agentica.Storage`.
- The runtime should still be usable in-memory.

### Slice 7: Boundary Adapters

- Add MCP hosting or MCP tool-consumption adapters only after the core run envelope works in-process.
- Keep MCP SDK types out of Agentica runtime contracts.
- Treat MCP as a boundary envelope around Agentica's outcome envelope.

## Design Sources

Agentica is informed by local work in:

- `C:\Users\Zythis\source\repos\nanda`
- `C:\Users\Zythis\source\repos\bookforge`
- `C:\Users\Zythis\source\repos\__draft_old\cognition`
- `C:\Users\Zythis\source\repos\__draft_old\MC NeoForge - MazeQuest`

The useful ideas to preserve are typed contracts, tool legality, observations, receipts, explicit state, bounded execution, and visible events.

The ideas to avoid importing directly are product vocabulary, hidden gatekeepers, giant prompts, unbounded loops, and project sprawl.

See [Agentica.ObjectGraph.md](docs/Agentica.ObjectGraph.md) for the proposed namespace, object, and execution graph.

See [Agentica.Design.Decisions.md](docs/Agentica.Design.Decisions.md) for locked decisions on package shape, MCP boundaries, LLM planning cutover, and planning-system lessons from Nanda.
