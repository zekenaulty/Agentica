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
| P07 #54 | `codex/lab-provider-integration` | [PR #63](https://github.com/zekenaulty/Agentica/pull/63) merged at `8b466e5`, both CI jobs passed run `42325652843`. 508 client cases; Windows 614 Lab cases; Linux 607 + 7 platform skips; 359 JavaScript checks. Issue closed. |
| P08 #55 | `codex/provider-recovery-qualification` | [PR #62](https://github.com/zekenaulty/Agentica/pull/62) merged at `74d470a`, both CI jobs passed run `42325135654`. 35 packed scenarios, including genuine returned-before-acceptance process restart; all review findings resolved. Issue closed. |
| P09 #56 | `codex/provider-live-qualification` | Guarded real `gpt-6-luna` / `high` full Lab loop passed with unchanged reopen and zero new calls. Browser checks passed on disposable 5094/5097 candidates. Final release CI/merge is recorded on the issue; see the detailed qualification overlay. |

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
| Physical inference attempts | 5 of 6 | 0 |
| Native input-count requests | 6 of 8 | 0 |
| Conservative priced exposure estimate | US$0.00697625 of US$5 | US$0 |

No credential was provisioned. The ledger is cumulative across branches,
corrections and resumed work. Unknown outcomes retain exposure; a changed attempt
ID or host restart cannot reset capacity. The five inferences all completed with
known usage; estimates use conservative rates and are not a provider invoice.

The initial 24,000-character scenario failed before HTTP on repair headroom.
The 80,000-character successor completed two tool decisions, then rejected a
9,613-token third preparation before inference POST. Both old chats and their
evidence remain. An explicit successor retained the same ledger; an offline
three-phase fixture first qualified a 40,000-character projection, after which
the real three-decision run completed at 2,996 / 6,136 / 4,389 input tokens.
No approved cap was increased. See
[P09 qualification](../../implementation/provider-readiness-20261106/P09-QUALIFICATION.md).

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

A later optional paired handoff for qualified P07 main `8b466e5` passed all 35
packed cases. Its manifest and package/DLL hashes are retained beside
`provider-consumer-proof/Agentica/artifacts/provider-consumer/e0a3b598791846c39ec4d1674a54892f/feed`.
Bridge may retain its existing pin until its own isolated consumer qualification.

## Decision-changing learning

Accepted lessons belong under `.agents/learning/records` with exact evidence and
recheck conditions. Routine progress and cumulative attempt accounting stay here.
