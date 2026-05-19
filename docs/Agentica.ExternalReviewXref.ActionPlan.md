# Agentica External Review Xref And Action Plan

Date: 2026-05-11

This document cross-references the Grok and Opus 4.6 reviews against the current local repository state and turns grounded concerns into action tracks.

The local worktree reviewed here includes uncommitted and untracked changes. The findings below reflect the current files, not only the last committed state.

## Rereview Summary

Both reviews are directionally accurate. Agentica's strongest property is still the contract-first execution loop:

```text
Model proposes -> Runtime validates -> Tools execute -> Receipts prove.
```

The core strengths are real in code:

- `Agentica` owns run, plan, validation, execution, events, receipts, observations, artifacts, and outcome envelopes.
- `Agentica.Clients` isolates provider and LLM planning code.
- `Agentica.CLI` owns host harnesses such as MazeQuest, Quest, HexQuest, WorkbenchQuest, and campaign-style demos.
- Validation is fail-closed for unknown tools, kind/effect mismatches, input schema failures, invalid dependencies, disallowed effects, and malformed batches.
- Outcome proof is structural: success must come from receipts, observations, artifacts, validation issues, completion evidence, and stop reasons, not report prose.
- The test suite is no longer tiny. The current `dotnet test Agentica.slnx` run executed 128 passing tests, including runtime invariants, client mapping, harness behavior, orchestration tests, cooldown enforcement, run-pressure projection, and opt-in live Gemini smoke-test guards.

## Closed In This Hardening Pass

The following review items are closed for the current slice:

- Cooldown / repeated query loop: runtime cooldown enforcement, scoped cooldown keys, mutation reset, duplicate batch early-out, and prompt pressure are implemented and tested.
- Prompt query-first pressure: planner prompt language now frames read-only queries as scarce missing-precondition checks, not reassurance, and prefers bounded action when public context is sufficient.
- Run/time pressure projection: tool-surface policy summaries now expose remaining step budget, refinement/continuation budget, elapsed time, timeout/remaining timeout, time pressure, run pressure, and recommended planning posture.
- User-facing reason output: console sinks and MazeQuest watch output prefer `UserFacingReason` and only fall back to planner-facing `ExecutionIntent`.
- Gemini structured schema mapping: Gemini maps valid `LlmStructuredOutputOptions.JsonSchema` into `ResponseJsonSchema` and rejects invalid schema JSON as `BadRequest`.
- Per-task orchestration `MaxRuns`: `TaskRunCounts` now enforces per-task budgets, and validated graph replacement clears old task counts without letting unchanged tasks loop.
- Orchestration boundary doc mismatch: resolved by decision. Orchestration is incubating in core; splitting into `Agentica.Orchestration` remains a future boundary milestone, not an active contradiction.
- Event/intent/tool-surface substrate: enriched events, public execution intent, user-facing reasons, tool-surface snapshots, planning frames, and no-ghost envelope references are implemented for this slice.

The orchestration boundary finding is closed as a documented incubation decision. The earlier contradiction was:

```text
README.md said long-horizon orchestration was not part of the core runtime package.
docs/Agentica.DeferredWork.md said the orchestration package was deferred.
docs/CodexGoal.Agentica.OrchestrationAdaptiveSupervisor.md said Agentica.Orchestration should become a separate class library when reusable.
Current code had Agentica/Orchestration/*.cs inside Agentica.csproj.
```

That mismatch is now documented as deliberate incubation in core after the migration. The remaining decision is when to split stable reusable contracts back into a separate `Agentica.Orchestration` package.

The approval finding is also real, but intentionally left as design work for now:

```text
ToolDescriptor.RequiresApproval is metadata, not a pre-execution runtime gate.
```

Approval scope needs a design pass before implementation. The open question is whether approval belongs to Agentica policy, host policy, user policy, agent-to-agent delegation, or a combination.

## Review Claim Xref

