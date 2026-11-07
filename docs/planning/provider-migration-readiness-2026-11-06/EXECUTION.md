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
| P03 / P05 / P06 | `codex/provider-integration` | [PR #61](https://github.com/zekenaulty/Agentica/pull/61) merged at `a26e8d8`; exact head `82980bb` passed both Windows/Linux CI jobs, run `42322353955`. Issues #50/#52/#53 closed. |
| P07 #54 | `codex/lab-provider-integration` | Lab adoption and explicit transcript mode integrated. Root checks: 48 affected host and 89 core cases passed. Independent review prompted borrowed-transport binding and durable count custody corrections; worker's 60 focused cases passed. Final review/CI/browser pending. |
| P08 #55 | `codex/provider-recovery-qualification` | [PR #62](https://github.com/zekenaulty/Agentica/pull/62) open at `5454ba4`. Full packed recovery: 35 offline scenarios passed, including genuine returned-before-acceptance process restart. Independent re-review resolved all three findings. Root integrated package proof also passed all 35. CI pending. |
| P09 #56 | Root integration lane | Exact `gpt-6-luna` metadata GET returned HTTP200; current model/control/pricing verified, including the official free input-count tariff. Guarded disposable live driver in progress. Paid loop/browser qualification pending P07. |

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

Current [OpenAI token-count billing guidance](https://help.openai.com/en/articles/4936856-understanding-and-counting-tokens)
explicitly states that Responses input counting is free. This verified tariff does
not turn a measurement into inference usage; the separate eight-request cap still
applies. Model pricing and conservative generation reservations remain required.

## Integration boundary

Bridge epic #88 owns PM01–PM18; Cloud #66 owns its native TypeScript phase. Technical
handoffs may be exchanged under the owner's authorization. Agentica producer proof
is not installed Nyx acceptance. Native continuation, admission and effective
controls remain separate gates, and Bridge owns captured route/bundle assignments.

Bridge requested and received an immutable local paired package handoff for merged
core `a26e8d8`. Its manifest, nuspec source revisions, embedded DLL revisions,
package/DLL SHA-256 values and isolated 15-case A5 proof are retained at
`provider-consumer-proof/Agentica/artifacts/provider-consumer/f8aedab43e284df4b691550adbd45602`.
These are local Windows SDK 10.0.302 artifacts, not downloaded CI artifacts.
Consumer installation/qualification remains Bridge-owned.

## Decision-changing learning

Accepted lessons belong under `.agents/learning/records` with exact evidence and
recheck conditions. Routine progress and cumulative attempt accounting stay here.
