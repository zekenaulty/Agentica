# Codex Goal: Agentica Run Continuity Spine

> Lifecycle: **Incubating** · Completion: **85%** · Canonical status: [Agentica Product Status And Goal Xref](Agentica.ProductStatus.md)

## Feature Summary

Add a run-level continuity subsystem that keeps a compact, evidence-grounded interpretation of a run across planning calls, refinements, tool calls, orchestration tasks, and bounded Agentica runs.

Working feature name:

```text
Run Continuity Spine
```

Recommended model names:

```text
RunProjection        = initial long-arc expected plan/model for a run.
GoalSpine            = compact current mission continuity object injected into context.
BreadcrumbLedger     = append-only compact continuity trail.
DivergenceLedger     = expected-vs-actual mismatch trail.
PatternLibrary       = optional future reusable lessons across runs.
```

Core invariant:

```text
GoalSpine shapes cognition.
Receipts, observations, artifacts, host checks, and verifiers prove reality.
```

This is not hidden chain-of-thought storage. It is public operational memory maintained by runtime evidence.

## Problem Statement

Agentica currently has useful local intent primitives:

- `PlanStep.Intent`
- `ExecutionIntent`
- plan reasons
- observations
- receipts
- artifacts
- outcome reports
- planning frames
- orchestration phase reports

These explain individual actions and bounded run outcomes.

They do not reliably preserve the evolving answer to:

```text
What are we trying to accomplish?
What did we expect would happen?
What actually happened?
Where did reality diverge from expectation?
What lesson or constraint must shape the next decision?
```

This creates the following failure modes:

1. Continuity drift
   The planner can locally optimize while forgetting the run-level goal or latest reality.

2. Repeated mistakes
   The agent can repeat a failed inference pattern because the correction is buried in raw history.

3. Weak postmortem learning
   Logs can be inspected, but there is no first-class expected-vs-actual model.

4. Context bloat
   The planner needs too much raw trace to reconstruct current strategic state.

5. Shallow intent
   Step intent exists, but the evolving mission interpretation is not visible enough.

6. Hallucination masking
   Model-authored hypotheses can reappear as if they were proven unless runtime continuity records what was disproven.

ChessQuest exposed the need clearly:

```text
Expected: tactical phase creates advantage.
Actual: tactical phase lost material.
Lesson: do not treat one-ply projection as safety.
Next decision pressure: stabilize before speculative attacks.
```

That continuity should not need to be reconstructed from raw receipts every turn.

## Non-Goals

Do not build:

- hidden chain-of-thought storage
- unbounded transcript memory
- vector memory
- autonomous cross-run learning in V1
- a replacement for receipts, observations, artifacts, or verifiers
- a giant context dump
- a system where model claims become proof
- a new planner hierarchy
- a domain-specific ChessQuest-only memory object

GoalSpine is not proof.

GoalSpine is a compact operational guide grounded in proof.

## Existing Related Primitives

Inspect and reuse these before introducing new runtime concepts:

- `RunRequest`
- `WorkflowPlan`
- `PlanStep`
- `ExecutionIntent`
- `ExecutionEvent`
- `Observation`
- `ToolResult`
- receipts
- artifacts
- `PlanRefinement`
- `OutcomeEnvelope`
- `OutcomeReport`
- `PlanningFrame`
- `IPlanningFrameProjector`
- `PlanningRequest.ExecutionContext`
- `TaskGraphPlan`
- `TaskNode`
- `TaskGraphRefinement`
- `TaskOrchestrator`
- `WorkContextSnapshot`
- scenario-specific planning-frame compilers
- ChessQuest phase reports

The first implementation should compile across these primitives. It should not replace them.

## Local Intent vs GoalSpine

Local intent:

```json
{
  "action": "Play g1f3",
  "rationale": "Develop the knight and prepare for castling.",
  "expectedOutcome": "Improve development."
}
```

GoalSpine:

```json
{
  "rootGoal": "Win the game as White. Draw is not success.",
  "currentInterpretation": "Opening development succeeded, but tactical phase lost material.",
  "activePriority": "Stabilize before speculative attacks.",
  "latestRealityUpdate": "Material delta is negative after the prior phase.",
  "knownDivergence": "Expected tactical gain; actual material loss.",
  "nextDecisionPressure": "Model opponent replies before claiming safety or advantage."
}
```

They are not interchangeable.

Local intent explains the next action.

GoalSpine explains what the whole run currently means.

## Proposed Data Models

### RunProjection

```csharp
public sealed record RunProjection(
    string ProjectionId,
    string RunId,
    int Version,
    string RootGoal,
    string ExpectedArc,
    IReadOnlyList<ExpectedPhase> ExpectedPhases,
    IReadOnlyList<RunAssumption> Assumptions,
    IReadOnlyList<RunRisk> Risks,
    IReadOnlyList<ReplanTrigger> ReplanTriggers,
    IReadOnlyList<string> SuccessCriteria,
    IReadOnlyList<string> KnownConstraints);
```

### ExpectedPhase

```csharp
public sealed record ExpectedPhase(
    string PhaseId,
    string Name,
    string Objective,
    string ExpectedEntryCondition,
    string ExpectedExitCondition,
    IReadOnlyList<string> ExpectedActions,
    IReadOnlyList<string> Risks);
```

### RunAssumption

```csharp
public sealed record RunAssumption(
    string AssumptionId,
    string Summary,
    string Status,
    IReadOnlyList<EvidenceRef> EvidenceRefs);
```

Suggested `Status` values:

```text
assumed
supported
contradicted
obsolete
```

### RunRisk

```csharp
public sealed record RunRisk(
    string RiskId,
    string Summary,
    string Mitigation,
    string Severity);
```

### ReplanTrigger

```csharp
public sealed record ReplanTrigger(
    string TriggerId,
    string Summary,
    string Source);
```

Suggested `Source` values:

```text
host
planner
domain
policy
```

### GoalSpine

```csharp
public sealed record GoalSpine(
    string SpineId,
    string RunId,
    int Version,
    string RootGoal,
    string CurrentInterpretation,
    string ActivePriority,
    string LatestRealityUpdate,
    string? KnownDivergence,
    string NextDecisionPressure,
    IReadOnlyList<string> ActiveConstraints,
    IReadOnlyList<string> RecentLessons,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<EvidenceRef> EvidenceRefs,
    DateTimeOffset UpdatedAt);
```

Hard rules:

- `CurrentInterpretation` may summarize.
- `ActivePriority` may shape next planning.
- `KnownDivergence` must cite evidence when non-null.
- `RecentLessons` must be operational and compact.
- `EvidenceRefs` must point to receipts, observations, artifacts, validation issues, host checks, or outcome facts.

### GoalSpineUpdate

```csharp
public sealed record GoalSpineUpdate(
    string UpdateId,
    string RunId,
    int PreviousVersion,
    int NewVersion,
    string Summary,
    string SourceKind,
    string SourceId,
    GoalSpine Previous,
    GoalSpine Current,
    IReadOnlyList<EvidenceRef> EvidenceRefs,
    DateTimeOffset UpdatedAt);
```

Suggested `SourceKind` values:

```text
initial_projection
receipt
observation
artifact
validation_issue
plan_refinement
phase_report
outcome
host_state
```

### BreadcrumbEntry

```csharp
public sealed record BreadcrumbEntry(
    string EntryId,
    string RunId,
    int Sequence,
    string Kind,
    string Summary,
    string? StepId,
    string? ToolId,
    string? ReceiptId,
    string? ObservationId,
    string? ArtifactId,
    string? PlanId,
    string? PhaseId,
    DateTimeOffset At);
```

Suggested `Kind` values:

```text
run.projected
goalspine.initialized
step.intent
tool.invoked
receipt.emitted
observation.made
artifact.emitted
plan.refined
phase.completed
divergence.detected
goalspine.updated
outcome.reported
```

