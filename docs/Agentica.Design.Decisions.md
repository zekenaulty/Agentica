# Agentica Design Decisions

This document records decisions that should keep Agentica small while still moving toward the real goal: an LLM-driven planner/executor runtime.

The decisions are meant to constrain future implementation work. They are not an architecture ceremony and they should not create new projects by themselves.

## Decision 001: Package-First, In-Process-First

Agentica is a compact .NET runtime package for bounded tool-using workflows.

The first working form is an in-process library used by a console host. It is not shaped around microservices, storage, queues, auth, deployment, or transport.

Current solution shape:

```text
  Agentica.slnx
  Agentica/                  runtime package
  Agentica.CLI/              console proof host
  Agentica.Clients/          provider SDK isolation for LLM integrations
```

Do not add projects such as `Agentica.Core`, `Agentica.Runtime`, `Agentica.Tools`, `Agentica.Storage`, or `Agentica.Events` unless a real packaging or dependency boundary proves it is necessary.

Use folders and namespaces inside `Agentica` to carry deep semantic meaning without project bloat.

## Decision 002: Agentica Owns The Workflow Proof Envelope

Agentica owns the internal workflow execution envelope:

```text
RunRequest
  -> WorkflowPlan
  -> validated PlanStep sequence
  -> query/read tools
  -> observations
  -> plan refinement
  -> action/mutation tools
  -> receipts
  -> OutcomeEnvelope
```

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
- Operator-visible events.

The host owns:

- Domain state.
- Domain vocabulary.
- Tool implementations.
- Persistence.
- Approval behavior.
- Product policy.
- Presentation and final voice.
- Quotas, usage accounting, metrics, and rate-limit economics.

Execution truth comes from observations, receipts, artifacts, validation issues, and terminal state. It does not come from narration.

Core rule:

> The model proposes. Agentica validates. Tools execute. Receipts prove. Outcome reports narrate, but never prove.

## Decision 003: MCP Is Adapter Or Hosting Layer, Not Runtime Identity

Agentica is not an MCP server as its core identity.

Agentica may later be exposed through MCP as a tool such as `agentica.run`. Agentica may also consume remote MCP tools by adapting `tools/list` descriptors into `ToolDescriptor` objects and `tools/call` results into `ToolResult`, `Observation`, `Artifact`, and `Receipt` objects.

There are two envelopes:

- MCP envelope: external JSON-RPC transport, lifecycle, capability negotiation, and tool call wrapper.
- Agentica envelope: internal workflow execution proof, plan versions, receipts, observations, refinements, details, and report.

The Agentica runtime package must not depend on MCP SDK types.

MCP support belongs in a later boundary adapter after the core run contract is proven.

## Decision 004: Deterministic Planning Is A Harness, Not The Destination

The first console slice should be deterministic so the runtime contract can be tested without provider variance.

That does not mean Agentica should spend many slices avoiding LLM planning. LLM planning and reasoning are the product.

`Agentica.Clients` bridges LLM inference into Agentica contracts. It gives the model Agentica-shaped problem-solving surfaces: objective, tool descriptors, effects, observations, receipts, current plan state, and required structured output schema. The model reasons and proposes. The runtime validates and executes.

Provider-call reliability belongs in `Agentica.Clients`, not in the runtime execution loop. Transient LLM generation failures should be retried at the client boundary before `PlannerUnavailable` reaches Agentica. Runtime blocked retries are for workflow blockage and recovery, not for masking temporary provider transport failures.

The first client retry policy uses a deliberately conservative generation call timeout of 10 minutes. That timeout bounds one `ILlmClient.GenerateAsync` call, including retries. It is a local reliability guard for early provider smoke testing and should later become configurable.

Cutover rule:

> Add LLM planning as soon as Agentica can reject invalid model output before any mutation-capable tool runs.

Sequence:

1. Deterministic envelope proof: request, plan, query, observation, refinement, action, receipt, outcome.
2. Fail-closed plan validation: unknown tools, invalid effects, malformed steps, bad dependencies, and missing approvals stop before execution.
3. LLM initial planner behind async `IWorkflowPlanner`: the model proposes a `WorkflowPlan`; Agentica validates it.
4. LLM refinement loop: observations feed a model-backed continuation decision such as continue, refine, stop blocked, ask, or finish.
5. LLM outcome report: report generation summarizes receipts, observations, artifacts, blockers, and stop reason.

Keep the deterministic planner permanently as a regression fixture and safety baseline.

## Decision 005: Nanda Planning System Is A Meta-Model, Not A Runtime Dependency

Nanda's `resources/plans` system has a useful meta relationship to Agentica. It is a slow, explicit, human-readable version of what Agentica needs to do at runtime:

```text
plan folder
  -> plan.md
  -> steps/index.md
  -> step folders
  -> decisions
  -> risks
  -> validation
  -> artifacts
  -> compiled review projection
```

Value:

- Folder scope keeps plans bounded.
- Stage state makes work status explicit.
- Step folders force visible execution slices.
- Decisions and risks keep tradeoffs near the plan.
- Validation files keep acceptance separate from intent.
- Artifacts preserve proof and supporting material.
- The compiler produces a reviewable projection without making the compiled file the source of truth.

Weight:

- Stage folders are too heavy for every Agentica runtime run.
- Markdown folder trees are too heavy as runtime state.
- A compile script is useful for human review, but the runtime needs structured objects, not compiled prose.
- Large plan folders can become their own project-management system instead of the execution substrate.

