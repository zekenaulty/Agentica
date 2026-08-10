# Receipted Context and Memory Management System

- **Concept baseline:** v0.2  
- **Project:** Nyx companion for Raistlin  
- **Status:** Refined architecture and implementation plan grounded in the current repository and Agentica  
- **Source report: receipted-context-memory-system-v0.1.md (historical local source artifact; local path intentionally omitted from portable downstream instructions)**  
- **Repository snapshot:** Raistlin Bridge working tree on 2026-08-03, HEAD `245a0ee274eab286cf682a0597a0e05237d2be7c`  
- **Agentica refere**  
- **PORTABILITY / CLOUD EXECUTION NOTE**  
-   
- **This document records an implementation-grounded design snapshot, but it must not be treated as proof that a downstream cloud agent can access the repository, the historical local source report, Google Drive, or any path/symbol named below. Repository paths, symbols, commit hashes, migration numbers, and implementation observations are historical/source-reported evidence from the stated snapshots until independently verified in the active execution environment. If a Codex Cloud session has no checked-out repository or filesystem access, use this document as architecture context only: do not clone, do not follow local paths, do not stop on missing sources, do not claim current code state, and label repository-dependent questions UNKNOWN. Implementation instructions in later sections become a repository-verification/implementation plan for a future repository-enabled session rather than executable work for the current cloud session.**  
-   
- **nce snapshot:** `zekenaulty/Agentica` commit `3ff02d4212b7daec822ca6e094908559d72f0f3d`

## 1\. Decision summary

The central rule from v0.1 remains correct:

> Memory is durable, evidence-bearing state. Context is a compiled, temporary projection of permitted state for one inference.

The repository inventory changes the starting point. Raistlin Bridge already has a substantial governed-memory vertical slice:

- encrypted, source-linked proposals;  
- versioned claims and engrams;  
- durable interpretive frames;  
- correction and forgetting lineages;  
- tombstones;  
- participant and conversation scoping;  
- bounded cue/lexical activation;  
- persona activation;  
- single and staged cognition;  
- generation, evaluation, delivery, memory, and persona receipts.

Therefore v0.2 does **not** propose another memory schema or a rewrite of the existing memory ledger. It introduces a receipted, stage-aware context compiler around the current behavior.

The first engineering objective is:

> For every logical provider call in a turn, persist a pre-dispatch activation receipt containing the complete materialized candidate decision set under recorded query bounds, an immutable ordered inference-context frame, verbatim-field and canonical-logical-request hashes, a retention-limited encrypted copy of the application request fields, and linkage to every prepared provider attempt and known or unknown outcome—without changing current selection behavior or `ProviderCallRequest` fields.

The principal architecture decisions are:

1. Preserve `MemoryClaimRecord`, `MemoryEngramRecord`, and `InterpretiveFrameRecord` as the durable memory domain.  
2. Name the new per-inference structure `InferenceContextFrame`. `InterpretiveFrame` already has a canonical implemented meaning and must not be overloaded.  
3. Separate considered candidates from selected rendered cells.  
4. Compile one frame for each logical provider call, not one vague frame per turn.  
5. Keep relational tables as authoritative domain state.  
6. Add an append-only semantic lifecycle ledger for ordering, correlation, audit, and timeline projections; do not convert the application to full event sourcing.  
7. Treat provider roles and trust boundaries as authority controls. Textual layer order alone is not a security boundary.  
8. Preserve exact compiler and payload replay only while retained source content and keys remain available. Forgetting and deletion take precedence over historical byte reconstruction.  
9. Borrow Agentica's contract discipline and proof boundary, but add no Agentica runtime dependency to the memory or prompt path.  
10. Keep Raistlin-channel memory derivation disabled until the compiler and receipt baseline pass shadow and adversarial tests.

## 2\. Scope

### In scope

- current local-tester participant and conversation namespaces;  
- existing messages, claims, engrams, interpretive frames, tombstones, persona snapshots, and cognition artifacts;  
- every current conversation-generation provider stage:  
  - orientation;  
  - generation;  
  - search generation;  
  - evaluation;  
  - revision;  
  - compression;  
- candidate collection and complete decision accounting;  
- provider-role and data-trust boundaries;  
- deterministic projection, canonical serialization, and hashing;  
- context budgets and overflow behavior;  
- activation, frame, request, provider-call, output, delivery, and observation linkage;  
- an activation/frame inspector;  
- compiler replay and retained application-request reconstruction;  
- semantic lifecycle events and a timeline projection.

Synthetic persona-quality evaluation remains an isolated fixture workflow. It may reuse the compiler infrastructure later, but it is not part of the first conversation-context slice.

### Explicitly deferred

- an Agentica package/runtime dependency;  
- autonomous tools or background initiative;  
- automatic memory acceptance;  
- cross-participant memory sharing;  
- relationship-state implementation;  
- a vector database or embeddings as canonical memory;  
- arbitrary user-authored context formulas;  
- unrestricted persona mutation;  
- model-authored facts becoming identity or durable truth;  
- replacing source evidence with summaries;  
- full event sourcing;  
- cryptographic signatures intended to defeat a malicious host administrator.

## 3\. Current implementation baseline

### 3.1 Existing data and services