### DivergenceEntry

```csharp
public sealed record DivergenceEntry(
    string DivergenceId,
    string RunId,
    int Sequence,
    string Expected,
    string Actual,
    string Severity,
    string Interpretation,
    string RecommendedAdjustment,
    IReadOnlyList<EvidenceRef> EvidenceRefs,
    DateTimeOffset At);
```

Suggested `Severity` values:

```text
notice
warning
major
terminal
```

### PatternExtractionCandidate

Future only:

```csharp
public sealed record PatternExtractionCandidate(
    string PatternId,
    string Name,
    string Description,
    string Trigger,
    string Consequence,
    string SuggestedGuardrail,
    IReadOnlyList<EvidenceRef> EvidenceRefs);
```

Do not implement `PatternLibrary` in V1.

## Runtime Flow

### At Run Start

1. Receive `RunRequest`.
2. Compile `RunProjection`.
3. Compile initial `GoalSpine`.
4. Append breadcrumbs:
   - `run.projected`
   - `goalspine.initialized`
5. Inject compact `GoalSpine` into planner context / planning frame.

Initial `GoalSpine` should be reproducible and mostly deterministic.

LLM-authored projection can be added later, but V1 should not require an LLM to create the first spine.

### During Step Execution

For each meaningful step:

1. Capture local intent.
2. Append breadcrumb for step intent.
3. Execute tool.
4. Append receipt/observation/artifact breadcrumbs.
5. Apply rule-based divergence detection.
6. Update `GoalSpine` when meaningful reality changed.
7. Inject updated `GoalSpine` into next planner/refinement context.

### During Plan Refinement

1. Provide current compact `GoalSpine`.
2. Provide recent divergences only, not full ledger.
3. Planner proposes refinement.
4. Runtime validates refinement.
5. Append plan refinement breadcrumb.
6. Update `GoalSpine` if refinement changes the active priority, constraints, or open questions.

### At Orchestration Boundary

For task-level orchestration:

1. Orchestrator compiles a task-level `GoalSpine`.
2. Child `AgenticaRunner` receives scoped spine in `RunRequest.Context`.
3. Child run returns `OutcomeEnvelope`.
4. Orchestrator updates parent spine from child outcome and task acceptance.

Do not pass full parent ledgers into child runs.

Use the compact spine only.

### At Outcome

Produce an expected-vs-actual postmortem:

- initial expected arc
- actual arc
- major divergences
- unresolved open questions
- successful adaptations
- repeated failure patterns
- guardrail recommendations

This postmortem is report material. It is not proof.

## Planner Context Integration

The planner-facing object should be compact.

Recommended context key:

```text
goalSpine
```

Example:

```json
{
  "goalSpine": {
    "rootGoal": "Win the game as White. Draw is not success.",
    "currentInterpretation": "The opening phase completed, but tactical play lost material.",
    "activePriority": "Stabilize and avoid further material loss before seeking tactics.",
    "latestRealityUpdate": "Material delta is negative after tactical exchanges.",
    "knownDivergence": "Expected tactical advantage; actual receipts show material loss.",
    "nextDecisionPressure": "Verify opponent replies before claiming safety or advantage.",
    "activeConstraints": [
      "Do not treat legal projection as move quality.",
      "Do not claim checkmate without terminal verifier.",
      "Prefer defensive stabilization while material delta is negative."
    ],
    "recentLessons": [
      "One-ply project_line only proves legality and resulting board state.",
      "Tactical claims require modeled opponent replies or explicit uncertainty."
    ],
    "openQuestions": [
      "Can the position be stabilized?",
      "Is there counterplay despite material deficit?"
    ]
  }
}
```

Rules:

- Include `GoalSpine`, not full ledger.
- Keep fields short.
- Bound list lengths.
- Strip raw model hidden reasoning.
- Mark hypotheses as hypotheses.
- Cite evidence refs when possible.