| Review concern | Raised by | Current evidence | Rereview judgment | Action |
| --- | --- | --- | --- | --- |
| Runner is getting heavy | Grok, Opus | `Agentica/Execution/AgenticaRunner.cs` is about 1490 lines. `RunAttemptAsync` handles planning, validation, continuation, step selection, execution result recording, refinement, timeout/cancellation, retries, events, cooldowns, and outcome assembly. | Confirmed design debt, not currently an execution bug. The flow is still readable, but it is carrying too many reasons to change. | Add a refactor trigger and extract only when the next runner feature lands or tests become hard to target. |
| Approval metadata is not enforced before execution | Opus, follow-up review | `ToolDescriptor.RequiresApproval` is captured on descriptors/events, but `AgenticaRunner` does not gate execution before `ITool.ExecuteAsync`. | Confirmed. Intentionally not implemented in the current pass pending scope design. | Add approval scope decision track. Do not add runtime gating until approval grants and host/user/agent boundaries are clear. |
| Orchestration boundary may bloat core | Grok, Opus | `Agentica/Orchestration` currently sits in the core project. Docs now label it as incubating in core with split criteria. | Confirmed and partially resolved by documentation. | Keep the intended dependency direction. Split to `Agentica.Orchestration` when reusable contracts or durable replay justify it. |
| Prompt engineering surface is brittle | Grok, Opus | `WorkflowPlanPromptBuilder` and `TaskGraphPromptBuilder` carry substantial safety and JSON-shape instructions. Runtime validation catches bad plans after generation; prompt contract tests now cover intent, context batching, cooldown, and anti-stall language. | Partially confirmed. Coverage has improved, but model-output negative cases still matter. | Add fake-model negative cases before relying on live MazeQuest or hidden-world runs as confidence. |
| More edge-case tests are needed | Grok, Opus | Many tests exist, including runtime invariants and orchestration tests. Gaps remain around prompt output leaks, malformed graph refinements, runner cancellation edge cases, and planner-output anti-leak checks. | Partially confirmed. Coverage is stronger than the reviews imply, but gaps are real. | Add focused regression tests in the tracks below. |
| Documentation vs reality gap | Grok | README and deferred-work docs are honest overall, but now conflict with current `Agentica/Orchestration` placement. Goal docs also describe future cockpit/orchestration states that may look more complete than the shipped runtime. | Confirmed. | Add "current implementation status" notes to docs affected by orchestration and cockpit/event goals. |
| Tool execution exceptions are generic | Opus | `AgenticaRunner` catches generic `Exception` around tool execution and converts it into failed receipts/diagnostics. Planner exceptions have a dedicated taxonomy. | Confirmed medium-priority improvement. | Add a small tool failure taxonomy if hosts need retry/repair decisions beyond receipt status. |
| Batch execution could make run state races fragile | Opus | Batch tools run with `Task.WhenAll`, then `RecordToolResult` mutates `AgenticaRun` sequentially. Current runner-owned lists are safe under that invariant. | Not an active bug, but the invariant is implicit. | Document and test that only the runner mutates `AgenticaRun`; avoid passing `AgenticaRun` into tools. |
| `AgenticaIds.New` has only 8 hex chars after prefix | Opus | `Agentica/AgenticaIds.cs` truncates generated ids to `prefix_` plus 8 GUID hex chars. | Confirmed. Fine for small demos, weak for durable logs and cross-run references. | Increase entropy before ids become externally durable. |
| Compact/Trim helper duplication | Opus | Similar helpers exist in `ConsoleEventSink`, `DeterministicWorkContextCompiler`, campaign context compilers, MazeQuest watch/projector code. | Confirmed low-priority cleanup. | Extract a tiny internal text compaction helper when touching those areas next. |
| Orchestration state mutability | Opus | `OrchestrationState` exposes settable properties and mutable lists. | Confirmed, acceptable while orchestration is in-process only. Risk rises with persistence/resume/replay. | Leave for now, but introduce transition methods before persistence or concurrent orchestration. |
| Tool input dictionaries are weakly typed | Opus | `PlanStep.Input` is `IReadOnlyDictionary<string, object?>`; schema validation uses `ToolInputValueType`, ranges, enums, and `JsonElement` numeric handling. | Confirmed tradeoff, not a current problem. | Do not overcorrect yet. Add typed adapters only when multiple host/tool surfaces need them. |
| MazeQuest harness complexity is growing | Grok, Opus | MazeQuest now includes capability surfaces, cockpit frames, pathfinding, energy/health, objective chains, route evaluation, loop detection, and rich watch output. | Confirmed but intentional. It is a pressure harness, not runtime vocabulary. | Keep MazeQuest code in CLI/tests. Add anti-leak and surface-contract tests as it grows. |
| Per-task `MaxRuns` is modeled but not enforced | Follow-up review | `TaskNode.MaxRuns` is validated and now tracked through `OrchestrationState.TaskRunCounts`. `TaskOrchestrator` blocks exhausted tasks before re-execution. | Resolved in current pass. | Keep tests covering per-task run budget. |
| User-facing reason is captured but not printed | Follow-up review | Console sinks now prefer `ExecutionEvent.UserFacingReason` and fall back to `ExecutionIntent` only when no projection exists. | Resolved in current pass. | Keep operator/log output separate from planner-facing intent. |
| Gemini schema option is modeled but unused | Follow-up review | `LlmStructuredOutputOptions.JsonSchema` now maps to Gemini `GenerateContentConfig.ResponseJsonSchema`; invalid schema JSON is rejected as `BadRequest`. | Resolved in current pass. | Add provider-specific schema tests as other clients/adapters are added. |

