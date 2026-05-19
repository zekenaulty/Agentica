# Codex Goal: Agentica Event Intent Surface

Status: implemented for the current slice.

## Mission

Add a public, structured execution-intent surface to Agentica's existing lifecycle events without turning Agentica into an event bus, memory system, persistence layer, or platform.

The goal is to make a run answer:

```text
What did Agentica decide to do?
Why was that action publicly justified?
What did Agentica expect to learn or prove?
What tool surface was visible when that decision was made?
What receipt, observation, artifact, or validation issue backs the later outcome?
```

The answer must be usable by hosts, UI, logs, future memory projections, and debugging tools without exposing hidden chain-of-thought or requiring consumers to chase opaque internal Agentica-only references.

## Current State

Agentica now has the execution lifecycle spine:

```text
run.created
request.accepted
plan.created
batch.started
step.started
observation.made
receipt.emitted
batch.completed
plan.refined
outcome.reported
run.succeeded / run.blocked / run.failed / run.stopped
```

Current event shape now includes sequence/context/source, public `ExecutionIntent`, optional `UserFacingReason`, diagnostics, payload, and envelope-resolvable evidence refs. Tool-surface snapshots and planning frames are captured in the outcome detail envelope for the planner calls that used them.

Original baseline event shape:

```csharp
public sealed record ExecutionEvent(
    string EventId,
    string Type,
    DateTimeOffset At,
    IReadOnlyDictionary<string, string> Data);
```

Closed in this slice:

- Enriched execution events with source, sequence, context, payload, diagnostics, and evidence refs.
- `ExecutionIntent` prompt support plus fallback from `PlanStep.Reason`.
- `UserFacingReason` projection for human-facing output.
- Planner-visible `ToolSurfaceSnapshot` capture.
- Host-projected `PlanningFrame` capture.
- Envelope-resolvable references for events, receipts, observations, artifacts, validation issues, and tool surfaces.
- No ghost references requirement for this slice: exposed ids resolve inside the outcome envelope, while host-owned capability surfaces remain host data.

Remaining limitations:

- Agentica still does not provide event transport, persistence, replay, subscriptions, or memory projection.
- The default user-facing reason projector is intentionally generic; rich human-facing language belongs to host projectors.
- `ToolSurfaceSnapshot` captures what Agentica exposed to the planner, not the host's hidden/blocked/demoted capability universe.
- Planner intent remains public explanation only; hidden chain-of-thought and provider-private reasoning are not exposed as runtime truth.

## Core Boundary

Agentica owns:

- Run lifecycle facts.
- Public execution intent for validated plan steps.
- Planner-visible tool surface snapshots.
- Envelope-resolvable references between events, plans, steps, receipts, observations, artifacts, validation issues, and tool surfaces.
- Diagnostics needed to debug Agentica execution.

Hosts own:

- Domain state.
- Full private capability universe.
- Hidden, blocked, demoted, or policy-filtered capabilities.
- Persistence, replay, event bus transport, memory projection, UI routing, and product policy.
- Richer capability-surface snapshots when the host needs to reason about tools that were not exposed to Agentica.

Agentica emits structured lifecycle facts. Everything else is a projection the host owns.

## Non-Negotiable Constraints

1. Do not build an event bus in Agentica.
2. Do not add persistence, replay, queues, durability, memory, vector storage, or transport semantics.
3. Do not remove or rename current event wire names in this slice.
4. Do not remove current `ExecutionEvent.Data` keys used by CLI and watch sinks.
5. Do not expose raw provider thought summaries or hidden chain-of-thought.
6. Do not expose hidden host/oracle state through intent, tool surfaces, diagnostics, or payloads.
7. Do not make hosts depend on opaque Agentica internal codes as the only explanation for a refinement decision.
8. Do not create ghost references: every Agentica-owned id referenced by an event must resolve inside the same `OutcomeEnvelope`.

## Language Rule

Use "intent" for the public explanation surface.

