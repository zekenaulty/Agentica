# Codex Goal: Agentica Orchestrated Campaign Runs

## Goal

Add a small host-side orchestration concept above Agentica runs so longer goals can be completed as a chain of bounded, receipt-backed runs.

This is not a replacement for `AgenticaRunner`. It is a higher-level host pattern:

```text
Campaign goal
  -> milestone plan
  -> Agentica run for milestone 1
  -> inspect OutcomeEnvelope
  -> Agentica run for milestone 2
  -> inspect OutcomeEnvelope
  -> continue until campaign definition of done is proven
```

The value is contextually scoped chaining over time. A single Agentica run remains small and bounded. A campaign gives the host a disciplined way to preserve progress across many bounded runs without asking one run to become an unbounded agent loop.

## Core Rule

```text
Agentica decides how to do a bounded thing.
The orchestrator decides what bounded thing comes next.
```

The orchestrator consumes `OutcomeEnvelope` data. It must not consume narrative confidence as proof.

## Why This Matters

The current runtime is good at bounded execution:

```text
RunRequest
  -> one plan or refinement call
  -> validated small plan slice
  -> one or more tool calls
  -> receipts, observations, artifacts
  -> terminal outcome
```

Recent batch work expands a run from single-operation turns to small single-to-multi-operation turns. That improves efficiency because the model can propose a few safe steps once, and the runtime can validate and execute them without an LLM call between every operation.

The next horizon is not a larger single run. The next horizon is many bounded runs coordinated by a host-owned campaign layer.

## Non-Goals

Do not make the orchestrator into Agentica v2.

The orchestrator must not:

- Choose tool calls.
- Execute tools.
- Bypass `WorkflowPlan` validation.
- Rewrite plans mid-run.
- Interpret model narration as success.
- Own host domain semantics inside the runtime package.
- Add hidden memory, hidden chain-of-thought, or unbounded self-refinement loops.

Do not move campaign vocabulary into the `Agentica` runtime package until the pattern proves it needs a reusable runtime contract. The first version should live in a host harness.

## Responsibilities

Agentica owns bounded workflow execution:

- `RunRequest`
- `WorkflowPlan`
- `PlanStep`
- validation
- step scheduling
- tool invocation
- observations
- receipts
- artifacts
- `OutcomeEnvelope`

The campaign host owns long-horizon progression:

- campaign goal
- milestone list
- current milestone
- milestone acceptance checks
- evidence-backed progress snapshots
- next `RunRequest`
- campaign-level stop conditions

## Carry-Forward Snapshot

The hard part of orchestration is not starting the next run. The hard part is deciding what crosses the run boundary.

The orchestrator should not pass the full prior `OutcomeEnvelope` into every next prompt. That bloats context and makes unrelated history sticky. It also should not pass a freeform model-written recap as proof. That invites the same failure mode Agentica is trying to avoid: narrative becomes mistaken for state.

The campaign host should compile a structured progress snapshot from `OutcomeEnvelope` data plus host-owned state:

```text
CampaignProgressSnapshot
  CampaignId
  Goal
  CurrentMilestoneId
  CompletedMilestones[]
  ProvenFacts[]
  OutstandingFacts[]
  ArtifactRefs[]
  ReceiptRefs[]
  Blockers[]
  HostStateProjection
```

Rules:

- `ProvenFacts` must be derived from receipts, artifacts, observations, or host state checks.
- `OutstandingFacts` must come from milestone definitions, blockers, or failed acceptance checks.
- `ArtifactRefs` and `ReceiptRefs` must point back to real envelope evidence.
- `HostStateProjection` is domain-owned and should be the smallest useful state slice.
- The model may help propose or explain a snapshot, but the accepted snapshot is host-compiled and evidence-backed.

This is the orchestration equivalent of a thought-signature carry-forward: compact enough to fit the next run, structured enough to validate, and grounded enough not to become self-authored memory.

## Planner Notes Across Runs

