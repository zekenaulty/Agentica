# Codex Goal: Agentica Workflow Routing Ontology

## Mission

Define whether Agentica should create classification and routing behavior, and define the smallest generic workflow-execution ontology needed to avoid the flat tool trap without importing host vocabulary such as TurtleQuest IR, Minecraft nouns, maze pathfinding, campaign-specific routes, or UI concepts.

The answer is:

```text
Agentica should provide routing/classification vocabulary, enforcement points, prompt exposure, provenance, and receipts.

Agentica should not create domain routes, domain classifiers, behavior catalogs, hidden-state routing, pathfinding, recovery strategies, or host-specific control graphs.
```

Agentica should know workflow semantics:

```text
primitive vs behavior
observe vs validate vs execute vs complete
slice completion vs mission completion
facts-only guidance vs policy-recommended guidance
LLM-chosen vs host-chosen vs runtime-chosen
changed frame vs unchanged frame
```

Agentica should not know host semantics:

```text
Minecraft
turtle movement IR
maze frontiers
pathfinding internals
mining recipes
protected blocks
book scenes
campaign domain routes
host-hidden capability state
```

## Why This Exists

Agentica now has a stronger event, intent, tool-surface, planning-frame, cooldown, and run-pressure substrate.

The next pressure is not that Agentica lacks more domain logic. The pressure is that serious harnesses are forcing the planner to distinguish between different kinds of callable things:

```text
move_forward                 primitive
dig_line_return              bounded behavior
evaluate_moves               classifier / query
validate_work_order          validator
complete_objective           completion claim
render_map                   human/debug observation
start_behavior               behavior launcher
get_behavior_status          progress query
recover_from_blocker         recovery skill
```

Without structured semantics, every tool is just a flat verb with `ToolKind` and `ToolEffect`.

That creates the flat tool trap:

```text
The planner wastes reasoning reconstructing abstraction levels from tool names and prose.
The host repeats the same prompt instructions in every harness.
The model may choose primitive spam when a bounded behavior exists.
The model may treat a validator as progress.
The model may treat slice completion as mission completion.
The model may not know whether a frame is raw facts or host policy guidance.
The trace cannot clearly say who made the important decision.
```

The fix is not a TurtleQuest IR in Agentica.

The fix is a small workflow-execution ontology that hosts can use to label tools, planning frames, decisions, and completion evidence.

## Angular UI Router Analogy

The old Angular UI Router mental model is useful, but only as an analogy.

In UI Router:

```text
URL/state transition
  -> route match
  -> resolve data
  -> activate nested views
  -> render UI
```

In Agentica:

```text
objective/current evidence
  -> host classifies active workflow context
  -> host projects public route/frame data
  -> Agentica exposes tool surface + planning frames
  -> planner chooses next edge
  -> Agentica validates and executes tool steps
  -> receipts mutate host state
  -> host projects the next route/frame
```

Useful mapping:

| UI Router concept | Agentica analogue |
| --- | --- |
| State name | Workflow route/frame kind |
| Route params | Public scoped context and tool inputs |
| Resolve data | Host-projected facts, receipts, affordances, budgets, pressure |
| Nested views | Nested planning frames or active route stack |
| Transition guard | Plan validator, effect policy, tool refusal, completion evaluator |
| Route outlet | Planner-facing frame/context slot |
| Child route | More constrained capability surface after choosing a task/behavior |

Important differences:

1. UI routing is usually driven by a URL or deterministic event.
2. Agentica routing is evidence-driven and may be cyclic.
3. UI routes render views; Agentica routes expose decision surfaces.
4. UI route transitions usually do not execute domain work; Agentica transitions may invoke tools and mutate host state.
5. Agentica is traversing a graph of possible workflow states, not a fixed linear route tree.

So the correct adaptation is:

```text
Do not build a UI router in Agentica.
Do use route/frame vocabulary to make the active workflow locus explicit.
```

## Core Definitions

### Classification

Classification is labeling a tool, frame, piece of evidence, or decision with generic workflow semantics.

