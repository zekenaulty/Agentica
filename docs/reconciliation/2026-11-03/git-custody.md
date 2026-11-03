# Agentica Git custody and consolidation audit

Captured: 2026-11-03T12:10:05.020623+00:00. Main baseline: `origin/main` at `977d0cab5421fd4e09fd33f1224f241b7e425abe`.

## Result

- 19 registered worktrees, 24 local branches and 12 fetched origin branch refs were inspected. All local branches with an upstream exactly match it. A read-only `ls-remote --heads origin` matched the fetched branch heads.
- Only two independent committed feature frontiers require admission to main: `codex/lab-clean-room-chat` (9 new commits), and `codex/maze-action-recovery` (26 new commits including the durable host-operation stack). Other task branches are already in main or fully represented by those frontiers through ancestry or patch equivalence.
- The primary checkout has 12 preserved local files: 3 tracked document edits plus 9 untracked guidance/learning/direction files. No other peer checkout had tracked or untracked changes at capture. SHA-256 and byte length for every primary dirty file are in `git-custody.json`.
- Local `main` appears ahead 1 / behind 58, but its lone commit `d58e3aa` is patch-equivalent to main-reachable `144ab2a`. There is no unique implementation to cherry-pick from local main.
- No stash exists. Of 25 reflog-only historical commits, 22 have reachable patch equivalents; the other 3 are superseded amendments reviewed below. No unharvested behavior was found in the reflog scan.

This is a custody/history audit, not runtime qualification, PR approval, or permission to remove an active checkout. Root owns GitHub review and all repository mutations.

## Consolidation order

1. Preserve the primary dirty set and ignored runtime state. Harvest the local guidance, learning and typed-obligation direction into the root-owned reconciliation branch; transplant only the three document hunks that remain current.
2. Review and merge the clean-room frontier at exact head `785040e3a7dc2c0b3c905716f14c8fbe01143a8e` after its current checks and review are satisfactory. Its 9 commits sit directly above the captured main.
3. Review the durable host-operation stack in order: operation Stop → explicit decision adoption → durable initial-start admission/wake replay → retained action recovery. The final `27cb2ce` branch contains all 26 commits. Existing stacked PRs can retain those boundaries; qualify each changed head against the newly merged base.
4. Reconcile the roadmap/issues against the resulting main and publish gaps with precise acceptance. Do not reapply the task branches already represented by the approved feature stack.
5. Refresh this audit before retirement. Unattached merged branches can then be retired if no open PR depends on them; attached worktrees remain subject to live/runtime custody. The main ref can be advanced by root after its patch-equivalent local commit is intentionally accounted for.

## Branch ledger

Ahead/behind are commit ancestry counts against the captured `origin/main`. A `+` from `git cherry` means no exact patch equivalent in main; it does not mean the work is absent from a later feature branch.