Long-horizon orchestration needs some planner-visible context that behaves like notes. The blur to avoid is treating notes as proof.

Campaign notes should be part of the progress snapshot or a closely related structured working-context object. They may include:

- hypotheses
- open questions
- known blockers
- candidate next checks
- map or route assumptions
- compact host-state facts

They must not include hidden chain-of-thought or raw provider thinking text. They also must not be accepted as completion evidence by themselves.

Suggested structure:

```text
CampaignWorkingContext
  ProvenFacts[]          evidence-backed, safe to rely on
  Hypotheses[]           useful but unproven
  OpenQuestions[]        need tool evidence
  KnownBlockers[]        backed by receipts or host checks where possible
  NextConsiderations[]   planning hints, not proof
  EvidenceRefs[]         refs supporting proven entries
```

Retention rules:

- Keep active working context in `RunRequest.Context` while it is useful.
- Compact it before it crowds out the current objective, tool catalog, map projection, or recent receipts.
- Preserve evidence refs when compacting proven facts.
- Drop stale hypotheses and solved open questions.
- If a campaign blocks and waits for attention, persist the snapshot and working context as host-owned state.

This belongs near Agentica because the runtime/planner boundary needs a generic context discipline. Domain-specific memory still belongs to the host.

Current implementation direction:

```text
CampaignProgressSnapshot
  Evidence-backed state summary.

CampaignWorkingContextSnapshot
  System-curated planning support.
```

The working context is deliberately not a tool surface. The planner does not get free access to create durable notes. Later, an LLM summarizer may propose a structured update, but the host/system must curate it before it enters the next run context.

## Minimal Campaign Shape

A minimal campaign object should be boring:

```text
CampaignDefinition
  CampaignId
  Title
  Goal
  Milestones[]
  DefinitionOfDone

CampaignMilestone
  MilestoneId
  Objective
  DependsOn[]
  Optional
  Priority
  RequiredOutcomeStatus
  RequiredEvidence[]
  ContextProjection

CampaignState
  CampaignId
  ActiveMilestoneId
  CompletedMilestones[]
  BlockedMilestones[]
  AvailableMilestones[]
  PriorRunRefs[]
  ProgressSnapshot
  Status
```

`PriorRunRefs` may point to full `OutcomeEnvelope` records in logs or host storage. The active cross-run context should be the compiled `CampaignProgressSnapshot`, not the raw envelope set.

## Execution Flow

```text
1. Host loads a CampaignDefinition.
2. Host computes available milestones from the dependency graph.
3. Host builds a RunRequest for that milestone.
4. AgenticaRunner executes the run normally.
5. Host evaluates the OutcomeEnvelope against milestone acceptance.
6. If accepted, host advances to the next milestone.
7. If blocked, host may stop, retry with bounded retry policy, or ask for operator input.
8. Campaign succeeds only when the final definition of done is evidence-backed.
```

The host may include the current `CampaignProgressSnapshot` in `RunRequest.Context`, but that context is supporting information for planning. The proof remains the receipts, artifacts, observations, validation issues, and host acceptance checks that produced the snapshot.

## Milestone Graphs

The first campaign should be authored but graph-shaped. It should not be a purely linear list, and it should not start with procedural generation.

The runner should compute available milestones:

```text
AvailableMilestones =
  milestones where:
    not completed
    not blocked
    all required dependencies are satisfied
```

Selection policy for the first slice should be deterministic:

```text
choose lowest priority available milestone
then lowest declared order
```

This proves non-linear orchestration without making failures ambiguous.

Suggested first graph:

```text
M1: acquire lantern
M2: acquire bronze key
M3: explore dark archive        depends on M1
M4: unlock bronze vault         depends on M2
M5: recover moon sigil          depends on M3
M6: recover sun sigil           depends on M4
M7: optional cache              depends on M1 or M2
M8: open final gate             depends on M5 + M6
```

Minimum graph behaviors to prove:

- Multiple available milestones can exist at once.
- A milestone can depend on multiple prior milestones.
- Optional milestones can be skipped without blocking campaign success.
- A final gate can require convergence from two branches.
- A blocked milestone does not create false campaign success.

