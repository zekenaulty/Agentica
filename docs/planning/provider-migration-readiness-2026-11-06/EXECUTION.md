# Provider readiness execution ledger

## Authority and goal

Owner approval: 2026-11-06, current Agentica chat: create the goal, begin work and
document key learnings. Epic #47 / P01–P09 (#48–#56) retain the approved acceptance.
The execution goal is active. Stable reviewed changes may be merged through PRs.
Writes stay in Agentica and its workspaces. The primary dirty checkout is preserved.

## Active slices

| Slice | Workspace / branch | State |
| --- | --- | --- |
| P01 #48 | `codex/provider-attempt-evidence` | [PR #58](https://github.com/zekenaulty/Agentica/pull/58) merged at `bdf036f`; both CI jobs passed. Issue closed. |
| P02 #49 | `codex/provider-outcome-retention` | [PR #60](https://github.com/zekenaulty/Agentica/pull/60) merged at `96f8b27`; both CI jobs passed. Issue closed. |
| P04 #51 | `codex/provider-capability-report` | [PR #59](https://github.com/zekenaulty/Agentica/pull/59) merged at `01b176a`; both CI jobs passed. Issue closed. |
| P03 / P05 / P06 | `roadmap-reconciliation/Agentica`, `codex/provider-integration` | Shared preparation, required admission/counting, private durable codec and native search integrated. 488 client tests passed before the final endpoint-report regression. Combined PR/CI next. |
| P07 #54 | `lab-shared-provider-adoption/Agentica` | Lab adoption and explicit transcript mode implemented; 602 Lab tests and 30 UI checks passed before final review corrections. Typed search custody and optional decoder fixes under focused recheck. |
| P08 #55 | `provider-recovery-consumer/Agentica` | Early A5 [PR #57](https://github.com/zekenaulty/Agentica/pull/57) merged at `004ad7a`, both CI jobs passed. Full packed recovery commit `56ae681`: 34 offline scenarios passed, actual child-process reopen; root review/CI pending. |
| P09 #56 | Root integration lane | Official model/control/pricing pages fetched; authorized existing credential model metadata GET returned exact `gpt-6-luna`, HTTP200. Paid loop/browser qualification pending P07. |

P03's implementation now reuses P02 transport, P04 reports and P05 binding checks.
Its actual merge dependencies are P01/P02/P04/P05; the manifest and native issue
edges reflect that refinement. The approved publication snapshot's 19 edges remain
historical; the current execution graph has 22. No acceptance criterion was removed.

Each stable PR was checked at its exact reviewed head immediately before merge.
P04's synthetic URI credential fixture caused a secret-scan false positive. Its
original local branch was preserved; a clean replacement used `UriBuilder`, with
an exact force-with-lease and a full new successful CI run. No real secret was used.

## Cumulative live-work ledger

The approved isolated P09 envelope is US$5, six physical inference attempts and
eight native input-count requests. Each inference is capped at 8,000 admitted input
tokens and 8,192 configured output tokens. Verify current pricing and reserve before
dispatch. This envelope does not fund Bridge migration, reindex or production.

| Counter | Consumed | Unresolved |
| --- | ---: | ---: |
| Physical inference attempts | 0 | 0 |
| Native input-count requests | 0 | 0 |
| Paid provider work | US$0 | US$0 |

No provider call or credential provisioning occurred in these offline slices.
Keep counts cumulative across branches, retries and resumed work. Unknown outcomes
stay unresolved until reconciled; they cannot be reset by changing an attempt ID.

One read-only model metadata GET has completed. It is neither inference nor native
input counting and does not consume either physical-attempt allowance above.

## Integration boundary

Bridge epic #88 owns PM01–PM18; Cloud #66 owns its native TypeScript phase. Technical
handoffs may be exchanged under the owner's authorization. Agentica producer proof
is not installed Nyx acceptance. Native continuation, admission and effective
controls remain separate gates, and Bridge owns captured route/bundle assignments.

## Decision-changing learning

Accepted lessons belong under `.agents/learning/records` with exact evidence and
recheck conditions. Routine progress and cumulative attempt accounting stay here.