| Local branch | Head | Ahead / behind main | Custody disposition |
| --- | --- | --- | --- |
| `codex/capability-test-suites` | `408407a` | 0 / 17 | Already main ancestor; retirement candidate after runtime/worktree custody. |
| `codex/chessquest-replay-resume` | `30ee354` | 0 / 73 | Already main ancestor; retirement candidate after runtime/worktree custody. |
| `codex/lab-clean-room-chat` | `785040e` | 9 / 0 | Independent active clean-room feature; review/merge. |
| `codex/lab-durable-handoff` | `1e1a2df` | 0 / 2 | Already main ancestor; retirement candidate after runtime/worktree custody. |
| `codex/lab-provider-settings` | `54831b4` | 0 / 3 | Already main ancestor; retirement candidate after runtime/worktree custody. |
| `codex/lab-web-host` | `7e233cd` | 0 / 5 | Already main ancestor; retirement candidate after runtime/worktree custody. |
| `codex/maze-action-recovery` | `27cb2ce` | 26 / 2 | Independent durable host-operation feature frontier; review/merge stack. |
| `codex/maze-adoption-tests` | `50d635c` | 10 / 2 | Fully represented by durable frontier (4 patch equivalents); no separate harvest. |
| `codex/maze-compact-outcome` | `a1f00ac` | 21 / 2 | Ancestor of durable host-operation frontier; retain until stack admitted. |
| `codex/maze-decision-adoption` | `d65fb5b` | 11 / 2 | Ancestor of durable host-operation frontier; retain until stack admitted. |
| `codex/maze-operation-stop` | `6d8db3b` | 6 / 2 | Ancestor of durable host-operation frontier; retain until stack admitted. |
| `codex/maze-start-admission` | `adb5cfe` | 23 / 2 | Ancestor of durable host-operation frontier; retain until stack admitted. |
| `codex/maze-start-sdk` | `ea8d808` | 14 / 2 | Fully represented by durable frontier (3 patch equivalents); no separate harvest. |
| `codex/maze-start-store` | `deff229` | 15 / 2 | Fully represented by durable frontier (4 patch equivalents); no separate harvest. |
| `codex/maze-stop-tests` | `1888eef` | 3 / 2 | Fully represented by durable frontier (3 patch equivalents); no separate harvest. |
| `codex/maze-wake-replay` | `577a214` | 23 / 2 | Fully represented by durable frontier (1 patch equivalents); no separate harvest. |
| `codex/mcp-sdk-acceptance` | `6ee6224` | 1 / 28 | All 1 non-ancestor commits patch-equivalent in main; retirement candidate after custody. |
| `codex/mcp-streaming-first-slice` | `e075d93` | 0 / 19 | Already main ancestor; retirement candidate after runtime/worktree custody. |
| `codex/provider-reasoning-controls` | `eedd9df` | 1 / 28 | All 1 non-ancestor commits patch-equivalent in main; retirement candidate after custody. |
| `codex/provider-run-continuation` | `cd34431` | 4 / 28 | All 4 non-ancestor commits patch-equivalent in main; retirement candidate after custody. |
| `codex/reasoning-effort-cli` | `f9c7773` | 1 / 24 | All 1 non-ancestor commits patch-equivalent in main; retirement candidate after custody. |
| `codex/semantic-projection` | `d964b3e` | 1 / 31 | All 1 non-ancestor commits patch-equivalent in main; retirement candidate after custody. |
| `main` | `d58e3aa` | 1 / 58 | Local-only commit patch-equivalent in main; safe to reconcile ref after recheck. |

### Local-only branch names

`codex/chessquest-replay-resume`, `codex/maze-adoption-tests`, `codex/maze-compact-outcome`, `codex/maze-start-sdk`, `codex/maze-start-store`, `codex/maze-stop-tests`, `codex/maze-wake-replay`, `codex/mcp-sdk-acceptance`, `codex/provider-reasoning-controls`, `codex/provider-run-continuation`, `codex/reasoning-effort-cli`, `codex/semantic-projection`.

### Remote-only branch name

`origin/zekenaulty-cloud-audit` at `144ab2a` is already an ancestor of main. It is a retirement candidate once PR/activity ownership is checked. No remote-only work needs harvesting.

### Exact patch mappings already integrated in main

| Historical branch/commit | Main-reachable equivalent |
| --- | --- |
| `codex/mcp-sdk-acceptance` / `6ee6224` | `dfd5bf8` |
| `codex/provider-reasoning-controls` / `eedd9df` | `61b6eca` |
| `codex/provider-run-continuation` / `67bf370` | `e8b065e` |
| `codex/provider-run-continuation` / `bee4ace` | `61b6eca` |
| `codex/provider-run-continuation` / `d418b6a` | `8d8b801` |
| `codex/provider-run-continuation` / `cd34431` | `e075d93` |
| `codex/reasoning-effort-cli` / `f9c7773` | `575bd52` |
| `codex/semantic-projection` / `d964b3e` | `28f8831` |
| `main` / `d58e3aa` | `144ab2a` |

## Primary dirty work to harvest

All paths below are relative to `C:/Users/Zythis/source/repos/Agentica`. Source files were read only; do not replace current main documents with whole stale copies. Exact contents differ from both main and the clean-room head.

