# Bridge / Agentica provider migration alignment

- **Date:** 2026-11-06
- **Revision:** 1.1, companion to [PLAN.md](PLAN.md) and [stories.json](stories.json)
- **Status:** owner approved on 2026-11-06; epic #47 and P01–P09 issues #48–#56 are published; execution goal is not yet created
- **Publication:** [verified issue index and dependencies](PUBLICATION.md)
- **Agentica code baseline:** `cb9154dacb327880585d518d66ee24773b8cc1b9`
- **Bridge reviewed code baseline:** `187b203c45d316e8f166dcb1cbd239e5a564a321`

## Source packet and supersession

The owner supplied the following unchanged PDFs. Page references identify their internal page numbers.

| Source | Pages | SHA-256 |
| --- | ---: | --- |
| `nyx-openai-migration-final-plan.pdf` | 7 | `ac13e501220dc10db9aa296d9c13cc4e8e11e145b610359770632753c187096f` |
| `nyx-provider-migration-story-plan.pdf` | 14 | `5bd37e151d468f8b53143ee8e234cb92e2040026f7f91fe4a6a6da27792b3e3e` |

Also considered: the owner-supplied Lumina catch-up beginning “The core plan is good. I would keep P01–P09...”. It identifies obsolete Gemini rollback, operation routing terminology, embedding/media ownership and local recovery/publication corrections.

The earlier Agentica proposal's operational instruction to keep Gemini running for rollback is superseded. The current Bridge packet requires an **offline, OpenAI-only first cutover**, with no new Google inference or embedding calls through normal work, probes, preparatory retrieval, repair, recovery or fallback. Preserve retained results, old vectors, private native state and original attempts as required by host policy. Recover eligible unfinished obligations through a recorded OpenAI transition. Drive/Docs transport remains usable.

Agentica's Gemini and other provider implementations remain legitimate reusable library capabilities. Offline fixtures and retained adapters do not enable Google model use in the Bridge migration. The supplied documents are alignment inputs; their descriptions of owner permissions do not themselves create a goal, spending, service activation or cross-repository mutation in this chat.

## A1–A6 producer/consumer handoff

These are stable planning references. Add actual issue URLs and enabling commit/package pins after approval; no issue has been created by this record.

