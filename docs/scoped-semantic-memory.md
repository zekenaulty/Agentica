# Scoped semantic retrieval and linked storage exports

## Ownership

`Agentica.Memory` implements bounded encoding, vector-space validation, ranking,
canonical source validation, exact citations and linked export execution. It has
no database, provider SDK, conversation UI, memory admission policy or second
agent executor. Hosts supply canonical sources, identities, eligibility and
persistence. Historical statements do not become current beliefs or instructions.

`Agentica.Clients.Embeddings` supplies the OpenAI HTTP adapter. Lab supplies the
SQLite model mapping, write provider and optional conversation integration.
Nyx/Bridge remains independently owned and unchanged.

```text
host-bound canonical sources + embedding profile
  -> SemanticMemoryEngine.CreateExportAsync
  -> immutable MemoryIndexExport (references, hashes, ranges, vectors)
  -> LinkedMemoryExporter<TModel>
  -> host IMemoryExportMapper<TModel>
  -> host IMemoryStorageProvider<TModel>.WriteAsync
  -> exact export acknowledgement

bound query + host projection reader
  -> matching vector space -> cosine rank
  -> fresh canonical source reader -> exact source validation
  -> PresentedEvidenceSegment + PresentedEvidenceCitation
  -> existing host/Agentica planning and tool loop
```

## Core contracts

- `MemoryScope` contains namespace, participant, agent and conversation identities.
  Participant/Agent perspective selects the actual corresponding author ID. A
  role label alone cannot identify an actor. A host must authenticate and bind
  these identities before exposing a capability to an untrusted caller.
- `MemorySourceKind.Conversation` and `GovernedMemory` are separate source
  classes. The latter means the **host has already admitted** the source; the
  engine never creates or promotes governed memories.
- `MemoryEmbeddingProfile` pins provider, model, configuration revision,
  dimensions, document projection revision and query projection revision. Any
  change creates another vector space. No implicit model/dimension fallback.
- `MemoryIndexRequest` bounds source and projection work and declares the exact
  sequence/time cut and completeness of the supplied source set. Chunks are exact
  UTF-16 ranges with surrogate-safe boundaries. Exports retain no source text.
- `MemorySearchRequest` binds the same scope, perspective, source class, profile
  and cut. Reader filtering happens before candidate enumeration/LIMIT. Core
  additionally rejects foreign, mixed-profile or malformed references before
  cosine ranking. Ties use stable projection identity.
- Every returned quote resolves through `IMemorySourceReader` to a currently
  eligible source with matching actor, revision, full content hash, sequence,
  timestamp, range and slice hash. Citations use **absolute source UTF-16
  offsets**, end-exclusive. They are evidence references, not truth/attention
  claims or live instructions.
- `MemoryCoverage` is relative to the declared bounded source/projection cut.
  Partial scans, stale/revoked sources and budget omissions remain explicit.
  Candidate counts cover valid in-scope/profile projections; inspected counts
  cover attempted canonical reads. Foreign candidates contribute neither count.
- Public constructors copy retained collections. Profile/projection/export
  identities are recomputed from their declared inputs. A storage mapper must
  validate persisted identity fields on reconstruction, as Lab's mapper does.
- `LinkedMemoryExporter<TModel>` performs mapping and the write call, then checks
  that the acknowledgement links to the exact export. It never retries an
  uncertain write. Host writes must be atomic and reconcile repeated export IDs.

## OpenAI adapter

`OpenAiEmbeddingClient` uses one HTTP request per call to `/v1/embeddings`.
Its options pin the complete profile and bind the credential/endpoint. It sends
the exact supplied document/query strings, validates response model, indices,
count, dimensions, finite nonzero vectors and optional raw input usage, then
returns vectors in input order. Text, batch, wire and response limits are
explicit. Cancellation propagates; there is no hidden retry or error-body echo.
Provider usage remains unknown when the provider does not return it.

The adapter is provider-specific; the core ports are provider-neutral. Additional
embedding providers implement the same contract and need their own profile and
transport qualification. This does not assert embedding parity for other vendors.

