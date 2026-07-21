# Codex Goal: Agentic Harness Host Pattern

> Lifecycle: **Incubating** · Completion: **90%** · Canonical status: [Agentica Product Status And Goal Xref](Agentica.ProductStatus.md)

## Mission

Define the host-side pattern for Agentica benchmark and domain harnesses.

The goal is to make harnesses such as MazeQuest, HexQuest, hidden-world orchestration tests, and TurtleQuest easier to build without rediscovering the same boundary each time:

```text
Domain host owns reality.
Domain host owns safe behaviors.
Agentica sees bounded capabilities.
The planner chooses goals, tasks, behaviors, and recovery paths.
Receipts prove what happened.
Completion artifacts gate success.
```

This is not a new runtime project and not a domain framework. It is a design contract for how hosts expose a scoped world to Agentica and to `Agentica.Orchestration`.

## Why This Exists

Recent harness work exposed a recurring developer pain:

```text
The code can be implemented once the target is concrete,
but the correct domain abstraction level has to be re-explained for every harness.
```

The failure mode is usually one of these:

- Exposing raw primitive actions when the planner should see behavior-level capabilities.
- Feeding the planner a task list instead of a problem and capability surface.
- Letting the hidden test oracle leak into public planner context.
- Treating static tool descriptors as the full capability picture.
- Building a custom bridge protocol that bypasses Agentica's scenario, tool, receipt, artifact, and outcome pattern.
- Mistaking narrative reports for proof.

This document exists to make those choices explicit before each new harness grows around the wrong center.

Companion document:

```text
CodexGoal.Agentica.CockpitSurfaceEngine.md
```

That document distills the cockpit/HUD research notes into the concrete `CapabilitySurfaceEngine` design. Treat `AgenticHarness.Cockpit.Hud.md` as research transcript, not the normative implementation target.

## Core Boundary

Agentica is not the domain.

```text
Agentica
  plans bounded work
  validates tool calls
  invokes host tools
  records observations, receipts, artifacts, and outcomes

Agentica.Orchestration
  decomposes larger goals
  delegates bounded tasks to Agentica
  observes OutcomeEnvelope results
  evaluates task acceptance
  refines the task graph

Harness Host
  owns domain state
  owns legal actions
  owns deterministic procedures
  owns hidden oracle state when testing
  emits receipts and artifacts
  refuses illegal work
```

The host may be an in-process CLI scenario, a bridge to a game, a service adapter, or a future API-backed work context. The boundary remains the same.

## The Harness Shape

A good Agentica harness has these pieces:

```text
Board / World
  authoritative state, hidden state, deterministic rules

Scenario
  initial state, user objective, budgets, success criteria

Session
  mutable run-local state and receipt-backed progress

ToolCatalog
  scoped tools or behaviors exposed to Agentica

OutcomeEvaluator
  host validation, completion artifacts, final evidence projection
```

Existing CLI harnesses already follow this pattern:

```text
Board -> Scenario -> Session -> ToolCatalog -> AgenticaRunner -> Receipts/Observations/Artifacts -> OutcomeEvaluator
```

New harnesses should align with that shape unless there is a concrete reason not to.

## Static Manifest And Live Surface

Tool descriptors are necessary, but they are not enough.

The `CapabilitySurfaceEngine` is the deterministic compiler that turns the static manifest plus current host reality into the live surface:

```text
DomainHarnessManifest + host state + scope + actor + goal + policy + receipts + budget
  -> CapabilitySurfaceEngine
  -> ActiveCapabilitySurface
```

The cockpit metaphor refers to the rendered active surface. The engine is the reusable abstraction.

A static descriptor says:

```text
This host has a capability named dig_line_return.
```

A live capability surface says:

```text
Given this actor, scope, state, policy, budget, and receipt history:
  dig_line_return is legal now
  craft_item is blocked because no recipe provider is available
  return_home is legal because path receipts exist
  mine_until_full is demoted because inventory is nearly full
  completion requires final position, path receipts, and inventory delta
```

