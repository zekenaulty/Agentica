# Agentica Runtime Contracts

This is a compact reference for packages and hosts that want to build on Agentica without depending on Lab harnesses.

Agentica is the bounded proof engine:

```text
RunRequest
  -> IWorkflowPlanner
  -> WorkflowPlan
  -> plan validation
  -> tool execution
  -> observations, receipts, artifacts
  -> OutcomeEnvelope
```

## Core Boundary

`Agentica` owns:

- request shape
- plan shape
- tool catalog contracts
- validation
- bounded execution
- event stream
- receipts, observations, artifacts
- terminal outcome
- outcome envelope

Hosts own:

- domain state
- tool implementations
- orchestration across runs
- persistence
- routing/classification
- approvals and product policy
- prompt/domain projections
- long-lived domain memory

## Request

`RunRequest` is the bounded objective passed to one `AgenticaRunner` run.

Use `Context` for scoped supporting data such as a campaign snapshot or host projection. Do not put unbounded history in request context.

## Planner

`IWorkflowPlanner` creates or refines a `WorkflowPlan`.

The planner proposes. Agentica validates.

The planner must not be treated as proof of execution. Only receipts, observations, artifacts, validation issues, host checks, and terminal state prove what happened.

## Plan

`WorkflowPlan` contains executable `PlanStep` records.

`PlanStep` includes:

- `StepId`
- `ToolId`
- `Kind`
- `Effect`
- `Input`
- `Reason`
- `DependsOn`
- `BatchId`

Use `DependsOn` for execution ordering only.

A dependency may reference:

- an earlier step in the same submitted plan slice
- a step id listed in `PlanningRequest.ExecutionContext.CompletedStepIds`

A dependency must not reference receipt ids, observation ids, artifact ids, plan ids, or guessed prior steps. Prior observations and receipts are planner evidence, but they are not dependency ids unless their producing step id is known and completed.

Plan validation rejects dependencies that are neither earlier same-plan steps nor completed prior-run steps. It also rejects any new plan step that reuses a completed step id.

Use `BatchId` only for independent read-only query steps that can safely run together.

## Tools

Hosts register tools through `ToolCatalog`.

`ToolDescriptor` declares:

- tool id
- name
- kind
- effect
- input schema
- approval requirement
- retry safety

`ToolEffectPolicy` controls allowed effects. The default policy is local-first and rejects external side effects and destructive tools.

Every `ToolRegistration` must also provide an authoritative `ToolSecurityDeclaration`: effect, data read boundaries, boundaries exposed back to a planner, external-output trust classification, approval requirement, retry safety, and provenance. Descriptor effect, approval, and retry fields are planner projection only; compilation rejects any mismatch with the security declaration and rejects every `Unknown` security value.

`ToolCatalog` compiles registrations into one immutable `CompiledToolManifest`. Planner projections and nested schema/context collections are defensive snapshots. Compilation rejects blank, oversized, or canonically duplicate identities, non-finite/reversed bounds, and bounds on nonnumeric fields. It caps registrations, aggregate fields, and per-field allowed values before building immutable field/required/allowed-value indexes. `ManifestHash` is a canonical, order-independent `sha256-v1:` digest over the complete planner/security/provenance surface; per-request `SurfaceId` remains a separate random correlation id.

Immediately before every dispatch, Agentica recompiles the caller-owned registration sources, compares the current manifest to the plan-pinned hash, resolves the executor from that freshly checked manifest, and rechecks kind, effect, security grant, planner boundary, and input. A measured aggregate node/work budget bounds those mandatory recompilations across an attempt; exhaustion or registration drift fails closed with a canonical refusal before `ITool.ExecuteAsync`.

`ToolDescriptor.RequiresApproval` is fail-closed. A sensitive dispatch requires an unexpired one-shot `ToolExecutionGrant` bound to a host-supplied authorization scope, attempt number, step id, canonical invocation-input digest, exact manifest, tool id, complete outbound boundary set, accepted inbound external-output classification, grant id, and nonblank issuer. Policy snapshots derived from the same in-memory grant share atomic consumption state. Before `ITool.ExecuteAsync`, the runtime writes `ToolGrantConsumption` into the authoritative attempt ledger with the issuer, expiry, allowed outbound boundaries, allowed external outputs, and complete invocation binding. That grant lineage cannot cross another step, run, or retry; a fresh retry requires a fresh attempt-bound issuance. Reconstructing a grant creates new host-owned state, so global grant-id uniqueness and durable replay protection are outside this in-process primitive. A grant does not override `ToolEffectPolicy`; both independent checks must allow dispatch. There is no ambient or implicit approval path.

`ToolSecurityPolicy` also classifies initial run data and planner locality. A null external-planner allowance means local. A non-null set, including an empty set, declares an external planner and lists the only data boundaries it may receive. Implementations that cross a trust boundary implement `IExternalWorkflowPlanner`; the runner refuses to call them unless an explicit external-planner policy exists. Tool-produced boundaries remain sticky for the run and are projected through later plan steps, so a workspace read cannot be followed by an external transmission whose grant omits `WorkspaceContent`.