| Area | Implemented baseline | Primary code |
| :---- | :---- | :---- |
| Chronicle | Encrypted participant and companion messages with typed source classes | [Schema.cs](http://../apps/companion/Storage/Schema.cs), [CompanionStore.cs](http://../apps/companion/Storage/CompanionStore.cs) |
| Memory domain | Proposals, claims, engrams, evidence, interpretive frames, reviews, tombstones | [MemoryModels.cs](http://../apps/companion/Domain/MemoryModels.cs), [CompanionStore.Memory.cs](http://../apps/companion/Storage/CompanionStore.Memory.cs) |
| Observation | Durable leased jobs; exact `Remember this: ...` extraction; ordinary-turn abstention | [MemoryObservationService.cs](http://../apps/companion/Memory/MemoryObservationService.cs) |
| Governance | Manual create, accept, restrict, reject, correct, frame review, and forget | [MemoryGovernanceService.cs](http://../apps/companion/Memory/MemoryGovernanceService.cs), [LocalApi.cs](http://../apps/companion/Local/LocalApi.cs) |
| Memory activation | Authorization, scoped SQL filtering, cue/lexical scoring, item and character limits | [MemoryActivationService.cs](http://../apps/companion/Memory/MemoryActivationService.cs), [CompanionStore.MemoryRuntime.cs](http://../apps/companion/Storage/CompanionStore.MemoryRuntime.cs) |
| Persona activation | Approved seed/snapshot plus bounded facet projection and receipts | [PersonaActivationService.cs](http://../apps/companion/Persona/PersonaActivationService.cs) |
| Shared turn context | Recent-message window plus memory and persona activation packets | [ContextAssembler.cs](http://../apps/companion/Conversation/ContextAssembler.cs) |
| Stage requests | Stage-specific system instructions and JSON input payloads | [ProviderPromptCompiler.cs](http://../apps/companion/Provider/ProviderPromptCompiler.cs) |
| Cognition | Single/staged selection, typed orientation, validation, repair, evaluation, revision, compression | [CompanionGenerationPipeline.cs](http://../apps/companion/Conversation/CompanionGenerationPipeline.cs) |
| Run receipts | Provider usage, stage receipts, memory/persona activation, generation result | [CompanionStore.cs](http://../apps/companion/Storage/CompanionStore.cs) |
| Delivery | Authenticated bridge ledger, route epochs, outbox, attempts, accepted/failed outcomes | [CompanionStore.Bridge.cs](http://../apps/companion/Storage/CompanionStore.Bridge.cs) |
| Inspector | Memory ledger, proposal/frame governance, latest selected influences | [MemoryPanel.tsx](http://../apps/companion/WebClient/src/MemoryPanel.tsx) |

### 3.2 Current turn flow

flowchart LR

    I\["Inbound request validated"\] \--\> P\["Persona activation"\]

    P \--\> R\["Inbound message and generation run committed"\]

    R \--\> M\["Memory candidate query and activation"\]

    R \--\> C\["ContextPackage"\]

    M \--\> C

    C \--\> S{"Single or staged"}

    S \--\>|"single"| G\["Generation request"\]

    S \--\>|"staged"| O\["Orientation request"\]

    O \--\> V\["Validated TurnOrientation"\]

    V \--\> G

    G \--\> E\["Evaluation"\]

    E \--\> X{"Pass / revise / block"}

    X \--\>|"revise"| G2\["Revision request"\]

    X \--\>|"pass"| D\["Response and receipts committed"\]

    G2 \--\> D

    D \--\> J\["Authorized local observation job"\]

    J \--\> Q\["Proposal or abstention"\]

### 3.3 Material gaps in current context receipts

The current system records counts and selected memory influences, but it does not yet record:

- candidates discarded for no relevance;  
- records displaced by item or character limits;  
- distinct suppression reason codes;  
- frame IDs for confirmed interpretive text included with an engram;  
- recent messages considered but omitted by the context window;  
- complete persona suppression decisions in a shared context surface;  
- stage-specific ordered layers;  
- exact component text/version used by each stage;  
- pre-dispatch token estimates;  
- a canonical hash of `ProviderCallRequest.SystemInstruction` plus `ProviderCallRequest.Input` and generation options;  
- an immutable request envelope from which the provider request can be reconstructed;  
- a context receipt when compilation or generation fails before normal completion.

The current `SuppressedCount` is arithmetic: `CandidateCount - SelectedCount`. It combines no-match, item-limit, and character-budget outcomes without preserving which record received which decision.

## 4\. Resolved inventory questions

The questions left open in v0.1 are resolved as follows.

| v0.1 question | Repository answer | v0.2 consequence |
| :---- | :---- | :---- |
| What does `Frames` mean? | The Memory UI counts candidate `InterpretiveFrameRecord` rows. They are durable interpretations attached to engrams. | Preserve `InterpretiveFrame`. Use `InferenceContextFrame` for compiled call context. |
| Is `Engram` canonical? | Yes. `MemoryEngramRecord` and `memory_engrams` are implemented domain records, not a label over `MemoryItem`. | Keep existing terminology. |
| How do scopes compose? | `participant` and `conversation` are mutually exclusive persisted values. Participant scope applies across that participant; conversation scope applies only to the matching conversation. | Do not invent composable scope graphs in the first compiler slice. |
| Which proposal fields persist? | All current proposal fields persist and are validated: source IDs, type, proposition, scope, sensitivity, authority, confidence, engram fields, renderings, optional frame, supersession, actor, and transform version. | Reuse the ledger; do not duplicate proposal storage. |
| What is a candidate interpretation? | An inert `InterpretiveFrame` created with an accepted proposal. It cannot influence generation until confirmed; disputed/rejected frames stay non-influential. | Treat it as a governed secondary lens, not as an ordinary low-confidence fact. |
| What are cues? | Encrypted, operator-visible retrieval features used for exact phrase and token overlap. | Preserve them as retrieval metadata; receipt whether a cue matched. |
| What is compact reconstruction? | Durable encrypted engram text, visible to the operator and currently projected directly into the provider memory packet. | In v0.2 it becomes source material for a versioned deterministic projector, not an implicitly trusted prompt fragment. |
| Where is prompt assembly? | `ContextAssembler` selects recent messages; `ProviderPromptCompiler` creates stage-specific system instructions and JSON inputs. Version constants exist, but exact components and request bytes are not persisted. | Introduce a stage compiler and canonical request receipt around these two boundaries. |
| Are token counts available? | Actual provider input/output/thought token usage is recorded after calls. Context admission currently uses characters; no exact preflight tokenizer is implemented. | Keep character parity first, add a versioned estimator, and label estimates honestly. |
| Can `audit_events` carry the event spine? | It is a generic mutable table without a monotonic sequence, correlation/causation, hash chain, or append-only triggers. | Leave it as legacy audit and add a purpose-built append-only lifecycle ledger. |
| Are state domains separate? | Participant, conversation, memory, persona, authorization, and bridge state are distinct. Relationship state is not implemented; temporary local session state is not a durable memory domain. | Do not create placeholder relationship records in the compiler slice. |
| What starts observation? | Successful non-bridge local chat with `memory_derivation` authorization queues a durable job atomically with response completion. Search simulation and all bridge work are excluded. | Formalize surface-specific delivery eligibility before enabling any bridge observer. |

## 5\. Non-negotiable invariants

### Evidence and epistemic state

1. Raw source messages and typed external evidence outrank derived summaries.  
2. A summary, compact rendering, orientation, working-context frame, or model report is a projection; it is never silently promoted to primary evidence.  
3. Facts, hypotheses, interpretations, preferences, active threads, corrections, and suppressions remain distinct record types or explicit epistemic classes.  
4. Every selected dynamic cell has resolvable source references, intended effect, selection reason, trust class, authority class, and measured cost.  
5. Every candidate admitted into the in-scope candidate pool has exactly one terminal decision.  
6. Assistant/model output cannot be the sole evidence for a user fact, relationship claim, or identity change.

### Capability and lifecycle separation

7. The response generator cannot directly create, edit, accept, restrict, supersede, delete, or activate memory, persona, or relationship state.  
8. Post-turn observers emit typed observations or proposals, not persistence commands.  
9. Observation eligibility is established by surface-specific delivery policy and an idempotent delivery/commit receipt.  
10. A model-authored orientation, evaluation, summary, or report is untrusted input to deterministic validation.

### Isolation and authority

11. Namespace and participant boundaries are enforced in storage queries before content decryption.  
12. Cross-namespace records are outside a turn's candidate universe and must not appear as candidate IDs or leak through exclusion counts.  
13. Conversation scope, sensitivity, status, validity, supersession, and tombstones are applied before relevance scoring.  
14. Provider system instructions, application-owned policy, and untrusted data are separate roles/boundaries. Lower-trust text cannot gain authority by appearing earlier or scoring higher.  
15. Token pressure cannot remove mandatory trusted contracts to admit optional memory, persona, artifacts, or history.

### Determinism, receipts, and privacy

16. Given the same context base snapshot, call-input manifest, `asOf` value, policy/component/projector/serializer/token-estimator versions, and stage request, the compiler produces the same candidate decisions and frame hash.  
17. Every logical provider call links to exactly one compiled request package; provider retries link to the same package unless the logical request changes.  
18. At commit time, receipt references must resolve within the same run/turn scope. Historical references later resolve as `live`, `tombstoned`, or `unavailable_after_deletion`; an unexplained missing or foreign-scope reference is a ghost reference and fails validation.  
19. Canonical plaintext hashes are computed before randomized encryption. Ciphertext is storage, not canonical identity.  
20. Forgetting, legal deletion, and cryptographic erasure override exact historical application-request reconstruction. Content-free structural receipts may remain under policy; unkeyed fingerprints of short or guessable personal text may not.  
21. Narrative reports and UI projections are not proof. Persisted domain records, canonical receipts, delivery evidence, and validated events are proof.

## 6\. Agentica crosswalk

Agentica is a key design resource, but it is not a memory framework. Its own runtime contract assigns persistence, domain projections, and long-lived memory to the host. The useful transfer is contract discipline.

| Agentica mechanism | Status at `3ff02d4` | Source | v0.2 treatment |
| :---- | :---- | :---- | :---- |
| Typed `PlanningFrame` with version, payload, evidence refs, and tool-surface linkage | Implemented | [PlanningFrame.cs](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/Agentica/Planning/PlanningFrame.cs) | **Adopt and strengthen:** add authority/trust, budget, cell hashes, stage identity, and parent links. |
| Planner requests can count-limit recent observations/receipts and pass them to an optional projector; a bounded `GoalSpine` frame is also appended | Implemented mechanism; count limits/projector are optional | [PlanningContextOptions.cs](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/Agentica/Planning/PlanningContextOptions.cs), [PlanningRequestFactory.cs](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/Agentica/Execution/PlanningRequestFactory.cs) | **Adopt with stricter defaults:** compile bounded context instead of dumping an unbounded ledger. |
| `GoalSpine` as compact, receipt-linked continuity that is explicitly not proof | Code exists; Run Continuity subsystem incubating (85%) | [GoalSpine.cs](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/Agentica/Continuity/GoalSpine.cs), [product status](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/docs/Agentica.ProductStatus.md) | **Adapt later:** use for active-thread/task continuity, never as canonical user memory. |
| Typed working context separates facts, questions, hypotheses, blockers, and impacts | Implemented in experimental/incubating orchestration | [WorkContext.cs](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/Agentica/Orchestration/Context/WorkContext.cs), [compiler](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/Agentica/Orchestration/Context/DeterministicWorkContextCompiler.cs) | **Adopt conceptually:** preserve epistemic type in task/context artifacts. |
| Planner proposes; Agentica validates and authorizes; tools execute; normalized receipts, observations, artifacts, host checks, terminal state, and completion evidence establish scoped truth | Implemented contractual boundary | [Agentica runtime contracts](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/docs/Agentica.RuntimeContracts.md#planner) | **Adopt:** generator and observer outputs are never self-authorizing. |
| Semantic events carry typed context, evidence, diagnostics, and monotonic per-run-attempt order | Implemented Event Intent mechanism; ledger is in-memory | [ExecutionEvent.cs](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/Agentica/Events/ExecutionEvent.cs), [sequence contract](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/docs/CodexGoal.Agentica.EventIntentSurface.md#sequence-numbers) | **Adapt:** use a durable SQLite sequence and transactional append. |
| Event sinks are best-effort observers and cannot own business effects | Implemented contractual boundary | [Event delivery contract](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/docs/Agentica.RuntimeContracts.md#event-delivery) | **Adopt:** UI/analytics projections never become competing state machines. |
| Agentica-owned IDs cited by events resolve within the same outcome envelope or carry diagnostics | Implemented Event Intent rule | [Ghost Data Rule](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/docs/CodexGoal.Agentica.EventIntentSurface.md#ghost-data-rule) | **Strengthen as a host extension:** validate the full context-to-delivery reference graph at commit time. |
| Deterministic active capability-surface compiler and surface receipt | Generic engine incubating (70%); host-specific Maze/Chess prototypes exist | [Capability Surface Engine](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/docs/CodexGoal.Agentica.CockpitSurfaceEngine.md), [product status](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/docs/Agentica.ProductStatus.md) | **Adapt as a design target, not an implemented generic primitive:** receipt what was visible, hidden, mandatory, optional, or blocked. |
| Agentica core does not own durable host memory | Implemented contractual boundary | [Core boundary](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/docs/Agentica.RuntimeContracts.md#core-boundary) | **Preserve:** no Agentica memory dependency. |

Important qualification: Agentica's current planning frames do not provide context-layer authority, token accounting, complete candidate suppression receipts, canonical frame hashes, previous-frame linkage, durable persistence, or replay. Those are host-side extensions in this design, not claims about existing Agentica behavior.

Under the [Agentica Source-Available Conceptual Reference License](https://github.com/zekenaulty/Agentica/blob/3ff02d4212b7daec822ca6e094908559d72f0f3d/LICENSE), conceptual discussion and reference are permitted; copying, adapting, or incorporating protected implementation expression requires prior written permission. This plan cites architectural concepts and imports no Agentica code.

## 7\. Canonical terminology

### Durable memory domain

- **Memory proposal:** governed candidate for durable memory.  
- **Claim:** versioned proposition with provenance, scope, sensitivity, confidence, validity, and status.  
- **Engram:** versioned retrieval/reconstruction unit linked to one or more claims.  
- **Interpretive frame:** governed, durable secondary interpretation attached to an engram.  
- **Tombstone:** content-free suppression record that prevents stale re-creation.  
- **Active thread:** future lifecycle-bearing continuity record; currently only an implemented claim type.

### Active context domain

- **Context base snapshot:** immutable, transactionally consistent identifier/version/hash manifest for turn-start domain state.  
- **Context call-input manifest:** immutable exact manifest of base-snapshot records plus later derived artifacts used by one logical provider call.  
- **Context candidate:** an in-scope record or component considered for a specific layer and logical provider call.  
- **Context candidate decision:** the terminal eligibility/selection decision for one candidate.  
- **Context cell:** a selected, deterministically rendered, source-linked unit placed in an inference frame.  
- **Context layer:** an ordered authority/trust partition within a frame.  
- **Context profile:** versioned stage policy declaring permitted layers, order, projectors, budgets, and overflow behavior.  
- **Context activation:** one complete compile operation for one logical provider call.  
- **Inference context frame:** immutable structured snapshot of the selected ordered layers and cells.  
- **Provider request package:** the exact application-level system instruction, serialized input, generation options, provider/model/configuration identity, and hashes prepared for one logical call. It is not a claim about raw HTTP bytes.  
- **Context activation receipt:** immutable activation, decision set, frame manifest, and request-package receipt fixed before dispatch.  
- **Context lifecycle view:** time-varying assembled view joining immutable activation proof to later provider, output, delivery, and observation events.  
- **Context timeline:** ordered lifecycle projection across turns and stages.

### Terms deliberately kept separate

| Do not conflate | Reason |
| :---- | :---- |
| `InterpretiveFrame` and `InferenceContextFrame` | One is durable memory interpretation; the other is temporary compiled call context. |
| Candidate and cell | Suppressed/deferred candidates do not become rendered cells. |
| Frame and provider request | A frame is provider-neutral structured context; a request package includes provider/stage serialization and generation settings. |
| Receipt and UI view | A receipt is immutable evidence; the UI is a projection over it. |
| Compiler replay and inference replay | Recompiling a request can be deterministic; reproducing a provider output generally is not. |
| Event ledger and domain state | Events order lifecycle truth; relational tables remain authoritative content/state. |

## 8\. Refined architecture

flowchart TB

    subgraph INGEST\["Turn ingestion and source state"\]

        IM\["Immutable inbound message"\]

        RUN\["Generation run"\]

        SNAP\["Context base snapshot\<br/\>consistent IDs \+ versions \+ hashes"\]

        MAN\["Per-call input manifest\<br/\>base state \+ derived artifacts"\]

        IM \--\> RUN \--\> SNAP

        SNAP \--\> MAN

    end

    subgraph STORES\["Authoritative relational domain stores"\]

        CHR\["Conversation chronicle"\]

        MEM\["Claims \+ engrams \+ evidence"\]

        IFR\["Interpretive frames"\]

        TMB\["Tombstones \+ supersession"\]

        PER\["Approved persona/profile"\]

        ART\["Typed task/search artifacts"\]

        CMP\["Versioned authored components"\]

    end

    subgraph COMPILE\["Stage-aware context compiler"\]

        PROF\["Context profile"\]

        COL\["Scoped candidate collector"\]

        DEC\["Hard policy \+ terminal decisions"\]

        BUD\["Mandatory resolver \+ budgets"\]

        PRJ\["Deterministic projectors"\]

        F\["InferenceContextFrame"\]

        PKG\["ProviderRequestPackage"\]

        PROF \--\> COL \--\> DEC \--\> BUD \--\> PRJ \--\> F \--\> PKG

    end

    MAN \--\> COL

    CHR \--\> COL

    MEM \--\> COL

    IFR \--\> COL

    TMB \--\> DEC

    PER \--\> COL

    ART \--\> COL

    CMP \--\> COL

    subgraph INFER\["Inference pipeline"\]

        CALL\["Provider attempt(s)"\]

        VAL\["Typed parsing \+ validation"\]

        OUT\["Candidate / evaluation / response"\]

        CALL \--\> VAL \--\> OUT

    end

    PKG \--\> CALL

    subgraph PROOF\["Persistent proof"\]

        ACT\["Context activation \+ decisions"\]

        FRAME\["Encrypted frame \+ hashes"\]

        REQ\["Encrypted application request fields"\]

        PCR\["Provider call receipts"\]

        DEL\["Output \+ delivery receipts"\]

        EVT\["Append-only lifecycle events"\]

    end

    DEC \-.-\> ACT

    F \-.-\> FRAME

    PKG \-.-\> REQ

    CALL \-.-\> PCR

    OUT \-.-\> DEL

    ACT \-.-\> EVT

    FRAME \-.-\> EVT

    REQ \-.-\> EVT

    PCR \-.-\> EVT

    DEL \-.-\> EVT

    subgraph OBS\["Governed post-turn observation"\]

        ELIG\["Delivery/commit eligibility"\]

        EX\["Typed observer"\]

        GV\["Deterministic validator/governor"\]

        PQ\["Proposal queue"\]

        REV\["Human/policy review"\]

        ELIG \--\> EX \--\> GV \--\> PQ \--\> REV

    end

    DEL \--\> ELIG

    REV \--\> MEM

    REV \--\> IFR

    REV \--\> TMB

### 8.1 Three compilation levels

The system needs a consistent turn-start base, an exact per-call manifest, and per-call frames.

#### Turn-level `ContextBaseSnapshot`

Captures:

- namespace, participant, conversation, turn, and input-message IDs;  
- `asOf` timestamp;  
- source database/schema version;  
- active profile/snapshot ID;  
- memory/persona authorization versions;  
- policy-manifest hash;  
- exact IDs, versions, and content/structural hashes for base records admitted to the snapshot;  
- recent-message upper sequence;  
- tombstone/supersession versions;  
- component versions.

The base snapshot must be read in one SQLite read transaction or assembled optimistically and revalidated before pre-dispatch commit. A timestamp or `MAX(updated_at)` value alone is not a snapshot.

#### Per-call `ContextCallInputManifest`

Later calls depend on artifacts that do not exist at turn start: orientation output, evaluator output, prior candidate text, search configuration, or compression input. Each logical call therefore records the exact:

- base-snapshot record refs used by that call;  
- derived-artifact IDs, versions, and hashes;  
- stage/profile/component/projector versions;  
- verbatim current inputs and generation-option manifest;  
- reference-resolution state at commit time.

This manifest is the replay input. The base snapshot establishes consistent turn-start state; the call manifest adds later derived state without pretending it existed earlier.

#### Per-call `ContextActivation`

One activation is created for each logical request produced by the pipeline:

- initial orientation;  
- orientation repair;  
- initial generation;  
- search generation;  
- evaluation;  
- revision;  
- compression.

Provider transport retries for the same exact request reuse the same activation and request package. A repair or revision that changes the request receives a new activation even when its provider stage name is unchanged.

### 8.2 Frame lineage

Each `InferenceContextFrame` may carry two distinct parent links:

- `derivationParentFrameId`: earlier frame in the same run whose artifact is included or transformed, such as orientation → generation or evaluation → revision;  
- `continuityParentFrameId`: most recent comparable frame for the same conversation, profile, and stage across prior turns.

Do not use one ambiguous `previousFrameHash` for both relationships.

## 9\. Authority, trust, and layer profiles

### 9.1 Provider roles are the primary boundary

The provider request has at least two different trust channels:

1. **Trusted application instruction**  
     
   - fixed system/stage contract;  
   - safety and ontology boundaries;  
   - output schema/transport requirements;  
   - explicit instruction that all supplied dynamic content is untrusted data.

   

2. **Structured application data**  
     
   - messages and current input;  
   - approved persona projection;  
   - accepted memory projection;  
   - task/search artifacts;  
   - orientation/evaluation/candidate artifacts.

Memory, persona, messages, web evidence, and model-authored artifacts remain data even when approved for inclusion. Approval controls whether they may influence a call; it does not promote them into the provider's system role.

### 9.2 Canonical layer registry

The initial registry should be code-defined and versioned. Database-authored layer formulas are deferred.

| Order | Layer kind | Typical source | Trust class | Default requirement |
| ----: | :---- | :---- | :---- | :---- |
| 1 | `constitutional_contract` | approved system component | trusted instruction | mandatory, fail on overflow |
| 2 | `stage_contract` | orientation/generation/evaluation/etc. component | trusted instruction | mandatory, fail on overflow |
| 3 | `output_transport_contract` | schema, character ceiling, ViaPath rules | trusted instruction | mandatory |
| 4 | `approved_seed_identity` | approved authored seed/profile | governed application data | mandatory when active |
| 5 | `persona_projection` | selected approved persona facets | governed application data | optional/bounded |
| 6 | `mode_and_stance` | deterministic turn/cognition policy | governed application data | mandatory or fixed-small |
| 7 | `relationship_projection` | future relationship state | governed sensitive data | deferred |
| 8 | `user_memory_projection` | accepted claims/engrams/confirmed interpretations | governed untrusted data | optional plus mandatory corrections |
| 9 | `context_artifacts` | search/task/external artifacts | untrusted external data | optional/bounded |
| 10 | `turn_plan` | validated orientation | model-authored untrusted artifact | staged calls only |
| 11 | `active_conversation` | recent messages and current input | untrusted participant/assistant data | current input mandatory |
| 12 | `candidate_or_evaluation` | prior candidate/evaluator artifact | model-authored untrusted artifact | stage-specific mandatory |

The ordinal is a deterministic serialization convention. It does not make lower data trusted or let higher data override the provider system role.

### 9.3 Stage profiles

Each stage has a versioned `ContextProfile`. The current request shapes become explicit:

| Stage | Required dynamic layers | Optional layers | Notes |
| :---- | :---- | :---- | :---- |
| Orientation | active conversation/current input, selected memory | none initially | Current behavior does not include persona; preserve parity in Phase 1\. |
| Generation (single) | current input, recent conversation, active seed/profile | memory, persona | Preserve current candidate request. |
| Search generation | explicit query, conversation, active seed/profile | memory, persona | Web grounding is a tool result after request, not preexisting trusted context. |
| Generation (staged) | current input, bounded evidence excerpts, validated turn plan, active seed/profile | memory, persona | The plan is a navigation artifact, not authority. |
| Evaluation | source messages, candidate, output contract | memory, persona, turn plan | Evaluator compares untrusted artifacts to sources. |
| Revision | prior candidate, evaluator defects, bounded evidence | memory, persona, turn plan | Evaluator instructions are typed data under the revision system contract. |
| Compression | prior candidate and output contract | none | It must not regain omitted memory or history. |

Phase 1 must receipt these profiles without changing their current contents. Later policy changes require a new profile version and parity/quality evidence.

## 10\. Candidate and decision model

### 10.1 Security boundary and bounded materialization

The Phase 1 guarantee is a **complete materialized candidate set under recorded query bounds**. It means every record returned across the authorized, in-scope collection boundary receives a terminal decision. It does **not** mean loading or receipting foreign participant records so they can later be marked suppressed, nor does it pretend that records beyond a configured query limit were considered.

Collection proceeds in two levels:

1. **Security-scoped metadata query**  
     
   - namespace and participant predicates are mandatory;  
   - conversation scope and authorization are applied before content decryption;  
   - foreign IDs and counts are never exposed to the activation.  
   - collector version, sort, limit, returned count, and known/unknown truncation are receipted separately.

   

2. **In-scope decision pipeline**  
     
   - status;  
   - sensitivity permission;  
   - evidence availability;  
   - validity interval;  
   - supersession/tombstone;  
   - stage permission;  
   - relevance;  
   - duplication/diversity;  
   - item/layer/global budget.

Every record admitted to level 2 receives one persisted terminal decision.

A `ContextCollectionGateReceipt` records authorization, scope policy, query version/order/limit, and whether the materialized pool may have been truncated. Security-gate exclusions are not candidate rows. A later phase may safely explain same-participant metadata exclusions, but it still must not surface foreign-scope record identities.

### 10.2 Decision enums

#### Eligibility

eligible

ineligible

#### Selection decision

mandatory

selected

suppressed

deferred

#### Collection/security gate codes

collection\_authorized

authorization\_absent

scope\_gate\_applied

query\_bound\_reached

query\_truncation\_unknown

#### Initial materialized-candidate reason codes

selected\_mandatory

selected\_cue\_exact

selected\_lexical\_overlap

selected\_recent\_window

selected\_active\_profile

restricted\_sensitivity

inactive\_status

outside\_validity\_window

superseded

tombstoned

evidence\_unavailable

unsupported\_source\_class

stage\_not\_allowed

no\_relevance\_signal

below\_relevance\_threshold

duplicate\_coverage

item\_limit

layer\_budget

global\_budget

mandatory\_overflow

content\_unavailable\_after\_deletion

Phase 1 only needs codes that describe current behavior. New scoring and conflict codes appear only when behavior changes.

### 10.3 Decision accounting invariant

For each activation's materialized pool:

candidate\_count

  \= mandatory\_count

  \+ selected\_count

  \+ suppressed\_count

  \+ deferred\_count

Every selected or mandatory decision links to exactly one projected cell. Every suppressed or deferred decision has no cell and has exactly one terminal reason code.

## 11\. Deterministic compilation

### 11.1 Projection

Persistence objects are not serialized directly. Each layer owns a versioned pure projector:

record metadata \+ permitted decrypted fields \+ projection policy

    \-\> ContextCell

A memory cell includes:

{

  "cellId": "ctxcell\_...",

  "layer": "user\_memory\_projection",

  "sourceRefs": \[

    {"kind": "memory\_claim", "id": "claim\_..."},

    {"kind": "memory\_engram", "id": "engram\_..."},

    {"kind": "source\_message", "id": "msg\_..."}

  \],

  "renderedText": "Raistlin prefers the name Raistlin; speech-to-text may render it as Raceland.",

  "intendedEffect": "resolve\_entity",

  "authorityClass": "explicit\_corrected\_fact",

  "trustClass": "governed\_untrusted\_data",

  "evidenceState": "available",

  "selectionReason": "selected\_cue\_exact",

  "characterCount": 82,

  "estimatedTokens": 21,

  "renderHash": "sha256-v1:..."

}

`renderedText` and `renderHash` belong to the purgeable encrypted frame-content representation. The durable cell manifest keeps only its cell key, source/version refs, non-content structural hash, trust/authority metadata, order, and measured cost.

Confirmed interpretive frames included with an engram must appear as separate source references or separate cells. Their IDs cannot disappear into an unreceipted string list.

### 11.2 Canonical serialization

Canonical frame/request hashing uses:

- UTF-8 without BOM;  
- Unicode NFC normalization;  
- LF newlines;  
- invariant numeric formatting;  
- explicit property order for typed records;  
- lexicographically sorted keys for maps;  
- stable array order;  
- explicit null policy;  
- no insignificant whitespace;  
- a versioned canonicalizer identifier;  
- SHA-256 with a named prefix such as `sha256-v1:`.

The durable structural frame hash excludes correlation-only values such as random database IDs, receipt IDs, timestamps, ciphertext, key IDs, and retention metadata. It also excludes rendered text and unkeyed content-derived hashes. It includes stage/profile/policy versions, ordered layer/cell keys, stable source/version refs, trust/authority metadata, decision links, and measured costs. A separate canonical content hash lives only in the purgeable encrypted content row.

Two application-level request receipts are distinct:

- **Verbatim field receipt:** length-prefixed exact `ProviderCallRequest` fields and option values, with no Unicode or newline normalization. This proves field parity.  
- **Canonical logical request:** normalized, versioned representation used for semantic comparison and compiler replay.

Neither is a claim to capture raw HTTP bytes, SDK-internal serialization, transport headers, or provider-side transformations. Capturing those requires an explicit provider-adapter/network receipt.

Hashes cover plaintext before encryption. While content is retained, persist:

- a verbatim-field hash for exact application-field comparison;  
- a canonical logical-request hash for deterministic replay checking;  
- encrypted canonical content for retained reconstruction;  
- optional `ciphertextHash` for storage-corruption detection;  
- provider/model/configuration, SDK/adapter, response-schema, tool-configuration, projector, serializer, canonicalizer, and encryption-key identifiers.

Randomized ciphertext must never be used as the frame identity.

An unkeyed hash of short or guessable personal text is not a privacy-preserving, “non-reversible” receipt. Content-derived hashes live with purgeable encrypted content or use a keyed construction under the retention policy; append-only structural events retain no raw personal text or guessable standalone content fingerprint.

### 11.3 Determinism envelope

“Same state” means the same:

- context base snapshot plus call-input manifest;  
- `asOf` timestamp;  
- namespace/participant/conversation/turn/stage;  
- authorization and sensitivity permissions;  
- component and active-profile versions;  
- context-profile version;  
- collector/query version and limits;  
- scoring/diversity policy version;  
- projector and serializer versions;  
- canonicalizer version;  
- tokenizer/estimator version;  
- provider request options;  
- deterministic tie-break order.

If a model, embedding service, or other nondeterministic operation later contributes to activation, its exact result, provider/model/version, and receipt become part of the call-input manifest. The deterministic compiler consumes that recorded result; it does not rerun it during replay.

## 12\. Budget model

### 12.1 Honest measurement

The current implementation can enforce deterministic character limits and receives actual token usage only after a provider call. v0.2 must not label a heuristic as an exact token count.

Each layer records:

- characters;  
- estimated tokens;  
- estimator/version;  
- post-call actual prompt tokens at the package/call level;  
- estimate error when actual usage is available.

The first slice keeps current character admission exactly. A later provider-supported count-token call or validated local tokenizer can become the preflight estimator under a new policy version.

### 12.2 Complete equation

safe\_input\_budget

  \= provider\_context\_limit

  \- configured\_max\_output\_tokens

  \- provider\_safety\_margin

compiled\_input

  \= trusted\_instruction\_tokens

  \+ structured\_dynamic\_data\_tokens

  \+ provider\_wrapper\_overhead\_tokens

compiled\_input \<= safe\_input\_budget

All layers, system instructions, JSON/schema overhead, search/tool configuration, and the current input count against the same provider context window.

### 12.3 Per-layer policy

Each stage profile declares:

minimum

target

maximum

priority

mandatory class

overflow behavior

estimator version

Initial overflow behaviors:

- `fail`: trusted contracts cannot be represented safely;  
- `select`: retain the highest-ranked cells;  
- `select_diverse`: retain ranked cells with coverage constraints;  
- `compact_with_receipt`: use a versioned compression artifact while retaining source references;  
- `defer`: omit from this call and record the reason;  
- `clarify_or_narrow`: return a visible bounded failure when required dynamic context cannot fit.

### 12.4 Mandatory overflow order

1. Fail if the constitutional/stage/output contracts alone exceed the safe budget.  
2. Preserve the current participant input.  
3. Preserve applicable corrections, tombstone/suppression effects, and safety/ontology boundaries.  
4. Preserve the minimum active seed identity required by the active profile.  
5. Compact or reduce older conversation according to a receipted policy.  
6. Remove optional persona, memory, artifacts, and duplicate coverage by declared priority.  
7. If mandatory dynamic content still does not fit, do not silently truncate it; return a typed `context_mandatory_overflow` failure or request a narrower operation.

## 13\. Persistence model

### 13.1 Target records

#### `ContextBaseSnapshot`

id

run\_id

namespace\_id

participant\_id

conversation\_id

input\_message\_id

as\_of

message\_sequence\_watermark

record\_manifest\_json            \-- exact IDs, versions, structural/keyed hashes; no raw content

persona\_activation\_id / snapshot\_id

authorization\_versions\_json

component\_versions\_json

policy\_manifest\_hash

schema\_version

created\_at

#### `ContextCallInputManifest`

id

base\_snapshot\_id

run\_id

stage

logical\_call\_sequence

base\_record\_refs\_json

derived\_artifact\_refs\_json

component\_versions\_json

provider\_option\_manifest\_json

application\_input\_manifest\_json \-- field names, refs, lengths, structural/keyed hashes; no raw content

reference\_state\_json

manifest\_hash

created\_at

#### `ContextActivation`

id

call\_input\_manifest\_id

run\_id

stage

logical\_call\_sequence

logical\_call\_kind

context\_profile\_version

collector\_version

decision\_policy\_version

budget\_policy\_version

projector\_manifest\_hash

status: compiled | failed

failure\_code

candidate\_count

mandatory\_count

selected\_count

suppressed\_count

deferred\_count

character\_count

estimated\_token\_count

token\_estimator\_version

content\_retention\_policy\_version

created\_at

Insert one terminal activation row. `started` and failure progression belong in lifecycle events, avoiding a mutable receipt row. The activation does not point back to its frame or package; unique child rows reference the activation, avoiding circular immutable inserts.

#### `ContextCollectionGateReceipt`

id

activation\_id

source\_kind

authorization\_version

scope\_policy\_version

query\_version

sort\_version

configured\_limit

returned\_count

truncation\_state: no | yes | unknown

gate\_codes\_json

receipt\_hash

created\_at

This is the bounded collection proof. It deliberately contains no foreign-scope row IDs and is separate from terminal decisions for materialized candidates.

#### `ContextCandidateDecision`

id

activation\_id

ordinal

source\_record\_type

source\_record\_id

layer\_kind

eligibility

selection\_decision

reason\_code

score\_components\_json           \-- numeric/coded features only; no raw matched tokens/cues

intended\_effect

authority\_class

trust\_class

evidence\_state

character\_cost

estimated\_token\_cost

selected\_cell\_key

selected\_cell\_structural\_hash

decision\_hash

created\_at

#### `InferenceContextFrame`

id

activation\_id

stage

derivation\_parent\_frame\_id

continuity\_parent\_frame\_id

context\_profile\_version

canonicalizer\_version

structural\_frame\_hash

layer\_summary\_json

cell\_manifest\_json              \-- keys/structural hashes/ordinals, no rendered text or content hash

character\_count

estimated\_token\_count

material\_change\_codes\_json

created\_at

#### `InferenceContextFrameContent`

frame\_id

content\_key\_id

frame\_cipher                    \-- ordered layers/cells including rendered text

verbatim\_content\_hash

canonical\_content\_hash

ciphertext\_hash

expires\_at

created\_at

The immutable frame contains a non-content structural/cell manifest. Rendered text lives in a separately purgeable encrypted content row. Normalized layer/cell tables may be added only when measured UI/analytics needs justify the duplication.

#### `ProviderRequestPackage`

id

activation\_id

frame\_id

stage

contract

generation\_id

provider

model

configuration\_version

provider\_sdk\_version

provider\_adapter\_version

response\_schema\_hash

tool\_configuration\_hash

max\_output\_tokens

thinking\_level

max\_reply\_characters

google\_search\_enabled

content\_capture\_mode: retained | manifest\_only

structural\_package\_hash

created\_at

#### `ProviderRequestPackageContent`

request\_package\_id

content\_key\_id

system\_instruction\_cipher

input\_cipher

verbatim\_fields\_cipher

canonical\_logical\_request\_cipher

verbatim\_fields\_hash

canonical\_logical\_request\_hash

ciphertext\_hash

expires\_at

created\_at

#### `ProviderDispatchRecord`

id

request\_package\_id

attempt

provider

model

configuration\_version

prepared\_at

dispatch\_idempotency\_key

A dispatch record is committed before network I/O. A later terminal provider-call receipt references it. A prepared dispatch without a terminal receipt is an explicit `unknown` outcome after crash/recovery; it must never be silently retried as though no call occurred.

#### `RetainedContentKey`

id

content\_kind

content\_record\_id

wrapped\_dek

wrapping\_key\_version

expires\_at

created\_at

Each retained frame or request body uses a random data-encryption key (DEK). The installation key wraps the DEK; it does not encrypt every retained body directly. The key row and its content row are retention-managed and purgeable together.

#### `LifecycleEvent`

sequence                    \-- authoritative monotonic order

event\_id

stream\_kind

stream\_id

event\_type

correlation\_id              \-- normally run/turn

causation\_event\_id

actor

policy\_version

subject\_refs\_json

reason\_code

payload\_metadata\_json           \-- bounded IDs/codes only; no raw personal content

payload\_metadata\_hash

previous\_event\_hash

event\_hash

created\_at

### 13.2 Hybrid state/event decision

Raistlin Bridge will not become fully event-sourced in v0.2.

- Existing relational rows remain authoritative for messages, memory, persona, generations, and delivery.  
- Lifecycle events are appended transactionally with meaningful state transitions.  
- Events provide semantic order, correlation, causation, UI timeline input, and audit.  
- A lifecycle event cannot mutate domain state by being replayed through a UI observer.  
- Projection rebuilds may use events plus authoritative state, but events do not replace encrypted content tables.  
- An immutable `ContextActivationReceipt` is fixed pre-dispatch. A `ContextLifecycleView` is assembled at read time from that receipt plus later dispatch, provider, output, delivery, and observation events.

The current `audit_events` table remains a legacy audit trail. It should not be silently reinterpreted as the new spine.

### 13.3 Append-only and reference integrity

Structural receipt, dispatch, and event tables receive database triggers preventing update and delete. Frame/request content rows and wrapped per-content keys are explicitly retention-managed material, not immutable proof; policy may delete them and must append a `ContextContentPurged` event containing only safe structural refs and reason codes.

Before commit, validate:

- every activation references its run and call-input manifest;  
- every decision references the same activation scope;  
- every selected cell key/structural hash resolves in the frame's immutable cell manifest;  
- every selected cell source resolves in the same participant namespace at commit time;  
- every frame references its activation;  
- every request package references its frame/activation;  
- every provider call references its request package;  
- every output/delivery/observation link resolves;  
- no event cites a future or foreign-scope object.

After deletion, historical resolution is explicit:

- `live`: the source/content remains available;  
- `tombstoned`: the source lineage was deliberately forgotten;  
- `unavailable_after_deletion`: structural proof remains but content was purged;  
- `ghost`: no valid lifecycle explanation exists; this is an integrity failure.

## 14\. Event vocabulary

Initial context lifecycle:

TurnInputCommitted

ContextBaseSnapshotCaptured

ContextCallInputManifestCaptured

ContextActivationStarted

ContextCandidateCollected

ContextCandidateDecided

ContextFrameCompiled

ContextPackageSerialized

ContextCompilationFailed

ProviderAttemptPrepared

ProviderAttemptStarted

ProviderAttemptCompleted

ProviderAttemptFailed

ProviderAttemptOutcomeUnknown

OrientationValidated

OrientationRejected

GenerationCandidateProduced

EvaluationCompleted

RevisionRequested

ResponseCommitted

DeliveryQueued

DeliveryAttempted

DeliveryAccepted

DeliveryFailed

ObservationEligible

ObservationStarted

ObservationAbstained

MemoryProposalCreated

MemoryProposalReviewed

MemoryClaimAccepted

MemoryClaimSuperseded

MemoryClaimRestricted

MemoryForgotten

TombstoneCreated

ContextContentPurged

TurnFinalized

Do not emit one event per candidate solely to create a noisy event log if the normalized decision table already stores the complete set. One `ContextFrameCompiled` event may reference the activation and its decision-set hash. Events describe lifecycle; candidate rows provide detailed evidence.

## 15\. Replay, retention, and forgetting

### 15.1 Three replay claims

| Replay kind | Guarantee |
| :---- | :---- |
| Compiler replay | When every referenced version/content remains resolvable and permitted, re-run the deterministic compiler from the retained base snapshot, call-input manifest, content, and versions; the decision set and frame hash must match. Otherwise report replay unavailable, never a partial match. |
| Application-request reconstruction | Reconstruct the exact `ProviderCallRequest` fields from the retained request package; both the verbatim-field and canonical logical-request hashes must match. |
| Inference replay | Issue a new provider call using a reconstructed request. It is a new attempt and is not expected to reproduce the old output. |

No claim is made that the package reconstructs raw SDK/HTTP bytes, headers, network timing, or provider-side transformations.

### 15.2 Safe retention baseline

Phase 1 may not persist full frame/request content until the retention and cryptographic-erasure boundary exists. The baseline is:

- retain content-free structural receipts, safe reason codes, version IDs, and relationship manifests under the receipt-retention policy;  
- retain encrypted frame/request bodies for **seven days by default in the local development profile**; production profiles must set an explicit duration and may choose zero;  
- encrypt each retained content record with a random DEK, store only a wrapped DEK separately, and purge the body and wrapped key together;  
- purge immediately when a referenced source is forgotten/deleted or authorization requires revocation, regardless of the nominal expiry;  
- append a content-free `ContextContentPurged` event with structural IDs and a safe reason code;  
- keep raw personal text, rendered-cell text, verbatim request fields, and low-entropy content hashes out of lifecycle events and durable structural manifests;  
- make backup/export retention and restore/import reapply key tombstones and content-purge state before content becomes readable.

The current shared `ContentCipher` does not by itself provide per-record cryptographic erasure. Phase 1 must add the wrapped-DEK boundary above or run in manifest/hash-only mode with full-content reconstruction explicitly unavailable.

### 15.3 Deliberate orientation-retention change

The current repository deliberately avoids retaining the typed orientation transcript. Retaining a staged generation request body would indirectly retain that orientation because the validated plan is one of its application input fields. This plan changes that policy only inside the seven-day, wrapped-DEK content boundary.

If that policy change is not accepted, orientation-derived call manifests keep structural artifact references/hashes only; full request bodies for those calls are not retained and exact application-request reconstruction is unavailable. Structural receipt and compiler-version verification still work where source content remains available.

### 15.4 Deletion precedence

Exact reconstruction is conditional:

> The exact application-level provider request fields are reconstructable while their permitted source content, component versions, encrypted frame/request body, and decryption keys remain retained.

When a source is forgotten or deleted:

- remove or cryptographically erase prohibited content;  
- mark affected frame/package content `unavailable_after_deletion`;  
- retain content-free structural metadata, source-type/ID relationships where policy permits, safe keyed/structural hashes, reason codes, and deletion events;  
- purge unkeyed hashes derived from low-entropy personal content with that content;  
- prohibit provider replay from incomplete content;  
- allow structural verification that a historical package existed without recovering its text.

The system must never retain a second undeletable copy of forgotten content merely to satisfy an audit slogan.

### 15.5 Tamper evidence

For the local single-host threat model, Phase 1 requires:

- append-only database triggers on structural receipt, dispatch, and event tables;  
- canonical hashes;  
- monotonic event sequence;  
- previous-event hash chaining.

A later phase may add an installation-key HMAC or external anchor. Plain hashes alone prove equality, not who authored a record or whether an administrator rewrote the entire database.

## 16\. Post-turn observation policy

### 16.1 Surface-specific delivery semantics

| Surface | Observation eligibility |
| :---- | :---- |
| Local test UI | Response transaction committed successfully under explicit local-memory authorization. This is an explicit local non-transport delivery policy. |
| ViaPath manual/automatic delivery | All required parts accepted under the active route epoch and delivery authorization. |
| Failed/uncertain delivery | Ineligible by default; a future explicit non-delivery observation policy may permit limited telemetry, never user-memory derivation. |
| Search simulation | Ineligible. |
| Continuity bootstrap | Ineligible. |
| Shadow bridge generation | Ineligible. |
| Evaluation/quality fixtures | Ineligible. |

### 16.2 Idempotency

Observation eligibility is keyed to the logical delivered response, not an individual attempt/outcome row that may change across multipart retries:

local:

  response\_message\_id \+ local\_commit\_policy\_version

bridge:

  response\_message\_id

  \+ route\_id

  \+ route\_epoch

  \+ logical\_delivery\_group\_id

  \+ observation\_policy\_version

Observation jobs and proposals carry stable deduplication keys. Retrying a worker cannot create duplicate proposals or reapply an accepted memory transition.

### 16.3 Future semantic observer boundary

The current exact-command extractor remains the baseline. A future semantic observer may:

- abstain;  
- propose a typed claim;  
- propose an active-thread transition;  
- flag a possible correction or conflict;  
- request human clarification.

It may not:

- accept its own proposal;  
- alter confidence on access frequency alone;  
- create identity from assistant prose;  
- infer sensitive facts without explicit policy;  
- resurrect tombstoned content;  
- treat the delivered response as proof that its claims are true.

## 17\. Implementation roadmap

### Phase 0 — vocabulary and parity freeze

**Outcome:** this v0.2 document becomes the design baseline.

1. Preserve `InterpretiveFrame` and adopt `InferenceContextFrame`.  
2. Freeze current provider request fixtures for every stage.  
3. Record current memory/persona candidate ordering and selected results.  
4. Define canonical enums and reason codes in code.  
5. Pin the Agentica learning snapshot and document adopt/adapt/defer decisions.  
6. Do not merge context-policy changes into the grounded-search/ViaPath dirty work.  
7. Freeze the content-retention profile, wrapped-DEK design, purge propagation, and orientation-retention decision before storing any full request body.

**Exit criteria**

- current-stage request snapshots exist;  
- terminology has no collision;  
- current behavior is reproducible by tests.

### Phase 1 — behavior-preserving activation envelope

**Outcome:** complete observability around current behavior.

1. Add `ContextBaseSnapshot`, `ContextCallInputManifest`, `ContextActivation`, `ContextCollectionGateReceipt`, `ContextCandidateDecision`, `InferenceContextFrame`, `ProviderRequestPackage`, and `ProviderDispatchRecord` domain records.  
2. Add schema migration v17 with append-only structural receipts, a minimal lifecycle ledger, retention-managed content/key tables, and provider-call linkage.  
3. Refactor memory activation to return terminal decisions for every materialized in-scope current candidate under a separately receipted query bound:  
   - no relevance;  
   - selected;  
   - item limit;  
   - character limit.  
4. Capture recent-message window decisions and current persona decisions.  
5. Wrap existing `ProviderPromptCompiler` output without changing `ProviderCallRequest` fields.  
6. Canonicalize and hash the frame/request, then retain encrypted content only through the wrapped-DEK policy.  
7. Persist activation/frame/package and a prepared dispatch record before network I/O; fail closed if those commits fail.  
8. Link retries, failures, successful calls, and crash-recovered unknown outcomes to the prepared dispatch/package.  
9. Persist applicable compile/failure receipts even when the run does not complete.  
10. Add the minimal event types needed to order compile, prepare, outcome, and purge transitions.  
11. Add a read-only activation API and minimal inspector.

This slice preserves provider request content and selection behavior, but mandatory receipt persistence intentionally changes availability semantics: a call that cannot be receipted is not dispatched.

**Exit criteria**

- provider request parity is verbatim-field exact at the `ProviderCallRequest` boundary;  
- selected memory and persona are unchanged;  
- every candidate decision accounts exactly;  
- failed calls still expose the compiled context receipt;  
- same fixture produces the same frame/request hashes.

### Phase 2 — deterministic stage compiler and layer profiles

**Outcome:** prompt assembly becomes a first-class subsystem.

1. Replace implicit assembly with code-defined, versioned `ContextProfile` records.  
2. Split collection, policy decisions, budgeting, projection, frame compilation, and provider serialization into pure boundaries.  
3. Persist exact authored component versions instead of relying only on compiler constants.  
4. Enforce trust classes and stage allowlists.  
5. Add per-layer character budgets and honest token estimates.  
6. Add mandatory-overflow failures.  
7. Add derivation and continuity parent links.  
8. Measure request size, latency overhead, and token-estimate error.  
9. Enforce the new provider-role, stage-authority, and mandatory-overflow policies under new profile versions; Phase 1 only characterizes the existing boundary.

**Exit criteria**

- each stage declares its complete layer profile;  
- no persistence object is dumped directly into a request;  
- authority-preservation and overflow tests pass;  
- frame diffs explain all material changes.

### Phase 3 — lifecycle enrichment, replay, and inspector

**Outcome:** lifecycle state is inspectable without creating competing state machines.

1. Enrich the minimal Phase 1 `LifecycleEvent` ledger across generation, delivery, and observation while preserving monotonic sequence and correlation/causation.  
2. Add timeline projections without making event consumers responsible for domain effects.  
3. Add compiler replay and retained application-request reconstruction commands.  
4. Add reference-graph validation and ghost-reference tests.  
5. Build activation list, frame/layer/cell inspector, frame diff, and timeline.  
6. Add content retention state and deletion-aware replay UI.

**Exit criteria**

- an operator can answer what the model saw, why, under which policy, and what resulted;  
- compiler replay matches hashes;  
- deletion makes content replay unavailable without damaging structural audit;  
- UI state is a projection over authoritative records/events.

### Phase 4 — governed memory lifecycle completion

**Outcome:** move from manual recall laboratory toward safe longitudinal memory.

1. Add typed conflict records and correction/conflict policy.  
2. Give active threads explicit open/paused/resolved/expired states.  
3. Use `validFrom`/`validTo` and declared temporal stability.  
4. Add reinforcement/corroboration records without truth-by-access.  
5. Add active-memory restrict/unrestrict and rollback workflows.  
6. Add proposal deduplication beyond exact normalized text.  
7. Introduce a semantic observer in shadow mode with abstention and proposal telemetry.  
8. Keep automatic acceptance disabled until measured precision and rollback evidence justify a separate decision.

**Exit criteria**

- corrections, contradictions, expiration, sensitivity, and long-gap threads have deterministic fixtures;  
- semantic observation cannot directly mutate memory;  
- all transitions are receipted and reversible where policy permits.

### Phase 5 — retrieval policy improvement

**Outcome:** improve recall only after a trustworthy baseline exists.

1. Measure missed recall, false recall, duplicate coverage, and context churn.  
2. Add minimum relevance thresholds and diversity.  
3. Add staleness, temporal validity, conflict, and sensitivity costs.  
4. Add entity/alias resolution prerequisites.  
5. Evaluate semantic retrieval as a candidate generator, not as canonical memory.  
6. Preserve deterministic post-retrieval policy and complete decisions.

**Exit criteria**

- the new policy outperforms the Phase 1 baseline on longitudinal fixtures;  
- namespace and authority tests remain unchanged;  
- semantic indexes can be rebuilt from authoritative records.

### Phase 6 — Raistlin-channel shadow validation and hardening

**Outcome:** determine whether real-channel memory observation is safe to enable.

1. Run context receipts against real Raistlin turns with derivation still disabled.  
2. Validate multipart delivery eligibility and exactly-once observation rules.  
3. Run prompt-injection tests across messages, memories, persona, web evidence, turn plans, and evaluator artifacts.  
4. Test backup, restore, migration, key loss, deletion propagation, and event-chain validation.  
5. Review retention and sensitive-record access.  
6. Make a separate, evidence-backed enablement decision.

## 18\. First implementation vertical slice

### 18.1 Narrow deliverable

Implement Phase 1 for current local chat and current stage requests. Do not change ranking, budgets, prompt text, cognition selection, provider models, observation policy, or bridge behavior.

### 18.2 Likely code changes

| Work item | Likely location |
| :---- | :---- |
| New context receipt models and enums | `apps/companion/Domain/ContextModels.cs` |
| Migration v17, structural append-only triggers, and retention-managed content/key tables | [Schema.cs](http://../apps/companion/Storage/Schema.cs) |
| Persistence/read APIs | new `CompanionStore.Context.cs` |
| Complete memory decision result | [MemoryActivationService.cs](http://../apps/companion/Memory/MemoryActivationService.cs), [CompanionStore.MemoryRuntime.cs](http://../apps/companion/Storage/CompanionStore.MemoryRuntime.cs) |
| Message-window decisions, base snapshot, and per-call input manifest | [ContextAssembler.cs](http://../apps/companion/Conversation/ContextAssembler.cs) or a new `ContextBaseSnapshotCompiler` |
| Stage frame and request package wrapper | new `InferenceContextCompiler.cs` around [ProviderPromptCompiler.cs](http://../apps/companion/Provider/ProviderPromptCompiler.cs) |
| Pre-dispatch persistence and linkage | [ProviderCallExecutor.cs](http://../apps/companion/Provider/ProviderCallExecutor.cs), generation pipelines, and [CompanionRuntime.cs](http://../apps/companion/Conversation/CompanionRuntime.cs) |
| Receipt API | [LocalApi.cs](http://../apps/companion/Local/LocalApi.cs) |
| Inspector | new `ContextPanel.tsx` or a focused expansion of [MemoryPanel.tsx](http://../apps/companion/WebClient/src/MemoryPanel.tsx) |
| Integration tests | [CompanionApiTests.cs](http://../tests/companion/CompanionApiTests.cs) |

### 18.3 Migration v17 minimal tables

The first migration should prefer a compact hybrid over premature normalization:

context\_base\_snapshots

context\_call\_input\_manifests

context\_activations

context\_collection\_gate\_receipts

context\_candidate\_decisions

inference\_context\_frames

inference\_context\_frame\_content

provider\_request\_packages

provider\_request\_package\_content

provider\_dispatch\_records

retained\_content\_keys

lifecycle\_events

Add nullable activation/package linkage to provider-call receipts or use an immutable join table if SQLite migration constraints make that safer.

The immutable frame stores an ordered content-free cell manifest. The separately purgeable encrypted frame-content row may hold rendered layers/cells for the first inspector. Add normalized `context_frame_layers` and `context_frame_cells` only after query/analytics needs are measured.

### 18.4 Safe implementation sequence

1. Capture golden `ProviderCallRequest` fixtures for all current stages.  
2. Implement and test the wrapped-DEK retention/purge boundary; otherwise select manifest/hash-only mode.  
3. Introduce canonical serialization and hash tests in isolation.  
4. Add domain records and migration, including the minimal lifecycle ledger.  
5. Make `MemoryActivationService` return a richer internal result while preserving the public packet.  
6. Produce decisions for messages, memory, and persona.  
7. Wrap compiler output into a frame/package and verify golden parity.  
8. Commit a prepared dispatch before network I/O.  
9. Link successful, failed, and recovery-classified unknown provider attempts.  
10. Expose read-only APIs.  
11. Add the inspector only after receipt invariants pass.

## 19\. Acceptance tests

### Phase 1 blocking tests

1. **Request parity:** every current stage produces verbatim-identical `ProviderCallRequest` system instruction, input, limits, thinking level, and tool flags before and after wrapping.  
2. **Selection parity:** existing memory/persona fixtures select the same records in the same order.  
3. **Collection-bound receipt:** each collector records its query/order/limit and known, unknown, or absent truncation without exposing foreign-scope identities.  
4. **Complete accounting:** for the materialized pool, `candidate = mandatory + selected + suppressed + deferred`.  
5. **Terminal reason:** every suppressed/deferred materialized candidate has one registered reason code.  
6. **Selected linkage:** every selected/mandatory decision resolves to one frame cell key/structural hash.  
7. **Interpretive-frame linkage:** included confirmed frame text resolves to its frame ID.  
8. **Pre-dispatch persistence:** an activation/frame/package and prepared dispatch exist before provider network I/O begins.  
9. **Crash window:** a prepared dispatch with no terminal provider receipt is surfaced as `unknown`; recovery does not silently retry it.  
10. **Failure visibility:** provider failure, timeout, cancellation-after-dispatch, and validation failure retain the applicable structural context receipt.  
11. **Retry identity:** transport retries of an unchanged request share one request package and have distinct prepared dispatch records.  
12. **Logical-change identity:** orientation repair, revision, or compression that changes input creates a new activation/package.  
13. **Canonical determinism:** reordered map insertion or randomized encryption does not change canonical frame/request hashes.  
14. **Namespace isolation:** foreign participant rows never enter candidates, receipts, exclusion counts, or decrypted material.  
15. **Conversation isolation:** conversation-scoped memory appears only in its conversation.  
16. **Status/sensitivity:** restricted, deleted, superseded, expired, or unauthorized records cannot become cells.  
17. **Reference state:** unresolved or foreign-scope references prevent receipt commit; later deletion resolves as `tombstoned` or `unavailable_after_deletion`, not an unexplained ghost.  
18. **Append-only boundary:** structural receipt, dispatch, and lifecycle tables reject update/delete; content and wrapped-key rows can be policy-purged.  
19. **Content retention:** full request/frame bodies cannot persist without a wrapped DEK and expiry; zero-retention mode emits structural receipts only.  
20. **Deletion precedence:** forgetting purges affected bodies, wrapped DEKs, and low-entropy content hashes; structural metadata remains inspectable and replay is refused.  
21. **Restore precedence:** backup/import cannot make content readable when its key/content tombstone is already in force.  
22. **Prompt-boundary characterization:** hostile strings remain encoded inside the same current application-data fields and do not mutate trusted field structure. Phase 1 records output regressions but does not claim immunity from model prompt injection.  
23. **Budget honesty:** estimates are labeled with their estimator; post-call actual token usage is not misrepresented as a preflight measurement.  
24. **Dirty-tree isolation:** Phase 1 changes do not alter the in-progress grounded-search/ViaPath behavior outside explicit integration points.

### Phase 2 authority and adversarial tests

- each stage admits only declared layers and preserves trusted-instruction versus application-data roles;  
- untrusted text cannot be promoted into a trusted request field by ranking, projection, or overflow;  
- mandatory trusted fields fail explicitly rather than being silently displaced;  
- adversarial suites measure instruction-following resistance across memory, persona, conversation, search, orientation, evaluation, and candidate artifacts; passing means an agreed measured threshold, not a proof that model behavior cannot be influenced.

### Longitudinal memory fixtures retained from the broader plan

- stable identity and alias repair;  
- changing preference;  
- explicit correction;  
- recurring preference with corroboration;  
- active thread after a long gap;  
- expired time-bounded fact;  
- sensitive fact excluded without permission;  
- unresolved contradiction;  
- semantic duplicate;  
- forgotten lineage and stale extraction;  
- cross-participant isolation;  
- prompt injection and authority confusion.

## 20\. Metrics

### Compiler and receipt health

- activation success/failure rate;  
- candidate decisions by reason code;  
- selected cells per layer;  
- frame/request size by stage;  
- estimated versus actual prompt tokens;  
- compilation latency;  
- receipt storage growth;  
- frame churn and material-change codes;  
- replay/hash mismatch rate;  
- reference-integrity failure rate.

### Memory quality

- required-memory recall rate;  
- false or irrelevant recall rate;  
- correction precedence failures;  
- duplicate coverage;  
- sensitive-memory exclusion failures;  
- stale/expired recall;  
- repeated-question avoidance;  
- proposal precision, abstention rate, and human acceptance rate;  
- rollback/forget propagation failures.

### End-to-end cost and quality

- model calls per turn;  
- input/output/thought tokens by stage;  
- staged versus single quality delta;  
- latency by compilation, provider, evaluation, delivery, and observation;  
- context overhead as a fraction of provider input;  
- delivery and observation idempotency failures.

No retrieval-policy change is accepted merely because it retrieves more. It must improve required recall without unacceptable false recall, context cost, leakage, or response-quality regression.

## 21\. Threat model

| Threat | Required control |
| :---- | :---- |
| Instructions embedded in messages, memory, persona, web results, orientation, or evaluator text | Provider-role separation, trust classes, delimiters/typed JSON, stage allowlists, injection fixtures |
| Cross-namespace existence/content leakage | Scoped query before decryption; no foreign candidate IDs/counts; API scope checks |
| Sensitive-memory exposure | Independent authorization, sensitivity filter, access receipt, fail closed |
| Stale extraction resurrects forgotten material | Tombstone/fingerprint check before proposal creation and acceptance |
| Correction races with compilation | Transactional base snapshot, per-call input manifest, and pre-dispatch revalidation |
| Model persuades observer to write memory | Typed observer schema, source validation, independent governor, no persistence capability |
| Forged or rewritten receipts | Append-only triggers, canonical hashes, event hash chain, actor/policy metadata |
| Ghost references | Same-scope reference graph validation |
| Exact replay defeats deletion | Key/content erasure and `unavailable_after_deletion` state |
| Budget pressure drops authority | Reserved mandatory layers and typed overflow failure |
| Complete receipts create excessive retention | Tiered encrypted-content retention plus durable minimal metadata |
| Event observer mutates truth | Transactional domain writes own effects; UI/sinks are read-only projections |

## 22\. Decisions intentionally postponed

The following require evidence from Phase 1–3:

1. Exact preflight tokenizer/provider count-token integration.  
2. Normalized frame layer/cell tables versus encrypted canonical frame JSON.  
3. Installation HMAC or external receipt anchoring.  
4. Production retention tuning beyond the seven-day local-development default, including whether a production profile retains full encrypted request bodies at all.  
5. Whether semantic candidate generation uses embeddings, lexical expansion, or a model.  
6. Automatic memory proposal acceptance thresholds, if ever enabled.  
7. Relationship-state schema.  
8. Agentica as a bounded task shell for explicit tool work.

None of these blocks the behavior-preserving activation envelope.

## 23\. Definition of v0.2 architecture complete

The architecture work is complete when:

- the current implementation and terminology are accurately represented;  
- the Agentica concepts are source-linked and marked adopt/adapt/defer;  
- `InterpretiveFrame` and `InferenceContextFrame` are unambiguous;  
- candidate, cell, frame, request package, receipt, and event roles are distinct;  
- authority and trust are enforced through provider roles and policy, not prose order;  
- replay and deletion guarantees do not contradict each other;  
- the event-spine decision is explicit and not full event sourcing;  
- the first migration/slice is narrow enough to preserve successful-call request, selection, and response behavior while making receipt-preparation failure explicitly fail closed;  
- acceptance tests can prove parity, completeness, isolation, determinism, failure visibility, and deletion precedence.

The next engineering action is Phase 1: capture golden provider-request fixtures, then implement the pre-dispatch activation/frame/package receipt around current behavior.  