The planner needs the live surface, not the raw inventory of everything the host might ever do.

The conceptual equation is:

```text
Context + Scope + Actor + Goal + State + Policy + Recipes + Receipts
  -> ActiveCapabilitySurface
```

This should begin as a documented pattern and deterministic compiler inside harnesses, not as a new package or large abstraction.

## Relationship To Agentica ToolSurfaceSnapshot

`ActiveCapabilitySurface` and `ContextSurfaceReceipt` are host-owned records.

They are richer than Agentica's core tool surface because they describe how the host compiled domain reality into the public planner surface:

```text
ActiveCapabilitySurface
  host-owned
  domain-aware
  may include available, preferred, blocked, denied, demoted, hidden, and unavailable bindings
  may include public-safe reasons and source receipt refs
  may include hashes or counts for hidden/private host data

ToolSurfaceSnapshot
  Agentica-owned
  domain-neutral
  records only the ToolDescriptor list and bounded planning context actually exposed to the planner
  does not model hidden, demoted, blocked, denied, or unavailable host capabilities
```

The handoff is:

```text
Host ActiveCapabilitySurface
  -> public-safe projection
  -> ToolCatalog / ToolDescriptor set + RunRequest.Context
  -> Agentica PlanningRequest
  -> Agentica ToolSurfaceSnapshot
```

Agentica must not try to reconstruct or own host-hidden capability state. If a capability was filtered out, demoted, blocked, denied, or hidden before Agentica saw the tool catalog, the host surface record is the authority for why.

If a benchmark later asks whether the planner had a better tool available, answer with both records:

```text
ToolSurfaceSnapshot
  What Agentica actually showed the planner.

ActiveCapabilitySurface / ContextSurfaceReceipt
  What the host knew, filtered, hid, demoted, blocked, or preferred before projection.
```

If hidden capability names are themselves answer keys, the host record should store public-safe hashes, counts, categories, or redacted ids instead of leaking names.

## DomainHarnessManifest

A `DomainHarnessManifest` is the cold, static domain contract.

It describes what the harness can expose in principle:

```text
domain id and version
actor types
state projection rules
capability families
behavior levels
receipt kinds
artifact kinds
completion gates
anti-leak rules
planner abstraction level
```

The manifest is not fed raw to the planner. It is too broad and too static.

The manifest is compiled with current state, scope, policy, and receipts into an active surface.

## ActiveCapabilitySurface

An `ActiveCapabilitySurface` is the hot, scoped planning contract.

It describes what matters now:

```text
surface id and version
actor id and scope
world or work context projection
available capabilities
preferred capabilities
blocked capabilities
denied capabilities
demoted capabilities
known blockers
evidence that could change blockers
expected receipt and artifact proof
planner output abstraction level
```

Blocked and denied capabilities are part of the surface. Negative bindings are important because they teach the planner not to invent illegal work.

Example:

```text
available:
  turtlequest.dig_line_return(length)

blocked:
  turtlequest.craft_item(item, count)
    reason: recipe provider is not available in this scenario

demoted:
  turtlequest.move_forward()
    reason: primitive command; prefer behavior-level tool unless recovering from failure
```

Negative bindings must be public-state-safe.

Safe reasons may cite:

- Known policy.
- Known missing observations.
- Known resource gaps.
- Known public blockers.
- Current budget or execution mode.
- Safer preferred behaviors.

Unsafe reasons must not reveal:

- Hidden oracle state.
- Hidden rewards.
- Future receipts.
- Hidden dependency chains.
- Hidden route facts.
- Answer-key task ordering.

Example:

```text
safe:
  castle_unlock is blocked because the public seal is still closed.

unsafe:
  castle_unlock is blocked because the silver key is hidden in the forest shrine.
```

Use precise binding states:

```text
Available
  Legal and relevant enough to expose.

Preferred
  Available and recommended for this goal, scope, state, and policy.

Demoted
  Available but discouraged because a safer, higher-level, cheaper, or more relevant capability exists.

Blocked
  Not currently executable because public preconditions are missing. Evidence may later unblock it.

Denied
  Policy forbids it under the current envelope.

Hidden
  Not planner-visible because exposing it would leak hidden state, exceed budget, or distract the planner.

Unavailable
  The host does not have this capability.
```

Blocked, denied, demoted, and hidden are not synonyms. Coding agents should preserve the distinction because each state implies a different planner behavior.

## Capability Levels

Hosts should expose capabilities at the highest useful safe level.

Primitive commands are sometimes necessary, but they should not be the default planning surface for complex domains.

Example from TurtleQuest:

```text
Primitive commands
  inspect
  moveForward
  turnLeft
  dig
  place

Procedural skills
  digLine(length)
  digTunnel(width, height, length)
  returnHome()
  depositInventory()
  harvestTree()
  buildWall()

Orchestration behaviors
  mineUntilInventoryThreshold()
  digTunnelWithDeposits()
  exploreWithReturnBudget()
  buildFromBlueprint()
  recoverFromBlockedPath()
```

The LLM should not spend its reasoning budget reinventing a tunnel-digging state machine one block at a time.

The host owns safe procedures and emits intermediate receipts. Agentica monitors the receipts and replans only when evidence says something unexpected happened.

## Behavior Lifecycle

Behavior-level tools may not complete inside one model turn or one host tick.

A host-owned behavior should follow this lifecycle:

```text
1. Active surface is rendered.
2. Planner selects a behavior and parameters.
3. Host validates the behavior request against current state, policy, and budget.
4. Host starts the behavior and emits a start receipt.
5. Host executes primitive actions or internal state machine steps.
6. Host emits progress receipts.
7. Behavior completes, blocks, fails, or is cancelled.
8. Host emits a terminal behavior receipt.
9. Active surface is refreshed from new state and receipts.
10. Planner completes, recovers, or chooses the next behavior.
```

Long-running hosts should expose behavior control shapes instead of pretending every operation is instant:

```text
start_behavior
get_behavior_status
cancel_behavior
resume_behavior, later if needed
complete_objective, or host-issued completion artifact
```

For short deterministic benchmarks, a blocking behavior tool is acceptable if it still emits receipt-backed progress and terminal evidence.

## Behavior Contract Gate

The active surface must be enforced before host execution.

The `BehaviorContractGate` validates planner output against the current `ActiveCapabilitySurface`:

```text
planner output matches planner output abstraction level
selected capability exists on the active surface
binding state allows execution
parameters satisfy current input constraints
policy permits the effect
budget permits the action
approval is present when required
blocked or denied capabilities are not executed
primitive controls are not used when behavior-level mode is required
```

If validation fails, the host must not execute. The failure should become a validation issue or refused receipt that can shape the next surface.

## Planner Output Abstraction

Every active surface should say what abstraction level the planner is expected to produce.

Treat this as a mechanically enforceable mode, not only prompt prose.

Initial vocabulary:

```text
ObservationOnly
  Planner may inspect, classify, or summarize. It must not propose executable work.

BehaviorInvocation
  Planner may choose one bounded host behavior and parameters.

RecipeInvocation
  Planner may choose one known recipe or procedure template and parameters.

CompositeBehaviorPlan
  Planner may propose a bounded sequence or graph of behavior-level tasks.

PrimitivePlanDryRun
  Planner may propose primitive steps for inspection, validation, or explanation only.

PrimitivePlanLive
  Planner may execute primitive steps. This is a deliberately enabled recovery, debug, or benchmark mode.
```

Default to behavior-level output for embodied or hazardous hosts.

For TurtleQuest live mode, the default should be `BehaviorInvocation` or `RecipeInvocation`. `PrimitivePlanLive` should be a deliberate test or recovery setting, not the normal surface.