Planner-visible descriptors are filtered before every planning call. Tools categorically denied by the current effect policy, planner-boundary policy, or absence of a potentially matching unconsumed grant are not advertised. Exact step/input grant matching is still rechecked after the planner returns a frozen plan and immediately before dispatch.

Mutation-capable work must be `ToolKind.Action` and must declare the matching effect.

## Execution Policy

`ExecutionPolicy` bounds a run:

- max steps
- max refinements
- cooperative in-process timeout/cancellation
- planning mode
- max plan continuations
- max blocked retries
- batch size
- parallelism
- effect policy
- planner-visible context limits
- retryable blocked stop reasons
- exact mutation tool ids authorized to retry
- frozen tool security policy, external-planner boundary allowance, and exact execution grants
- bounded best-effort event-sink delivery wait

`PlanningRequest.ExecutionContext` gives planners compact run-local execution state, including completed step ids and completed step summaries. This is the only supported way for refined or continuation plans to depend on work completed by earlier plan versions in the same Agentica run.

Keep defaults conservative for library use. Increase limits at the host boundary only when the host can explain why.

Blocked retry is a fresh run attempt, not a continuation of the same invocation. The default frozen `BlockedRetryPolicy` retries only `ToolUnavailable` and authorizes no mutation tool. A mutation can repeat only when its current registration declares `ToolRetrySafety.Idempotent`, the host policy names that exact tool id, and any sensitive dispatch has a new grant bound to the retry attempt. Retry evaluation uses validated frozen plan steps and registrations across the complete attempt history; tool-returned receipt identity cannot grant retry authority.

`ExecutionPolicy.Timeout` drives a linked cancellation token. It is a cooperative in-process deadline, not process isolation: Agentica awaits planners and tools that ignore cancellation so mutation effects cannot continue after a falsely terminal timeout envelope. External adapters must enforce transport deadlines; untrusted mutation-capable execution requires a future worker-process isolation contract.

Fallback boundaries classify exception graphs with a bounded, cycle-safe walk. `OutOfMemoryException`, `StackOverflowException`, and `AccessViolationException` propagate even when nested in cancellation or any aggregate branch; they are never rewritten into ordinary planner, tool, observer, or Chat effect proof.

## Batching

Agentica can execute independent read-only query steps in a parallel batch.

Validation rejects:

- batches when disabled by policy
- batches larger than policy allows
- batches exceeding parallelism
- mutation-capable batch steps
- dependencies within the same batch

Tool-internal jobs or route execution remain host/tool concerns. Agentica schedules tool invocations; tools own their internals.

## Completion

Every `AgenticaRunner` host must explicitly supply an `ICompletionEvaluator`. There is no constructor fallback. Evaluators receive an immutable `CompletionContext`, return the exact evidence references that satisfied completion, and cannot mutate the live run ledger. Empty evidence-based definitions of done are invalid. Returned evidence must resolve inside the current attempt.

`PlanExhaustionCompletionEvaluator` remains an explicit opt-in for procedural demos whose entire definition of done is “the valid plan ran.” It does not prove an external objective or artifact exists. Exceptions or invalid output from a completion evaluator produce `CompletionEvaluationFailed` while preserving all prior invocation proof.

A narrative report is not completion proof. If a host needs an artifact or host state check, require it through the completion evaluator or host acceptance logic.

## Tool Result Boundary

Tool results are untrusted output. Agentica assigns canonical receipt, observation, and artifact ids; pins step/tool identity and completion time to the invocation; replaces tool evidence with canonical links; and converts returned data into an immutable JSON-safe value tree. One budget spans the complete result: depth 32, 16,384 collection items/nodes, 1 MiB retained bytes, 256 KiB per string, and 256 KiB per binary value. Unknown DTOs and JSON values must survive bounded canonicalization; duplicate keys, cycles, non-finite numbers, oversize content, null/malformed results, undefined statuses, `Accepted`, and `Partial` results cannot fall through to success.

Source-owned capability tokens may be carried through canonical aliases inside the envelope and restored only at a subsequent tool-dispatch boundary. They do not become runtime evidence identity or retry authority.

Every real dispatch receives a terminal canonical receipt even when the tool throws or cancellation arrives after an effect. A read-only parallel batch records every dispatched sibling before deriving the terminal outcome.

## Event Delivery

`AgenticaRun.Events` is the authoritative in-memory event ledger. Each event is deep-frozen before it reaches the ledger, capped at 32 structured levels, 16,384 structured values, 65,536 characters per string, and 1 MiB for the complete serialized event. Reason projectors and `IEventSink` receive separate immutable snapshots, so an observer cannot mutate authoritative proof. Snapshot-limit failures retain a bounded typed diagnostic event.