Avoid naming this feature "chain-of-thought exposure" or "reasoning trace."

`ExecutionIntent` is a compact, public, operator-facing artifact:

```csharp
public sealed record ExecutionIntent(
    string Action,
    string Rationale,
    string? ExpectedOutcome = null);
```

Intent fields mean:

```text
Action
  What Agentica is about to do.

Rationale
  Why this action is justified from public request context, tool descriptors,
  observations, receipts, artifacts, validation issues, or host-provided public context.

ExpectedOutcome
  What receipt, observation, artifact, validation result, or decision this action is expected to produce.
```

Intent must not contain:

- raw private reasoning
- provider thought summaries
- system/developer prompt text
- hidden oracle state
- hidden rewards, hidden route facts, hidden dependency chains, or answer keys
- future receipts or unearned artifacts
- secrets
- speculative facts stated as known facts

## Ghost Data Rule

Expose data the host can use.

Do not expose reference-only data that requires the host to know Agentica internals before it can interpret the run.

Every event field must fall into one of these categories:

```text
Host-meaningful
  Tool descriptor, receipt, observation, artifact, request context, policy summary,
  public rationale, public blocker, public validation message.

Envelope-resolvable
  run id, plan id, step id, batch id, receipt id, observation id, artifact id,
  validation issue id/code when paired with message, tool surface id.

Diagnostic-only
  Planner failure kind, validation code, exception class, guard name.
  These may help Agentica debugging, but must not be the only host-facing explanation.
```

If an event references:

```text
planId
stepId
batchId
receiptId
observationId
artifactId
toolSurfaceId
validationIssueCode
```

then the same `OutcomeEnvelope` must contain the referenced object or an explicit diagnostic message explaining why it cannot.

## Target Event Shape

Keep the existing constructor shape compatible, then add optional enriched fields.

Recommended first-slice shape:

```csharp
public sealed record ExecutionEvent(
    string EventId,
    string Type,
    DateTimeOffset At,
    IReadOnlyDictionary<string, string> Data)
{
    public long? Sequence { get; init; }

    public string? Source { get; init; }

    public ExecutionEventContext? Context { get; init; }

    public ExecutionIntent? Intent { get; init; }

    public IReadOnlyList<EvidenceRef> EvidenceRefs { get; init; } = [];

    public IReadOnlyDictionary<string, object?> Payload { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    public ExecutionDiagnostics? Diagnostics { get; init; }
}
```

`Data` remains the compatibility surface for existing CLI/watch consumers.

`Payload` is for structured, host-usable, event-local details. It must not duplicate full receipts, observations, artifacts, plans, or tool surfaces when ids are enough.

## Event Context

Add a compact context object:

```csharp
public sealed record ExecutionEventContext(
    string? RunId = null,
    int? AttemptNumber = null,
    string? PlanId = null,
    int? PlanVersion = null,
    string? StepId = null,
    string? BatchId = null,
    string? ToolId = null,
    string? ReceiptId = null,
    string? ObservationId = null,
    string? ArtifactId = null,
    string? ToolSurfaceId = null);
```

Populate only fields that are meaningful for that event.

Do not require hosts to parse `Data` for new code. Keep `Data` only for backward compatibility.

## Diagnostics

Diagnostics should be explicit and secondary:

```csharp
public sealed record ExecutionDiagnostics(
    string? Code = null,
    string? Message = null,
    string? ErrorClass = null,
    string? FailureKind = null);
```

Use diagnostics for:

- planner exceptions
- validation failures
- tool exception wrappers
- policy guard failures
- cancellation/timeout classification

Diagnostics are not proof and not host memory.

When diagnostics include an internal code, also include a readable message.

## Tool Surface Snapshot

Capture the planner-visible tool surface used for each planning decision.

This is not the host's full private capability universe. It is the exact public tool descriptor set Agentica supplied to the planner.

Boundary:

```text
ToolSurfaceSnapshot
  Agentica-owned.
  Records what Agentica actually exposed to the planner.
  Built from PlanningRequest.ToolDescriptors plus the bounded planning context.

ActiveCapabilitySurface / ContextSurfaceReceipt
  Host-owned.
  Records the richer domain surface the host compiled before it chose what Agentica should see.
  May include available, preferred, blocked, denied, demoted, hidden, or unavailable capability bindings.
```

Agentica must not try to model host-hidden capability state. If a capability was hidden, demoted, blocked, denied, or filtered before `ToolDescriptor` projection, that belongs in the host surface record, not in Agentica core.

Recommended runtime type:

```csharp
public sealed record ToolSurfaceSnapshot(
    string SurfaceId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ToolDescriptor> ToolDescriptors,
    PlanningExecutionContext ExecutionContext,
    IReadOnlyList<EvidenceRef> ObservationRefs,
    IReadOnlyList<EvidenceRef> ReceiptRefs,
    IReadOnlyDictionary<string, object?> PolicySummary);
```

Add it to `DetailEnvelope`:

```csharp
IReadOnlyList<ToolSurfaceSnapshot> ToolSurfaces
```

Events should reference tool surfaces by `toolSurfaceId`, not duplicate the full descriptor list.

This enables future questions such as:

```text
Was the selected tool visible to the planner?
Was a read-only tool available before a mutation-capable tool?
Was there any tool capable of producing the missing artifact?
Did the planner choose poorly, or was the required tool not exposed?
```

It does not answer:

```text
Did the host have a better private capability that it intentionally hid?
Was a demoted capability excluded for budget, safety, or abstraction-level reasons?
Was a blocked capability withheld because explaining it would leak hidden state?
```

Those questions belong to a host-owned `ActiveCapabilitySurface` or `ContextSurfaceReceipt`.

If the host wants to connect the two layers, it may include a public-safe `surfaceId`, `surfaceHash`, or `contextSurfaceReceiptId` in `RunRequest.Context`, a receipt, or an artifact. Agentica may preserve that value as ordinary context, but it must not interpret hidden/blocked/demoted capability semantics as core runtime concepts.

## Prompt Changes

### Workflow Planner System Instruction

Add:

```text
Every plan step must include public execution intent.

Execution intent is not hidden chain-of-thought. It is an operator-facing explanation of:
- action: what Agentica is about to do
- rationale: why this action is justified from public request context, tool descriptors, observations, receipts, artifacts, validation issues, or host-provided public context
- expectedOutcome: what receipt, observation, artifact, validation result, or decision this step is expected to produce

Do not include raw private reasoning, provider thought summaries, system/developer instructions, hidden oracle state, future receipts, secrets, or speculative facts as intent.
Keep each field concise.
```

### Initial Workflow Plan JSON

Change step shape from:

```json
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
```

to:

```json
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
    "action": "Inspect the current decoded state.",
    "rationale": "The objective requires changing a decoded field, and current public state is needed before selecting a mutation.",
    "expectedOutcome": "A read-only observation describing the relevant decoded fields."
  }
}
```

Keep `reason` during the transition. New code should prefer `intent`.

### Workflow Refinement Prompt

Add:

```text
For refined steps, intent.rationale must explicitly connect the new step to the latest public observation or receipt.
Do not say "continue" or "handle observation" unless the rationale names the public blocker, uncertainty, or evidence that changed the plan.
```

Refinement reason code remains separate from step intent.

## Contract Changes

Add:

```csharp
PlanStep.Intent
WorkflowPlanStepJsonContract.Intent
ExecutionIntentJsonContract
```

Mapping rule:

```text
If intent is present, store it.
If intent is missing but reason is present, create an intent fallback:
  Action = "Invoke {toolId}."
  Rationale = reason
  ExpectedOutcome = null
If both are missing, leave intent null.
```

Do not fail the first implementation solely because `intent` is missing. Add stricter validation only after live planner reliability is proven.

## Runner Event Population

Populate enriched fields at existing lifecycle points.

### run.created

Context:

```text
runId
attemptNumber
```

Source:

```text
Runner
```

Payload:

```text
request origin
hasContext
```

### plan.created

Context:

```text
runId
planId
planVersion
toolSurfaceId
```

Intent:

```text
Action: "Create an executable plan slice."
Rationale: plan description or planning reason, bounded to public-safe text.
ExpectedOutcome: "A validated plan slice that Agentica can execute against the visible tool surface."
```

Payload:

```text
description
step count
step ids
tool ids
```

Do not duplicate full plan. The full plan is already in `DetailEnvelope.PlanVersions`.

### step.started

Context:

```text
runId
planId
planVersion
stepId
batchId
toolId
toolSurfaceId
```

Intent:

```text
step.Intent, or fallback from step.Reason
```

Payload:

```text
kind
effect
requiresApproval if available from tool descriptor
inputSummary or redacted input summary
```

Keep `Data["step"]`, `Data["tool"]`, and `Data["batch"]` compatibility keys.

### observation.made

Context:

```text
runId
stepId
toolId
observationId
```

Evidence:

```text
observation ref
```

Payload:

```text
observation kind
summary if available
```

### receipt.emitted

Context:

```text
runId
stepId
toolId
receiptId
observationId if result included one
artifactId if result included one
```

Evidence:

```text
receipt ref
observation ref if present
artifact ref if present
```

Payload:

```text
receipt status
receipt message
durationMs when available
```

Do not duplicate full receipt data unless the event is explicitly diagnostic. The receipt is already in the envelope.

### plan.refined

Context:

```text
runId
fromPlanId
toPlanId
planVersion
toolSurfaceId
```

Intent:

```text
Action: "Refine the current plan."
Rationale: normalized refinement reason plus latest public observation/receipt summary.
ExpectedOutcome: "A new validated plan slice that responds to the latest evidence."
```

Evidence:

```text
observation ref
receipt ref
```

Payload:

```text
reason code
fromPlanId
toPlanId
```

### outcome.reported and terminal events

Context:

```text
runId
```

Evidence:

```text
completion evidence refs
validation issue refs or diagnostics if relevant
```

Payload:

```text
status
stopReason
blockers
completed step count
receipt count
artifact count
```

## Sequence Numbers

Add monotonically increasing per-run-attempt sequence numbers to emitted events.

Timestamps remain useful, but sequence is the authoritative in-run order.

For parallel batches, `step.started` events are sequenced before tool tasks begin, and `receipt.emitted` events are sequenced in result-recording order after `Task.WhenAll`.

## Event Source Naming

Use stable source names:

```text
Runner
Planner
Tool:{toolId}
CompletionEvaluator
OutcomeReporter
Orchestrator
TaskPlanner
TaskAcceptanceEvaluator
WorkContextCompiler
```

Do not use class names as the primary source unless they are already public contract names.

## Orchestration Follow-Up

This goal focuses first on Agentica runner events.

After runner events stabilize, apply the same model to `Agentica.Orchestration`:

```text
task_graph.created
orchestration.started
task.selected
run.dispatched
task.acceptance.evaluated
work_context.compiled
task_graph.refined
orchestration.completed
```

Task-level events should carry task-level intent, not tool-level step intent.

If task graph planning adds intent later, use:

```csharp
TaskNode.Intent
TaskGraphRefinement.Intent
```

## Implementation Milestones

### Milestone 1: Types Only

- Add `ExecutionIntent`.
- Add `ExecutionEventContext`.
- Add `ExecutionDiagnostics`.
- Add optional enriched fields to `ExecutionEvent`.
- Add `ToolSurfaceSnapshot`.
- Add `ToolSurfaces` to `DetailEnvelope`.

Do not change prompts yet.

### Milestone 2: Runner Population

- Add per-run event sequence.
- Populate `Source`, `Context`, and `Payload` for existing event emissions.
- Preserve all existing `Type` and `Data` behavior.
- Capture tool surface snapshots when creating planning requests.
- Reference tool surface ids from plan-related events.