For `AgenticaRunner`, planner output is tool-level:

```text
call turtlequest.start_behavior
call turtlequest.get_behavior_status
call turtlequest.complete_objective
```

For `Agentica.Orchestration`, planner output is task-level:

```text
investigate reachable forest shrine
clear reachable tunnel objective
recover after blocked return path
collect missing material after receipt reveals dependency
```

The orchestration planner must not be asked to produce primitive tool steps. It should create and refine task nodes.

The Agentica planner must not be asked to invent hidden domain facts. It should call bounded host tools and respond to receipts.

When orchestration delegates to Agentica, the handoff is:

```text
TaskNode objective + task scope + current host/work context
  -> RunRequest
  -> runner-level active surface
  -> tool-level WorkflowPlan
```

The orchestrator should not pass its own task graph as a tool plan. The runner gets a bounded objective and builds or receives the runner-appropriate active surface for that task from the same host-owned state and receipt trail.

Prompt builders should render the active surface, or a projection of it, as planner context. Raw tool descriptors remain the callable contract, but they should be augmented or narrowed by current availability, blockers, preferred behaviors, expected proof, and output abstraction level.

## Planner Frames And Cockpit Projection

Some host context is too dynamic to live only in the initial `RunRequest.Context` and too semantic to express as static tool descriptors.

The host may compile a per-planning-turn frame:

```text
host state + receipt trail + active surface + public policy
  -> PlanningFrame
  -> planner prompt context
  -> cockpit/HUD projection
```

This is the cockpit compiler/projector boundary. It is still host-owned. Agentica only carries the resulting `PlanningFrame` as public planner context and records which core `ToolSurfaceSnapshot` was exposed at the same turn.

For MazeQuest, the first concrete frame is:

```text
MazeQuestCockpitFrame
  currentState
  recentTrajectory
  progressSignals
  loopSignals
  resourceRisk
  escapeCandidateMoves
  recommendedPlannerPosture
  plannerGuidance
```

The frame is intentionally non-oracle:

- It may expose recent public moves, repeated public cells, objective signal trend, frontier gain trend, visible local risks, and bounded-risk posture.
- It may classify a visible move as productive, neutral, looping, blocked, or risk_branch.
- It must not expose hidden routes, hidden object locations, future receipts, or answer-key task ordering.

This gives the LLM trajectory awareness without requiring it to mine raw receipts and without turning Agentica core into a domain model.

Prompt shape:

```text
If projected context frames are present, treat them as current host-compiled public context.
Prefer the newest frame over stale request context for the same state.
When loopSignals.stagnationSuspected is true, avoid moves classified as looping unless all other legal actions are blocked.
When resourceRisk.boundedRiskAllowed is true and all safe options are stagnant, a bounded risk_branch move may be justified.
ExecutionIntent.Rationale must name the public loop/progress/risk tradeoff for an escape move.
```

The future cockpit/HUD should render the same frame instead of inventing a parallel diagnosis.

## Anti-Leak Rule

Harnesses with hidden state must maintain a strict oracle boundary.

The planner may see:

```text
public objective
public state projection
known rules
available and blocked capabilities
known blockers
receipts from completed work
expected proof shapes
```

The planner must not see:

```text
hidden full map
hidden dependency graph
hidden unlock rewards
hidden final route
answer-key task list
success signal before host validation
future receipts
```

The planner proposes work. The host decides what actually happened.

```text
LLM proposal -> host execution -> receipt -> updated surface
```

Receipts are generated by the host, not by the model.

## Receipts As Ground Truth

Receipts are evidence. Learning signals, summaries, and reports are interpretations.

A host should emit receipts for:

- Legal action accepted.
- Legal action refused.
- Behavior started.
- Behavior progressed.
- Behavior completed.
- Behavior blocked.
- State changed.
- Inventory or artifact changed.
- Capability became available or blocked.
- Completion was validated or refused.

