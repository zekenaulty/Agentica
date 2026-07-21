# Codex Goal: Agentica.Lab WorkbenchQuest Harness

> Lifecycle: **Implemented** · Completion: **100%** · Canonical status: [Agentica Product Status And Goal Xref](Agentica.ProductStatus.md)

## Mission

Build a host-provided WorkbenchQuest test harness inside `Agentica.Lab`.

The goal is to create a bounded, raw-output task world that tests whether an LLM planner backed by Gemini can solve an abstract user objective using ordinary tools, small planning slices, observations, receipts, and verification evidence.

WorkbenchQuest is the next harness after MazeQuest:

```text
MazeQuest:
  proves Agentica can execute and adapt inside a guided closed world.

WorkbenchQuest:
  proves Agentica can decompose, investigate, patch, verify, and complete inside a bounded open-ended workbench.
```

This is not an Agentica runtime feature. It is a Lab host scenario that supplies domain state, files, checks, patching tools, completion rules, and optional watch output around Agentica's existing planner/executor loop.

Core distinction:

```text
Tools expose raw evidence.
The planner derives the next useful action.
Receipts and checks prove success.
```

## Non-Negotiable Boundary

This work must stay host-side unless it exposes a generic runtime gap already captured by the runtime design decisions.

Allowed write scope:

```text
Agentica.Lab/
docs/
Agentica.Tests/       only for host/runtime regression coverage if needed
```

Avoid runtime changes unless they are generic Agentica seams such as tool input validation, completion evidence, planner context shaping, continuation limits, or structured planning artifacts.

Do not add WorkbenchQuest, case board, files, checks, patches, ledgers, config audits, mapping, or code-fix vocabulary to `Agentica` runtime contracts.

The CLI may reference existing Agentica runtime and client APIs, but WorkbenchQuest concepts must not become runtime vocabulary.

## Why Do This

MazeQuest is useful but structurally generous:

- The stage exposes legal actions.
- The objective surface explains much of the planner contract.
- Tool outputs pre-digest the world into move evaluations, objective warmth, local risk, and frontier gain.
- Many failures include recovery guidance.

That proves the execution envelope. It does not prove that an LLM can take an abstract user prompt and solve a bounded real task from raw evidence.

WorkbenchQuest adds the missing pressure:

- The user objective is abstract.
- The tool catalog is generic.
- File and check tools return raw text.
- Search returns raw matches.
- Patch tools mutate only after validation.
- Completion requires check-backed evidence, not planner narration.
- The planner must decide what to inspect, when evidence is sufficient, when mutation is justified, and when verification proves completion.

This tests real behaviors:

- Task decomposition.
- Evidence gathering.
- Hypothesis formation through visible planning artifacts.
- Avoiding premature mutation.
- Small-scope planning under thinking-token pressure.
- Valid structured output after reasoning-heavy prompts.
- Recovery after failed verification.
- Receipt-backed final reporting.

## Why Not To Do This

The risks are scope, cost, and ambiguous signal:

- It can become a general coding agent benchmark.
- Scenarios can grow until runs become slow and flaky.
- Raw file content can overload the planner context.
- Gemini thinking can consume enough budget that the model fails to emit valid structured output.
- Patch tools can hide too much if they accept high-level intentions instead of concrete diffs.
- Checks can accidentally become answer oracles.
- Completion can become too easy if the evaluator only checks final status and not evidence quality.

Mitigation:

- Keep the first scenarios tiny and deterministic.
- Cap files, file sizes, search results, check output, steps, refinements, and planning turns.
- Prefer raw output over scored recommendations.
- Require concrete patch content for mutation.
- Require verification after mutation.
- Treat planning memos as auditable intent, not proof.
- Keep receipts, observations, artifacts, and completion evaluators as the only success evidence.

## First MVP

Create a new scenario folder:

```text
Agentica.Lab/Scenarios/WorkbenchQuest/
```