Examples:

```text
Tool abstraction level: Behavior
Tool workflow role: Execute
Completion scope: BehaviorSlice
Planner guidance level: RankedOptions
Decision maker: host_projector
```

Classification must be public, explicit, and host-verifiable. Agentica must not infer domain classification from names like `dig`, `mine`, `scan`, `move`, or `complete`.

### Routing

Routing is choosing the active semantic surface for the next decision.

Examples:

```text
Raw exploration mode
Loop-break mode
Known-route travel mode
Behavior-invocation mode
Recovery mode
Completion-validation mode
Conversation-only mode
```

The host owns domain routing.

Agentica may record and expose routing metadata through `PlanningFrame`, events, and outcome details, but it should not decide that a Minecraft turtle is now in `branch_mining.recovery.blocked_by_gravel` mode.

### Behavior

Behavior is a host-owned bounded operation exposed as a tool.

Examples:

```text
dig_line_return(length)
move_to_visible_cell(x, y)
collect_visible_item(itemId)
validate_inventory_delta(item, count)
complete_objective(evidence)
```

Agentica may validate the tool call shape and record receipts. The host owns legality, execution, domain invariants, and completion facts.

### Route Frame

A route frame is a planning frame whose payload describes the active public workflow locus.

In the current runtime, this can be represented as:

```text
PlanningFrame.Kind = "workflow.route"
PlanningFrame.Payload = host-owned route data
PlanningFrame.ToolSurfaceId = current Agentica ToolSurfaceSnapshot id
```

Do not add a core route graph type until at least two real hosts converge on the same structure.

## Ownership Matrix

| Concern | Agentica Core | Host | Agentica.Clients | Agentica.Orchestration |
| --- | --- | --- | --- | --- |
| Tool kind/effect validation | Owns | Uses | None | Uses via runs |
| Tool execution semantics vocabulary | Owns small generic types | Assigns values | Serializes in prompts | May use for task routing |
| Domain tool classification | Does not infer | Owns | None | May consume host result |
| Domain route graph | Does not own | Owns | None | May own generic task graph only |
| Active planning frame | Records and exposes | Projects | Renders to LLM prompt | May request per task |
| Hidden/blocked/demoted capability universe | Does not own | Owns | None | Does not own unless host exposes |
| Planner guidance level | Defines labels, records | Chooses level | Renders to LLM prompt | May record per task |
| Decision attribution vocabulary | Defines record shape | Supplies host decision attribution | May map LLM choices | May attribute graph decisions |
| Pathfinding / protected policy / recipes | Does not own | Owns | None | None |
| Completion evidence scope | Defines generic scopes | Emits scoped receipts/artifacts | Prompts model to respect scope | Accepts/rejects task evidence |
| Recovery authority label | Defines labels if needed | Chooses and enforces | Prompts model | May select orchestration policy |

## What Agentica Should Create

Agentica should create generic records and enum-like vocabularies that describe workflow execution semantics.

### ToolExecutionSemantics

Proposed shape:

```csharp
public sealed record ToolExecutionSemantics(
    ToolAbstractionLevel AbstractionLevel = ToolAbstractionLevel.Unspecified,
    IReadOnlyList<ToolWorkflowRole>? Roles = null,
    bool IsBounded = false,
    bool IsHostOwned = true,
    CompletionScope? ProducesCompletionScope = null);
```

Rules:

- `IsBounded` defaults to `false`.
- Legacy tools are not trusted as bounded.
- Hosts must opt in when a tool is a bounded behavior, recipe, or compound action.
- Agentica should not infer boundedness from `ToolKind.Action` or `ToolEffect.WritesLocalState`.
- Agentica should render this metadata to planner prompts.
- Agentica may use it in validators only for generic contradictions, not domain legality.

Example generic contradictions:

```text
CompletionClaim tool with no declared completion scope.
Validator tool marked as ProducesCompletionScope=Mission but effect is Unknown.
Primitive tool marked IsBounded=true without host-owned semantics.
```