| Path | Disposition |
| --- | --- |
| `README.md` | Harvest selected MCP client/status corrections and direction link onto current main; do not copy the stale whole README. |
| `docs/Agentica.DeferredWork.md` | Harvest selected MCP client/status corrections onto current main; reconcile remaining work against current source. |
| `docs/Agentica.ProductStatus.md` | Harvest MCP client parity wording into current lifecycle/status file; preserve later merged status content. |
| `.agents/learning/MEMORY.md` | Harvest local development learning registry; verify evidence pins and retain corrections separately from implementation claims. |
| `.agents/learning/records/agentica-conservative-input-budget.json` | Harvest as pinned core observation; Lab native counting now differs and must be stated to avoid reinstating byte-based production admission. |
| `.agents/learning/records/agentica-host-owned-state.json` | Harvest pinned host-ownership lesson. |
| `.agents/learning/records/agentica-observability-is-not-call-admission.json` | Harvest pinned observability/admission lesson. |
| `.agents/learning/scope.json` | Harvest Agentica learning scope configuration. |
| `.agents/skills/agentica-work-learning/SKILL.md` | Harvest repository-local learning skill; global/shared helpers are external dependencies and must not be edited in this Agentica-only task. |
| `.agents/skills/agentica-work-learning/agents/openai.yaml` | Harvest companion skill metadata. |
| `AGENTS.md` | Harvest repository-local development guidance. |
| `docs/Agentica.TypedObligationActivation.Direction.md` | Harvest whole planning-only direction note after checking linked decision revisions; dated implementation claims remain pinned to e075d93. |

## Worktree custody

| Worktree path | Branch / HEAD | Tracked and untracked state | Runtime/ignored custody |
| --- | --- | --- | --- |
| `C:/Users/Zythis/source/repos/Agentica` | `codex/mcp-streaming-first-slice` / `e075d93` | 12 preserved local files | `.agentica/`, `.env`, `Agentica.Lab/.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/lab-clean-room-chat/Agentica` | `codex/lab-clean-room-chat` / `785040e` | Clean at capture | `.agentica/` |
| `C:/Users/Zythis/.codex/worktrees/maze-action-recovery/Agentica` | `codex/maze-action-recovery` / `27cb2ce` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/.codex/worktrees/maze-adoption-tests/Agentica` | `codex/maze-adoption-tests` / `50d635c` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/.codex/worktrees/maze-compact-outcome/Agentica` | `codex/maze-compact-outcome` / `a1f00ac` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/.codex/worktrees/maze-operation-stop/Agentica` | `codex/maze-decision-adoption` / `d65fb5b` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/.codex/worktrees/maze-start-admission/Agentica` | `codex/maze-start-admission` / `adb5cfe` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/.codex/worktrees/maze-start-sdk/Agentica` | `codex/maze-start-sdk` / `ea8d808` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/.codex/worktrees/maze-start-store/Agentica` | `codex/maze-start-store` / `deff229` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/.codex/worktrees/maze-stop-tests/Agentica` | `codex/maze-stop-tests` / `1888eef` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/.codex/worktrees/maze-wake-replay/Agentica` | `codex/maze-wake-replay` / `577a214` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/.codex/worktrees/roadmap-reconciliation/Agentica` | `detached` / `977d0ca` | Root-owned new checkout; this report adds untracked files after capture. | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/Documents/Codex/2026-09-30/files-pasted-by-the-user-you/work/agentica-lab-web` | `codex/lab-durable-handoff` / `1e1a2df` | Clean at capture | `Agentica.Lab.Web/.agentica/` |
| `C:/Users/Zythis/Documents/Codex/2026-09-30/files-pasted-by-the-user-you/work/agentica-mcp-acceptance` | `codex/mcp-sdk-acceptance` / `6ee6224` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/Documents/Codex/2026-09-30/files-pasted-by-the-user-you/work/agentica-provider-continuation` | `codex/provider-run-continuation` / `cd34431` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/Documents/Codex/2026-09-30/files-pasted-by-the-user-you/work/agentica-reasoning-cli` | `codex/reasoning-effort-cli` / `f9c7773` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/source/repos/Agentica-projection` | `codex/semantic-projection` / `d964b3e` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/source/repos/Agentica-provider-controls` | `codex/provider-reasoning-controls` / `eedd9df` | Clean at capture | Ignored build/output paths only, or none; see JSON. |
| `C:/Users/Zythis/source/repos/Agentica-worktrees/capability-test-suites` | `codex/capability-test-suites` / `408407a` | Clean at capture | Ignored build/output paths only, or none; see JSON. |