Completion must be artifact-gated where possible:

```text
mazequest.objective_completed
hexquest.objective_completed
turtlequest.objective_completed
hiddenworld.objective_completed
```

The artifact should only be emitted after host validation, never because the planner said the goal is done.

## Context Surface Receipt

Whenever a live capability surface is rendered for a planner, the harness should eventually be able to record a provenance receipt.

Minimum useful fields:

```text
manifest id and version
surface id and version
surface hash
actor id
scope id
policy id
goal hash
state projection hash
receipt ids used
exposed capabilities
preferred capabilities
blocked capabilities
denied capabilities
demoted capabilities
planner output abstraction level
rendered context hash
```

This receipt is not required for the first implementation of every harness, but it is the right long-term grounding record. It makes prompt/context engineering inspectable instead of invisible.

This is the host-side counterpart to Agentica's `ToolSurfaceSnapshot`.

```text
ContextSurfaceReceipt
  Explains the host compilation process.
  May mention blocked, denied, demoted, hidden, or unavailable capability bindings when public-safe.

ToolSurfaceSnapshot
  Records the final Agentica-visible tool surface.
  Does not explain private host filtering decisions.
```

The two records should be linkable by public-safe ids or hashes when useful, but Agentica core should not depend on `ContextSurfaceReceipt` semantics.

For benchmark harnesses, require this much earlier. If the point is to prove whether the planner solved the problem without hidden help, the rendered surface needs a receipt or snapshot from the start.

Benchmark-oriented fields should also include:

```text
planner kind
model id, when LLM-backed
LLM request hash
LLM response hash
compiled IR hash
recipe ids exposed
recipe ids selected
host expansion id or hash
execution mode: live | dry-run | replay | hybrid
```

This is how benchmark runs answer whether the result came from planner reasoning, deterministic replay, host expansion, or accidental context leakage.

## Surface Budget

An active surface must stay small enough to be useful.

Large domains should not expose every capability with extra metadata. The surface compiler should rank, hide, demote, or summarize.

Default budget guidance:

```text
Expose the few preferred capabilities most relevant to the current goal.
Expose available capabilities that are legal and plausibly useful.
Expose blocked capabilities only when relevant to current planning.
Demote primitive controls when behavior-level capabilities exist.
Hide irrelevant capabilities.
Hide capabilities whose reasons would leak oracle state.
Summarize or retrieve large recipe catalogs instead of dumping them.
```

Surface budgeting is where scope bias becomes operational. It keeps the active surface from becoming “all tools, but with extra paperwork.”

## Test Harness Requirements

A harness intended to test orchestration intelligence should prove more than “the loop runs.”

It should test:

- The planner can infer useful task granularity from a goal and live surface.
- The planner does not receive an answer-key task list.
- The planner does not execute blocked work early.
- The planner creates tasks or behavior requests, not primitive tool micromanagement.
- Receipts update the next available work frontier.
- Refinement can add, remove, reorder, or replace tasks after new evidence.
- Hidden state does not leak into planner output.
- Completion is impossible without host proof.

For hidden-world style tests, scenario validation should check:

```text
solvable
non-leaky
orchestration-required
deterministic
```

The benchmark harness should be a world, not a ruler that accidentally hands out the answer.

Anti-leak checks should become automated.

Useful probes:

- Snapshot the rendered planner context.
- Assert it does not contain hidden term ids, hidden reward ids, hidden route ids, hidden dependency ids, or future receipt ids.
- Assert completion artifact names are not shown before they are earned unless they are part of public proof rules.
- Assert behavior-level harnesses do not expose raw primitive controls when the active surface requires behavior-level output.
- Assert orchestration planners are not handed the oracle-known task list.
- Assert blocked and denied reasons do not contain hidden oracle facts.

These probes are not a replacement for semantic review, but they catch the most common accidental leaks.

