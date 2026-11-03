# Agentica qualification matrix and remaining gates

Status: **draft evidence snapshot for [Q-02 / #26](https://github.com/zekenaulty/Agentica/issues/26)**,
2026-11-03, **14:47 UTC**. This document does not close #26, OA-12, the program
or any remaining implementation issue. It uses read-only GitHub/source data;
no tests, builds, provider calls or external-host operations were run to prepare it.

The 14:47 tables and gate plan are retained as historical evidence. The
**16:23 UTC execution overlay at the end** records later admission and CI facts;
use that overlay for the later source state.

The original [planning reconciliation](planning-reconciliation.md) retains its
`977d0ca` source cut and later dated updates. Current issue acceptance was read
directly for #10, #20, #24, #25, #26 and #31. PR metadata, exact-head Actions runs
and their job/step states were queried for #35–#41. These observations can age;
refresh the exact head and checks before admitting a change.

## Proof vocabulary

| State | Meaning here |
| --- | --- |
| PASS | A specified recorded gate passed at its identified source; scope and provenance remain attached |
| FAIL | A specified gate failed; earlier/local passes do not override it |
| PENDING | The queried gate has not completed successfully; no admission is inferred |
| NOT-RUN | This required experiment or follow-on gate has not been executed for the stated claim |
| UNKNOWN | Current installed/external behavior or a required observation is not established |
| SKIP | An explicitly deferred or out-of-scope experiment; not a pass |
| N/A | The slice does not exercise that boundary |

Local focused counts below are **recorded in the PR**, not newly executed by this
document worker. They are distinct from remote exact-head CI and main admission.
The failed gates are not diagnosed from step names alone.

## Source and admission ledger

| Slice | Exact reviewed/CI head | Main admission | Recorded focused evidence |
| --- | --- | --- | --- |
| [PR #35](https://github.com/zekenaulty/Agentica/pull/35), schemas and artist profiles; #21 and partial #10 | `236ee5e09c379f45361380b52725c421f2ebc3bb` | PASS: merge `dcc46a40754d6910de8669f11485a89619bb51c2`, 14:16:20Z | 45 fake-HTTP/planner/artist checks; configured model and compatible temperature; no live parity |
| [PR #36](https://github.com/zekenaulty/Agentica/pull/36), usage semantics; #22 | `ffcef01f7d2955ad6015589fdf9481481f20af6e` | PASS: merge `9c96c8f672e2611a07f8f7d5d3455f336dddebf5`, 14:36:27Z | 152 focused client checks; raw counters preserved; missing/invalid arithmetic stays unknown; generated count is not a bill |
| [PR #38](https://github.com/zekenaulty/Agentica/pull/38), C-01/N-01; #18/#28 | `9186540f788ea69cc5f948f496ddd441f730245f` | PASS: merge `6715ea1a76fef184f80a2bd0729f69489e8abda1`, 14:24:08Z | 23 contract checks and three synthetic reference cases; no complete-loop or migration claim |
| [PR #37](https://github.com/zekenaulty/Agentica/pull/37), binding readiness; #23 | `ceb2f5690c8336024346d6eec730af972961218c` | PENDING; PR open | 24 adapter/runtime/real SDK-loopback checks reported locally; current Windows gate FAIL |
| [PR #39](https://github.com/zekenaulty/Agentica/pull/39), C-02/N-02; #19/#29 | `80ac346402f8016ee26f79a7c73e13d71ce05de9` | PENDING; PR open | 26 conformance/harness checks and five CLI comparisons reported locally; current Windows gate FAIL |
| [PR #40](https://github.com/zekenaulty/Agentica/pull/40), historical faculty; #30 | `2d2637fed1932ebdc225d83784a8d214a98bd687` | PENDING; PR open, depends on #37/#39 admission | 15 real SDK/native conformance cases reported locally; current Linux formatting gate FAIL |
| [PR #41](https://github.com/zekenaulty/Agentica/pull/41), bounded Windows fixture setup | `bcf5a4e87853db7e939e3491e0d59c7a0b456d1d` | PENDING; PR open | 5 native-admission and 19 workspace-resource checks reported locally; current Windows gate FAIL |
| Q-07 / [#32](https://github.com/zekenaulty/Agentica/issues/32), durable operation frontier | Historical frontier `27cb2ce9717438edfe19487e3920faa183112c97`; see the [bounded review](durable-frontier-review.md) | PENDING root-owned consolidation branch/PR qualification | Two P2 corrections and focused regressions prepared; earlier 342 Web / 328 SDK counts qualify their older documented cut, not the consolidated final head |

### Current Windows/Linux gates

| PR | Exact-head Actions run | Windows | Linux |
| --- | --- | --- | --- |
| #35 | [41647933060](https://github.com/zekenaulty/Agentica/actions/runs/41647933060) | PASS | PASS |
| #36 | [41649888671](https://github.com/zekenaulty/Agentica/actions/runs/41649888671) | PASS | PASS |
| #38 | [41648131957](https://github.com/zekenaulty/Agentica/actions/runs/41648131957) | PASS | PASS |
| #37 | [41656156329](https://github.com/zekenaulty/Agentica/actions/runs/41656156329) | FAIL: deterministic tests, step 11 | PASS |
| #39 | [41656174701](https://github.com/zekenaulty/Agentica/actions/runs/41656174701) | FAIL: deterministic tests, step 11 | PASS |
| #40 | [41656201740](https://github.com/zekenaulty/Agentica/actions/runs/41656201740) | PENDING: job in progress | FAIL: formatting, step 10 |
| #41 | [41655968326](https://github.com/zekenaulty/Agentica/actions/runs/41655968326) | FAIL: deterministic tests, step 11 | PASS |

For #39 the run-level response still said in progress while its queried jobs had
already completed as listed. Job observations are retained explicitly rather
than inventing a later final run conclusion. For #37/#41 the queried runs had
completed with failure. These are snapshots of the heads above, not assertions
about later corrections or reruns.

## Qualification by behavior and proof level

| Boundary | Contract/component proof | Real runner / host acceptance | Actual MCP SDK | Current admission / remaining gap |
| --- | --- | --- | --- | --- |
| Exact manifest identity, perspective, source spans and coverage | PASS, admitted #38 | N/A for C-01 alone | N/A | Meaningful identity changes, immutable bounded inputs and invalid references are covered; reuse execution remains #24 |
| Partial coverage and unknown original effect | PASS, admitted #38 | Local PASS reported in #39, with residual/host veto checks | Local PASS for historical coverage in #40; not effect dispatch | #39/#40 remain unadmitted; no production custody assertion |
| Preferred source unavailable, exact mirror selected | Contract/corpus PASS #38 | Local PASS #39: actual planner → tool → observation → refinement → submission → independent host acceptance | #40 separately qualifies an installed exact-cut faculty | Need admitted dependencies, not a new loop or a substituted secondary source |
| Lost effect response, exact original attempt reconciled | Contract/corpus PASS #38 | Local PASS #39; read-only reconciliation and no original-effect dispatch capability | N/A to the read-only historical faculty | Original attempt/owner, acceptance and cumulative allowance remain explicit; installed external ledger UNKNOWN |
| Inert shadow | Local PASS #39 | Proposal only; executor deliberately absent | N/A | No canonical result, effect, speech or memory promotion; not accepted fulfillment |
| Bound applicability/readiness/integrity | Existing host grant contract; local PASS #37 | Generic runtime/adapter checks locally reported | Actual SDK loopback locally reported | #37 current Windows gate FAIL; no persistent host store or reconnect manager |
| Historical evidence faculty | Source/descriptor/input/output revisions and host semantics pinned in #40 | Local PASS #40: real runner and whole-record host predicate | Actual pinned client/server, structured results, protocol negotiation, scope/drift/identity refusal, cancellation and restart locally PASS | #40 current formatting gate FAIL; no production server/catalog admission |
| Supplied Responses schema and artist profile | PASS #35 fake HTTP and planner seams | Planner wiring covered; no live judgment claim | N/A | Admitted; remaining all-provider defaults audit #10 open |
| Output/thinking inclusion | PASS #36 fixtures and conservative arithmetic | Consumption does not imply a cost estimate | N/A | Admitted; prices/billing and model availability remain separate |

The synthetic reference corpus is version 1. Its source and golden manifest
identities remain in `Agentica.Tests/Fixtures/NyxLearning/SyntheticObligationScenarios.cs`
and [nyx-reference-baseline.md](../../learning/nyx-reference-baseline.md).
The runtime/host proof sources are `ObligationHostConformanceTests`,
`NyxLearningHarnessTests`, `NyxFacultyMcpConformanceTests`, the optional projector
and fulfillment adapter, and the sample's `LearningExperiment`.
No document's dated Bridge pin establishes current installed behavior.

### Preview, live providers and migration are separate gates

| Boundary | Evidence disposition |
| --- | --- |
| Lab clean-room user preview | Historical PASS recorded in PR #11: UI/draft preservation, Markdown and quiet traces; native input-count endpoint exercised separately. [PR #11 admission review](pr11-review.md) pins final head `cc813f6a9c2b1a851a1698b52ab2e96a4a68716f`, merge `3cb2d63395fee277a8eb790c57b271cd6277125b` and green [run 41643125122](https://github.com/zekenaulty/Agentica/actions/runs/41643125122). No fresh user-preview session was run for this matrix or the new obligation slices. |
| Current obligation/faculty user preview | NOT-RUN. Disposable CLI/SDK fixtures do not establish a human-operated preview or installed route. |
| Live OpenAI generation / search for this wave | NOT-RUN. Earlier native token counting is endpoint/admission evidence, not generated-answer, search or full Harness quality. |
| Live Gemini, Anthropic, xAI and Ollama qualification for this wave | SKIP: live checks were deferred by the owner. Offline seams remain required; no inferred live parity. |
| Actual provider stream / reasoning quality in the new learning host | UNKNOWN. Fixture JSON deltas prove event plumbing; MCP SSE is transport framing. Opaque signatures are not reasoning summaries or reusable semantic evidence. |
| Current Bridge paired caller/cache enablement and full OA-12 | UNKNOWN / NOT-RUN; external owner evidence required. Agentica-only fixtures cannot close it. |
| Nyx Lab migration, NL-05 routing and NL-06 legacy retirement | UNKNOWN / SKIP in this Agentica-only work. No installed route, data migration or operational replacement is admitted. |
| Current paired Maze adoption of Q-07's consolidated frontier | NOT-RUN here; separate owner/controller proof. Prior Lab/host proof does not qualify a later unmerged frontier. |

## Exact #26 acceptance disposition

| Issue acceptance | Current evidence | Remaining gate |
| --- | --- | --- |
| Both source grounding and effect continuation use the same optional contracts with independent host acceptance | #38 admitted; local #39 PASS reported | Admit #39 after current Windows failure is resolved and exact-head gates pass |
| Attribution, partial coverage, wrong perspective, invalid references, stale dependencies and unknown custody countercases | #38 contract cases and #39/#40 local conformance records | Preserve their source pins through dependency reconciliation; admit #39/#40; stale runtime reuse invalidation remains #24 |
| Matched cold/warm context, provider calls/tokens where available, first useful observed event and accepted fulfillment | Fixture call counts and planner-user-prompt characters measured in #39; first fixture JSON timestamp/elapsed time instrumented; token/cost null | NOT-RUN: matched cold/warm experiment with complete mounted-input measures and retained numeric observations; depends on #24 |
| Invalidation/restart overhead without invented savings/intelligence | #40 locally covers actual SDK restart meaning; no measured reuse-overhead study | NOT-RUN: #24 lookup/revalidation/invalidation/cancellation/recovery comparisons; protocol restart alone is not reuse overhead |
| Keep full Bridge/cache enablement, installed behavior and deferred live evidence UNKNOWN | Explicitly retained in this draft | Keep those dispositions in the final qualified report; no external closure from this matrix |

Known fixture synthesis counts are **three calls for source grounding**, **two
for effect reconciliation**, and **one inert-shadow proposal** in their declared
cases. The instrumentation's `PlannerUserPromptCharacters` counts the final user
message of each planner call; it excludes system instructions, output schemas
and transport overhead. It must not be relabelled complete context size or model
tokens. `FirstFixtureTextMilliseconds` is time to fixture JSON text, not time to
reasoning or accepted progress. Actual numeric context/latency observations are
not published by this draft. Warm reuse is explicitly `NOT-MEASURED`, and
provider tokens/cost remain null. There is no measured percentage improvement.

## Bounded follow-on plan after current CI stabilizes

No implementation is started by this document. Keep one owner per file set and
serialize builds/locks through the coordinator. Separate source-only planning
from enabling a new activation, route or effect. Use the existing runner,
manifest, evidence and acceptance seams.

### Gate 0 — Admit the current source safely

1. Diagnose exact failed job logs for #37/#39/#41; the shared failed step name
   does not prove the same cause. Preserve real operation deadlines and resource
   assertions while correcting fixture setup or genuine implementation defects.
2. Correct #40 formatting and rerun changed-head checks. A green earlier head
   cannot qualify a shared fixture patch or dependency merge.
3. Reconcile main/dependencies, refresh reviews/checks and admit only stable
   exact heads. #40 requires #37/#39 admission; #39 requires the admitted #38
   foundation. Do not waive Windows in favor of Linux.
4. Keep Q-07's independent consolidation/CI gate and ignored runtime custody
   intact. A clean source tree is not permission to retire a live workspace.

### Lane A — [C-03 / #20](https://github.com/zekenaulty/Agentica/issues/20)

Prerequisite: admitted #19/#29. Start with a documented mapping onto current
descriptors, security declarations, catalog and manifests. Prototype compatibility
filtering in the learning host using both exact-source and original-attempt
cases. Reject known input/output/scope/effect/readiness mismatches deterministically
with bounded reasons. One deliberately bound discovery tool may return candidate
identities for unresolved HOW; candidates do not install or widen a grant.
Recheck current grants, revocation, integrity and resources on reuse.

Deliver only the missing optional fields demonstrated by both cases, their
mapping document and focused falsification tests. No universal IR, broad registry,
automatic installation or new scheduler. Extract core types only when those
two cases expose a concrete shared need.

### Lane B — [R-01 / #24](https://github.com/zekenaulty/Agentica/issues/24)

Prerequisite: admitted #19/#29. Use a bounded, sample-host-owned exact reuse and
original-attempt store under `samples/Agentica.NyxLearningHost/Reuse/`; no core
cache database. Equivalent manifests resolve one validated artifact, but each
consumer independently accepts and receives its own receipt. Two concurrent
misses share one dispatch and truthful pending/ready/unavailable/indeterminate
state. Cancel one waiter without discarding another's eligible work.

Qualify interruption before and after dispatch with the original attempt and
cumulative allowance retained. Revalidate source, perspective, scope, question,
policy and corpus horizon; a newly relevant source invalidates an old completeness
or absence claim even if selected cuts did not change. A compatible warm hit
must add **zero synthesis calls**. Record lookup, validation, invalidation,
recovery and acceptance cost rather than claiming that every warm hit is free.
No fuzzy answer cache or provider-private reasoning enters semantic reuse.

### Lane C — [R-02 / #25](https://github.com/zekenaulty/Agentica/issues/25)

Prerequisite: admitted #19/#29. Freeze one synthetic journal/source cut and
matched acceptance. Compare checkpoint+receipts, compatible native replay,
selected exact evidence and one bounded interpreted-recovery candidate. Mark
unsupported native replay SKIP instead of inventing a provider token/signature.
Every candidate must keep missing coverage, supported/provisional/refuted claims,
residual work and exact presented references. Unknown effects require original
attempt reconciliation before any retry.

Keep interpreted recovery off by default. Fixture plumbing can qualify a
contract, cost counter and recovery refusal; it cannot establish live semantic
benefit. Retain NOT-RUN for live judgment until a separately authorized experiment
earns promotion under the same cumulative budget. No hidden reasoning
reconstruction, bulk trace export or automatic policy promotion.

### Lane D — [Q-02 / #26](https://github.com/zekenaulty/Agentica/issues/26)

Finalize `docs/qualification/obligation-activation-v1.md` after Gate 0 and Lane B
provide their actual evidence. Each retained experiment row should include:

- Exact source/contract/corpus and generation-policy revisions; OS/SDK and command.
- Declared horizon, presented source/coverage, consumer acceptance revision,
  original attempts, remaining allowance and final residual/fulfillment state.
- Complete mounted-input characters/UTF-8 bytes, and provider-native tokens only
  when actually measured; distinguish cumulative calls, lookup and validation work.
- First useful **observed** event, accepted-fulfillment time, invalidation/restart
  overhead, canonical receipt references and individual PASS/FAIL/UNKNOWN/SKIP.

Match acceptance and horizons across cold/warm cases. Publish useful negative
results, not just successful reuse. Retain external Bridge/cache/installed/live
UNKNOWN. This draft is a partial evidence index, not completion of these gates.

### Lane E — [N-04 / #31](https://github.com/zekenaulty/Agentica/issues/31)

Prerequisites: admitted #19/#29/#30; #24 before claiming warm zero-synthesis reuse;
#26 evidence before promotion. Compose the existing sample into an independently
startable/stoppable/restartable historical-review seam with disposable,
versioned host state/configuration and no other host database access. Reuse one
runner/provider boundary; validate interpretation before separate per-consumer
acceptance. Exercise both native and bound MCP evidence paths.

Expose the five existing provider seams with explicit unsupported-control errors,
Gemini Interactions default and GenerateContent on demand. Record actual emitted
events; preserve opaque private state separately from displayed summaries.
Provider-free/default fixtures and protocol tests may pass independently of live
model runs. No automatic Nyx routing, production cutover or second executor.

### Independent inventory lane — [Provider defaults / #10](https://github.com/zekenaulty/Agentica/issues/10)

The admitted schema/artist correction is only partial progress. Inventory client,
console, Web, image/text profiles, environment/service overrides, examples,
benchmarks, context/output budgets, transport, reasoning controls, timeout/retry
and pricing assumptions. Produce a dated keep/change matrix from current official
provider/account/API evidence; document intentional default differences and
preserve active planners' captured settings.

Prioritize verification of the owner's reported December 1 Gemini redirect;
this draft does not verify that notice or select a successor. Check both
Interactions and GenerateContent, applicable API surfaces, thinking, schema,
streaming, usage and continuation before changing defaults. Keep custom model
overrides and local Ollama selection explicit. Offline resolution/unsupported
control tests precede any change. Define live smoke gates separately and retain
the owner's deferred-provider SKIPs; fixture success cannot prove availability,
quality, billing or live parity.

Lanes A/B/C and the #10 inventory can progress independently after their source
prerequisites, with nonoverlapping file ownership. Lane D consumes results;
Lane E composes admitted seams. This ordering creates no new dependency on
Bridge/Cloud/Nyx/Maze implementation and leaves full external adoption/retirement
with their owning hosts.

## Execution overlay — 2026-11-03, 16:23 UTC

This overlay updates dispositions without replacing the failed-head evidence
above. The current documentation source is
`dcfffe23059536263251f008d09f377548fe4f15`: admitted main
`2e5c39d742698df41b0a96a7959e417c81cee039` plus the N-03 candidate. GitHub merge
metadata and exact-head Actions records were read; this documentation worker ran
no tests, builds, providers or external-host operations.

### Admitted source

| Slice | Admission / exact head | Later hosted evidence |
| --- | --- | --- |
| C-01 / N-01 | [#38](https://github.com/zekenaulty/Agentica/pull/38), merge `6715ea1a76fef184f80a2bd0729f69489e8abda1`; head `9186540f788ea69cc5f948f496ddd441f730245f` | PASS [41648131957](https://github.com/zekenaulty/Agentica/actions/runs/41648131957); unchanged admitted foundation |
| H-01 | [#35](https://github.com/zekenaulty/Agentica/pull/35), merge `dcc46a40754d6910de8669f11485a89619bb51c2`; head `236ee5e09c379f45361380b52725c421f2ebc3bb` | PASS [41647933060](https://github.com/zekenaulty/Agentica/actions/runs/41647933060) |
| H-02 | [#36](https://github.com/zekenaulty/Agentica/pull/36), merge `9c96c8f672e2611a07f8f7d5d3455f336dddebf5`; head `ffcef01f7d2955ad6015589fdf9481481f20af6e` | PASS [41649888671](https://github.com/zekenaulty/Agentica/actions/runs/41649888671) |
| H-03 | [#37](https://github.com/zekenaulty/Agentica/pull/37), merge `a841b414d12051c742bc7b25f3753ceb3427b24d` at 15:28:04Z; head `b7784d6482baaadf9da0de6d8f5f418941491171` | PASS [41660754156](https://github.com/zekenaulty/Agentica/actions/runs/41660754156); earlier Windows failure remains attached to its older head |
| C-02 / N-02 | [#39](https://github.com/zekenaulty/Agentica/pull/39), merge `1f0609efe598558d642f9a8794b8e6ff1e5fb49c` at 15:56:26Z; head `a03e20c18a70dd06fc7862ef7b37c83a91ded384` | PASS [41666172418](https://github.com/zekenaulty/Agentica/actions/runs/41666172418); the real comparative runner/host slice is admitted |
| Q-07 | [#42](https://github.com/zekenaulty/Agentica/pull/42), merge `2e5c39d742698df41b0a96a7959e417c81cee039` at 16:02:08Z; head `123a11b05c8406e68842eb1cc5e8428e35eda9ad` | PASS [41666200580](https://github.com/zekenaulty/Agentica/actions/runs/41666200580); consolidated durable start/operation/action source is admitted |
| Q-04 / Q-08 | [#11](https://github.com/zekenaulty/Agentica/pull/11), merge `3cb2d63395fee277a8eb790c57b271cd6277125b`; head `cc813f6a9c2b1a851a1698b52ab2e96a4a68716f` | PASS [41643125122](https://github.com/zekenaulty/Agentica/actions/runs/41643125122); retained-trace correction remains admitted |

### N-03 current candidate

The exact historical-evidence faculty is **implemented in this source**, with
native and actual pinned SDK paths, full source/scope/range checks, explicit
coverage/unavailable/error results and whole-record consumer acceptance.
[PR #40](https://github.com/zekenaulty/Agentica/pull/40) is still open in the queried
merge metadata. Its head is `dcfffe23059536263251f008d09f377548fe4f15`.

The earlier pending CI state has resolved:
[run 41671185912](https://github.com/zekenaulty/Agentica/actions/runs/41671185912)
is completed with success for that exact head. Both
[Windows job 126039668018](https://github.com/zekenaulty/Agentica/actions/runs/41671185912/job/126039668018)
and [Linux job 126039668337](https://github.com/zekenaulty/Agentica/actions/runs/41671185912/job/126039668337)
passed all listed required steps. This qualifies the candidate's hosted gates;
main admission remains pending until an actual merge reference exists. A green
run does not establish a deployed MCP server, provider judgment or Nyx migration.

### Local consolidated qualification

The coordinator records the following local results for the consolidated source.
They are separate from the remote run above and overlap with one another:

| Local population | Recorded result | Interpretation |
| --- | --- | --- |
| Full Lab Web | 573 PASS, 1 local link-capability SKIP | Real service/storage/socket boundary; local capability skip remains explicit |
| Post-review focused correction | 8 PASS | Focused transport/socket regression gate; does not replace the full suite |
| Combined host/chat JavaScript | 357 PASS | Host SDK and chat regressions; no live external host or provider implied |
| Combined obligation loop/faculty | 41 PASS | Real runner plus deterministic provider fixtures and actual SDK conformance |

Do not sum those counts into unique coverage or transfer them to an untested
future head. Fixture JSON timing and MCP SSE still do not prove live reasoning.
No fresh user chat, provider generation or external migration was exercised by
this documentation update.

### Remaining gates after admission progress

[Q-02 / #26](https://github.com/zekenaulty/Agentica/issues/26) **remains open**.
Admission now resolves the source/runner dependency gate for #19/#29 and the
durable Q-07 branch gate; it does not supply matched cold/warm measurements.
The N-03 dependency remains a reviewed candidate awaiting admission at this
snapshot. The earlier issue-acceptance table's unmeasured-cost and external
UNKNOWN dispositions remain in force.

The explicit parallel plan is now:

1. **#10 independent inventory:** current all-provider defaults/API evidence,
   override precedence and unsupported-control seams; retain deferred live SKIPs.
2. **#20 host compatibility/discovery:** the two admitted learning cases can
   qualify bounded semantic matching using existing descriptors before adding
   optional fields. Preserve installed authority; discovery installs nothing.
3. **#24 exact reuse/custody:** shared original attempt, independent consumer
   acceptance, interruption/invalidation and measured warm zero-synthesis work.
4. **#25 recovery comparison:** matched source/journal horizons and acceptance,
   original unknown effects retained, no extra activation by default.
5. **#26 measurement publication:** consume #24's real cold/warm/context/cost and
   invalidation/restart results; do not substitute this documentation for them.
6. **#31 standalone composition:** admit #30 before composition; use #24 before
   claiming warm reuse and #26 before promotion. Keep state/configuration
   independently disposable and the existing runner as execution owner.

The first four lanes can use distinct owners and file claims. The detailed
boundaries in the historical plan still apply. Measured reuse savings, live
provider parity, full Bridge paired caller/cache enablement, installed Nyx
behavior, NL-05/NL-06 routing/retirement and production migration remain
**NOT-RUN, SKIP or UNKNOWN**, according to their original proof level. No issue
closure or external-host action is performed by this overlay.

## Final review correction: 2026-11-03, 16:47 UTC

The source now includes `b2589c12bfe55ffac868a6182c935949ebf755f0`. The
earlier exact-head both-green run remains historical evidence for `dcfffe2`.
Independent review identified a canceled-client Windows response-disposal path
in the new faculty fixture. Its cleanup now tolerates only known disconnect
codes 64/1229, matching the admitted runtime fixture; unexpected cleanup failures
still propagate. Request processing and assertions are unchanged.

The root reran all **15 faculty conformance tests: PASS**, including actual SDK
transport and cancellation. This is a focused local Windows/.NET 10.0.401 gate,
with the repository SDK pin restored unchanged afterward. Fresh exact-head
[run 41677313030](https://github.com/zekenaulty/Agentica/actions/runs/41677313030)
is pending at this snapshot. Admission still requires both hosted lanes, a
current review and an exact-head merge. [PR #40](https://github.com/zekenaulty/Agentica/pull/40)
retains the subsequent outcome; this document does not predict it.

The final documentation/recovery-publisher check also passes all **three offline
recovery tests**. Every GitHub call in that test is mocked; it is not an issue
publication replay. No new provider call, user-chat operation, external host
change or migration was performed.
