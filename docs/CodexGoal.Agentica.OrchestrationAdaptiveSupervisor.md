# Codex Goal: Agentica Adaptive Supervisory Orchestration

## Goal

Define and build `Agentica.Orchestration` as the adaptive supervisory layer above bounded Agentica runs.

This layer is the Nanda supervisory intelligence pattern applied to Agentica:

```text
Large objective
  -> task graph decomposition
  -> Agentica run for one bounded task
  -> observe OutcomeEnvelope
  -> evaluate task acceptance
  -> refine task graph when reality changes the plan
  -> continue until the larger goal is evidence-backed, blocked, failed, or cancelled
```

This is not the UI, not a persona, and not an API controller. It is the work-governing runtime that decides what work happens, in what order, with what compact context, and whether the work actually achieved the larger goal.

The campaign runner is a useful prototype, but it is not the product boundary. A campaign runner sequences a predefined graph. The orchestration layer must adapt the graph.

## Design Intent

This package intentionally builds another version of Agentica's core pattern at a higher level of abstraction.

It should carry forward the lessons from Agentica:

- The model proposes structured plans.
- The runtime validates before execution.
- Execution is bounded by policy.
- Evidence comes from receipts, artifacts, observations, validation issues, and host checks.
- Narration may explain, but it does not prove.
- Refinement is explicit, recorded, and bounded.
- Context carry-forward is compiled and scoped, not hidden memory.
- Stop states must be honest.

The difference is grain:

```text
Agentica
  bounded tool-step process runner

Agentica.Orchestration
  bounded task-level process runner
```

This is not a license to create an unbounded recursive agent hierarchy.

Do not keep adding higher and higher planning layers because the shape repeats. The orchestration layer is the supervisory layer needed to govern many bounded Agentica runs. It should be powerful enough to adapt task graphs, but bounded enough that it remains inspectable, testable, and stoppable.

## Core Rule

```text
AgenticaRunner plans and executes tool-level work.
Agentica.Orchestration plans and supervises task-level work.
Agentica.API exposes and persists the semantics.
The host/tool layer touches real domain state.
```

The orchestration layer must never call tools directly. It assigns bounded work to `AgenticaRunner` through a run executor contract and judges the resulting evidence.

## Why This Matters

Agentica already has the lower loop:

```text
RunRequest
  -> WorkflowPlan
  -> validated PlanStep sequence
  -> tool execution
  -> observations, receipts, artifacts
  -> planner refinement
  -> OutcomeEnvelope
```

Longer objectives need the same shape at a higher grain:

```text
LargeTaskRequest
  -> TaskGraphPlan
  -> selected TaskNode
  -> Agentica RunRequest
  -> OutcomeEnvelope observation
  -> task acceptance evaluation
  -> task graph refinement
  -> OrchestrationOutcomeEnvelope
```

The orchestrator's observation is an `OutcomeEnvelope`. The orchestrator's refinement is a validated mutation to the task graph: add a task, replace a task, remove a task, add a dependency, revise acceptance criteria, mark a task blocked, or change priority.

Without task graph refinement, the layer is only a sequencer. The intended product is an adaptive orchestrator.

## Non-Goals

Do not build `Agentica.API` first as the owner of orchestration behavior.

The API must not own:

- Task decomposition.
- Complexity classification.
- Task graph mutation.
- Task acceptance semantics.
- Progress context compilation.
- Replanning decisions.
- Long-horizon work strategy.

Do not make orchestration into Agentica v2.

The orchestrator must not:

- Choose tool calls.
- Execute tools.
- Bypass `WorkflowPlan` validation.
- Interpret report prose as proof.
- Store hidden chain-of-thought.
- Persist self-authored planner memory as fact.
- Carry full prior `OutcomeEnvelope` objects into every next run prompt.
- Depend on HTTP, queues, databases, auth, or transport concerns.
- Spawn another orchestration layer as its default refinement mechanism.
- Use recursive decomposition without explicit depth, run, refinement, and budget limits.

Do not treat the existing campaign vocabulary as the final package vocabulary. Campaigns may remain a host/demo concept. The package should use generic orchestration terms.

## Layering

