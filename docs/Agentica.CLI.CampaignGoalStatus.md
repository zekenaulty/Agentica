# Agentica CLI Campaign Goal Status

> Lifecycle: **Historical** · Completion: **100%** · Canonical status: [Agentica Product Status And Goal Xref](Agentica.ProductStatus.md)
>
> Rename note: `Agentica.CLI` was renamed to `Agentica.Lab`; legacy names below are retained as historical evidence.

Status note for `docs/CodexGoal.Agentica.OrchestratedCampaignRuns.md`.

## Implemented

The first campaign orchestration slice lives in the CLI host under:

```text
Agentica.CLI/Scenarios/Campaign
```

This is intentional. Campaign vocabulary remains host-side and has not been added to the `Agentica` runtime package.

Implemented host contracts:

- `CampaignDefinition`
- `CampaignMilestone`
- `CampaignState`
- `CampaignProgressSnapshot`
- `CampaignPriorRunRef`
- `CampaignRequiredEvidence`

Implemented host services:

- `CampaignGraph`
- `CampaignAcceptance`
- `CampaignProgressSnapshotCompiler`
- `CampaignRunner`

Implemented harness:

- `DungeonCampaignBoard`
- `DungeonCampaignSession`
- `DungeonCampaignTools`
- `DungeonCampaignDeterministicPlanner`
- CLI command surface through `campaign list` and `campaign run`.

## What This Proves

The campaign slice proves multi-run orchestration above Agentica:

```text
campaign milestone
  -> RunRequest
  -> normal AgenticaRunner run
  -> OutcomeEnvelope
  -> host acceptance check
  -> evidence-backed progress snapshot
  -> next milestone
```

The DungeonCampaign graph is authored and intentionally small:

```text
acquire_lantern
acquire_bronze_key
explore_dark_archive        depends on acquire_lantern
unlock_bronze_vault         depends on acquire_bronze_key
recover_moon_sigil          depends on explore_dark_archive
recover_sun_sigil           depends on unlock_bronze_vault
optional_cache              optional, depends on acquire_lantern
open_final_gate             depends on recover_moon_sigil + recover_sun_sigil
```

The deterministic runner skips the optional cache and still succeeds because required milestones and final host state are satisfied.

## Carry-Forward Context

The active cross-run context is `CampaignProgressSnapshot`, not the full set of prior envelopes.

The snapshot carries:

- completed milestones
- proven facts
- outstanding facts
- artifact refs
- receipt refs
- blockers
- compact host state projection

The snapshot compiler is deliberately lossy for host state. It keeps small scalar values and short scalar arrays, but drops oversized strings and unsupported object graphs. This is a prompt-surface guard: campaign context should stay compact and evidence-backed.

## Planner Notes And Working Context

The current campaign slice uses two carry-forward objects:

- `CampaignProgressSnapshot`: evidence-backed state summary.
- `CampaignWorkingContextSnapshot`: system-curated planning support.

The intended rule is:

```text
Notes may guide planning.
Receipts, observations, artifacts, validation issues, and host checks prove state.
```

`CampaignWorkingContextSnapshot` includes:

- proven facts
- hypotheses
- open questions
- known blockers
- next considerations
- evidence refs
- active plan summary
- scope
- update timestamp

This remains visible, typed, bounded, and compactable. It is deterministic and system-curated in the current slice. There is no `create_note` tool.

Future LLM summarization may propose updates to the working context, but accepted context should still be filtered and evidence-aware. Unsupported claims may become hypotheses or open questions; they must not become proven facts.

## Acceptance Discipline

Milestone acceptance uses:

- required `RunOutcomeStatus`
- required artifact kind
- required receipt/tool status
- required host state predicate

It does not use `OutcomeReport` prose as proof.

## Logging

Campaign CLI runs can write a campaign-level rollup:

```text
campaign-result.json
campaign-run-001-outcome.json
campaign-run-002-outcome.json
...
```

The rollup includes:

- campaign id
- status
- completed milestones
- blocked milestones
- prior run refs
- run ids
- final progress snapshot

## LLM Planning Surface

The LLM planner prompt now distinguishes:

- one safe step when preconditions are uncertain
- 2-3 step safe slices when preconditions are established
- read-only batches only for independent query steps
- sequential unbatched mutation-capable steps
- `dependsOn` for explicit step dependencies
- `campaign.progress` as scoped carry-forward context, not proof of new tool results

This is prompt discipline only. The runtime still validates plans before execution.

## Dungeon Crawler Harness Notes

The next dungeon harness should distinguish route planning from game mechanics:

- Use `dungeon.move_route` for bounded same-floor route batches.
- Keep `change_floor` explicit and stairs-bound.
- Make `MaxRouteLength` visible to the planner.
- Prefer raw recovery receipts over host-provided branch menus.
- Keep CYOA-style branch options as a separate easier mode.
- Keep host truth separate from map text shown to the LLM.
- Scope map projection aggressively to avoid prompt bloat.

## Deferred

Intentionally deferred:

- procedural campaign generation
- procedural dungeon crawler generation
- structured planner notes / working-context API
- task complexity classification and planner routing
- persistence or resume storage
- background jobs, queues, schedulers
- multi-agent orchestration
- CLUE-style deduction harness
- moving campaign abstractions into the runtime package

## Boundary Review

No campaign or dungeon vocabulary exists under `Agentica/`.

`CampaignRunner` currently remains under `Agentica.CLI/Scenarios/Campaign`. That is acceptable for this slice because it is still a host harness. If another host needs the same abstraction, the next boundary should be a cleaner host-side namespace or package, not the core runtime by default.