`IEventSink` is a best-effort observer, not durable proof storage. Each callback receives a detached snapshot and is waited only for `ExecutionPolicy.EventSinkDeliveryTimeout` (one second by default, capped at one minute). The first exception or delivery timeout is captured as `DetailEnvelope.EventDeliveryFailure`; delivery to that sink is circuit-broken for the rest of the attempt while canonical events, receipts, artifacts, batches, and the business outcome continue to be recorded. A timed-out observer callback may finish later and therefore must not perform authoritative business effects.

A host that needs durable audit guarantees must add a future write-ahead/outbox contract. A synchronous observer callback is not such a guarantee. Outcome-reporter and user-facing-reason projection failures use deterministic fallbacks and likewise cannot erase post-effect proof; those in-process callbacks remain trusted not to block indefinitely.

## Outcome

`OutcomeEnvelope` is the machine-readable result:

- `Outcome`
- `Report`
- `Receipts`
- `Details`
- `PriorAttempts`

Runtime-produced request, plan, receipt, detail, continuity, prior-attempt, and orchestration projections are bounded, deeply detached snapshots with read-only collection shells. Caller or planner mutation during execution or after return cannot rewrite historical proof.

The top-level envelope is the final attempt. `PriorAttempts` contains every complete earlier envelope in chronological order, including its receipts, observations, artifacts, events, diagnostics, and terminal outcome. `DetailEnvelope.RunAttempts` remains the compact cross-attempt index.

`DetailEnvelope` includes plan versions, refinements, observations, artifacts, batches, events, validation issues, run-attempt summaries, grant-consumption evidence, and the first event-delivery failure if one occurred.

Downstream systems should consume `OutcomeEnvelope`, not console text.

## Invariants

These are intentionally covered by tests:

- Plan validation failure prevents tool execution.
- Unknown tools fail before execution.
- Every real tool dispatch has a canonical terminal receipt, including cancellation after dispatch.
- Complete prior attempt envelopes remain reachable from the final envelope.
- No mutation repeats unless idempotency and exact tool authorization are both present.
- Success requires explicit completion-evaluator satisfaction and resolved selected evidence.
- Untrusted tool identity, evidence, status, and mutable payload claims are normalized or rejected.
- Planner projections and security authority come from one compiled, canonically hashed manifest.
- Registration drift, insufficient grants, and disallowed planner/data boundaries fail before tool dispatch.
- Sensitive grants are invocation-bound, atomically consumed once across snapshots sharing one in-memory grant state, and preserved with their issued authority as attempt evidence before dispatch; durable/global replay prevention remains host-owned.
- External planner calls require an explicit boundary policy; an empty allowance permits no classified data.
- `IEventSink` exceptions or blocking delivery cannot erase or indefinitely delay the authoritative in-memory proof ledger; outcome reporters and reason projectors remain trusted in-process callbacks.
- Report prose is not proof.
- Read-only batches are recorded and bounded.
- Batch failures do not invent success.
- Outcome JSON includes batch and dependency fields.
- Core has no CLI, campaign, dungeon, workbench, maze, Gemini SDK, MCP, storage, or logging dependency.

## Minimal Host Construction

```csharp
var runner = new AgenticaRunner(
    planner,
    toolCatalog,
    eventSink,
    outcomeReporter,
    executionPolicy,
    completionEvaluator);

var envelope = await runner.RunAsync(new RunRequest("Do bounded work."));
```

## Orchestration

The generic orchestration API is an Incubating experimental surface. It is excluded from current product claims pending broader host integration, durable/public-contract decisions, and measured reliability.

Higher-level orchestration should construct separate Agentica runs.

It should pass compact scoped context into `RunRequest.Context`, inspect the resulting `OutcomeEnvelope`, and then decide the next bounded objective.

It should not choose tool calls, bypass validation, or rewrite plans mid-run.

The incubating generic orchestrator now enforces a nonempty, kind-valid acceptance contract for every executable task and a nonempty global definition of done. A task cannot become complete merely because a custom evaluator says `Accepted`: declared criteria are independently evaluated and every cited evidence reference must resolve in the child envelope or host snapshot. Host-state values use bounded type-preserving structural comparison; values that merely have the same `ToString()` representation are not equal. Orchestration success additionally requires the aggregate definition of done to resolve against accepted child proof and current host state.

Initial/refinement planner failures, invalid graphs, invalid or unsupported graph mutations, host projection failures, context-compiler failures, child-executor failures, acceptance failures, and definition-of-done failures normalize into a truthful orchestration envelope. Cancellation remains cancellation and process-integrity failures such as out-of-memory are not swallowed. The last valid plan and every retained bounded child envelope remain reachable. The orchestrator freezes a dispatch record before each child call and replaces it only after the returned envelope crosses the bounded snapshot boundary; throw, null, cancellation, malformed proof, or proof overflow leaves an explicit partial/indeterminate dispatch record rather than an empty history. Only the eight advertised transactional mutation kinds are supported; model-authored `MarkTaskAccepted` and `MarkTaskBlocked` authority mutations do not exist.