## Acceptance Checks

Campaign milestone acceptance should be simple and explicit:

- Required `RunOutcomeStatus`.
- Required artifact kind.
- Required receipt status/tool id.
- Required blocker absence or presence.
- Optional host-specific state check.

The campaign should never accept a milestone because the report sounds successful.

## Harness Roles

WorkbenchQuest, DungeonCampaign, and a later CLUE-style harness test different gaps. They should not be treated as interchangeable.

```text
WorkbenchQuest
  Tests planner/tool reasoning inside a bounded run.

DungeonCampaign
  Tests multi-run orchestration over time.

Mini deduction / CLUE-style harness
  Tests multi-agent context isolation after campaign boundaries are stable.
```

WorkbenchQuest remains useful because it pressures raw evidence, file interpretation, patching, verification, and completion proof. But by itself it mostly tests planner intelligence and tool use. It does not fully prove campaign orchestration unless wrapped as a multi-run campaign.

## First Orchestrator Harness Candidate

Use a minimal dungeon campaign, not a rich game engine.

The useful part of the Zelda-like idea is not world size. It is milestone-structured dependency progression:

```text
DungeonCampaign
  1. Acquire item A.
  2. Use item A to unlock dungeon B.
  3. Acquire key/state B.
  4. Use B to reveal or unlock C.
  5. Satisfy final gate using accumulated proof.
```

This is a direct orchestrator test:

- Chaining multiple bounded Agentica runs.
- Carrying structured progress forward.
- Advancing from milestone evidence.
- Requiring state from prior runs.
- Stopping honestly when a milestone is blocked.
- Avoiding a monolithic long-running agent loop.

The harness should stay intentionally small:

- No combat.
- No animation.
- No procedural world sprawl.
- No enemies.
- No large inventory system.
- No game-specific logic inside Agentica.

The host owns dungeon state, item gates, room transitions, and completion checks. Agentica only sees tools, observations, receipts, artifacts, and the next bounded objective.

## Secondary Workbench Campaign Candidate

A Workbench campaign is still useful as a separate slice after the campaign runner exists:

```text
ReleaseCampaign
  1. Investigate the failing release gate.
  2. Patch frontend config.
  3. Patch backend route.
  4. Patch release manifest.
  5. Verify the release gate and complete.
```

This maps better to real software work, but it is less pure as an orchestration proof because the milestones are more naturally parts of one repair workflow. DungeonCampaign is the cleaner proof that the orchestrator can carry state across separate bounded runs.

## Stretch Goal: Procedural Campaign Graphs

Procedural campaign generation is valuable, but it should come after the authored campaign graph proves the orchestrator contract.

Do not combine procedural generation with the first campaign runner slice. If the first version fails, the cause should be easy to localize: runner, snapshot, acceptance, or host scenario. A procedural graph generator would add another failure source too early.

Later, add a host-owned generator:

```text
CampaignGraphGenerator
  Seed
  MilestoneCountRange
  BranchFactor
  DependencyDensity
  OptionalMilestoneRatio
  ConvergenceGateCount
```

Generated graph validation rules:

- Graph is acyclic.
- At least one valid path reaches a final gate.
- Every required milestone is reachable.
- Final gate depends on at least two prior branches.
- Optional milestones are not required for campaign success.
- Dependency distribution stays within configured limits.
- Generated objectives remain host-owned and do not add vocabulary to Agentica runtime.

Useful procedural distributions to test later:

- Mostly linear with one branch.
- Wide early branch with late convergence.
- Optional side objectives.
- Blocked prerequisite requiring a different available milestone first.
- Multiple final-gate dependencies.

Procedural generation should pressure campaign orchestration, not become a game generator project.

## Stretch Goal: Procedural Dungeon Crawler

A procedural dungeon crawler can be a good later harness when it remains an orchestration and reasoning test, not a game project.

The key split:

```text
StructuredDefinition
  Host truth: floors, tiles, items, locks, stairs, gates, dependencies, objectives

PromptProjection
  LLM-facing maps, legends, visible state, compact summaries
```

Design rule:

```text
Maps are evidence.
The campaign graph is truth.
The LLM reads maps.
The host validates structure.
```

Dungeon bounds:

- 1-3 floors per dungeon.
- Small grids, such as 5x5 to 9x9.
- One primary milestone objective per dungeon.
- No combat, health, enemies, animation, or large inventory system.
- Movement is route-batched but bounded.

Primary movement tool:

```text
dungeon.move_route
  route: ["E", "E", "S", "W"]
```

The host executes the route internally and returns a receipt with:

- attempted route
- completed route
- failed step, if any
- current floor and position
- blocker type
- required item or state, if known
- discovered facts
- unattempted route segment, if route was longer than allowed

Primary recovery mode should be raw evidence:

```text
RawRecovery
  Receipt reports blocker + position + required state.
  Agent must inspect/map-read/replan.
```

Choice-oriented recovery is valid but separate:

```text
ChoiceRecovery
  Receipt may include host-authored branch choices.
  This tests constrained decision-making, not raw recovery.
```

Do not make `branchOptions` the default. A receipt should explain what happened; it should not solve the next move.

Floor transitions:

- `move_route` does not cross floors.
- `dungeon.change_floor` is a separate action.
- `change_floor` requires standing on a stairs tile.
- The receipt returns the new floor and landing position.

Route length:

- `MaxRouteLength` must be visible to the planner through tool description or input schema.
- If a route exceeds the limit, the host may execute the first valid segment and return `notAttemptedRoute`, rather than refusing the entire route.

Prompt economics:

The dungeon harness adds another prompt surface:

- system instruction
- milestone objective
- campaign progress snapshot
- tool catalog
- recent observations
- recent receipts
- world map summary
- dungeon map legend
- current floor map

The map projection must stay scoped. Prefer current floor raw map, other floors summarized unless inspected, and a stable short legend.

## CLUE-Style Agentic Multiplayer

A CLUE-style harness is interesting, but it should be treated as a later research harness, not the next implementation target.

Useful pressure it would add:

- Multiple agents with partial information.
- Private notes versus public events.
- Turn order.
- Hypothesis formation.
- Evidence exchange.
- Bluff/noise handling if desired.
- Final accusation as a high-stakes completion claim.

Why it is harder than it first appears:

- It introduces multi-agent state and hidden information.
- It needs a strict referee host to prevent leakage.
- It needs per-agent context projection.
- It needs public receipts and private observations.
- It can become a social deduction engine instead of an Agentica harness.

The right minimal version would not be full CLUE. It would be a small deduction table:

```text
3 suspects
3 rooms
3 tools
1 hidden solution
2-3 agent players
public turn log
private hand/evidence per player
referee-owned validation
```

Each agent action would still be a bounded Agentica run:

```text
observe public log
inspect private notes
choose one question or accusation
receive receipt-backed response
update private notes
```

This would test campaign orchestration plus per-agent context isolation. It should come after a single-agent campaign harness works, because multiplayer adds another axis of complexity.

## Recommended Sequence

1. Keep improving single-run bounded execution.
2. Prove read-only batching and dependency validation.
3. Define the campaign contract and `CampaignProgressSnapshot`.
4. Build a tiny host-side campaign runner.
5. Build an authored graph-shaped DungeonCampaign harness to prove multi-run orchestration.
6. Add a Workbench campaign to pressure real-world milestone handoff.
7. Add procedural campaign graph generation as a host-owned stretch goal.
8. Add a minimal deduction harness only after campaign boundaries are stable.
9. Consider richer game-like harnesses later.

## Design Boundary To Preserve

Campaign orchestration is a host concern unless and until multiple hosts need the same abstraction.

Agentica should remain the bounded proof engine:

```text
model proposes
runtime validates
tools execute
receipts prove
host orchestrates time
```