## Lab usage

Configure → Thread history recall → **Use semantic conversation retrieval**,
then save. Both thread recall and semantic retrieval default off in new chats.
Auto selects historical evidence before generation. Agent-directed Harness mode
uses the existing `conversation.search` and `conversation.read` capabilities,
with the normal planner → tool → observation → refinement → response loop.
Manual pins remain exact local reads regardless of the semantic switch.

Lab binds namespace `agentica-lab`, participant `local-owner`, agent
`lab-assistant` and the selected conversation. These are stable single-user Lab
identities, independent of the selected generation model. This integration never
searches another conversation or enables personal/governed memory. Only completed
user/assistant text at the frozen pre-turn cut is eligible; reasoning, tool
output, traces and the active turn are excluded.

The initial profile is OpenAI `text-embedding-3-small`, 1536 dimensions. It uses
the existing service-side OpenAI credential. After durable turn admission, the
first semantic search scans at most 512 canonical headers and encodes at most 32
missing sources / 64,000 characters. Whole oversized sources are omitted rather
than silently shortened. Existing complete projections skip document encoding.
At most four embedding dispatches are allowed per bound turn, including query
encoding. The two perspectives share an identical cached query vector. This
allowance is separate from generation calls and shown in embedding trace events.
The existing turn deadline and historical quotation/call budgets still apply.
Semantic search returns one bounded result window (offset zero); truncated
coverage is explicit and no continuation cursor is fabricated. Local lexical
inspection retains its separate paging contract.

Provider calls are visible as `recall.embedding.started/completed/indeterminate`;
saved exports as `recall.semantic.export`. A failed/uncertain embedding or write
latches semantic retrieval unavailable for that bound turn. There is no implicit
retry or lexical fallback. A later deliberately admitted turn is new work and
can try again. Local diagnostics remain lexical and are labeled accordingly.

**Check context** defers semantic lookup and reports that limitation. Admission
preflight checks the mandatory input/floor without historical provider calls.
Neither context preview nor opening the local inspector encodes history. Recall
Off keeps the original request path and makes no source or embedding call.

The existing collapsed **Thread recall evidence manifest** inspector links exact
original ranges. Semantic entries additionally retain export/projection/profile
IDs, actual author, perspective and similarity. These citations establish source
attribution. They do not automatically annotate every assertion in a generated
answer or prove the model used the source.

## Persistence and migration

`SemanticMemoryStorageMapper` translates core exports into explicit versioned Lab
records. `SemanticMemoryStorageProvider` writes those models transactionally to
`chat/semantic-memory.sqlite3` (schema 1), alongside the existing canonical chat
database. No canonical schema migration or transcript rewrite is required.
Exact repeated exports are idempotent; altered identities/models are rejected.
Source replacement retires earlier links, and late old writes cannot revive them.
Readers bind scope/profile/cut before LIMIT and re-resolve canonical eligibility.

The projection store contains vectors, source references, actor identifiers,
hashes, ranges and export metadata, **not canonical text or credentials**. It is
still sensitive derived data. Hosts own retention, deletion, encryption and
purging; canonical unavailability prevents citation but is not a vector-erasure
receipt. The core provides no universal retention or participant memory schema.

## Qualification

Core, adapter and Lab tests use deterministic embedding/HTTP seams and actual
SQLite stores. The host proof exercises the existing planner/search/read/refine/
answer loop with an older source beyond the recent floor, then verifies durable
citations, actor perspectives, isolation, nonzero Unicode offsets, reopen/reuse,
preview/Off behavior and uncertain-provider handling. Synthetic vector similarity
does not measure real semantic quality. Live provider access, cost and retrieval
quality are separate qualifications.

Comparative source: Raistlin Bridge snapshot
`187b203c45d316e8f166dcb1cbd239e5a564a321`, semantic profile/search and citation
activation services. Its scope/profile/fresh-source discipline informed these
contracts; companion ontology and storage were not copied into core.