Do not overbuild these validators in the first slice. Prompt exposure and trace capture matter first.

### ToolAbstractionLevel

Proposed values:

```csharp
public enum ToolAbstractionLevel
{
    Unspecified,
    Primitive,
    Compound,
    Behavior,
    Recipe,
    Validator,
    EvidenceProducer,
    CompletionClaim,
    Conversation,
    PlannerAssist
}
```

Meaning:

| Level | Meaning |
| --- | --- |
| `Unspecified` | Legacy/default. Planner should not assume boundedness. |
| `Primitive` | Small atomic operation. May be useful for recovery or low-level control. |
| `Compound` | Combines multiple primitive operations under host validation. |
| `Behavior` | Bounded host-owned skill slice with clear receipt/progress semantics. |
| `Recipe` | Declarative or procedural host-owned plan pattern, often parameterized. |
| `Validator` | Checks state/evidence; does not itself mean progress. |
| `EvidenceProducer` | Produces receipt/artifact evidence used by completion or acceptance. |
| `CompletionClaim` | Asserts or requests completion for a declared scope. |
| `Conversation` | User interaction or ask/answer tool. |
| `PlannerAssist` | Helps plan or classify without executing domain progress. |

Planner rule:

```text
Prefer bounded Compound, Behavior, or Recipe tools when they match the objective and public context.
Use Primitive tools only when no bounded tool exists, when the host guidance permits primitive control, or when recovery requires it.
Use Validator and EvidenceProducer tools to establish proof, not as progress by themselves.
Use CompletionClaim tools only when matching scoped evidence exists.
```

### ToolWorkflowRole

Proposed values:

```csharp
public enum ToolWorkflowRole
{
    Unspecified,
    Observe,
    Classify,
    Plan,
    Validate,
    Execute,
    Recover,
    Complete,
    Report,
    Ask
}
```

Roles may be multi-valued.

Examples:

```text
maze.analyze_progress:
  AbstractionLevel = Validator or PlannerAssist
  Roles = Observe, Classify

turtlequest.dig_line_return:
  AbstractionLevel = Behavior
  Roles = Execute
  IsBounded = true
  IsHostOwned = true
  ProducesCompletionScope = BehaviorSlice

turtlequest.complete_objective:
  AbstractionLevel = CompletionClaim
  Roles = Complete
  ProducesCompletionScope = Mission
```

### CompletionScope

Proposed values:

```csharp
public enum CompletionScope
{
    Unspecified,
    Step,
    BehaviorSlice,
    WorkOrderSegment,
    Mission,
    Run,
    Conversation
}
```

Apply scope to completion requirements:

```csharp
public sealed record CompletionEvidenceRequirement(
    string Kind,
    string Value,
    CompletionScope Scope = CompletionScope.Run);
```

Rules:

- `Step` evidence proves a step only.
- `BehaviorSlice` evidence proves a bounded host behavior completed.
- `WorkOrderSegment` evidence proves a larger task segment completed.
- `Mission` evidence proves the semantic user objective completed.
- `Run` evidence proves this Agentica run is complete.
- `Conversation` evidence proves a conversation-only objective is complete.

Important:

```text
BehaviorSlice completion must not imply Mission or Run completion.
Validator success must not imply Mission completion unless a scoped requirement says it does.
CompletionClaim tools must name the scope they claim.
```

### PlannerGuidanceLevel

Proposed values:

```csharp
public enum PlannerGuidanceLevel
{
    Unspecified,
    FactsOnly,
    ClassifiedOptions,
    RankedOptions,
    PolicyRecommended,
    HostControlled
}
```

This belongs on `PlanningFrame`, not `ToolDescriptor`.

Meaning:

| Level | Meaning |
| --- | --- |
| `FactsOnly` | Raw public facts, receipts, legal tools. |
| `ClassifiedOptions` | Options have public labels such as blocked, risky, productive, looping. |
| `RankedOptions` | Options are ordered or scored, but planner still chooses. |
| `PolicyRecommended` | Host recommends a posture or option while leaving choice to planner. |
| `HostControlled` | Host chooses or rescues; no longer a clean model-choice benchmark. |