The first workbench should be intentionally small:

```text
scenario: broken_check
files: 2-5 small text/code files
check: deterministic local validator
allowed mutations: patch files inside the scenario sandbox only
max steps: small integer
max refinements: small integer
max extra planning memos: 3
max consecutive planning memos: 1
planner context: bounded recent observations and receipts
completion: check passes and required artifact exists
```

The current static quest and MazeQuest should remain as lower layers of the test stack.

## Test Ladder

Use this ladder to classify harness value:

```text
Layer 1: Deterministic proof slice
  Does the runtime envelope work?

Layer 2: MazeQuest
  Can the model choose and adapt inside a guided closed world?

Layer 3: WorkbenchQuest
  Can the model solve an abstract task using raw tools and bounded planning?

Layer 4: Product workflow harnesses
  Can the system survive real product work without product vocabulary leaking into Agentica?
```

Do not make MazeQuest harder to cover this gap. WorkbenchQuest should prove a different capability.

## Scenario Board

The first board should include small deterministic scenarios:

```text
1. The Broken Check
   The validation check is failing. Find the cause, fix it, and verify.

2. The Mismatched Ledger
   Two small datasets disagree. Reconcile them and report discrepancies.

3. The Unsafe Config
   Audit a tiny config/app folder for a known class of security mistake.

4. The Missing Mapping
   A pipeline output is wrong. Inspect inputs, find the bad mapping, patch it, verify.

5. The Ambiguous Failure
   Something broke after the last change. Inspect evidence before deciding whether to patch.
```

MVP may implement only `broken_check`, but the board should be shaped so additional scenarios fit without changing Agentica runtime contracts.

## Tool Surface

Use raw and boring tools:

```text
workbench.list_files        ReadOnly
workbench.read_file         ReadOnly
workbench.search            ReadOnly
workbench.run_check         ReadOnly
workbench.diff              ReadOnly
workbench.apply_patch       WritesLocalState
workbench.write_note        WritesLocalState
workbench.complete          WritesLocalState
```

Do not add:

```text
workbench.evaluate_next_action
workbench.recommend_file
workbench.identify_bug
workbench.score_patch
workbench.sense_objective
workbench.legal_best_move
```

Tool outputs should look like real workbench evidence:

```text
file paths
file contents
search matches
test/check output
diffs
patch receipts
notes
completion artifacts
```

Tool outputs should not include:

```text
recommended next step
objective warmth
bug location score
patch risk score
frontier gain
```

## Scenario State Model

Keep host state simple and deterministic:

```text
WorkbenchScenario
  id
  title
  objective
  files
  initialFiles
  checkDefinition
  completionRules
  notes
  patchHistory

WorkbenchFile
  path
  content
  readOnly
  maxBytes

WorkbenchCheckResult
  status: passed | failed
  output
  evidenceRefs

WorkbenchPatchReceipt
  status: applied | refused
  changedPaths
  diffSummary
  reason
  evidenceRefs
```

The host owns all scenario vocabulary and all scenario state.

## Broken Check MVP

A minimal `broken_check` scenario can be:

```text
Objective:
  The check is failing. Find and fix the problem, then verify.

Files:
  README.md
  src/Calculator.txt
  tests/CalculatorTests.txt

Check:
  workbench.run_check returns one failing assertion with raw expected/actual text.

Expected planner behavior:
  1. Run the check.
  2. Read the failing output.
  3. Inspect the relevant test and source files.
  4. Decide whether implementation or expectation is wrong.
  5. Apply a concrete patch only after enough evidence.
  6. Run the check again.
  7. Complete only when the check passes and the patch receipt exists.
```

The check should be deterministic and cheap. It does not need to invoke a real compiler in the first slice.

## Planning Escalation

WorkbenchQuest should pressure bounded planning without asking for hidden chain-of-thought.

The agent may request or emit a structured planning artifact when the next safe action is not obvious:

```text
PlanningMemo
  reason
  knownFacts
  uncertainties
  evidenceNeeded
  mutationAllowed
  mutationPreconditions
  nextPlanSlice
  outputBudgetRisk
```

Allowed reason codes:

```text
ambiguous_next_action
insufficient_evidence
mutation_risk
conflicting_observations
failed_verification
completion_check
blocked
```

The planning memo is not private reasoning and not proof. It is an auditable, compact planning artifact that must become a normal plan or refinement before tools execute.

Runtime rule:

```text
Extra thinking may justify the next plan slice.
Extra thinking may not replace tool evidence.
```

## Thinking Budget Pressure

This harness should specifically test Gemini-backed planning with thinking enabled.

The planner must be able to:

- Think in small scopes.
- Emit valid structured output after a reasoning-heavy prompt.
- Avoid starving the final plan/tool-call JSON with internal thinking token usage.
- Use extra planning only when evidence is missing or mutation risk is high.
- Stop planning and use tools once the next evidence step is clear.

Recommended host policy:

```text
MaxPlanningMemoTokens = small
MaxExtraPlanningTurnsPerRun = 3
MaxConsecutivePlanningTurns = 1
RequirePlanningMemoBeforeMutationWhenEvidenceMissing = true
RequireVerificationAfterMutation = true
RequireToolEvidenceForCompletion = true
```

If Gemini cannot emit a valid plan within budget, the run should end as honest blockage or planner unavailable. It must not synthesize success.

## Planner Prompt Guidance

WorkbenchQuest prompts should be less structurally generous than MazeQuest.

The prompt may say:

- Use the tools to gather evidence.
- Prefer small safe plan slices.
- Inspect before mutation.
- Patch only after evidence supports the change.
- Verify after mutation.
- Complete only with check-backed evidence.

The prompt should not say:

- Which exact files to read first.
- Which line contains the bug.
- Which patch to apply.
- Which scenario-specific answer is expected.
- Which tool call sequence completes the scenario.

The objective should be abstract enough to require decomposition:

```text
The check is failing. Find and fix the problem, then verify.
```

Do not embed the solution path in the objective.

## Completion Evidence

Completion requires host-proven evidence.

For `broken_check`, completion should require:

```text
1. At least one failed check observation before mutation.
2. At least one read observation for a relevant source or test file.
3. At least one applied patch receipt.
4. A later passing check observation.
5. A terminal `workbench.objective_completed` artifact emitted by `workbench.complete`.
```

The final outcome report may narrate the solution, but the success claim must cite observations, receipts, and artifacts.

## Acceptance Criteria

The first WorkbenchQuest harness is complete when:

1. `workbench list` shows at least one scenario.
2. `workbench preview broken_check` shows the scenario metadata without revealing the patch solution.
3. `workbench run broken_check --planner deterministic` completes the scenario through Agentica.
4. The tool catalog includes list, read, search, check, diff, patch, note, and complete tools.
5. Read/search/check tools return raw evidence, not recommendations.
6. `workbench.apply_patch` refuses paths outside the scenario sandbox.
7. `workbench.apply_patch` emits receipts for applied and refused patches.
8. `workbench.run_check` returns deterministic pass/fail observations.
9. `workbench.complete` emits `workbench.objective_completed` only when completion rules are satisfied.
10. The final `OutcomeEnvelope` can be consumed as JSON without parsing console text.
11. The Gemini planner path is bounded by step, refinement, timeout, and planning memo policy.
12. Gemini failures produce honest blockage, invalid-plan, or planner-unavailable outcomes instead of false success.

## Implementation Progress

Current CLI-only WorkbenchQuest slice:

- Added `Agentica.Lab/Scenarios/WorkbenchQuest/`.
- Added `workbench list`.
- Added `workbench preview broken_check`.
- Added `workbench run broken_check`.
- Added the `broken_check` scenario with a tiny in-memory file set:
  - `README.md`
  - `src/Calculator.txt`
  - `tests/CalculatorTests.txt`