## TurtleQuest As Reference Example

TurtleQuest is a useful reference because it forces scope:

```text
one actor
one position
one inventory
one tool surface
one bounded task
one receipt trail
```

The correct TurtleQuest planning surface is not “God can edit Minecraft.”

It is:

```text
This turtle, in this world, at this position, with this inventory,
can currently attempt these behaviors under these limits.
```

For the first benchmark, the player prompt:

```text
Dig a straight tunnel 5 blocks forward and return.
```

should map to a behavior-level capability:

```text
turtlequest.dig_line_return(length: 5)
```

The host expands the behavior into primitive turtle actions and emits receipts. Agentica monitors and completes only if the final receipt trail proves:

```text
start position recorded
five forward progress steps attempted
blocks mined recorded
return path executed
final position equals start
completion artifact emitted by host
```

Pathfinding should be host-owned:

```text
Tier 0: no pathfinding, only primitive movement
Tier 1: return-home by recorded path reversal
Tier 2: local pathfinding over known cells
Tier 3: exploration pathfinding with return budget and hazards
```

The planner may choose a pathfinding behavior. It should not invent one from atoms during a benchmark unless that is the explicit test.

## Save-Kingdom Hidden World As Reference Example

The save-kingdom harness should not expose a task list.

It should expose:

```text
world goal
public locations
current reachability
known blockers
available behavior capability
known rules
receipts so far
```

It should hide:

```text
which location gives which key
the full dependency graph
the exact number of tasks
the final success chain
```

The planner should decide:

```text
what should be investigated first
what is blocked
what evidence could change the plan
what task graph is worth trying now
```

The host should execute bounded expeditions and return deterministic receipts.

## Non-Goals

Do not build a large capability framework before the harnesses prove the need.

Do not split new projects by noun.

Do not add domain vocabulary to `Agentica`.

Do not make `Agentica.Orchestration` depend on TurtleQuest, MazeQuest, HexQuest, Minecraft, or any other host.

Do not let the planner inspect hidden oracle state.

Do not make memory authoritative over receipts.

Do not require every host to implement every concept in this document before it can be useful.

Do not promote these concepts into core runtime types before multiple harnesses converge on the same minimal shape.

Promotion candidates, later:

```text
CapabilitySurface
CapabilityBinding
PlannerOutputLevel
ContextSurfaceReceipt
```

Do not promote host-hidden capability state into Agentica core. Agentica may record what it exposed to the planner; the host records what it withheld, blocked, demoted, denied, or hid.

Do not promote TurtleQuest, MazeQuest, HexQuest, Minecraft, or other domain vocabulary.

## Recipe And Cookbook Relationship

A validated recipe is a precompiled behavior or plan pattern that was proven against some active surface shape.

Future recipe applicability should consider:

```text
manifest version
active surface hash
capability ids exposed
binding states
policy envelope
receipt expectations
host expansion hash
```

Do not build this yet. The immediate value is to make active surfaces renderable, hashable, and testable so future recipes have something stable to bind against.

## First Practical Slice

The next useful implementation should be small:

1. Start with MazeQuest, because it already has hidden state, local observations, host tools, and completion artifacts.
2. Hand-author a minimal manifest in docs or test fixtures.
3. Compile one deterministic active capability surface from current state and receipts.
4. Feed only that surface to the planner.
5. Add probe tests that reject:
   - hidden fact leakage
   - primitive micromanagement when behavior-level planning is expected
   - locked work as the first executable task
   - completion without host artifact proof
6. Record the surface shape in a receipt or test snapshot.

This proves the boundary before generalizing it.

## Completion Condition

This goal is complete when Agentica has a documented, repeatable host harness pattern and at least one harness demonstrates:

- host-owned truth,
- behavior-level capabilities,
- live capability surface projection,
- receipt-backed state updates,
- no hidden oracle leakage,
- artifact-gated completion,
- and planner output at the correct abstraction level.
