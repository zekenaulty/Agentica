# Codex Goal: Agentica Capability Surface Engine

## Mission

Define the `CapabilitySurfaceEngine`: a deterministic host-binding layer that compiles host reality into the constrained decision surface consumed by Agentica planners, runners, orchestration planners, and future operator views.

The cockpit metaphor is useful:

```text
Host reality -> cockpit -> pilot decision -> host execution -> receipts -> cockpit update
```

But the concrete abstraction is not a UI, HUD, persona, agent, or LLM call.

The concrete abstraction is:

```text
CapabilitySurfaceEngine
  compiles a current, public, scoped, policy-aware capability surface
  from host state, tool descriptors, receipts, actor scope, goal, and budget.
```

The text cockpit is only one rendering of that surface.

## Reference Loop

The working control loop is:

```text
1. Goal intake
2. Scope and context
3. Plan
4. Commit gate
5. Execute
6. Observe
7. Evaluate / reflect
8. Update state
```

Do not overstate this as an industry standard. Treat it as a useful plan-execute control-loop reference.

The `CapabilitySurfaceEngine` is not one numbered step in that loop. It is invoked around the decision points:

```text
before planning
before task dispatch
before runner decisions
after receipts
after state updates
before completion validation
```

The simpler shape is:

```text
State + Receipts + Scope + Goal + Policy + Actor + Tool Descriptors
  -> CapabilitySurfaceEngine
  -> ActiveCapabilitySurface / CockpitFrame
  -> Planner chooses within that frame
  -> Host executes
  -> Receipts mutate state
  -> CapabilitySurfaceEngine recompiles the next frame
```

## Why This Exists

Agentica already works without this layer. The runner can plan, execute tools, observe receipts, and refine. The orchestration layer can decompose work and delegate tasks.

The problem is not basic correctness. The problem is consistency, safety, and host authoring cost.

Without a shared surface engine, every host tends to:

- Expose raw tools differently.
- Dump too much context into prompts.
- Forget to demote primitives when safe behaviors exist.
- Leak hidden oracle facts through blocked reasons.
- Let planners choose at the wrong abstraction level.
- Reimplement capability filtering and mode detection differently.
- Force agents to query repeatedly for context the host already knows.
- Turn prompt rendering into ad hoc domain logic.

The surface engine standardizes the binding process:

```text
host asks: what exists?
surface engine asks: what matters, safely, now?
planner asks: what should I choose from this frame?
host proves: what actually happened?
```

## Non-Goal: A Smart Adapter

The surface engine must not become a hidden agent.

It must not:

- Call an LLM.
- Infer a plan.
- Semantically match arbitrary goals to tools.
- Summarize world state with model-generated prose.
- Decide the next action.
- Execute work.
- Validate completion as true.
- Learn from history autonomously.
- Spawn sub-agents.
- Hide reasoning in a second internal loop.

It should:

- Apply deterministic rules.
- Group capabilities.
- Classify binding states.
- Enforce surface budget.
- Select public state projections.
- Produce mode, focus, pressure, signals, and capability bindings.
- Render stable planner-facing context.
- Emit or support provenance receipts.

Litmus test:

```text
If removing the LLM makes the layer stop making sense, the layer is too smart.
```

## Ownership Model

Use these ownership rules to avoid ambiguity:

```text
Host Reality Layer
  owns domain truth, hidden oracle state, raw state, legal actions, side effects, and receipts.

CapabilitySurfaceEngine
  owns deterministic public framing: what is visible, relevant, legal, preferred, blocked, denied, demoted, hidden, or unavailable now.

Behavior Contract Gate
  owns enforcement that a planner-selected action matches the current active surface before execution.

AgenticaRunner
  owns bounded run planning and tool-level execution flow.

Agentica.Orchestration
  owns task-level decomposition, delegation, task acceptance, and graph refinement.

OutcomeEvaluator
  owns host validation and completion artifact emission.
```

Do not say the cockpit owns reality. The host owns reality.

The cockpit owns the current public interpretation frame.

The gate enforces the current frame.

Receipts update reality and cause the frame to be recompiled.