## Action Tracks

### 1. Orchestration Boundary Decision

Priority: P0

Problem:

The code and docs currently disagree about where orchestration belongs. The design docs say orchestration should be a separate package/class library once reusable, while `Agentica/Orchestration` is now inside `Agentica.csproj`.

Current decision:

`Agentica/Orchestration` is incubating inside the core project after the migration. This is acceptable for rapid contract discovery, but it must not become a dumping ground.

Remaining path:

1. Keep the intended dependency direction conceptually `Agentica.Orchestration -> Agentica`.
2. Keep campaign, hidden-world, and MazeQuest vocabulary in CLI/test harnesses.
3. Split into a new `Agentica.Orchestration` class library when contracts are reusable, state/replay becomes durable, or the layer changes independently from core run execution.

Acceptance criteria:

- README no longer says orchestration is CLI-only if core orchestration code remains.
- `docs/Agentica.DeferredWork.md` no longer lists orchestration as deferred without qualification.
- `docs/CodexGoal.Agentica.OrchestrationAdaptiveSupervisor.md` states whether the current code is incubating or package-ready.
- Either no `Agentica.Orchestration` source remains in `Agentica.csproj`, or the docs clearly name it as temporary incubation with split criteria.

Current status: accepted as incubating in core; docs updated.

### 1B. Approval Scope Decision

Priority: P1

Problem:

`ToolDescriptor.RequiresApproval` is metadata today. Enforcing it before execution is probably necessary before external/destructive/costly adapters, but the right approval boundary is not yet clear.

Open scope questions:

- Is approval granted by user, host policy, supervising agent, or runtime policy?
- Is approval attached to a tool id, effect class, plan step, input payload, run, or time window?
- Does approval belong in `RunRequest`, `ExecutionPolicy`, a host callback, or a durable approval record?
- Should read-only but costly/external tools use the same approval surface as destructive tools?

Recommended path:

1. Do not implement runtime approval gating until the grant model is clear.
2. Keep `RequiresApproval` visible on the tool surface and events.
3. Design the minimum approval grant shape before external/destructive adapters are treated as production-ready.
4. Add fail-closed tests once the grant model exists.

Acceptance criteria:

- A `RequiresApproval` tool cannot execute without an explicit matching approval grant.
- The approval grant is auditable and scoped tightly enough to avoid blanket approvals.
- Existing local deterministic harnesses do not gain approval ceremony where it adds no value.

### 2. Runner Refactor Guardrail

Priority: P1

Problem:

`AgenticaRunner` is still understandable, but it owns too many concerns. Extracting prematurely would create ceremony, but continuing to add features directly to the class will make regressions harder to localize.

Recommended path:

Do not refactor solely because of file length. Instead, set a trigger:

- next new planning loop mode
- next cancellation/timeout behavior
- next event enrichment pass
- next retry/repair pathway
- tests needing private runner behavior currently hidden inside `RunAttemptAsync`

Likely extraction order when triggered:

1. `StepExecutionCoordinator` for step/batch execution and tool exception wrapping.
2. `PlannerCallCoordinator` for create/continue/refine calls and cancellation diagnostics.
3. `OutcomeEnvelopeAssembler` for terminal outcome/report/detail assembly.
4. Leave `AgenticaRunner` as the readable top-level policy loop.

Acceptance criteria:

- Existing event order remains stable.
- Existing `OutcomeEnvelope` shape remains stable.
- Runner tests stay behavior-first, not implementation-coupled.

### 3. Tool Failure Taxonomy

Priority: P1

Problem:

Planner failures have `WorkflowPlannerException` and `WorkflowPlannerFailureKind`. Tool execution failures are currently normalized from generic exceptions into failed receipts/diagnostics. That is enough for honest termination, but not enough for future repair policy.

Recommended path:

Add a minimal taxonomy only when a host needs distinct handling:

- `ToolExecutionException`
- `ToolExecutionFailureKind` with values such as `InvalidHostState`, `Transient`, `Unavailable`, `Timeout`, `Unauthorized`, `Bug`, `Unknown`
- optional retryable flag or policy mapping at the host boundary

Do not retry mutation-capable tools automatically without explicit idempotency and effect policy.

Acceptance criteria:

- Generic exceptions still become failed receipts.
- Structured tool exceptions add diagnostics without bypassing receipt emission.
- Tests prove transient/read-only failures can be classified without changing success proof rules.

### 4. Id Entropy

Priority: P1

Problem:

`AgenticaIds.New` currently emits only 8 hex chars of GUID entropy after the prefix. That is acceptable for local demos, but weak for durable logs, multi-run references, and any future API or recorder.

Recommended path:

Change id generation before external durability:

```csharp
public static string New(string prefix) =>
    $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 1 + 16, prefix.Length + 1 + 32)];
```

or keep the full 32 hex chars if log readability is less important than collision resistance.

Acceptance criteria:

- Tests assert the intended suffix length.
- No tests depend on fixed generated id length except through explicit helpers.
- Existing deterministic ids in fake planners remain unchanged.

### 4B. Per-Task Run Budgeting

Priority: P2

Problem:

`TaskNode.MaxRuns` must be a real orchestration budget, not just planner-visible metadata.

Current status: implemented in the current pass.

Acceptance criteria:

- `OrchestrationState.TaskRunCounts` tracks per-task attempts.
- `TaskOrchestrator` refuses to re-run a task once `TaskNode.MaxRuns` is reached.
- Replacing/removing a task through a validated graph mutation clears the old task node run count; unchanged task nodes cannot loop past their budget.
- Exhausted tasks are surfaced as blocked with `MaxRunsReached`.
- Tests prove a refined graph cannot silently re-run the same task id past its budget.

### 5. Prompt Contract And Anti-Leak Tests

Priority: P1

Problem:

Prompt instructions are carrying important safety rules: use only supplied tool ids, keep dependencies valid, do not expose hidden state, keep intent public, and emit JSON-only structured outputs. Runtime validation catches many bad outputs, but prompt regressions can still degrade live runs.

Recommended path:

Add fake-model tests that feed planner outputs containing:

- hidden/oracle terms in task ids, objectives, intent, or context projection
- receipt ids or artifact ids incorrectly used as `dependsOn`
- blocked locations scheduled as executable tasks in hidden-world orchestration
- mutation-capable steps hidden as query steps
- report prose presented as acceptance proof
- invalid graph refinements that rewrite completed tasks

Acceptance criteria:

- Bad outputs fail before mutation-capable tool execution.
- Anti-leak failures are visible as validation issues, blocked outcomes, or harness refusal receipts.
- Live tests remain opt-in and are not the only guard.

### 6. Documentation Reality Pass

Priority: P1

Problem:

The docs are useful and unusually honest, but several describe future states beside current code. This is acceptable when clearly marked. It becomes risky when README-level status conflicts with current source placement.

Recommended path:

Update documentation with three explicit labels:

- `Implemented`
- `Incubating`
- `Deferred`

Apply first to:

- `README.md`
- `docs/Agentica.DeferredWork.md`
- `docs/CodexGoal.Agentica.OrchestrationAdaptiveSupervisor.md`
- cockpit/event goal docs if their implementation status changes

