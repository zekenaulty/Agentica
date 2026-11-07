# Provider readiness release qualification — 2026-11-06

This is an evidence overlay for [Epic #47](https://github.com/zekenaulty/Agentica/issues/47),
not a replacement for the approved [v1.1 plan](../../planning/provider-migration-readiness-2026-11-06/PLAN.md).
The final P09 PR still requires its exact-head Windows/Linux checks before merge;
[#56](https://github.com/zekenaulty/Agentica/issues/56) records that admission.

## Source, package and host boundaries

| Boundary | Evidence | Meaning |
| --- | --- | --- |
| P01/P02/P04 | PRs #58/#60/#59, exact heads passed both jobs | Retained facts, single configured sends, actual effective-control reports |
| P03/P05/P06 | PR #61, merge `a26e8d8`, run `42322353955` | Exact preparation/admission/counting, private bounded continuation, shared native search |
| P08 | PR #62, merge `74d470a`, run `42325135654` | 35 packed cases: 15 A5 + 20 recovery cases; actual child-process restart; Google.GenAI 1.13 coexistence |
| P07 | PR #63, merge `8b466e5`, run `42325652843` | Shared Lab clients, explicit transcript mode, bound forwarding defaults and durable pre-turn count journal |
| P09 driver | `8cd502c`, exact SDK 10.0.302; 14 offline guards/actual three-phase fixture | Cumulative physical/cost custody; no automatic resubmission; real Lab composition |
| Live OpenAI | Same driver/source; successful isolated scenario and read-only host reopen | Requested, effective and provider-reported model `gpt-6-luna`; requested/serialized effort `high`; no downgrade |
| Installed Bridge/Nyx/Cloud | Externally owned | Not established by these producer results |

P07's Windows job passed 508 client cases, 1,278 core cases (7 intentional live
skips), 614 Lab cases, and 359 JavaScript checks. Linux passed 508 client cases,
607 Lab cases (7 platform skips), 1,278 core cases (7 live skips), 359 JavaScript
checks, coverage gates, package/35-case consumer proof and container smoke. These
overlapping populations are not a unique test total. Later P09 checks qualify
their own final source, including its offline driver and browser correction.

### Immutable local package pair

Qualified source **`8b466e5f2795a26bf4f49756dc3d76592936053e`**, SDK 10.0.302,
fresh 35-case consumer proof; this pair is locally built, not downloaded CI output.
Both nuspec commits, DLL embedded revisions, restored nupkg bytes and resolved DLL
bytes were checked. Version `0.1.0-research.1` alone is not an artifact identity.

| Package | nupkg SHA-256 | DLL SHA-256 |
| --- | --- | --- |
| Agentica | `28a57e902883c0931449654a326017913ef5606805d2d0b25c316c1b95b503d8` | `79360ff4fbb0a98c6243def8c8ad2d7944680650d7737870b6a257caef9452bb` |
| Agentica.Clients | `23211723c16d815e35708fe2a6118548e4243aad15089d671aacd3bd910e05bf` | `09493b8a6da97945f99d606d7fcec2e8e9013bb77d599837747efcb988e50c33` |

The retained local feed/manifest is in the Agentica-owned `provider-consumer-proof`
worktree under `artifacts/provider-consumer/e0a3b598791846c39ec4d1674a54892f/`.
Bridge received the read-only handoff and owns its separate consumer qualification.

## Real model loop

The disposable candidate used separate port **5097**, new Lab-owned SQLite/scratch
state, existing `OPENAI_REPLIKA_KEY` mapped only in process memory, empty custom
instructions, memory/recall disabled, and only `scratch.read` / `utility.calculate`
bound in addition to the response tool. No existing preview or owner chat changed.

A fresh marker and two operands existed only in the scoped scratch file. The
successful run produced three plans, two refinements, three successful receipts,
an exact marker/product response artifact, resolved completion evidence and no
residual obligation. Event order proves model decisions after both observations:

| Evidence | Retained sequence |
| --- | ---: |
| Scratch observation / receipt | 777 / 778 |
| Calculation observation / receipt | 1,435 / 1,436 |
| Final response receipt | 2,648 |

All three provider responses completed with known usage. The exact native input
counts were **2,996 → 6,136 → 4,389**. Output totals were **262 / 366 / 618**,
including reasoning subsets **89 / 147 / 312**; reasoning is not added again.
All requests serialized `stream:true`, `store:false`, no stored conversation or
`previous_response_id`. Private replay was kept inside the provider continuation
path and was not persisted as ordinary chat memory.

The host was stopped and recreated against the same store and cumulative ledger.
Answer/settings/outcome/trace hashes remained identical; reopen dispatched **zero**
new provider calls. The successful turn retained 2,653 events with no dropped trace.

### Cumulative allowance and retained failures

| Work | Used / approved | Unresolved |
| --- | --- | --- |
| Physical inference | **5 / 6** | 0 |
| Native input count | **6 / 8** | 0 requests; measurements remain separate from inference usage |
| Conservative priced exposure estimate | **US$0.00697625 / US$5** | US$0 |
| Native web search / other providers | 0 | Deferred live; offline search and other-provider seams qualified separately |

This estimate uses conservative input/output policy rates and is not a provider
invoice. Current official [count billing guidance](https://help.openai.com/en/articles/4936856-understanding-and-counting-tokens)
states that native input counting is free. The physical eight-count cap still applies.

The history includes two retained unsuccessful experiments; neither was erased:

1. A 24,000-character setting failed before any HTTP because it could not leave
   the planner's mandatory input plus repair reserve.
2. At 80,000 characters, two actual decisions read and calculated correctly. The
   third exact native count was **9,613**; the guard rejected inference before POST
   at the approved 8,000-token ceiling. The two completed calls remain in the
   cumulative five-call total above.

An explicit successor decision retained both old chats, evidence and the same
ledger. A 40,000-character projection was first qualified with an actual offline
three-phase executor/runner/client fixture. It retained replay for decision two,
reset it for decision three, and preserved the accepted scratch/calculation
evidence. The subsequent real run completed within the original cumulative caps.
No budget was replenished or raised.

## Browser evidence

CUA inspected actual local pages; no browser HTTP mocks were used:

- Credential-free **5094** fixture: Markdown for both roles, partial streaming and
  completion, clickable safe source citation, saved high effort/custom instructions,
  reasoning summary after inspection, trace closed after reload, and a new blank
  clean room with search/recall/memory off.
- Real **5097** candidate: completed canonical answer, model/high settings, empty
  custom instructions, only the two bound utilities, saved usage and collapsed
  2,653-event trace. The older failed turn still displayed its known usage and one
  attempt not dispatched; inspection/reload created no new provider POST.

This inspection exposed a reopened-turn bug: **Resume live** relabelled an early
retained page without fetching the tail. Correction `adc35d9` adds a read-only
`tail=true` query, retains separate inspection cursors/newer SSE arrivals and keeps
the normal disclosures collapsed. Worker checks: 32 JavaScript and three persistence
cases passed. Root's rebuilt actual candidate loaded sequences **2,647–2,653**
and terminal completion from the 2,653-event saved turn; the page explicitly named
the 2,646 earlier events outside that bounded tail. No new inference/count occurred.

Restricted local evidence remains under the root worktree's
`.agentica/verification/p09-live/` and
`.agentica/verification/provider-readiness/browser/`. Structural scenario evidence
stores hashes, counts and causal sequences; private/native payloads and credentials
are excluded. Screenshots are local artifacts, not repository source.

## Learning and limits

Required accounting cannot depend on optional native/UI inspection. Borrowed HTTP
defaults must stay bound through forwarding, and native counts require durable
custody even before a turn exists. A returned paid proposal remains unaccepted
evidence until the host accepts or explicitly reconciles it. Host authorship and
source revision survive provider-role projection. Accepted, source-pinned lessons
are in the repository's learning lane.

Character ceilings, repair headroom, opaque replay size and native input tokens
are different constraints. **40,000 is this proof's setting, not a universal safe
default.** Exact native counting remains the final gate. Automatic drop-native /
recompile recovery after a tighter external native-count rejection is a follow-on
refinement; this release fails closed and records explicit recovery instead.

Other-provider live tests remain deferred as authorized. Production/concurrent
durable storage, installed Bridge acceptance, PM10 media, PM11–PM14 reindex/recall,
and PM16 native TypeScript Cloud work remain external. Broader reuse/learning
issues #10/#24/#25/#26/#31 retain their separate acceptance. See the concrete
[A1–A6 handoff](BRIDGE-HANDOFF.md).