## Core Types

These names are design vocabulary first. Do not promote all of them into core runtime types until multiple harnesses prove the same shape.

### DomainHarnessManifest

The cold, static host contract.

It describes what the host can expose in principle:

```text
domain id
domain version
actor types
state projection rules
capability definitions
capability groups
capability levels
binding rules
mode rules
receipt kinds
artifact kinds
completion gates
anti-leak rules
surface budget defaults
planner output levels
```

The manifest is not sent raw to the model.

### CapabilityDefinition

A host-declared capability that may become visible on an active surface.

Fields should be boring:

```text
id
display name
description
level
group
tool id or behavior id
input contract
effects
default budget cost
receipt expectations
artifact expectations
policy tags
primitive fallback relationship, if any
```

Capability levels:

```text
Primitive
  Single low-level host operation, such as move_forward or inspect_cell.

Behavior
  Bounded host-owned procedure, such as dig_line_return or explore_adjacent_cells.

Recipe
  Known parameterized procedure or cookbook entry.

CompositeBehavior
  Bounded sequence or graph of behavior-level work.

Task
  Orchestration-level work unit delegated to a runner.
```

### ActiveCapabilitySurface

The hot, scoped decision contract.

It describes what the planner may consider now:

```text
surface id
surface version
domain id and version
actor id
scope id
goal hash
mode
focus
pressure
state projection
signals
bindings
expected proof
planner output level
budget snapshot
rendering policy
source receipt ids
surface hash
```

### CockpitFrame

The human-readable or planner-facing rendering of an active surface.

A cockpit frame is not the source of truth. It is a projection.

Minimum frame sections:

```text
Mode
Focus
Objective
State Projection
Capability Bindings
Signals
Constraints
Expected Proof
Decision Instructions
```

### CapabilityBinding

A current binding between a capability definition and the active context.

Fields:

```text
capability id
binding state
reason
public evidence refs
input constraints
expected receipts
expected artifacts
priority
budget cost
planner visibility
```

Binding states:

```text
Preferred
  Legal, visible, and recommended for this goal, mode, focus, and state.

Available
  Legal and visible, but not specifically recommended.

Demoted
  Legal and visible, but discouraged because a safer, higher-level, cheaper, or more relevant capability exists.

Blocked
  Public preconditions are missing. Evidence may later unblock it.

Denied
  Policy forbids it under the current envelope.

Hidden
  Not planner-visible because exposing it would leak hidden state, exceed budget, or distract the planner.

Unavailable
  The host does not have this capability.
```

Hidden and unavailable are different:

```text
Hidden
  The capability exists, but the planner should not know about it now.

Unavailable
  The host cannot provide the capability.
```

### PlannerOutputLevel

The enforced abstraction level for planner output.

```text
ObservationOnly
  Planner may inspect, classify, or summarize. It must not propose executable work.

BehaviorInvocation
  Planner may choose one bounded host behavior and parameters.

RecipeInvocation
  Planner may choose one known recipe or procedure template and parameters.

CompositeBehaviorPlan
  Planner may propose a bounded sequence or graph of behavior-level work.

PrimitivePlanDryRun
  Planner may propose primitive steps for validation or explanation only.

PrimitivePlanLive
  Planner may execute primitive steps. This is an explicit recovery, debug, or benchmark mode.

TaskGraphPlan
  Planner may produce orchestration-level task nodes and dependencies.
```

Default rule:

```text
Embodied, hazardous, mutable, or long-running hosts should default to BehaviorInvocation or RecipeInvocation.
```

Primitive live planning must be opt-in.

## Mode, Focus, Pressure, Signals

The cockpit frame needs more than state and tools.

### Mode

Mode describes the current kind of work.

Examples:

```text
Inquiry
Exploration
Execution
Recovery
CompletionCheck
Debug
Blocked
AwaitingApproval
```

Mode changes what is shown and allowed.

Example:

```text
Mode = Recovery
  Prefer diagnostic and recovery behaviors.
  Demote ordinary progress behaviors.
```

### Focus

Focus describes the current center of attention.

Examples:

```text
current objective
current task
known blocker
resource constraint
failed behavior
completion proof
unknown context
```

Focus prevents surface bloat.

### Pressure

Pressure describes what is pushing decision-making.

Examples:

```text
budget
time
risk
uncertainty
hazard
missing proof
approval requirement
```

Pressure changes preferences.

Example:

```text
Pressure = high uncertainty
  Prefer inspect, probe, retrieve_context, or explore behaviors.
```

### Signals

Signals are compact facts derived from receipts and public state.

Examples:

```text
key acquired
return path established
inventory nearly full
exit still locked
last move refused
context insufficient
completion proof missing
```

Signals are not hidden chain-of-thought. They are public, receipt-backed, host-derived markers.

## Surface Compilation Lifecycle

The surface engine should run at defined points, not whenever a planner feels uncertain.

Required compilation points:

```text
Goal entry
  Build initial thin surface from objective, scope, policy, and host availability.

Before orchestration planning
  Build task-level surface for decomposition or pass-through decision.

At task dispatch
  Build task-scoped surface for the runner.

Before each runner decision
  Build current runner-level surface.

After each meaningful receipt
  Recompile blockers, preferences, mode, focus, signals, and expected proof.

Before completion
  Build completion-check surface and require host proof.
```

Avoid this pattern:

```text
agent -> agent -> agent
```

Prefer:

```text
surface -> planner -> host -> receipts -> surface -> planner
```

## Thin And Thick Surfaces

A surface may be intentionally thin or thick.

### Thin Surface

Use when the system lacks context.

Purpose:

```text
encourage bounded context acquisition
avoid false precision
avoid exposing irrelevant tools
```

Common preferred capabilities:

```text
retrieve_context
inspect_state
explore_adjacent
ask_clarifying_question
```

### Thick Surface

Use when the host has enough public state to support action.

Purpose:

```text
encourage execution
reduce unnecessary querying
show expected proof
focus behavior selection
```

Common preferred capabilities:

```text
execute_behavior
complete_objective
recover_from_known_blocker
```

The surface engine should not pretend a thin surface is thick. That produces blind action.

The surface engine should not keep a surface thin after enough evidence exists. That produces endless querying.

## Behavior Contract Gate

The surface is not only prompt guidance. It must be enforced.

The `BehaviorContractGate` sits between planner output and host execution.

It validates:

```text
planner output matches PlannerOutputLevel
selected capability exists on the active surface
binding state allows execution
parameters satisfy input constraints
policy permits the effect
budget permits the action
required approval is present
primitive controls are not used when behavior-level mode is required
blocked or denied capabilities are not executed
```

If validation fails:

```text
do not execute
emit validation issue or refused receipt
feed the issue into the next surface
allow planner refinement under budget
```

This is the piece that prevents the system from degrading back into "tools plus prompt."

## Negative Binding Safety

Negative bindings are useful but dangerous.

They tell the planner what not to do, but their reasons can leak oracle state.

Allowed reason sources:

```text
public policy
public missing observation
public resource gap
public blocker
current mode
current budget
current approval state
safer preferred behavior
```

Forbidden reason sources:

```text
hidden oracle state
hidden rewards
hidden route facts
hidden dependency chains
future receipts
answer-key task order
unearned completion artifacts
```

Examples:

```text
safe:
  unlock_castle is blocked because the public castle seal is still closed.

unsafe:
  unlock_castle is blocked because the silver key is hidden in forest_shrine_02.
```

If a true reason would leak hidden state, use a public-safe reason or hide the capability.

## Behavior As Contract Over Time

A behavior is not just a function call. It is a bounded commitment with lifecycle and proof.

Behavior contract:

```text
id
purpose
inputs
public preconditions
policy requirements
budget
progress receipts
terminal receipts
expected artifacts
cancel behavior
recovery behavior, optional
```

Lifecycle:

```text
surface rendered
planner selects behavior
gate validates request
host starts behavior
host emits start receipt
host executes internal primitive loop or state machine
host emits progress receipts
host completes, blocks, fails, or cancels
host emits terminal receipt
surface recompiles
planner completes, recovers, or chooses next behavior
```

