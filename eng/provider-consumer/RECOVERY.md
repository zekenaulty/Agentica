# P08: packed provider-only durable host

Run `eng/Verify-ProviderConsumer.ps1` with the repository's exact SDK **10.0.302**.
The entrypoint performs locked Release library restore/build/pack, copies only its
named consumer sources and global.json into a unique ignored artifact directory,
restores PackageReferences in a fresh cache, and compares restored nupkg SHA-256
bytes with this run's fresh packages. Google.GenAI **1.13.0** remains an explicit
coexisting dependency; no Google request is made. No ProjectReference, AgenticaRunner,
Lab service, scheduler, index or maintenance worker is constructed.

## The actual loop

RecoveryHost reads a bounded objective and authoritative pending items, compiles
an exact streaming request with LlmCallPreparation and its configuration report,
and requires host ILlmCallAdmission before transport. Admission durably reserves
the attempt, rechecking live authorization, eligibility, revocation, operation,
phase, captured route/bundle and cumulative budget. The deterministic model fixture
chooses a proposed bound accept-item action from its observation. The host validates
the supplied strict schema, capability, scope, version and current policy before
applying an effect. It persists receipts, accepted evidence, obligations, provider
facts and reservations, then exports the actual private native capsule explicitly.

The parent launches the **built packed consumer in a separate OS process**. The
child reopens host state, imports the capsule, prepares another model call, applies
another proposed action, calls again, and accepts completion only when canonical
items and distinct effect receipts establish the objective. Repeated delivery and
reopen perform no duplicate effect or inference. The host owns this workflow.

Four complete loops reopen matching, unavailable, malformed and model-incompatible
native state. Matching private history reaches the actual wire. Rejected capsules
cause reconstruction from accepted evidence, with original author/source/revision
explicit in a host-authored data envelope. Earlier assistant statements remain
agent evidence, never participant speech or trusted instructions. This does not
claim equivalent private reasoning or bypass OpenAI's native-history guard.

## Recovery and budget custody

The 20 P08 scenarios cover the four fresh-process loops; failed journal write;
lost response; refusal/HTTP failure; cancellation raced with provider success;
host schema failure after usage; missing usage; captured route/profile changes;
live policy; wrong bindings/duplicate attempt; representative Bridge contracts;
shared search; capsule authentication; false completion/effect proposals; durable
embedding reopen; an explicit retained-Google-to-OpenAI successor transition; and
the returned-before-Accept process crash window.

A lost original parks on fresh reopen. It receives no automatic retry. A recorded
host decision permits a distinct replacement attempt; accepted successor evidence
can continue the original objective while its unknown original and reservation
remain unchanged. Cumulative attempts and reservations survive reopen. A separate
transition imports synthetic historical Google uncertainty, retains its exact
operation/route/bundle/reservation, and finishes through OpenAI with **zero new
Google inference or embedding calls**.

Every unaccepted paid outcome requires reconciliation, including delivered-but-
unaccepted, refusal/HTTP failure, cancellation and host parsing rejection. A durable
returned flag without the proposal/native bytes is not enough to continue. The
returned-before-Accept regression has one child process deliver a response and exit
without acceptance, then a second child reopen and park it. No new inference or
older-capsule replay occurs. An explicit successor decision for each unresolved
original permits accepted-evidence reconstruction while retaining original facts
and reservations. Known failures likewise park; they do not imply retry authority.

Provider status, client delivery and host acceptance are separate. OpenAI's output
counter includes reasoning. Decimal **synthetic fixture rates** charge generated
output once; these are not current provider prices. Missing usage stays unknown,
and unknown reservations are retained. The HTTP error body is not parsed as usage;
its observed status/request identity survive with unknown usage. Measured stream
usage survives refusal, cancellation and host parsing failure.

The complete handler chain terminates in one counted offline fixture. It has no
network, redirect or retry layer; each attempt has one physical send. The fixture
checks actual serialized schema, authored input envelopes, model, stream=true,
store=false, absence of previous_response_id and private native replay. Production
hosts must independently qualify their handler chain.

## Routes, embeddings and storage

Existing work retains its captured operation-route bundle through settings edits
and restart; new admissions use the new bundle. Memory and conversation embedding
profiles are independent and unaffected by unrelated text-route edits.

All 15 original A5 scenarios remain. The new embedding case adds a durable host
journal, reopen/duplicate preservation, separate document/query profiles and exact
retained-profile/source-revision/eligibility rejection before transport. Those live
checks execute inside the actual embedding journal reservation reached by the
EmbeddingAttempt HTTP path; the tests mutate live policy after capturing the
request, prove zero physical POSTs and no reservation on rejection, and retain a
positive eligible-source case. It consumes
the existing OpenAiEmbeddingClient and builds no index, backfill or scheduler.
Bridge PM11 still owns its actual adapter/mapping/paid custody; PM12–PM14 own
canonical eligibility, maintenance and exact source/version coverage. New LLM
contracts do not implicitly extend IMemoryEmbeddingProvider.

The file journal flushes and atomically replaces state for **one synthetic owner**.
This does not qualify a concurrent database. The host uses AES-GCM with episode,
scope and bundle associated data, and tests ciphertext/scope rejection. Its fixture
key sits beside ignored state so child processes can reopen it: this is a test
arrangement, not production key management. Native exports/keys are never logged,
packaged or copied as consumer source. Only named source files are copied. CLI
home is run-local; process environment is restored and global SDK state is untouched.

## Bridge source and semantic-author boundary

BridgeContractFixtures was reviewed read-only against
**187b203c45d316e8f166dcb1cbd239e5a564a321**:

| Fixture | Source | Exact boundary / drift |
| --- | --- | --- |
| Foreground | Provider/ForegroundResponseSchemaCompiler.cs | Preserves decision/candidate/action, closed objects, required fields and capability alternatives. Synthetic reply candidate and accept-item input replace dynamic domain schemas. |
| Memory/evaluation | Provider/Contracts/MemoryOutputContract.cs and Memory/MemoryObservationService.Contracts.cs | Preserves root/action field names and nullable target/abstention; synthetic enum strings and no-action result. Full enum/length/rule projections and canonical memory authority stay with Bridge. |
| Document | GoogleDocuments/GoogleDocumentGeminiModel.cs and DocumentContexts/DocumentSubagentEngine.cs | Preserves answer, exact fragment IDs and quote objects. Declared shape becomes a supplied strict schema here; historical Google code used JSON mode plus host validation. |

Paths are under apps/companion. These are transport projections, not imports of
Bridge's entire dynamic compiler/parser or its current product state. Source authors
stay explicit in host evidence. Host observations have their own Host semantic
author, stable source ID and host-state revision independently of the provider's
User envelope role. Fallback wire assertions preserve that distinction alongside
participant User and agent Assistant evidence. Bridge must qualify its actual contracts at its
own current reviewed cut, including future drift.

The dedicated search case uses OpenAiWebSearch.CreateRequest, required native
search, external-untrusted sources/citations, unchanged Markdown offsets, JSON
reload and actual sensitive native export/import. A citation does not become host
truth or tool authority.

## Evidence boundary

This proves packed API compatibility, deterministic protocol wiring, single-owner
durable host restart/recovery and the configured offline send boundary. It does
not certify model judgement, concurrent storage, live provider/account support,
installed Nyx/Bridge, canonical memory migration or deployment. P08 closure still
requires the root's dependency merge and CI gates. No paid provider call or real
credential is obtained.