```text
Agentica.API
  auth, tenancy, persistence, tool binding, streaming, approvals

Agentica.Orchestration
  objective classification as planning
  task graph decomposition
  task graph validation
  task selection
  task-to-RunRequest compilation
  run outcome observation
  task acceptance
  task graph refinement
  compact working context compilation
  orchestration outcome aggregation

Agentica
  bounded run execution
  workflow planning
  plan validation
  tool invocation
  observations, receipts, artifacts
  OutcomeEnvelope

Host / Tool Layer
  domain state
  domain policy
  side effects
  external systems
```

The dependency direction should be:

```text
Agentica.Orchestration -> Agentica
Agentica.API -> Agentica.Orchestration
Agentica.API -> Agentica
Agentica -> no orchestration dependency
```

## Required Supervisory Loop

The orchestrator must have its own plan, execute, observe, refine loop:

```text
1. Receive a large objective and context.
2. Ask ITaskPlanner.CreatePlanAsync for a TaskGraphPlan.
3. Validate the task graph before any run starts.
4. Select the next runnable TaskNode.
5. Compile the TaskNode into an Agentica RunRequest.
6. Execute through IRunExecutor.
7. Treat the returned OutcomeEnvelope as the task-level observation.
8. Compile evidence-backed progress and working context.
9. Evaluate task acceptance.
10. If accepted, mark the task complete.
11. If partial, blocked, surprising, or plan-invalidating, ask ITaskPlanner.RefinePlanAsync for graph mutations.
12. Validate and apply graph mutations.
13. Continue until complete, blocked, failed, cancelled, waiting for input, or budget exhausted.
```

This mirrors `AgenticaRunner`, but at task granularity instead of tool granularity.

## Required Contracts

The orchestration package should introduce a first-class task planner:

```csharp
public interface ITaskPlanner
{
    Task<TaskGraphPlan> CreatePlanAsync(
        TaskPlanningRequest request,
        CancellationToken cancellationToken = default);

    Task<TaskGraphRefinement> RefinePlanAsync(
        TaskRefinementRequest request,
        CancellationToken cancellationToken = default);
}
```

It should introduce a first-class run executor:

```csharp
public interface IRunExecutor
{
    Task<OutcomeEnvelope> RunAsync(
        RunRequest request,
        CancellationToken cancellationToken = default);
}
```

`IRunExecutor` is the key boundary that lets orchestration be built before the API:

```text
InProcessAgenticaRunExecutor today
ApiRunExecutor later
QueuedRunExecutor later
DurableRunExecutor later
```

The orchestrator must not know whether a run happened in-process, through an API, through a queue, or on another machine.

## Complexity Classification

The first complexity decision belongs inside task planning, not in a separate router.

`ITaskPlanner.CreatePlanAsync` decides whether the objective is small or large by returning either:

```text
single-node task graph
  pass-through bounded run
```

or:

```text
multi-node task graph
  adaptive orchestration
```

Do not create a separate classifier that must agree with the task planner about complexity. One authority should decide both complexity and decomposition.

## Task Graph Plan

The initial task graph should be explicit and validated:

```text
TaskGraphPlan
  PlanId
  Objective
  Tasks[]
  DefinitionOfDone[]
  CreatedAt

TaskNode
  TaskId
  Objective
  DependsOn[]
  Optional
  Priority
  MaxRuns
  ContextProjection
  AcceptanceRequirements[]
```

The task planner produces task objectives, not tool steps.

Good task objective:

```text
Inspect the current persistence model and identify run/event storage requirements.
```

Bad task objective:

```text
Call read_file, then call write_file, then call run_tests.
```

Tool-level sequencing belongs to Agentica's `IWorkflowPlanner` and `AgenticaRunner`.

## Graph Refinement

`RefinePlanAsync` must return explicit graph mutation proposals, not a vague summary.

Suggested mutation kinds:

```text
AddTask
ReplaceTask
RemoveTask
AddDependency
RemoveDependency
ReorderPriority
MarkTaskBlocked
MarkTaskAccepted
ReviseAcceptanceCriteria
ReviseDefinitionOfDone
```

The orchestrator validates every mutation before applying it.

Validation rules:

- No cycles.
- No duplicate task ids.
- Dependencies must reference known tasks.
- Required tasks must remain reachable unless explicitly blocked.
- Completed tasks cannot be silently rewritten.
- Removed tasks cannot leave dangling dependencies.
- Acceptance criteria must remain evidence-checkable.
- Mutation count and refinement count must be bounded by policy.
- Decomposition depth must be bounded by policy.
- A task may be decomposed only through the same validated task graph mechanism, not by creating an invisible nested planner loop.

Example refinement:

```text
Observation:
  Task "implement run persistence" completed partially.

Evidence:
  Artifact shows execution attempts need a separate model before events can be persisted correctly.

Mutation:
  AddTask "design execution attempt model"
  AddDependency "implement run persistence" depends on "design execution attempt model"
  ReplaceTask "implement run persistence" with a narrower objective.
```

## Acceptance Evaluation

Run success is not task acceptance.

```text
Run success:
  Agentica completed a bounded objective according to its run contract.

Task acceptance:
  The orchestration layer agrees the larger task advanced with enough evidence.
```

Task acceptance should have richer states than true or false:

```text
Accepted
PartiallyAccepted
Rejected
Blocked
InvalidatedPlan
```

`InvalidatedPlan` is critical. It means the run may have succeeded, but the task graph is now known to be wrong or incomplete.

Acceptance must be grounded in:

- `RunOutcomeStatus`.
- Receipts.
- Artifacts.
- Observations.
- Validation issues.
- Host state checks.
- Explicit evidence refs.

Acceptance must not be grounded in:

- Outcome report prose alone.
- Model confidence.
- Freeform summaries.
- Planner notes.
- Hidden reasoning.

## Context Carry-Forward

Context carry-forward is the hardest part of the layer.

The orchestrator must not pass full historical envelopes into every next run. That bloats prompt context and makes irrelevant history sticky.

The orchestrator also must not ask an LLM to write a narrative recap and treat that recap as state.

Instead, it needs a compiler that produces a compact, typed, evidence-backed projection:

```csharp
public interface IWorkContextCompiler
{
    WorkContextSnapshot Compile(
        WorkContextCompilationRequest request);
}
```

The compiler consumes:

```text
current TaskGraphPlan
current OrchestrationState
active TaskNode
latest OutcomeEnvelope
latest TaskAcceptanceResult
previous ProgressSnapshot
previous WorkContextSnapshot
host state projection
```

The compiler emits:

```text
WorkContextSnapshot
  Objective
  ActiveTaskId
  CompletedTaskIds[]
  ProvenFacts[]
  OpenQuestions[]
  Hypotheses[]
  KnownBlockers[]
  PlanImpacts[]
  EvidenceRefs[]
  HostStateProjection
  UpdatedAt
```

Rules:

- `ProvenFacts` must be receipt-backed, artifact-backed, observation-backed, validation-backed, or host-state-backed.
- `Hypotheses` are allowed but must be marked as unproven.
- `OpenQuestions` should identify missing evidence.
- `KnownBlockers` should be concrete and cite evidence where possible.
- `PlanImpacts` should explain how evidence affects the task graph.
- `EvidenceRefs` should point to real envelope evidence without embedding full payloads.
- Size, count, age, and scope must be bounded.

The working context is a thought-signature-style projection: compact enough to fit the next planner call, structured enough to validate, and grounded enough not to become self-authored memory.

## Plan Impacts

`PlanImpacts` are the orchestration-specific missing piece from the campaign prototype.

Example:

```text
PlanImpact
  Kind: NewDependencyDiscovered
  Summary: Persistence schema depends on execution attempt modeling.
  EvidenceRefs:
    - artifact:artifact_123
```

Suggested impact kinds:

```text
NewDependencyDiscovered
TaskTooBroad
TaskNoLongerNeeded
AcceptanceTooWeak
AcceptanceTooStrict
ExternalBlockerDiscovered
HostStateChanged
ObjectiveNarrowed
ObjectiveExpanded
ContradictoryEvidence
```

`RefinePlanAsync` should receive plan impacts as structured input.

## Orchestration Outcome

The orchestration package needs its own outcome envelope:

```text
OrchestrationOutcomeEnvelope
  OrchestrationId
  Status
  StopReason
  Objective
  FinalTaskGraph
  CompletedTaskRefs[]
  BlockedTaskRefs[]
  RunRefs[]
  ProgressSnapshot
  WorkingContextSnapshot
  EvidenceRefs[]
  Report
  Details
```

The report may narrate. The envelope proves through task status, run refs, receipts, artifacts, observations, host checks, and evidence refs.

## First Implementation Direction

Create `Agentica.Orchestration` as a separate class library only when the boundary is ready to be made reusable. It should reference `Agentica`.

Initial source can be extracted from:

- `Agentica.CLI/Scenarios/Campaign/CampaignRunner.cs`
- `Agentica.CLI/Scenarios/Campaign/CampaignGraph.cs`
- `Agentica.CLI/Scenarios/Campaign/CampaignAcceptance.cs`
- `Agentica.CLI/Scenarios/Campaign/CampaignProgressSnapshotCompiler.cs`
- `Agentica.CLI/Scenarios/Campaign/CampaignWorkingContextCompiler.cs`
- `Agentica.Tests/CampaignHarnessTests.cs`

But the extraction must add the adaptive surface:

- `ITaskPlanner.CreatePlanAsync`
- `ITaskPlanner.RefinePlanAsync`
- `IRunExecutor`
- task graph mutations
- partial acceptance
- plan invalidation
- plan impacts
- orchestration-level outcome envelope
- single-node pass-through plans

Do not merely rename campaign types into a package.

## Minimal Deterministic Proof

Before an LLM-backed task planner, build a deterministic proof harness:

```text
1. Initial task graph has tasks A, B, C.
2. Task A succeeds and is accepted.
3. Task B succeeds at run level but returns evidence that task C is invalid.
4. Acceptance returns InvalidatedPlan.
5. RefinePlanAsync proposes inserting task B2 before C and revising C.
6. Orchestrator validates and applies the graph mutation.
7. Task B2 runs and is accepted.
8. Revised task C runs and is accepted.
9. Orchestration succeeds with run refs and evidence refs.
```

This proves adaptive orchestration without provider variance.

## Hidden-World Harness Refinement

The first orchestration-level benchmark harness should not hand the planner a task list.

The planner should receive:

```text
goal
public world state
public locations
public blockers
available orchestration capabilities
known rules
prior receipts / working context
```

The planner should then create task nodes from that capability surface.

Bad public surface:

```text
visibleTasks[]
  taskId
  objective
```

This is too close to telling the planner what to plan.

Better public surface:

```text
publicLocations[]
  locationId
  publicName
  state: reachable | blocked | completed
  knownBlocker

capabilities[]
  capabilityId
  description

knownRules[]
```

Example:

```text
Goal:
  Save the kingdom by opening the final sealed route.

Public locations:
  forest shrine: reachable
  lake temple: reachable
  mine depths: blocked by traversal gap
  mountain forge: dormant until missing material is recovered
  sealed castle gate: blocked until enough royal proof is recovered

Capabilities:
  undertake_expedition(location)
  review_receipts()
```

The planner may produce:

```text
Task:
  Undertake one bounded expedition at forest shrine to look for useful evidence, routes, or proof.
```

The hidden oracle decides what receipt, artifact, or unlock that expedition actually produces.

Rules:

- Receipts are generated by the harness, not the LLM.
- Hidden capability names must not appear in the public projection.
- The LLM must not see the full hidden dependency graph.
- The LLM may infer that blocked locations are future concerns, but it must not assume hidden unlocks.
- The executor must validate that attempted locations are reachable before accepting a task.
- The orchestrator consumes receipt-backed unlocks to refine the graph.

This keeps the harness focused on:

```text
Given problem + public state + capability surface, create task graph.
```

Not:

```text
Given task list, choose ordering.
```

## Live Test Readiness

Do not run live hidden-world orchestration until the following refinement pass is complete.

### Prompt and Contract Alignment

`LlmTaskPlanner` currently receives hidden-world data through generic request context serialization. Before live testing, its prompt should explicitly say how to use an affordance-shaped context:

- If context contains `publicLocations`, create task nodes from reachable public locations and available capabilities.
- Do not copy hidden unlocks, hidden capabilities, or guessed answer keys.
- Do not create tool-level `PlanStep` instructions.
- Do not assume blocked locations are executable unless public dependencies prove they are now reachable.
- Use receipts and working context to update the task graph after each run.

This should be phrased generically enough to help other orchestration domains, not only the hidden-world harness.

### Task Mapping Robustness

The deterministic planner can include exact `contextProjection` values such as:

```text
hiddenWorld.locationId
hiddenWorld.capabilityId
```

A live LLM may omit those fields or use different task ids. The hidden-world executor must remain able to map a generated task to a public location by:

1. `contextProjection.locationId`, when present.
2. task objective text containing public location id.
3. task objective text containing public location name.

Add a fake-LLM regression test where the model output omits `contextProjection` and uses only a natural-language objective. The executor should still map the task safely.

### Overplanning Tolerance

The LLM may include blocked locations in the initial task graph. That can be valid if those tasks have dependencies, but invalid if they are scheduled too early.