ZPD interpretation:

```text
FactsOnly = independent capability baseline.
ClassifiedOptions / RankedOptions = true scaffolding.
PolicyRecommended = high scaffolding / leading.
HostControlled = production control or rescue, not pure agent reasoning.
```

Agentica should record this level so traces honestly say whether the run was raw, scaffolded, guided, or host-controlled.

### DecisionAttribution

Proposed shape:

```csharp
public sealed record DecisionAttribution(
    string DecisionKind,
    string ChosenBy,
    bool AgentVisible,
    bool HostOwned,
    IReadOnlyList<EvidenceRef> EvidenceRefs);
```

Use string-backed constants instead of rigid enums for `DecisionKind` and `ChosenBy`.

Suggested constants:

```text
DecisionKind:
  skill_selection
  parameter_binding
  route_selection
  primitive_expansion
  recovery_policy
  completion_claim
  continuation_choice
  validation_decision

ChosenBy:
  llm
  agentica_runtime
  host_projector
  host_router
  behavior_catalog
  validator
  pathfinding_algorithm
  external_executor
  completion_evaluator
```

Rules:

- This is public provenance, not chain-of-thought.
- It should attach to plan steps, execution events, receipts, artifacts, and possibly outcome details.
- It should answer who made the decision, not why in hidden reasoning terms.
- `EvidenceRefs` must resolve inside the outcome envelope or be host-owned public frame references.

Example trace:

```text
LLM chose branch_mine_work_order.
Host behavior catalog expanded it.
Host pathfinder chose route.
External executor performed Minecraft commands.
Receipts proved movement and inventory delta.
LLM chose completion claim.
Completion evaluator accepted mission evidence.
```

### Frame And Surface Hashes

Agentica should hash what it owns:

```text
ToolSurfaceSnapshot.ToolSurfaceHash
```

Hosts should hash what they own:

```text
PlanningFrame.FrameHash
PlanningFrame.PreviousFrameHash
PlanningFrame.MateriallyChanged
PlanningFrame.ChangedReasons
```

Reason:

```text
Agentica can know whether its planner-visible tool surface changed.
Only the host can know whether host-projected domain state materially changed.
```

This supports anti-loop behavior:

```text
If the public frame did not materially change and a read-only query was just refused by cooldown,
the planner should not query again for reassurance.
It should act, choose a different evidence source, complete, or surface a blocker.
```

## What Agentica Should Not Create

Agentica should not create domain-specific classification or routing behaviors.

Do not add:

```text
ITurtleQuestRouter
Minecraft route nodes
Maze loop route nodes
Pathfinding route transitions
Mining recipe classifiers
Protected-block policies
Host-hidden capability bindings
Domain IR primitives
Domain behavior catalogs
Domain completion rules
```

Agentica should also not infer semantics from names:

```text
Tool id contains "complete" -> CompletionClaim
Tool id contains "validate" -> Validator
Tool id contains "move" -> Primitive
Tool id contains "plan" -> PlannerAssist
```

Hosts must explicitly label semantics. Unlabeled tools remain `Unspecified`.

Agentica should not create an internal classifier LLM to decide tool levels or routes. That would create a hidden second planner and make provenance worse.

## Route Stack Model

The nested route/view idea can be represented as a public route stack in a planning frame.

Example payload shape:

```json
{
  "routeStack": [
    {
      "routeId": "mission",
      "kind": "mission",
      "label": "Collect enough fuel and return",
      "guidanceLevel": "ClassifiedOptions"
    },
    {
      "routeId": "travel",
      "kind": "capability_family",
      "label": "Known-route travel",
      "guidanceLevel": "RankedOptions"
    },
    {
      "routeId": "recover_low_fuel",
      "kind": "risk_posture",
      "label": "Fuel-constrained recovery",
      "guidanceLevel": "PolicyRecommended"
    }
  ],
  "activeRouteId": "recover_low_fuel",
  "routeChanged": true,
  "changedReasons": [
    "fuel below route reserve",
    "known return path exists",
    "primitive exploration demoted"
  ]
}
```