The clean-room `.agentica/` contains live user chat state. The Lab durable-handoff checkout also has `Agentica.Lab.Web/.agentica/`. The primary checkout has `.env`, `.agentica/` and `Agentica.Lab/.agentica/`. None of those ignored contents were exported, hashed or copied. A clean Git status does not prove a checkout is disposable. Preserve service state and any needed ignored artifacts before considering removal.

## Reflog and special refs

22 of 25 reflog-only commits are already represented by reachable patch-equivalent commits. Three amendments differ from their retained replacement:

- `909dbfc` — Superseded by b2a0ccc: removes synthetic credential URI literal while retaining rejection coverage and adds qualification evidence. No lost behavior to harvest; do not resurrect old fixture literal.
- `0343e3b` — Superseded by 62c5b5a: replaces synthetic credential URI literal with equivalent UriBuilder test. No lost behavior to harvest.
- `974fe6e` — Superseded by 6c0d83e: adds ToolInvocation authority-resolution XML documentation, with identical executable source.

Two Codex UI turn-diff refs point to tree `3427abfce868a7a383955e5f72e644583eccac26`; they are tool bookkeeping, not independent commit branches. They were inventoried, not removed. No tags or stash ref were present.

## Recheck and limits

Peer recheck at 2026-11-03T12:12:34.357392+00:00: all 18 peer heads, statuses, and captured dirty-file hashes remained unchanged. The root-owned reconciliation checkout is intentionally excluded because concurrent work there is authorized.

- Read-only Git ref, history, tracked/untracked filename and SHA-256 capture for registered common-repository worktrees only.
- Ignored directories recorded only at Git matching level; contents are excluded from hashes and payload export.
- Remote state is the fetched origin snapshot captured by root; this subtask did not fetch or mutate refs.
- No builds, provider calls, checkout/reset/clean/switch/merge/archive/delete operations performed.
- Reflog-only commits inspected for patch equivalence; unreachable objects with no ref or reflog were not scanned and no garbage collection performed.
- Adjacent `Agentica.API`, `Agentica.Ficticius`, `Agentica.Orchestration`, `Agentica.TurtleQuest`, and `Agentica.Writes` are separate repositories, not common-repository worktrees. They were not audited or changed.
- Workspace sibling `verification/` directories and artifacts outside the registered Git checkout were not recursively inventoried; any later archive must separately preserve needed verification/runtime files.
- This snapshot will age as root merges PRs, creates branches, or harvests local files. Re-capture exact heads and statuses immediately before each action.

## Integration update: PR #11, 2026-11-03

The clean-room frontier identified above has now been admitted. [PR #11](https://github.com/zekenaulty/Agentica/pull/11) merged at **2026-11-03T13:41:34Z**, with exact final head `cc813f6a9c2b1a851a1698b52ab2e96a4a68716f` and main merge commit `3cb2d63395fee277a8eb790c57b271cd6277125b`. The merge's first parent is the captured main baseline `977d0cab5421fd4e09fd33f1224f241b7e425abe`; its second parent is the admitted head. Fetched `origin/main` resolves to this merge at the update.

[CI run 41643125122](https://github.com/zekenaulty/Agentica/actions/runs/41643125122) is successful for that exact head, including both Windows locked build/filesystem proof and Linux locked build/proof/package validation. The retained-trace review thread is resolved. [The admission review](pr11-review.md) records the head, merge, checks and focused correction.

This closes consolidation-order step 2 for the clean-room feature only. The original branch/ref counts, divergence, primary dirty-file hashes and peer-state ledger remain the dated pre-integration capture; they were not replaced with a partial current inventory. The separate durable host-operation frontier remains tracked by [#32](https://github.com/zekenaulty/Agentica/issues/32), outside this PR's admission evidence. Neither this merge nor green CI releases any checkout's ignored runtime data or authorizes branch/worktree retirement without fresh custody checks. No branch, worktree, service or user-data cleanup was performed by this documentation update.