Suggested initial budget:

```text
CurrentInterpretation: <= 240 chars
ActivePriority: <= 160 chars
LatestRealityUpdate: <= 240 chars
KnownDivergence: <= 240 chars
NextDecisionPressure: <= 200 chars
ActiveConstraints: <= 5 items
RecentLessons: <= 5 items
OpenQuestions: <= 3 items
```

## Event And Logging Integration

Add execution/orchestration event kinds:

```text
run.projection.created
goalspine.initialized
goalspine.updated
breadcrumb.appended
divergence.detected
postmortem.reported
```

Run logs should persist:

```text
run-projection.json
goalspine-current.json
goalspine-updates.jsonl
breadcrumb-ledger.jsonl
divergence-ledger.jsonl
postmortem.json
```

Console should not print the whole ledger.

Console may print compact high-signal updates:

```text
[goal] priority: Stabilize before speculative attacks.
[divergence] expected tactical gain; actual material loss.
```

## Divergence Detection V1

Start with rule-based divergence only.

Do not use an LLM summarizer in V1.

Initial generic divergence rules:

1. Expected mutation but receipt refused.
2. Expected mutation but no state changed.
3. Expected artifact but verifier refused.
4. Expected valid planner JSON but output was malformed.
5. Provider finish reason was truncation / max tokens.
6. Plan refinement reused completed step id.
7. Task graph had invalid acceptance kind.
8. Run succeeded but task acceptance was rejected.
9. Completion evaluator did not accept plan exhaustion.

ChessQuest-specific divergence rules:

1. Agent claimed checkmate but committed move was not checkmate.
2. Agent claimed check but committed move did not check.
3. Agent claimed safety from legal move or one-ply projection only.
4. Phase expected tactical gain but material delta worsened.
5. Phase selected conversion/endgame while material delta was negative.
6. Terminal state winner was not agent.
7. Move was refused due to stale legal move observation.

## Integration Slices

### Slice 1: Inventory Existing Primitives

Deliverable:

```text
docs/Agentica.RunContinuitySpine.Inventory.md
```

Inventory:

- `ExecutionIntent`
- `ExecutionEvent`
- `Observation`
- `ToolResult`
- receipts
- artifacts
- `OutcomeEnvelope`
- `PlanningFrame`
- `IPlanningFrameProjector`
- `TaskOrchestrator`
- `WorkContextSnapshot`
- ChessQuest phase reports

For each primitive record:

- what it already provides
- whether it can be evidence
- whether it can update a spine
- whether it should be planner-visible
- gaps

Acceptance:

- document identifies minimal integration seam for V1
- no code changes required

### Slice 2: Add GoalSpine Model And Default Compiler

Add:

```text
GoalSpine
GoalSpineUpdate
IGoalSpineCompiler
DefaultGoalSpineCompiler
GoalSpineOptions
```

Compiler methods:

```csharp
GoalSpine CompileInitial(RunRequest request);

GoalSpineUpdate UpdateFromReceipt(
    GoalSpine current,
    Receipt receipt,
    GoalSpineUpdateContext context);

GoalSpineUpdate UpdateFromRefinement(
    GoalSpine current,
    PlanRefinement refinement,
    GoalSpineUpdateContext context);
```

Acceptance:

- initial spine includes root goal and active priority
- update from refused receipt changes latest reality update
- update never treats report prose as proof
- bounded-size tests pass

### Slice 3: Add BreadcrumbLedger

Add:

```text
BreadcrumbEntry
BreadcrumbLedger
IBreadcrumbSink
InMemoryBreadcrumbLedger
```

Acceptance:

- step intent appends breadcrumb
- receipt appends breadcrumb
- observation appends breadcrumb
- ledger is not injected wholesale into planner context

### Slice 4: Add DivergenceLedger

Add:

```text
DivergenceEntry
IDivergenceDetector
RuleBasedDivergenceDetector
DivergenceLedger
```