| Bridge reference | Agentica producer | Exact delivered boundary | Bridge ownership |
| --- | --- | --- | --- |
| A1: unsuccessful-call evidence/usage | [P01 / #48](https://github.com/zekenaulty/Agentica/issues/48) + [P02 / #49](https://github.com/zekenaulty/Agentica/issues/49) | Observed identifiers, nullable usage, semantics, uncertainty and terminal classification on supported exits | Durable accounting, host acceptance, retries and objective continuation |
| A2: durable native continuation | [P05 / #52](https://github.com/zekenaulty/Agentica/issues/52) | Bounded versioned explicit sensitive export/import for existing carriers; compatibility result | Encryption, scope, persistence, retention and evidence-based episode reconstruction |
| A3: prepared/single-attempt inference | [P03 / #50](https://github.com/zekenaulty/Agentica/issues/50) using [P01 / #48](https://github.com/zekenaulty/Agentica/issues/48)/[P02 / #49](https://github.com/zekenaulty/Agentica/issues/49) | Immutable effective request identity, host admission hook and physical-attempt boundary | Operation resolver, authorization, journal, cumulative reservation and retry policy |
| A4: native search/source activity | [P06 / #53](https://github.com/zekenaulty/Agentica/issues/53); producer cases in [P08 / #55](https://github.com/zekenaulty/Agentica/issues/55) | Reuse Lab's existing OpenAI options/activity/sources/annotations | Existing search semantics, source validation/storage and UI; a qualified narrow adapter is an available integration choice |
| A5: existing embedding client | Existing `OpenAiEmbeddingClient`; isolated [P08 / #55](https://github.com/zekenaulty/Agentica/issues/55) proof and [P09 / #56](https://github.com/zekenaulty/Agentica/issues/56) handoff | Exact profile/input/vector contract, success usage and classified errors; host-observed HTTP injection seam | Job/query/source mapping, admission, observed failure usage, retry/exposure and historical indexing |
| A6: producer/package qualification | [P04 / #51](https://github.com/zekenaulty/Agentica/issues/51) + [P08 / #55](https://github.com/zekenaulty/Agentica/issues/55) + [P09 / #56](https://github.com/zekenaulty/Agentica/issues/56) | Effective capabilities, packed consumer evidence, exact artifact pins and limits | Actual Nyx consumer qualification and installed acceptance |

Deliver enabling artifacts incrementally. PM04 needs A1/A2/A3, not the whole Agentica program. P08's isolated A5 fixture can be published before unrelated search/Lab cases finish. Keep producer proof, consumer proof and installed proof distinct.

## PM01–PM18 dependency crosswalk

The story packet's first three pages define the register and A references; subsequent pages define acceptance. These external stories remain under the Bridge migration coordinator, including required PM16 in a separate Cloud workspace.

| Bridge story | Agentica relationship and boundary |
| --- | --- |
| PM01: custody/isolated baseline | Refresh exact source and dependency pins; Agentica supplies producer pins, while Bridge owns its custody |
| PM02: operation routing/model configuration | P04 reports supported/effective controls; P03 preserves resolved references; Bridge owns the exhaustive registry/resolver |
| PM03: captured execution/retrieval bundles | P03/P04/P05 consume resolved bindings; P08 tests settings edits, nested calls and restart pinning |
| PM04: text transport/continuation | Consume A1/A2/A3 under Bridge's existing provider/executor boundaries |
| PM05: schemas/fingerprints/accounting | P01–P04 and Bridge-shaped P08 fixtures; preserve actual supplied strict schemas and observed usage |
| PM06: objective recovery/cumulative limits | Consume A1–A3; P08 demonstrates host/client recovery without claiming actual Nyx qualification |
| PM07: task settings/effective health | P04 supplies client facts; Bridge owns operation settings, overrides and permitted probes |
| PM08: search/citation migration | A4/P06 supplies producer behavior; Bridge independently qualifies its consumer |
| PM09: document reading/synthesis | A1–A3 through the existing model port; document-engine extraction is unnecessary for this cutover |
| PM10: media planning/vision/generation | Shared text clients where applicable; dedicated qualified OpenAI capability adapters where needed |
| PM11: OpenAI embedding jobs/queries | A5; P08 proves exact profile/order/result/error/observed-usage seam; host owns adaptation |
| PM12: offline historical reindex | Required Bridge maintenance; existing A5 client supports transport, not scheduling or coverage acceptance |
| PM13: retained profiles/eligibility | Bridge owns source policy, forgetting, jobs, retained vectors and late-completion fences; P08 includes profile mismatch negatives |
| PM14: recall/coherent activation | Bridge proves actual eligible source/version coverage and atomically accepted route/retrieval activation |
| PM15: old Google-bound work through OpenAI | Preserve original attempts/results; reconcile effects; reconstruct at a recorded boundary using P01/P02/P03/P05 semantics |
| PM16: native TypeScript Cloud adapter | Required external phase after principal Bridge/reindex; share necessary schemas/fixtures, with no .NET library or C# service dependency |
| PM17: complete actual loops/recovery | A6 producer evidence plus Bridge's own foreground/detached/Cloud qualification |
| PM18: release/installed return online | External exact-candidate acceptance after PM17; P09 supplies artifacts, limits and handoff |

## Operation routing and captured bindings

Bridge resolves an actual operation/phase assignment, then its task default, then bundle default. Broad stage/contract labels alone cannot select a route because unrelated work shares those labels. Agentica executes the already-resolved request and reports what it used.

The consumer needs attributable opaque coordinates such as operation/phase, resolved route fingerprint, execution-bundle reference, source cut and applicable embedding-profile references. These describe handoff information; they are not a requirement to introduce Nyx enums, a router or a universal execution IR into Agentica.

- Each admitted host work unit captures its route bundle and independent memory/conversation retrieval profiles.
- Nested calls, repairs, retry decisions and restart recovery retain those bindings. New admissions see the current bundle.
- Current permissions, source eligibility, revocation and forgetting are rechecked when used; a captured bundle freezes neither authority nor source access.
- Per-route effective fingerprints remain distinct from full bundle identity. Unrelated text-route changes preserve historical embedding profiles.
- A tool's model assignment governs its own inference phases. Deterministic tools have no model. Preserve current combined memory prose/keyword construction as one request.
- An incompatible native capsule leaves the objective, accepted observations and remaining obligations available for host reconstruction. Preserve original uncertain custody and cumulative budgets through the transition.

## A5 embedding boundary: concrete source findings

Source reviewed: [OpenAiEmbeddingClient](../../../Agentica.Clients/Embeddings/OpenAiEmbeddingClient.cs), [embedding exception](../../../Agentica.Clients/Embeddings/EmbeddingClientException.cs), [core embedding contracts](../../../Agentica/Memory/MemoryContracts.cs), and [existing client fixtures](../../../Agentica.Tests/SemanticEmbeddingClientTests.cs). No new embedding call was made.

What the current client supplies:

- One `HttpClient.SendAsync` per invocation, with no internal retry or fallback; the injected handler chain still needs qualification.
- Exact immutable profile identity, detached ordered inputs, bounded serialized requests and dimensional/vector validation.
- Successful batches with profile identity, ordered vectors and nullable raw `usage.prompt_tokens`.
- Classified/sanitized errors and ordinary cancellation propagation.

What it does not currently supply:

- A public prepared-call/admission hook, host attempt identity or deterministic public request hash.
- Retained safe response identifiers, dispatch certainty or usage on exceptions/cancellation.
- A guarantee that usage survives malformed/refused vectors: validation occurs before successful usage extraction.
- Host job/source/query references, a maintenance scheduler, coverage acceptance or durable cumulative budgeting.

**Chosen integration:** Bridge PM11 adapts its embedding-provider interface over this existing client with a controlled admitted/observed `HttpClient`. Before forwarding, that transport binds the exact nonsecret request identity and host profile/purpose/source mapping and persists required reservation. It captures bounded safely observed response IDs/status/usage before downstream vector validation, so a valid usage object plus invalid vector can remain billable failed evidence. Missing usage stays unknown. Required admission failure prevents this request while recovery or independent work continues. Retry-handler/redirect behavior must not conceal another paid attempt.

A plain `try/catch` around `EmbedAsync` is insufficient to restore facts the public exception does not carry. P08's packed fixture demonstrates the transport seam with success, invalid-vector/known-usage, missing-usage, profile/order/reference mismatch, cancellation and no-retry cases. Its host example remains within Agentica; Bridge implements its own durable adapter. Reuse a generic helper only if it genuinely fits. P01–P06's LLM-specific APIs do not automatically cover embedding calls.

This bounded P08 addition costs approximately one to two aggregate agent-hours; the estimate is now 20–33 aggregate hours, with the existing elapsed contingency retained. No paid embedding call, index store, backfill worker or canonical-memory migration is added to this Agentica program.

## Required external cutover gates

Bridge's final packet, pages 2–6, requires:

1. Keep conversational Nyx offline during initial migration and historical OpenAI reindex.
2. Inventory eligible canonical source versions for each required participant and for memory/conversation domains, preserving real authorship and citation lineage.
3. Use exact query/index profiles. Retain old vectors without invoking a Google query encoder. Resume failed items/jobs and reconcile changes without clearing good progress or replenishing budgets.
4. Establish actual valid/decryptable/retrievable target-vector coverage for every required eligible source/version. Terminal processing status, failure counts and aggregate availability are insufficient.
5. Qualify historical scope/perspective/temporal recall and coherently activate accepted routes and both retrieval profiles.
6. Qualify required media routes using shared or dedicated OpenAI adapters. Explicitly report a missing optional operation and preserve unaffected capabilities; no silent Google fallback.
7. Complete required PM16 in the Cloud Worker with a native TypeScript OpenAI adapter after the principal Bridge/reindex milestone. Existing Google-bound Cloud work stays preserved without new Google model calls.
8. Complete actual integrated PM17 loops/recovery and PM18 exact-candidate installed acceptance before returning Nyx online.

These requirements remain external to Agentica engineering; this record neither performs nor authorizes Bridge/Cloud changes. The optional coordinated project/test reset belongs to that coordinator's preservation and effect scope. Nothing in it permits deleting Nyx history, canonical memory, keys or unresolved effect evidence here.

## Local failures, publication and resources

- Failure to persist a required reservation blocks the associated POST. Diagnose/repair, retain prepared inputs and continue independent eligible work.
- UI observer failure retains its observational scope; it cannot acquire journal authority.
- Invalid native continuation triggers compatible reconstruction or a concrete resumable wait, preserving the governing objective.
- A missing generated issue section withholds that issue while its body is repaired; retain known created IDs and continue independent valid publication. Do not publish incomplete issue bodies or abandon the epic because one issue failed.
- The US$5 / six-inference envelope is proposed only for the isolated Agentica P09 proof. It does not fund or configure Nyx production, historical reindex, Cloud migration or their defaults.
- Source-plan budgets and ownership are recorded as context. Actual issue publication, execution goal, paid proof, reindex spending and production effects remain governed by the owner's explicit next steps.

## Revision 1.1 disposition

P01–P09 remain intact. Corrected the operational rollback direction, operation/bundle terminology, A5 contract ownership, media gaps, required external reindex/Cloud gates, local publication recovery and proof-budget scope. The nine Agentica stories still lead to a usable provider-only handoff and complete Agentica-side acceptance. They do not declare the whole Nyx migration complete.