- Added the `missing_mapping` scenario:
  - `input/orders.csv`
  - `config/mapping.csv`
  - `expected/output.csv`
  - exact checker compares generated shipping labels to expected output.
- Added the `structured_doc_merge` scenario:
  - `merge_rules.txt`
  - `base.md`
  - `revision_a.md`
  - `revision_b.md`
  - `merged.md`
  - exact checker validates section ids, conflict winners, added support section, dropped legacy section, and final merged content.
- Added the `word_ladder` scenario:
  - `rules.txt`
  - `dictionary.txt`
  - `answer.json`
  - exact checker validates JSON shape, start/end words, dictionary membership, lowercase four-letter words, and one-letter transitions.
- Added a host-owned `WorkbenchQuestSession` with in-memory scenario files, initial file snapshots, read tracking, check history, patch history, note history, and completion state.
- Added raw workbench tools:
  - `workbench.list_files`
  - `workbench.read_file`
  - `workbench.search`
  - `workbench.run_check`
  - `workbench.diff`
  - `workbench.apply_patch`
  - `workbench.write_note`
  - `workbench.complete`
- Added input schemas for file reads, search, exact find/replace patching, and note writing.
- Added path sandbox validation that refuses rooted paths, parent traversal, unknown paths, and read-only patch targets.
- Added a deterministic check that returns raw pass/fail output without recommending a fix.
- Split deterministic checks by scenario id while keeping all checker logic host-side.
- Added exact find/replace patch application with receipt-backed diff output.
- Hardened Workbench mutation policy:
  - `workbench.apply_patch` refuses mutation until the run has a failed baseline `workbench.run_check` observation.
  - `workbench.apply_patch` refuses mutation until the run has read at least one relevant evidence file.
  - The Workbench objective contract now states these as mandatory preconditions rather than preferences.
  - Tool descriptors for `workbench.run_check`, `workbench.apply_patch`, and `workbench.complete` now surface the evidence protocol.
- Added completion rules requiring:
  - a failed check before mutation,
  - a relevant file read,
  - an applied patch,
  - a passing check after mutation,
  - and a terminal `workbench.objective_completed` artifact.
- Added a deterministic WorkbenchQuest planner that proves the current linear plan-slice model.
- Added deterministic proof paths for `broken_check`, `missing_mapping`, `structured_doc_merge`, and `word_ladder`.
- Added a WorkbenchQuest outcome reporter that grounds success claims in observations, receipts, and artifacts.
- Wired WorkbenchQuest through `AgenticaRunner` with evidence-gated completion, bounded planner context, timeout, and continuation/refinement limits.

No Agentica runtime contracts were changed for this slice.

Remaining Lab host work:

- Inspect richer Gemini run logs across more scenarios before deciding whether a small optional runtime `PlanningMemo` is justified later.
- Add additional scenarios such as `mismatched_ledger` and `missing_mapping` after the first Gemini signal is understood.

## Implementation Slices

### Slice 1: Goal and scenario skeleton

- Add this goal document.
- Add `Agentica.Lab/Scenarios/WorkbenchQuest/`.
- Add scenario descriptors and a deterministic `broken_check` fixture.
- Add `workbench list` and `workbench preview`.

### Slice 2: Raw tool catalog

- Add host-owned WorkbenchQuest session state.
- Add list/read/search/check/diff tools.
- Add patch/note/complete tools.
- Add input schemas and path sandbox validation.

### Slice 3: Deterministic runner proof

- Add deterministic WorkbenchQuest planner fixture.
- Wire `workbench run broken_check --planner deterministic` through `AgenticaRunner`.
- Require completion evidence.
- Emit JSON outcome envelope.

### Slice 4: Gemini planner smoke