### Milestone 3: Prompt And Contract Support

- Add `PlanStep.Intent`.
- Add `WorkflowPlanStepJsonContract.Intent`.
- Update initial and refinement prompt JSON shapes.
- Add fallback from `reason` to `intent`.
- Keep `reason` for compatibility.

### Milestone 4: Evidence And Ghost-Reference Checks

- Add tests that every event reference resolves inside the final `OutcomeEnvelope`.
- Add tests that diagnostics include messages when internal codes are present.
- Add tests that `toolSurfaceId` resolves to a `ToolSurfaceSnapshot`.

### Milestone 5: CLI And Logs

- Ensure `ConsoleEventSink` remains readable.
- Ensure MazeQuest watch sink still works.
- Ensure run logs serialize enriched event fields.
- Do not require UI changes in this slice.

### Milestone 6: Orchestration Planning Notes

- Document the matching orchestration event model.
- Do not implement orchestration events until runner event enrichment is stable.

## Required Tests

Add or update tests for:

1. Existing event ordering remains unchanged.
2. Existing event `Data` keys remain available.
3. `step.started` includes `ExecutionIntent` when planner supplies it.
4. `step.started` falls back from `PlanStep.Reason` when `Intent` is missing.
5. `plan.created` references a resolvable `ToolSurfaceSnapshot`.
6. `ToolSurfaceSnapshot` contains the planner-visible tool descriptors, limited observations, limited receipts, execution context, and policy summary.
7. Every event reference resolves inside `OutcomeEnvelope`.
8. Validation failures expose readable diagnostics and do not require host consumers to understand only internal codes.
9. Thought summaries from `Agentica.Clients` are not stored as execution intent or proof.
10. Existing MazeQuest/Quest/HexQuest harness tests continue passing.

## Verification Commands

Run:

```powershell
dotnet build Agentica.slnx
dotnet test Agentica.slnx
dotnet run --project Agentica.CLI -- run "Create a two-step workflow that queries state and then acts"
```

For a harness smoke test:

```powershell
dotnet run --project Agentica.CLI -- hexquest run record_scope_conflict_v2
```

Only run live Gemini tests when explicitly requested and credentials are configured.

## Completion Condition

This goal is complete when:

1. Existing event consumers still work.
2. Existing tests pass.
3. Enriched events serialize in outcome envelopes and run logs.
4. `ExecutionIntent` is captured from planner output when available.
5. `reason` fallback preserves deterministic and legacy planner behavior.
6. Planner-visible tool surfaces are captured once and referenced by id.
7. No event contains unresolvable Agentica-owned references.
8. Host-useful fields are self-contained or envelope-resolvable.
9. Diagnostic-only fields include readable messages and do not become host refinement requirements.
10. No hidden chain-of-thought, provider thought summaries, hidden oracle data, or future receipts are stored as public intent.
11. Agentica remains package-first and does not gain event bus, storage, memory, queue, replay, or transport responsibilities.

## Explicit Non-Goals

- Event bus implementation.
- Durable event store.
- Replay engine.
- Memory graph or distillation.
- MCP adapter changes.
- UI redesign.
- Raw chain-of-thought exposure.
- General host capability registry.
- Capturing host-hidden tools inside Agentica core.
- Modeling host blocked, denied, demoted, hidden, or unavailable capability bindings inside Agentica core.
- Making `ExecutionIntent` mandatory for all planners in the first slice.

## Design Rationale

This slice makes Agentica more inspectable without changing its identity.

`ExecutionIntent` gives operators and hosts a concise public answer to "why this step now?"

`ToolSurfaceSnapshot` gives future analysis enough context to distinguish planner weakness from missing or filtered capabilities.

Envelope-resolvable references prevent event streams from becoming ghost data.

Diagnostics stay available for agentic debugging, but they do not become the host's semantic refinement model.

The host remains the owner of memory, persistence, UI, hidden state, and richer capability surfaces.