Acceptance:

- refused mutation creates divergence
- FEN unchanged after expected mutation creates divergence in ChessQuest
- malformed planner JSON creates divergence
- max token finish reason creates divergence
- divergence references evidence

### Slice 5: Inject GoalSpine Into Planning Frames

Add `GoalSpine` to:

- `PlanningRequest` context projection where appropriate
- planning frame payloads
- ChessQuest `chessFrame` or adjacent `goalSpine`
- orchestration working context

Acceptance:

- planner frame includes compact spine
- full breadcrumb ledger is absent
- size budget enforced
- tests prove completion still depends on verifier/artifacts, not spine

### Slice 6: Outcome/Postmortem Report

Add postmortem report section:

```text
Expected Arc
Actual Arc
Major Divergences
Reality Updates
Recent Lessons
Recommended Guardrails
```

Acceptance:

- outcome includes expected-vs-actual summary
- evidence refs connect major divergence to receipts/observations/artifacts
- report does not become proof

### Slice 7: PatternLibrary

Future only.

Add after V1 proves useful in at least two harnesses.

Candidate extraction:

- repeated stale legal move observation
- repeated one-ply safety overclaim
- repeated JSON truncation
- repeated phase drift
- repeated no-op mutation attempt

Do not implement until there are enough logs to justify it.

## ChessQuest Application

Initial `RunProjection`:

```json
{
  "rootGoal": "Win as White. Draw is not success.",
  "expectedArc": "Opening development -> tactical pressure -> conversion or checkmate.",
  "expectedPhases": [
    {
      "name": "opening",
      "objective": "Develop pieces, control center, secure king."
    },
    {
      "name": "tactical",
      "objective": "Create or exploit advantage without blundering material."
    },
    {
      "name": "conversion",
      "objective": "Convert durable advantage into terminal win."
    }
  ],
  "risks": [
    "Model may confuse legal move with good move.",
    "Model may overclaim checkmate.",
    "Model may fail to model opponent replies."
  ]
}
```

After tactical phase loses material:

```json
{
  "rootGoal": "Win as White. Draw is not success.",
  "currentInterpretation": "The expected tactical advantage did not occur. The tactical phase produced material loss.",
  "activePriority": "Stabilize and avoid further material loss.",
  "latestRealityUpdate": "Material delta is negative.",
  "knownDivergence": "Expected tactical gain; actual material loss.",
  "nextDecisionPressure": "Require opponent-reply modeling before tactical claims.",
  "recentLessons": [
    "One-ply project_line is not a safety proof.",
    "Legal projection does not imply move quality."
  ]
}
```

ChessQuest-specific updates:

- unsupported safety claim updates `RecentLessons`
- stale legal observation refusal updates `NextDecisionPressure`
- material loss updates `ActivePriority`
- terminal loss updates `CurrentInterpretation` and stops planning

## OutpostQuest Application

Initial `RunProjection`:

```json
{
  "rootGoal": "Restore comms and evacuate survivors.",
  "expectedArc": "Stabilize oxygen -> restore power -> reach medbay -> repair comms -> evacuate.",
  "risks": [
    "Oxygen timer may force triage.",
    "Crew report may be incomplete.",
    "Repair kit may be insufficient for all systems."
  ]
}
```

After blocked medbay route:

```json
{
  "rootGoal": "Restore comms and evacuate survivors.",
  "currentInterpretation": "Medbay rescue is blocked by jammed door; oxygen is now the gating risk.",
  "activePriority": "Restore ventilation before further exploration.",
  "latestRealityUpdate": "Door jam cost time and did not progress rescue.",
  "knownDivergence": "Expected direct medbay access; actual access blocked.",
  "nextDecisionPressure": "Choose between power relay repair and forced door attempt.",
  "openQuestions": [
    "Can survivor remain alive long enough for power repair?",
    "Is there an alternate route to medbay?"
  ]
}
```

## Tests