Long-running hosts should expose:

```text
start_behavior
get_behavior_status
cancel_behavior
resume_behavior, later if needed
complete_objective or host-issued completion artifact
```

Blocking behavior tools are acceptable for small deterministic harnesses if they still emit receipt-backed progress and terminal evidence.

## Context Acquisition As Behavior

Context gathering should be represented as bounded behavior, not recursive wandering.

When a surface is thin, prefer context acquisition behaviors:

```text
retrieve_project_state
inspect_current_cell
scan_nearby
load_recent_receipts
ask_clarifying_question
```

These behaviors must have:

```text
scope
budget
stop condition
receipt output
surface mutation rule
```

Example:

```text
Initial mode:
  Inquiry

Preferred:
  retrieve_project_state

Receipt:
  loaded active tasks
  loaded recent run summary

Next mode:
  Analysis
```

The agent should not need to invent a context-gathering loop from scratch.

## Nested Consumers

Do not force runner and orchestrator into a shared `IPlanAndExecute` abstraction just because they share a loop shape.

They consume surfaces at different semantic levels.

```text
Orchestrator
  consumes task-level surface
  emits task graph or task mutations

Runner
  consumes behavior/tool-level surface
  emits behavior or tool invocations

Sub-agent, later if allowed
  consumes a bounded delegated surface
  emits result under explicit depth and budget limits
```

The shared substrate is not an inheritance hierarchy. It is the active surface contract.

Useful conceptual type:

```text
SurfaceConsumer
  consumes an ActiveCapabilitySurface
  emits an output allowed by PlannerOutputLevel
```

This may remain conceptual. Do not create an abstraction until implementation pressure proves it.

## Surface Rendering

Rendering is the last step, not the core feature.

The renderer converts an active surface into planner context.

A stable text rendering should include:

```text
Mission
Mode
Focus
Scope
Actor
State Projection
Signals
Preferred Capabilities
Available Capabilities
Demoted Capabilities
Blocked Capabilities
Denied Capabilities
Expected Proof
Output Contract
Stop Conditions
```

Rendering rules:

```text
Do not include hidden bindings.
Do not include unavailable capabilities unless useful for diagnostics.
Keep reasons short and public-safe.
Put preferred behaviors before raw primitives.
State the required PlannerOutputLevel.
State completion proof requirements.
Include recent receipt-derived signals, not full receipt dumps.
```

The renderer can be simple string construction. It does not need a templating framework unless implementation ergonomics demand it.

## Provenance And Receipts

Every benchmark run that claims planner quality should be able to answer:

```text
What did the planner see?
What was hidden?
What was preferred?
What was blocked?
What abstraction level was required?
What did the planner emit?
What did the gate accept or reject?
What did the host prove?
```

Use `ContextSurfaceReceipt` or a benchmark snapshot.

Fields:

```text
manifest id and version
surface id and version
surface hash
rendered context hash
actor id
scope id
goal hash
state projection hash
policy id
budget snapshot
planner output level
binding ids exposed
binding ids hidden
binding ids blocked
binding ids denied
binding ids demoted
source receipt ids
planner kind
model id, when LLM-backed
LLM request hash, when applicable
LLM response hash, when applicable
gate validation result
host expansion id or hash
execution mode: live | dry-run | replay | hybrid
```

For non-benchmark hosts, this may be reduced.

For benchmark harnesses, it should be early, not late.

## Anti-Leak Tests

Rendered surfaces must be testable.

Test categories:

```text
Hidden token checks
  Rendered context must not include hidden ids, hidden reward names, hidden route labels, or hidden dependency ids.

Binding reason checks
  Blocked, denied, and demoted reasons must be public-state-safe.

Output-level checks
  Planner output must match PlannerOutputLevel.

Primitive leakage checks
  Behavior-level surfaces must not expose primitive live controls unless explicitly allowed.

Completion proof checks
  Completion cannot succeed without host artifact proof.

Surface mutation checks
  Receipts must change the next surface when they change public state.

Determinism checks
  Same seed and same public state produce the same surface hash.
```