Agentica should harvest the discipline, not the filesystem workflow.

Mapping:

```text
Nanda plan folder       -> AgenticaRun scope
plan.md                 -> RunRequest + WorkflowPlan summary
steps/index.md          -> ordered PlanStep set
step folder             -> PlanStep plus step-local artifacts/receipts
decisions/              -> PlanRefinement + ContinuationDecision + stop rationale
risks/                  -> ValidationIssue + Blocker + StopReason
validation/             -> plan validation + completion conditions
artifacts/              -> Artifact + Receipt + EvidenceRef
compiled plan           -> OutcomeEnvelope + DetailEnvelope review projection
stage transition        -> RunStatus transition and terminal RunOutcome
```

Agentica runtime representation should be compact:

- `WorkflowPlan` is the executable plan contract.
- `PlanStep` is the executable slice.
- `PlanRefinement` records why the plan changed.
- `Receipt` and `Artifact` prove what happened.
- `OutcomeEnvelope` is the compiled review projection.

Agentica should not create a `resources/plans` clone inside the runtime.

## Decision 006: Visible Planning Artifacts Are Not Chain-Of-Thought

Agentica needs visible planning artifacts because operators and downstream systems need to know what was planned, what was checked, what changed, and why the run stopped.

Visible artifacts should include:

- Objective interpretation.
- Selected tools and rejected tools.
- Plan steps and expected outputs.
- Validation issues.
- Observations used for refinement.
- Plan refinement reason.
- Stop reason.
- Evidence references.
- Outcome report claims and their evidence.

Visible artifacts should not include hidden model chain-of-thought or raw provider thinking text.

If an LLM is used, it should emit structured planning objects and concise rationale fields grounded in evidence refs. Agentica stores those objects, not private reasoning traces.

Gemini thought summaries, when requested through `Agentica.Clients`, are diagnostic model output. They are not execution proof and must not replace receipts, observations, artifacts, validation issues, or stop reasons.

## Decision 007: Planning Must Stay Bounded

Agentica is a bounded planner/executor, not an open-ended autonomous loop.

Core execution policy should include only hard runtime guards:

- Max steps.
- Max refinements.
- Timeout.
- Cancellation.
- Planning mode.
- Approval wait behavior.
- Terminal stop rules.

Quotas, token budgets, usage pricing, rate limits, dashboards, and aggregate metrics belong at the host/plugin boundary.

The runtime must be able to stop honestly:

- Succeeded.
- Partially complete.
- Blocked.
- Planner unavailable.
- Waiting for approval.
- Failed.
- Cancelled.
- Plan invalid.

## Decision 008: Planning Mode Is Cadence, Not Authority

Agentica supports named planning modes through `ExecutionPolicy.PlanningMode`. This is a hook for future planning styles, not a separate planner abstraction.

Current modes:

- `Stepwise`: refine after every observation. This supports one-next-step planners and LLM-driven iterative execution.
- `QueryAndBlockerDriven`: refine after query observations or blocker/refusal observations.
- `BlockerDriven`: refine only when a tool result reports a blocker/refusal observation.
- `PlanOnly`: do not refine during execution.

Planning mode decides when the runner asks `IWorkflowPlanner.RefinePlanAsync` for another plan. It does not decide whether a run is truly successful. Success still needs to be grounded in receipts, artifacts, stop reasons, and the host-provided tool/outcome model.

## Decision 009: Harness Pressure Belongs In Generic Runtime Seams

Host harnesses such as static quests or maze stages are useful because they pressure planning, observation, recovery, and evidence. They must remain host scenarios, not Agentica runtime vocabulary.

The generic Agentica seams that support those harnesses are:

- Tool input contracts on `ToolDescriptor`.
- Pre-execution input validation.
- Tool effect policy through `ExecutionPolicy`.
- Completion evaluation through `ICompletionEvaluator`.
- Bounded continuation planning when completion is not proven.
- Bounded blocked retries through `ExecutionPolicy.MaxBlockedRetries`.
- Planner context shaping for recent observations and receipts.

Agentica may know that a tool requires a string input called `direction`; it must not know what north means in a maze.

Agentica may require an artifact kind before success; it must not own quest objective rules.

Agentica may bound planner-visible receipts and observations; it must not own fog of war or pathfinding.

If a run ends blocked, Agentica may start a bounded retry attempt. A retry is a fresh Agentica run with the same host tool catalog and host state, `RequestOrigin.Agent`, and an `agentica.retry` request context that describes only the immediately previous blocked attempt. This lets the planner reason about how to unblock or resume without carrying stale blockers forever. The retry still proposes a normal `WorkflowPlan`; Agentica still validates every tool, input, effect, receipt, and completion claim.

Extra thinking turns must stay inside the normal planning envelope. If the planner needs more granular reasoning, it returns a normal `WorkflowPlan` or `PlanRefinement` with an auditable reason code such as `ambiguous_action`, `blocked`, `low_confidence`, `conflicting_signals`, `completion_check`, `continue`, `resource_risk`, or `retry_unblock`. There is no hidden chain-of-thought store, no unbounded reasoning loop, and no execution path that bypasses plan validation.

## Decision 010: First Proof Slice Event Story

The first executable proof slice should produce this event shape:

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

The first slice proves the envelope. The next slices add validation and then LLM planning.

It should not wait for:

- MCP.
- Database recording.
- Vector memory.
- Multi-agent planning.
- Complex dependency graphs.
- Background queues.
- Dashboards.