This is host-owned payload.

Agentica may:

- carry it in `PlanningFrame`
- expose it to the planner
- record it in the outcome envelope
- link it to a tool surface
- attribute decisions against it

Agentica should not:

- validate domain route ids
- select the active domain route
- decide the route graph
- infer hidden route transitions

## Graph Traversal Model

Agentica is not traversing a UI tree. It is participating in an evidence-driven workflow graph.

At each planning turn:

```text
Current public frame + tool surface + receipts + observations + policy pressure
  -> planner chooses next edge as plan step(s)
  -> Agentica validates edge legality
  -> host executes tool edge
  -> receipt/observation/artifact proves result
  -> host updates state
  -> host projects next public frame
```

The graph may cycle:

```text
observe -> plan -> execute -> observe -> recover -> execute -> validate -> complete
```

The graph may narrow:

```text
mission
  -> work order
    -> behavior invocation
      -> validator
        -> completion claim
```

The graph may widen:

```text
blocked behavior
  -> query alternate affordance
  -> choose recovery
  -> re-enter behavior invocation
```

Agentica owns generic traversal mechanics:

- dependency validation
- batching constraints
- effect policy
- cooldown enforcement
- run pressure
- event ordering
- receipt/evidence capture
- completion evaluation calls

The host owns graph semantics:

- what states exist
- which states matter now
- what domain route is active
- what actions are legal
- what recovery options exist
- what completion means

## ZPD Framing

The Zone of Proximal Development lens gives the correct benchmark language.

Agentic ZPD:

```text
The zone between what the model can do from raw public facts
and what the host does for the model.
```

Scaffolding helps the agent see the task structure. Host control completes the task structure for the agent.

Use this ladder:

```text
FactsOnly
  Raw public state, receipts, legal tools.

ClassifiedOptions
  Public labels: safe, blocked, risky, looping, productive, known route.

RankedOptions
  Public ordering: likely best, likely wasteful, higher cost, lower confidence.

PolicyRecommended
  Host guidance: prefer bounded-risk loop break, prefer known route, avoid repeated query.

HostControlled
  Host chooses, rescues, auto-unsticks, or rewrites execution.
```

Benchmark rule:

```text
Every run should declare its guidance level.
```

Interpretation:

```text
If FactsOnly fails and ClassifiedOptions succeeds:
  the agent needed affordance scaffolding.

If ClassifiedOptions fails and RankedOptions succeeds:
  the agent needed option ordering but still chose.

If only PolicyRecommended succeeds:
  the agent needed strategic pressure from the host.

If only HostControlled succeeds:
  the task is outside the current agent-owned reasoning zone.
```

This prevents binary arguments about cheating.

The question becomes:

```text
At what guidance level does the agent enter its ZPD?
```

## Prompt Contract

Agentica.Clients should render workflow semantics into planner prompts.

Prompt rule examples:

```text
Tool semantics are authoritative public context.

Prefer bounded Behavior, Recipe, or Compound tools when public context and tool descriptors say they apply.

Use Primitive tools only when no bounded tool exists, when the active guidance level permits primitive control, or when recovery requires primitive action.

Use Validator and EvidenceProducer tools to establish proof; do not count validator success as domain progress unless scoped completion evidence says so.

Use CompletionClaim tools only when matching evidence satisfies the required completion scope.

Treat BehaviorSlice completion as local progress, not mission completion.

If the current planning frame did not materially change, do not repeat read-only queries for reassurance.

If guidance level is PolicyRecommended or HostControlled, reflect that in public intent and decision attribution.
```

The prompt should point at structured metadata. It should not be the only place these meanings exist.

## Validation Contract

First slice validation should be conservative.

Agentica should validate:

- Unknown tool ids.
- Kind/effect mismatches.
- Disallowed effects.
- Invalid dependencies.
- Invalid batch shapes.
- Input schema failures.
- Cooldown enforcement.
- Completion evidence requirements.

Agentica may later validate generic semantic contradictions:

- Completion claim without completion scope.
- Completion evidence with lower scope than required.
- Tool marked bounded but missing host-owned semantics.
- Planner attempts primitive route when policy frame says primitives are not exposed.

Agentica should not validate:

- Whether a path is safe.
- Whether a mining recipe is correct.
- Whether a maze move is strategic.
- Whether a route label is domain-valid.
- Whether hidden host facts justify a demotion.

## Event And Outcome Contract

Outcome details should let a reader reconstruct:

```text
What public route/frame was active?
What tool surface was visible?
What guidance level was used?
What semantics were attached to the selected tool?
Who chose the step?
What did the host execute?
What receipt/artifact proved it?
What completion scope was satisfied?
Did the public frame materially change before the next query?
```

This should be possible without hidden chain-of-thought.

## Proposed Implementation Order

### Slice 1: Tool Semantics

Add:

- `ToolExecutionSemantics`
- `ToolAbstractionLevel`
- `ToolWorkflowRole`
- optional `ToolDescriptor.Semantics`
- prompt rendering
- tests proving prompt includes semantics

Do not add domain validators yet.

### Slice 2: Completion Scope

Add:

- `CompletionScope`
- scoped `CompletionEvidenceRequirement`
- artifact/receipt conventions for completion scope
- tests proving `BehaviorSlice` evidence does not satisfy `Mission` or `Run` requirements

### Slice 3: Planner Guidance Level

Add:

- `PlannerGuidanceLevel`
- optional `PlanningFrame.GuidanceLevel`
- prompt rendering
- outcome/detail capture
- tests proving frames carry guidance labels

### Slice 4: Decision Attribution

Add:

- `DecisionAttribution`
- event attachment
- optional plan-step/receipt/artifact attachment if needed
- tests proving attribution is envelope-resolvable and public

### Slice 5: Frame And Surface Hashes

Add:

- `ToolSurfaceSnapshot.SurfaceHash`
- optional `PlanningFrame.FrameHash`
- optional `PlanningFrame.PreviousFrameHash`
- optional `PlanningFrame.MateriallyChanged`
- optional `PlanningFrame.ChangedReasons`
- tests proving repeated unchanged frames can be detected

### Slice 6: Host Route Frame Convention

Document, then optionally implement a helper for:

```text
PlanningFrame.Kind = "workflow.route"
```

Do not add a core route graph until multiple hosts independently need the same exact shape.

## Acceptance Criteria

This design is successful when:

1. A tool can declare whether it is a primitive, compound action, behavior, validator, evidence producer, or completion claim.
2. The planner prompt receives that declaration as structured public context.
3. A completion evaluator can distinguish behavior-slice evidence from mission/run evidence.
4. A planning frame can declare whether it is facts-only, classified, ranked, policy-recommended, or host-controlled.
5. Events/outcomes can attribute important decisions to LLM, Agentica runtime, host projector, host router, validator, behavior catalog, or external executor.
6. A repeated-query loop can be diagnosed by comparing unchanged public frames/tool surfaces plus cooldown refusals.
7. No TurtleQuest, MazeQuest, Minecraft, pathfinding, mining, or other domain vocabulary is added to Agentica core.

## Non-Goals

Do not build:

- UI router
- route graph engine
- host behavior catalog
- domain classifier
- pathfinder
- TurtleQuest IR
- hidden planner
- event bus
- persistence/replay layer
- MCP adapter
- cockpit UI
- memory system

## Final Boundary Statement

Agentica should create the generic language for workflow routing and execution semantics.

Hosts should create the domain routes, classifiers, behavior catalogs, and projections.

Agentica should record what the host exposed and what the planner chose.

The host should prove what actually happened.

The planner should not have to infer the execution layer from tool names and prose.

