# Agentica Runtime Contracts

This is a compact reference for packages and hosts that want to build on Agentica without depending on CLI harnesses.

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

`ToolEffectPolicy` controls allowed effects. The default policy is local-first and rejects external side effects and destructive tools.

Mutation-capable work must be `ToolKind.Action` and must declare the matching effect.

## Execution Policy

`ExecutionPolicy` bounds a run:

- max steps
- max refinements
- timeout
- planning mode
- max plan continuations
- max blocked retries
- batch size
- parallelism
- effect policy
- planner-visible context limits

`PlanningRequest.ExecutionContext` gives planners compact run-local execution state, including completed step ids and completed step summaries. This is the only supported way for refined or continuation plans to depend on work completed by earlier plan versions in the same Agentica run.

Keep defaults conservative for library use. Increase limits at the host boundary only when the host can explain why.

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

`ICompletionEvaluator` decides whether plan exhaustion is enough to complete.

A narrative report is not completion proof. If a host needs an artifact or host state check, require it through the completion evaluator or host acceptance logic.

## Outcome

`OutcomeEnvelope` is the machine-readable result:

- `Outcome`
- `Report`
- `Receipts`
- `Details`

`DetailEnvelope` includes plan versions, refinements, observations, artifacts, batches, events, validation issues, and run attempts.

Downstream systems should consume `OutcomeEnvelope`, not console text.

## Invariants

These are intentionally covered by tests:

- Plan validation failure prevents tool execution.
- Unknown tools fail before execution.
- Every completed step has a receipt.
- Success requires completion evaluator satisfaction.
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

Higher-level orchestration should construct separate Agentica runs.

It should pass compact scoped context into `RunRequest.Context`, inspect the resulting `OutcomeEnvelope`, and then decide the next bounded objective.

It should not choose tool calls, bypass validation, or rewrite plans mid-run.