Before live testing, decide and enforce the first harness rule:

```text
Initial executable tasks should target reachable locations.
Blocked locations may appear only as future tasks with explicit dependencies if the dependency is publicly known.
```

Since hidden unlock dependencies are intentionally not public, the safest first live harness should ask for reachable-location tasks only, then use refinement after receipts reveal a new frontier.

### Planner Output Leak Checks

The current harness checks that the public projection does not leak hidden capabilities. Before live testing, also assert that planner output does not contain hidden capability names in:

- task ids
- task objectives
- context projection values
- acceptance requirement fields

This catches a prompt leak or hallucinated answer-key problem before execution.

### Live Opt-In Guard

There are two relevant switches:

```text
AGENTICA_RUN_LIVE_LLM_TESTS=true
AGENTICA_HIDDEN_WORLD_PLANNER=gemini
```

Hidden-world live mode should require both.

This prevents accidental live orchestration if someone sets only the planner-mode variable while running the full test suite.

### Budget and Timeout Guard

Live hidden-world orchestration can call the LLM multiple times:

```text
initial task graph
refinement after each receipt-backed unlock
```

Before live testing, keep the budget conservative:

```text
MaxRuns: 8-12
MaxRefinements: 4-8
Global timeout: 10 minutes
```

If the planner blocks, overplans, or emits an invalid graph, stop honestly and inspect the envelope instead of retrying indefinitely.

### First Live Sequence

Run live tests in this order:

```text
1. Live_gemini_task_planner_smoke_test
2. Hidden_world_orchestration_completes_with_receipt_backed_unlocks
   with AGENTICA_RUN_LIVE_LLM_TESTS=true
   and AGENTICA_HIDDEN_WORLD_PLANNER=gemini
```

Do not start with the full live suite.

The first live goal is not model brilliance. It is to prove the contract:

```text
LLM creates task graph from public affordances.
Harness emits receipts.
Orchestrator refines from receipts.
No hidden facts leak.
No locked location executes early.
Outcome envelope remains inspectable.
```

## Required Tests

Add focused tests for:

- Single-node pass-through task graph.
- Multi-node graph dependency scheduling.
- Graph validation rejects cycles.
- Graph validation rejects dangling dependencies.
- Completed tasks cannot be silently rewritten.
- Run-level success can produce task-level partial acceptance.
- Task-level `InvalidatedPlan` triggers refinement.
- Refinement can insert a new task between existing tasks.
- Refinement can revise acceptance criteria.
- Refined graph is revalidated before execution continues.
- Context compiler keeps proven facts evidence-backed.
- Context compiler excludes unsupported report claims from proven facts.
- Context compiler includes plan impacts.
- `IRunExecutor` abstraction can be backed by an in-process fake.
- Orchestration stops honestly on blocked task.
- Orchestration stops honestly when max refinement count is reached.

## API Consequence

After `Agentica.Orchestration` exists, the API should expose semantics already owned by the package:

```http
POST /orchestrations
POST /orchestrations/{id}/start
GET  /orchestrations/{id}
GET  /orchestrations/{id}/tasks
GET  /orchestrations/{id}/runs
GET  /orchestrations/{id}/events
GET  /orchestrations/{id}/outcome

POST /runs
GET  /runs/{id}
GET  /runs/{id}/events
GET  /runs/{id}/outcome
```

The API may persist:

- Orchestration records.
- Task graph versions.
- Task graph mutations.
- Task states.
- Run refs.
- Outcome envelopes.
- Execution events.
- Approvals.
- Tool bindings.
- Work contexts.

The API must not invent orchestration behavior inside controllers or database services.

## Completion Condition

This goal is complete when:

1. `Agentica.Orchestration` has a clear adaptive supervisory contract.
2. The orchestrator has create-plan and refine-plan surfaces.
3. `IRunExecutor` is a first-class boundary.
4. A deterministic adaptive harness proves graph mutation after a run outcome.
5. Acceptance is separate from run success.
6. Context carry-forward is compiled, structured, bounded, and evidence-backed.
7. Plan impacts are represented and used by refinement.
8. The campaign prototype has either been adapted into the new package or left as a CLI harness that consumes the new package.
9. The API design can treat orchestration as a package capability, not controller-owned behavior.

Do not call this complete after only extracting the existing campaign runner. The extraction is only successful if it adds adaptive replanning at task granularity.