These checks are especially important for hidden-world, MazeQuest, HexQuest, and TurtleQuest benchmark claims.

## Harness Examples

### TurtleQuest

Initial cockpit:

```text
Mode:
  Execution

Focus:
  Tunnel task

Actor:
  Turtle at start position, facing north

Preferred:
  turtlequest.dig_line_return(length)

Available:
  inspect_environment

Demoted:
  move_forward
  dig_block
  turn_left
  turn_right

Blocked:
  craft_item, because recipe provider and materials are unavailable in this scenario

Expected Proof:
  start position recorded
  five forward progress receipts
  return path receipt
  final position equals start
  turtlequest.objective_completed artifact

Output Contract:
  BehaviorInvocation
```

Planner should choose:

```text
turtlequest.dig_line_return(length: 5)
```

Planner should not choose:

```text
move_forward
dig_block
move_forward
dig_block
...
```

unless the active surface explicitly allows `PrimitivePlanLive`.

### MazeQuest

Initial thin cockpit:

```text
Mode:
  Exploration

Focus:
  Unknown objective path

Preferred:
  explore_adjacent_cells
  inspect_cell

Available:
  move_to_known_cell

Blocked:
  unlock_exit, because no key is publicly known in inventory

Signals:
  key location unknown
  exit location unknown
  local neighborhood partially known

Output Contract:
  BehaviorInvocation
```

After receipt:

```text
Receipt:
  discovered key in reachable adjacent cell

Next surface:
  Mode = Acquisition
  Preferred = move_to_key, pick_up_key
  unlock_exit remains blocked
```

### Hidden-World Orchestration

Initial task-level surface:

```text
Mode:
  Exploration

Focus:
  Save kingdom objective with incomplete public map

Preferred:
  investigate_reachable_location

Blocked:
  enter_castle, because public castle seal is closed

Hidden:
  exact key locations
  full dependency graph
  final success chain

Output Contract:
  TaskGraphPlan
```

The planner should create task nodes from public affordances, not from an answer-key task list.

## Implementation Plan

Do not build a large framework first.

### Slice 1: Distill And Align Docs

Use this document as the clean design target.

Keep `AgenticHarness.Cockpit.Hud.md` as a research transcript or archive it later.

Cross-reference this document from `CodexGoal.Agentica.AgenticHarnessHost.md`.

### Slice 2: MazeQuest Surface Snapshot

Implement or hand-author a minimal MazeQuest manifest in tests or CLI scenario support.

Compile one `ActiveCapabilitySurface` from:

```text
maze state
current objective
known inventory
known local observations
policy
recent receipts
```

Render a cockpit frame and snapshot it.

### Slice 3: Behavior Contract Gate Probe

Add tests that reject:

```text
primitive live actions when PlannerOutputLevel = BehaviorInvocation
blocked capabilities
hidden capability references
missing required behavior inputs
completion without artifact proof
```

### Slice 4: Receipt-Driven Surface Mutation

Given a receipt such as:

```text
key acquired
blocked move refused
new route discovered
return path established
```

assert the next surface changes mode, focus, signals, and bindings correctly.

### Slice 5: TurtleQuest Behavior Surface

Use TurtleQuest to prove embodied behavior-level control:

```text
dig_line_return(5)
```

with primitives demoted or hidden unless recovery/debug mode is active.

### Slice 6: Promote Only What Repeats

After MazeQuest and TurtleQuest converge, consider promoting only the smallest reusable types:

```text
CapabilityBindingState
PlannerOutputLevel
ActiveCapabilitySurface
ContextSurfaceReceipt
```

Do not promote domain vocabulary.

## Completion Condition

This goal is complete when one harness demonstrates:

- deterministic surface compilation,
- public-safe binding reasons,
- mode, focus, pressure, and signals,
- rendered cockpit frame,
- behavior-level output contract,
- behavior contract gate enforcement,
- receipt-driven surface mutation,
- anti-leak tests,
- and artifact-gated completion.

The result should make the planner's next legal decision obvious without letting the surface engine make the decision itself.