Acceptance criteria:

- A new contributor can tell what exists, what is a local harness, what is incubating, and what is future.
- Design goal docs do not imply package boundaries that the code currently violates without calling out the mismatch.

### 6B. User-Facing Event Output

Priority: P2

Problem:

`ExecutionIntent` is useful planner/operator trace data, but user-facing surfaces should prefer `UserFacingReason`.

Current status: implemented in the current pass for `ConsoleEventSink` and MazeQuest watch output.

Acceptance criteria:

- Console/watch output prints `UserFacingReason` when present.
- Console/watch output falls back to `ExecutionIntent` only when no user-facing projection exists.
- Tests prove planner-facing rationale is not printed when a user-facing reason exists.

### 6C. Provider-Enforced Structured Output Schema

Priority: P2

Problem:

`LlmStructuredOutputOptions.JsonSchema` existed without Gemini mapping.

Current status: implemented in the current pass.

Acceptance criteria:

- Gemini maps `JsonSchema` into `GenerateContentConfig.ResponseJsonSchema`.
- Invalid JSON schema strings fail as `LlmClientErrorKind.BadRequest`.
- Provider-neutral contracts remain free of Google SDK types.

### 7. Shared Text Compaction Helper

Priority: P2

Problem:

Small `Compact` / `Trim` helpers are duplicated across event sinks, working context compilers, and host watch surfaces.

Recommended path:

When touching these files next, add a small helper such as:

```text
Agentica/Text/TextCompactor.cs
```

Keep it boring:

- whitespace normalization
- max-length truncation
- no domain vocabulary
- no dependency on console, JSON, or host types

Acceptance criteria:

- Existing console/watch text remains readable.
- Host-specific projections can still choose their own max lengths.

### 8. Orchestration State Transition Discipline

Priority: P2

Problem:

`OrchestrationState` is mutable by design today. That is simple and fine for in-process orchestration, but it will be fragile for replay, persistence, or parallel task execution.

Recommended path:

Delay heavy state machinery. Before persistence or resume support, add explicit transition methods or immutable snapshots for:

- task selected
- task accepted
- task blocked
- graph refined
- orchestration stopped

Acceptance criteria:

- State mutation is centralized before any durable store exists.
- Completed tasks cannot be rewritten or removed by graph mutation.
- Working context compilation consumes snapshots rather than half-mutated state.

## No Immediate Action

These points should be monitored, not changed now:

- `ToolInputSchema` plus `object?` dictionaries are acceptable for LLM JSON and MCP-style future adapters. Stronger compile-time typing can wait.
- Live Gemini tests should remain opt-in. Deterministic and fake-client tests should carry CI confidence.
- The license concern is product/business policy, not a runtime engineering concern.
- MazeQuest complexity is acceptable while it remains a host-owned pressure harness and does not leak into `Agentica`.

## Suggested Order Of Work

1. Design approval scopes and grant shape before implementing a runtime approval gate.
2. Add prompt anti-leak and graph-refinement negative tests.
3. Increase id entropy.
4. Add tool failure taxonomy if a concrete host repair policy needs it.
5. Refactor runner only when the next runner feature makes extraction pay for itself.
6. Split orchestration into a package only when stable contracts or durability make the boundary valuable.
7. Clean up shared text compaction opportunistically.

## Rereview Verdict

The external reviews are mostly fair and technically grounded. They understate the current test count and some newer anti-leak work, but they correctly identify the main risks:

- orchestration boundary pressure
- approval scope/gating design
- runner responsibility growth
- LLM prompt fragility
- documentation drift
- small reliability debts around ids, tool failure classification, and utility duplication

Agentica should not respond by adding broad infrastructure. The right response is to preserve the existing contract discipline, make the orchestration boundary explicit, and add targeted tests around the places where model output, host surfaces, and proof semantics meet.

## Verification

```text
dotnet test Agentica.slnx --no-restore
Passed! - Failed: 0, Passed: 128, Skipped: 0, Total: 128

dotnet build Agentica.CLI/Agentica.CLI.csproj --no-restore
Build succeeded.
0 Warning(s)
0 Error(s)
```