### GoalSpine Creation

```text
Given a RunRequest,
when CompileInitial runs,
then GoalSpine contains root goal, active priority, initial interpretation, and no divergence.
```

### GoalSpine Update From Receipt

```text
Given a refused tool receipt,
when UpdateFromReceipt runs,
then GoalSpine records latest reality update and next decision pressure.
```

### Divergence Detection

```text
Given expected mutation but receipt shows no mutation,
when divergence detection runs,
then DivergenceLedger records expected-vs-actual mismatch with evidence refs.
```

### Planner Context Injection

```text
Given updated GoalSpine,
when a planning frame is projected,
then compact goalSpine is included and full ledger is absent.
```

### No Proof Confusion

```text
Given GoalSpine says "appears complete",
when completion evaluator requires artifact,
then completion still fails without the artifact.
```

### Bounded Size

```text
Given many breadcrumbs,
when GoalSpine is projected,
then it stays under configured field/list limits.
```

### No Hidden Reasoning

```text
Given planner includes hidden/freeform reasoning text,
when GoalSpine update is compiled,
then hidden reasoning is not copied into GoalSpine.
```

### ChessQuest Regression

```text
Given tactical phase loses material,
when phase report updates GoalSpine,
then active priority becomes stabilization and recent lessons mention one-ply projection is not safety.
```

### JSON/Truncation Regression

```text
Given planner output truncates,
when repair is attempted,
then DivergenceLedger records truncation and repair outcome.
```

## Acceptance Criteria

V1 is complete when:

1. Every run can get a compact initial `GoalSpine`.
2. `GoalSpine` is included in planner-visible context.
3. Refused receipts can update `GoalSpine`.
4. Plan refinements can update `GoalSpine`.
5. A compact breadcrumb ledger records local continuity.
6. Rule-based divergences are recorded for at least three generic failure types.
7. ChessQuest can update `GoalSpine` from phase reports.
8. Outcome reports include expected-vs-actual summary.
9. Tests prove `GoalSpine` does not satisfy completion evidence.
10. Full ledgers are persisted to logs but not dumped into every prompt.

## Implementation Constraints

- Keep V1 deterministic where possible.
- Do not require live LLM calls for spine generation.
- Do not use vector storage.
- Do not store hidden chain-of-thought.
- Do not add cross-run pattern learning yet.
- Keep planner-facing projection bounded.
- Prefer evidence refs over embedded payloads.
- Add scenario-specific compilers only after generic seams exist.

## Open Questions For The User

1. Where should V1 live?
   - `Agentica` core
   - incubating under `Agentica/Continuity`
   - orchestration package only
   - Lab host first

   Recommendation:

   Start as generic runtime models plus a host/compiler seam. Avoid CLI-only if ChessQuest and future OutpostQuest both need it.

2. Should `RunProjection` be deterministic in V1, or should the planner be asked to create it?

   Recommendation:

   Deterministic V1. Add LLM-authored projection later as an optional mode.

3. Should `GoalSpine` exist for every `AgenticaRunner` run, or only when a host opts in?

   Recommendation:

   Opt-in at first through execution/planning context options.

4. Should orchestration maintain a parent `GoalSpine` separate from child run `GoalSpine`?

   Recommendation:

   Yes. Parent spine is task/campaign continuity. Child spine is bounded run continuity.

5. Should `GoalSpine` updates be emitted as normal execution events?

   Recommendation:

   Yes, but keep payload compact. Persist full update records in logs.

6. Should `GoalSpine` include domain-specific fields?

   Recommendation:

   No. Keep generic fields. Domain-specific facts belong in host context and evidence refs.

7. Should `PatternLibrary` be implemented before multiple harnesses produce repeated failures?

   Recommendation:

   No.

8. What should be the first non-ChessQuest harness to prove generic value?

   Candidate:

   OutpostQuest or another resource/timer/blocker scenario where expected-vs-actual continuity matters more than chess skill.