- Wire WorkbenchQuest into the existing Gemini planner path.
- Use small plan slices.
- Bound planning/refinement/timeout behavior.
- Capture honest failure modes.

### Slice 5: Additional scenarios

- Add `mismatched_ledger`.
- Add `missing_mapping`.
- Add `unsafe_config` only after mutation/evidence rules are stable.

## Verification Commands

Run before calling the host harness complete:

```powershell
dotnet build Agentica.slnx
dotnet test Agentica.slnx
dotnet run --project Agentica.Lab -- workbench list
dotnet run --project Agentica.Lab -- workbench preview broken_check
dotnet run --project Agentica.Lab -- workbench run broken_check --planner deterministic --timeout-seconds 120
```

Current verification evidence:

```powershell
dotnet build Agentica.slnx
# Passed

dotnet test Agentica.slnx
# Passed: 47

dotnet run --project Agentica.Lab -- workbench list
# Listed The Broken Check (broken_check)

dotnet run --project Agentica.Lab -- workbench preview broken_check
# Printed scenario metadata and tool surface without revealing the patch solution

dotnet run --project Agentica.Lab -- workbench run broken_check --planner deterministic --timeout-seconds 120
# Succeeded with workbench.objective_completed artifact

dotnet run --project Agentica.Lab -- workbench run missing_mapping --planner deterministic --timeout-seconds 120
# Succeeded with workbench.objective_completed artifact

dotnet run --project Agentica.Lab -- workbench run structured_doc_merge --planner deterministic --timeout-seconds 120
# Succeeded with workbench.objective_completed artifact

dotnet run --project Agentica.Lab -- workbench run word_ladder --planner deterministic --timeout-seconds 120
# Succeeded with workbench.objective_completed artifact

# Re-run after Workbench mutation hardening:
dotnet run --project Agentica.Lab -- workbench run broken_check --planner deterministic --timeout-seconds 120
dotnet run --project Agentica.Lab -- workbench run missing_mapping --planner deterministic --timeout-seconds 120
dotnet run --project Agentica.Lab -- workbench run structured_doc_merge --planner deterministic --timeout-seconds 120
dotnet run --project Agentica.Lab -- workbench run word_ladder --planner deterministic --timeout-seconds 120
# All succeeded with workbench.objective_completed artifacts

dotnet run --project Agentica.Lab -- workbench run broken_check --planner gemini --thinking-budget off --timeout-seconds 120
# Succeeded with workbench.objective_completed artifact

dotnet run --project Agentica.Lab -- workbench run broken_check --planner gemini --thinking-budget dynamic --timeout-seconds 120
# Succeeded with workbench.objective_completed artifact

dotnet run --project Agentica.Lab -- workbench run missing_mapping --planner gemini --thinking-budget off --timeout-seconds 180
# Produced useful failure signal: the planner patched before preserving failed-check-before-mutation evidence,
# completion refused workbench.objective_completed, and the run did not synthesize success.

dotnet run --project Agentica.Lab -- workbench run word_ladder --planner gemini --thinking-budget off --timeout-seconds 180
# After mutation hardening, succeeded with workbench.objective_completed artifact.
# The run included a refused receipt and recovered instead of creating an unrecoverable missing-baseline state.
```

If Gemini credentials are present:

```powershell
dotnet run --project Agentica.Lab -- workbench run broken_check --planner gemini --thinking-budget off --timeout-seconds 120
dotnet run --project Agentica.Lab -- workbench run broken_check --planner gemini --thinking-budget dynamic --timeout-seconds 120
```

If Gemini credentials are missing, Gemini paths should fail clearly or be skipped only when explicitly configured to do so. Secret values must never be printed.

## Completion Discipline

Do not judge success by the explanation sounding plausible.

Judge success by:

- Raw observations.
- Patch receipts.
- Check results.
- Completion artifacts.
- Honest terminal outcome.
- Bounded execution.

Planning memos help the user understand why the agent is acting. They do not prove that the action was correct.
