Codex Prompt: Agentica–Nyx Context Compiler Consolidation Audit

Cloud-safe architecture-only fallback with embedded evidence pack

EXECUTION MODE — READ THIS FIRST

You are operating in a cloud Codex session that must be treated as repository-unavailable for this task.

Assume all of the following are unavailable unless the user explicitly supplies them inside this task:

\- GitHub checkout or authenticated repository access;

\- any local workstation filesystem or Windows path;

\- Google Drive or Google Docs URLs;

\- local files referenced by earlier reports;

\- current branch, HEAD, worktree status, tests, schemas, migrations, or source files.

Do not attempt to clone repositories, open Google Docs URLs, resolve local file paths, or ask the user to paste the source documents. Do not stop because those capabilities are missing. The evidence pack below is the complete authorized corpus for this run.

This is an architecture audit and documentation task only. Do not modify code, create repository files, run migrations, claim to have inspected a worktree, or claim current implementation facts that are not present in the embedded evidence.

Return the entire audit in the final response. There is no required output path.

EVIDENCE DISCIPLINE

Use these labels for every material claim:

\- EMBEDDED SOURCE — stated in the supplied evidence pack.

\- HISTORICAL REPOSITORY OBSERVATION — a source document reported that a named implementation, symbol, file, test, or commit existed when that document was produced. This is not verification of the current repository.

\- DERIVED — an inference from two or more embedded facts.

\- PROPOSAL — a recommended change or experiment.

\- UNKNOWN CURRENT STATE — requires repository/runtime evidence that this cloud session does not have.

Never convert a historical repository observation into “current code does X.”

Never fabricate a source path, symbol, test result, commit state, or implementation detail.

Named source files and symbols below are provenance labels only. You are not expected to open them.

If a question cannot be answered from the pack, label it UNKNOWN CURRENT STATE and continue.

MISSION

Perform a bounded comparative architecture audit between:

1\. the Nyx/Raistlin Bridge context-and-memory architecture represented by the embedded v0.2 evidence;

2\. the Agentica architecture represented by the embedded consolidation evidence;

3\. the proposed common “bounded cognition / compiled frame” architecture.

Determine the smallest generic cognition contract supported by both designs.

The goal is NOT to make Agentica own companion memory and NOT to make Nyx depend on Agentica. The goal is to identify the smallest reusable execution semantics beneath both systems:

compiled cognitive frame

  → bounded operation

  → deterministic validation

  → host execution or generation

  → result evaluation

  → receipt-backed state transition

  → recompile or stop

The audit must ask what can be deleted, collapsed, or represented as metadata at least as aggressively as it asks what should be added.

SOURCE PROVENANCE

The embedded evidence is derived from three related documents prepared on 2026-08-09:

A. “Receipted Context and Memory Management System,” concept baseline v0.2 for Nyx/Raistlin Bridge.

   Historical snapshot labels recorded there:

   \- Raistlin Bridge snapshot: 245a0ee274eab286cf682a0597a0e05237d2be7c

   \- Agentica reference snapshot: 3ff02d4212b7daec822ca6e094908559d72f0f3d

   These hashes are provenance only; do not assume you can fetch or verify them.

B. “Bounded Cognition Architecture — Consolidation Report,” a synthesis across Nyx, Agentica, Domain of Domains, context compilation, ZPD/scaffolding, and governed adaptation.

C. The original local-oriented Codex audit prompt. This cloud-safe prompt replaces it because the cloud environment does not have the repository or external documents.

EMBEDDED EVIDENCE PACK

1\. Central architectural thesis

A capable model should not reconstruct its world, authority, memory, tools, and objective from an unbounded transcript. The host should compile a small, trustworthy cognitive frame, allow a bounded reasoning process to operate within it, validate the resulting transition, and preserve receipts sufficient to explain and improve the next compilation.

Consolidated flow:

canonical state and evidence

  ↓

domain topology \+ typed attribution

  ↓

hard authorization scope ∩ soft semantic scope

  ↓

ZPD / scaffolding policy

  ↓

context and capability compiler

  ↓

active cognitive frame

  ↓

bounded model/runtime execution

  ↓

validated result \+ receipts

  ↓

state update and slow adaptation proposals

The common primitive proposed by the consolidation report is:

compile current cognitive frame

  → select a bounded operation

  → validate legality / authority

  → execute against authoritative state

  → validate result / completion

  → emit receipts and update state

  → recompile or stop

A deliberately small conceptual execution IR was suggested:

Objective

ActiveFrame

Stage

Capability

Result

Receipt

This is conceptual vocabulary, not a recommendation to create six new classes.

2\. Nyx/Raistlin Bridge: durable memory versus active context

The v0.2 design keeps this invariant:

Memory is durable, evidence-bearing state.

Context is a compiled, temporary projection of permitted state for one inference.

The design reports that the repository already had a governed memory/cognition vertical slice including:

\- encrypted, source-linked memory proposals;

\- versioned claims and engrams;

\- durable InterpretiveFrame records;

\- correction, forgetting, supersession, and tombstone lineages;

\- participant and conversation scoping;

\- bounded cue/lexical memory activation;

\- persona activation;

\- single and staged cognition;

\- orientation, validation/repair, generation, evaluation, revision, and compression;

\- provider/stage, memory/persona, generation, delivery, and observation receipts.

Historical code-surface labels recorded by the source include:

Schema.cs

CompanionStore.cs

MemoryModels.cs

CompanionStore.Memory.cs

CompanionStore.MemoryRuntime.cs

MemoryActivationService.cs

MemoryGovernanceService.cs

PersonaActivationService.cs

ContextAssembler.cs

ProviderPromptCompiler.cs

CompanionGenerationPipeline.cs

ProviderCallExecutor.cs

CompanionRuntime.cs

LocalApi.cs

MemoryPanel.tsx

Treat those as historical evidence labels only.

The v0.2 design does NOT propose replacing the memory ledger. It proposes a stage-aware context compiler around existing behavior.

3\. Nyx/Raistlin Bridge: per-inference proof envelope

The first engineering objective in v0.2 is to produce a receipted envelope for every logical provider call without changing successful-call selection or request behavior.

The design introduces these conceptual/domain records:

\- ContextBaseSnapshot — transactionally consistent turn-start state identifier/version/hash manifest.

\- ContextCallInputManifest — exact per-logical-call manifest that combines the base snapshot with later derived artifacts.

\- ContextActivation — one complete compile operation for one logical provider call.

\- ContextCollectionGateReceipt — proof of authorized, bounded collection without exposing foreign-scope identities.

\- ContextCandidateDecision — exactly one terminal decision for each materialized in-scope candidate.

\- InferenceContextFrame — immutable ordered structured snapshot of selected layers/cells.

\- ProviderRequestPackage — exact application-level system instruction, input, generation options, provider/model/config identity, and hashes.

\- ProviderDispatchRecord — committed before provider network I/O; an unresolved prepared dispatch becomes an explicit unknown outcome rather than a silent retry.

\- LifecycleEvent — append-only semantic ordering/correlation/audit, while relational state remains authoritative.

Existing durable memory concepts remain separate:

\- MemoryClaimRecord

\- MemoryEngramRecord

\- InterpretiveFrameRecord

\- tombstone/supersession state

InterpretiveFrame and InferenceContextFrame must not be conflated:

the first is a governed durable memory interpretation; the second is temporary compiled inference context.

4\. Nyx: candidate accounting and security boundary

The design separates security-scoped collection from in-scope candidate decisions.

Security/authorization predicates are applied before content decryption. Foreign participant/namespace rows must never become candidate IDs, exclusion counts, or decrypted content.

For each materialized in-scope candidate pool:

candidate\_count

  \= mandatory\_count

  \+ selected\_count

  \+ suppressed\_count

  \+ deferred\_count

Every selected/mandatory decision resolves to a projected cell.

Every suppressed/deferred decision has no cell and exactly one terminal reason.

Representative reason classes include:

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

stage\_not\_allowed

no\_relevance\_signal

below\_relevance\_threshold

duplicate\_coverage

item\_limit

layer\_budget

global\_budget

mandatory\_overflow

content\_unavailable\_after\_deletion

The important generic lesson is complete, bounded decision accounting—not these exact companion-specific enums.

5\. Nyx: authority and stage-aware context

The provider request has a hard distinction between:

A. trusted application instructions: constitutional/stage/output contracts, safety/ontology boundaries, schema/transport requirements; and

B. structured application data: participant messages, persona projection, accepted memory, task/search artifacts, and model-authored orientation/evaluation/candidate artifacts.

Approval for inclusion does not promote dynamic data into the provider system role.

The source proposes ordered context layers such as:

constitutional\_contract

stage\_contract

output\_transport\_contract

approved\_seed\_identity

persona\_projection

mode\_and\_stance

relationship\_projection (deferred)

user\_memory\_projection

context\_artifacts

turn\_plan

active\_conversation

candidate\_or\_evaluation

The ordinal is serialization order, not an authority hierarchy that upgrades lower-trust data.

Stage profiles determine which layers are required or optional for orientation, generation, search generation, evaluation, revision, and compression.

6\. Nyx: three compilation levels and lineage

The design distinguishes:

\- turn-level ContextBaseSnapshot;

\- per-call ContextCallInputManifest;

\- per-call ContextActivation / InferenceContextFrame / ProviderRequestPackage.

Later calls may depend on orientation output, evaluator output, prior candidate text, search configuration, or compression input. Those derived artifacts belong in the per-call manifest rather than being falsely treated as turn-start state.

One logical request gets one activation. Provider transport retries of an unchanged exact request reuse that activation/package; a repair/revision/compression that changes the logical request gets a new activation.

InferenceContextFrame can carry two distinct parent relations:

\- derivationParentFrameId — causal/transform relationship within a run;

\- continuityParentFrameId — comparable frame across prior turns.

Do not collapse them into one ambiguous “previous frame.”

7\. Nyx: determinism, replay, retention, and deletion

The compiler boundary is intended to be deterministic given the same snapshot, call manifest, as-of value, authorization/policy/component/profile/projector/serializer/canonicalizer/estimator versions, provider options, and deterministic tie-break order.

The design distinguishes:

\- compiler replay — reconstruct the same decisions/frame when retained inputs remain available;

\- application-request reconstruction — reconstruct exact ProviderCallRequest fields while retained;

\- inference replay — issue a new provider call, with no expectation of reproducing the old stochastic output.

It explicitly does not claim to reconstruct raw HTTP/SDK-internal bytes unless a provider-specific receipt exists.

Deletion/forgetting outranks replay. Content may be retention-limited and cryptographically erased. Durable structural receipts must not become an undeletable second copy of personal content.

Relational domain state remains authoritative. Lifecycle events provide ordering, correlation, causation, audit, and projections; this is intentionally not full event sourcing.

8\. Nyx: reported gaps that motivated v0.2

The historical v0.2 inventory reported that the then-current implementation did not fully receipt:

\- candidates discarded for lack of relevance;

\- item/character-budget displacement reasons per record;

\- recent messages considered but omitted;

\- complete persona suppression decisions;

\- stage-specific ordered layers;

\- exact component text/version used per stage;

\- pre-dispatch token estimates;

\- canonical application-request identity;

\- a context receipt when compilation/generation fails before normal completion.

These are historical reported gaps, not claims about the current repository.

9\. Nyx: first behavior-preserving implementation slice

The source defines Phase 1 as a behavior-preserving activation envelope:

\- capture golden provider-request fixtures;

\- add snapshot/manifest/activation/decision/frame/package/dispatch records;

\- retain current ranking, budgets, prompt text, cognition selection, provider models, observation policy, and bridge behavior;

\- capture complete in-scope decisions for messages, memory, and persona;

\- wrap existing prompt compiler output;

\- persist receipt/package/dispatch before network I/O;

\- link retries, failures, successful calls, and crash-recovered unknown outcomes;

\- expose read-only inspection;

\- fail closed if mandatory pre-dispatch proof cannot be persisted.

Later phases would make stage compilation a first-class subsystem, enrich lifecycle/replay/inspection, complete governed memory lifecycle, improve retrieval policy, and validate the Raistlin channel in shadow mode.

10\. Agentica: embedded architecture baseline

The consolidation report characterizes Agentica as a generic bounded cognition/execution runtime whose useful transferable principles are:

\- host-owned authoritative reality;

\- bounded planner/model-visible projections;

\- deterministic validation of model proposals;

\- legal capability/tool surfaces;

\- normalized observations, receipts, artifacts, outcomes, and completion evidence;

\- domain semantics and durable host memory remain outside generic core.

Named Agentica concepts reported in the source include:

\- RunRequest

\- PlanningRequest

\- PlanningFrame

\- PlanningContextOptions / PlanningRequestFactory

\- ToolCatalog

\- ToolSurfaceSnapshot

\- DomainHarnessManifest

\- ActiveCapabilitySurface

\- ContextSurfaceReceipt

\- WorkflowPlan / PlanStep

\- GoalSpine

\- WorkContext

\- Observation

\- Receipt

\- Artifact

\- OutcomeEnvelope

\- orchestration / checkpoint concepts

\- PlanExecutionValidator / effect-policy / host checks

Again, these are source-reported architecture labels, not current code verified by this cloud session.

The consolidation report says Agentica already distinguishes host-owned hidden/domain state from the public-safe planning/tool surface. A reported capability-surface equation is:

Context \+ Scope \+ Actor \+ Goal \+ State \+ Policy \+ Recipes \+ Receipts

  → ActiveCapabilitySurface

The planner should receive the compiled live surface, not the raw inventory of everything the host knows.

11\. Agentica: ZPD / guidance model

The Agentica design vocabulary includes an agentic Zone of Proximal Development and a guidance ladder:

FactsOnly

ClassifiedOptions

RankedOptions

PolicyRecommended

HostControlled

Scope answers “where is cognition occurring?”

ZPD answers “how much assistance is needed there?”

The context compiler materializes that assistance.

Scaffolding can vary:

\- amount/type of context;

\- thin versus thick capability surface;

\- classified/ranked options;

\- tool abstraction level;

\- single versus staged cognition;

\- model/reasoning effort;

\- decomposition/orientation;

\- examples/counterexamples;

\- evaluator/rubric;

\- retrieval depth;

\- research permission;

\- stop/escalation rules.

Hard policy is the fence. ZPD scaffolding is the leash. The leash never widens the fence.

12\. Domain topology and scope

The consolidation report refines Domain of Domains as:

\- Domain — durable semantic/jurisdictional node, not necessarily a process or storage container.

\- Domain attribution — typed provenance-bearing relationship between canonical state/configuration and one or more domains.

\- Scope — compiled semantic projection for the current operation.

\- Agent scope node — activated domain boundary requiring independent judgment/context/policy/memory/evaluation/audit.

\- Capability/tool — execution surface.

\- Workflow — orchestration boundary.

A scope projection may use a primary semantic path plus weighted facets.

The scope path is a semantic program counter / join key. It may coordinate retrieval, memory activation, policy resolution, capability binding, model/stage selection, prompt priming, evaluation, artifact attribution, telemetry, and adaptation statistics.

It is NOT an authority token.

Effective cognitive scope must never widen authorization:

AuthorizedCognitiveScope

  \= HardAuthorizationScope ∩ SoftSemanticProjection

A classifier-generated domain/scope result is a low-authority hypothesis, not an authoritative route or tool grant.

Safe shape:

bounded classifier artifact

  → confidence \+ alternatives \+ provenance

  → deterministic scope/policy resolver

  → authorized effective scope

  → compiled frame/surface

13\. Agent activation and governed adaptation

The durable unit is not a permanently running “agent.” A model invocation is a temporary activation inhabiting a compiled frame. Durable semantic jurisdiction, approved cognitive specification, state, and evidence exist outside the activation.

Self-authoring must remain separate from self-sovereignty:

trace

  → candidate change

  → evaluation/security review

  → explicit promotion

A model may propose prompt/context/tool/profile/topology changes. It must not silently promote them into trusted policy, capability grants, or durable truth.

14\. Shared primitive and likely ownership boundary

Both designs need:

\- objective / operation contract;

\- bounded provenance-bearing frame;

\- legal capability surface;

\- stage or mode contract;

\- deterministic validation;

\- authoritative external state owner;

\- receipts/evidence;

\- continuation, revision, or termination semantics.

Rich persistence does not imply rich runtime algebra.

The strongest default is:

\- Nyx owns companion identity, participant/relationship semantics, memory ledger, historical backfill, delivery/transport, and host-specific lifecycle.

\- Agentica remains domain-neutral and consumes bounded frames/capability surfaces when generic execution is useful.

\- Do not import MemoryEngram, persona facets, ViaPath state, Nyx stage names, database/event-sourcing requirements, or participant ontology into Agentica core.

\- Do not make Nyx depend on Agentica merely because both implement a bounded cognition loop.

\- Consider shared extraction only after repeated proof from at least two concrete hosts.

15\. Concepts that may be equivalent primitives under different names

Evaluate these without forcing one-to-one mappings:

Nyx

\- ContextBaseSnapshot

\- ContextCallInputManifest

\- ContextActivation

\- InferenceContextFrame

\- ContextProfile

\- ContextCandidateDecision / selected ContextCell

\- TurnOrientation / EvaluationResult

\- memory/persona/context receipts

Agentica

\- RunRequest / PlanningRequest

\- PlanningFrame

\- ToolSurfaceSnapshot

\- ActiveCapabilitySurface / ContextSurfaceReceipt

\- WorkflowPlan / PlanStep

\- GoalSpine

\- Observation / Receipt / Artifact

\- OutcomeEnvelope

\- WorkContext

\- orchestration/checkpoint concepts

Possible shared semantics include:

\- canonical host state versus compiled inference state;

\- exact bounded input manifest;

\- stage/mode metadata;

\- model proposal versus deterministic acceptance;

\- capability visibility and authorization;

\- selected versus suppressed inputs;

\- provenance and chronology;

\- receipt-backed transition;

\- continuation/completion.

Ownership and lifecycle differences matter more than naming similarity.

16\. Known risks / design failure modes

Evaluate the proposed common contract against:

\- atom explosion: every persistence record becomes a runtime class;

\- hidden second planner: classifier silently decides routes/tools/authority;

\- semantic hysteresis: model output reinforces the classifier/context that caused it;

\- scope/authority confusion;

\- context prison: wrong scope prevents recovery;

\- provenance laundering through summaries/model reports;

\- self-confirming persona/user models;

\- historical-ingestion collapse;

\- plan/telemetry confusion;

\- unbounded architectural archaeology;

\- premature shared-package extraction.

17\. What this cloud run CANNOT establish

Without repository/runtime access, this run cannot establish:

\- current branches, commits, dirty worktrees, or file contents;

\- whether Nyx Phase 1 has landed or changed since the source report;

\- whether Agentica types have been renamed, removed, or refactored;

\- current test results or migration state;

\- whether any candidate common abstraction already exists in code;

\- whether implementation complexity makes a proposed simplification cheaper or more expensive than it appears on paper.

Do not compensate by pretending.

REQUIRED QUESTIONS

A. What overlap is strongly supported at the architecture level?

B. Which concepts appear to be the same primitive under different names?

C. Where do ownership/lifecycle differences make apparent mappings false?

D. What must remain Nyx/host-specific?

E. What must remain Agentica/domain-neutral?

F. What can be deleted, collapsed, or represented as metadata rather than a new runtime type?

G. Is any new Agentica core abstraction justified by the supplied evidence alone?

H. What is the smallest common frame-to-transition contract?

I. What evidence would a later repository-enabled audit need to confirm or falsify the architecture-level conclusions?

J. What single deterministic proof harness best tests the shared contract without importing companion vocabulary?

REFINEMENT LIMIT

Recommend no more than three refinements.

For each proposed refinement give:

\- problem supported by embedded evidence;

\- existing conceptual owners/types;

\- smallest proposed change;

\- why existing concepts may be insufficient;

\- what remains host-owned;

\- compatibility/coupling risk;

\- proof harness;

\- acceptance tests;

\- explicit non-goals;

\- what repository evidence is still required before implementation.

If the evidence does not justify a refinement, say so. “No core change; document a host/compiler pattern” is a valid and preferred outcome when supported.

MINIMAL PROOF-HARNESS REQUIREMENT

Specify exactly one small deterministic synthetic-host harness. Do not implement it.

A useful shape is a host that:

\- owns canonical and hidden state;

\- emits a low-authority semantic-scope hypothesis;

\- deterministically resolves hard and soft scope;

\- compiles a bounded frame and capability surface;

\- runs the same operation at two or more guidance/ZPD levels;

\- validates one legal transition;

\- records selected and suppressed context/capability reasons;

\- proves completion with receipts;

\- demonstrates recovery from an intentionally wrong scope hypothesis.

The harness must use generic vocabulary and must not require Nyx memory objects.

DELIVERABLE

Return one self-contained report with this structure:

\# Agentica–Nyx Context Compiler Consolidation Audit — Cloud Evidence Mode

\#\# 1\. Capability and evidence boundary

State explicitly that this is an architecture-only audit from embedded evidence and that current repository state is UNKNOWN.

\#\# 2\. Executive verdict

\#\# 3\. Nyx architecture represented by the evidence pack

\#\# 4\. Agentica architecture represented by the evidence pack

\#\# 5\. Concept crosswalk

For each mapping, show:

\- Nyx concept

\- Agentica concept

\- shared semantic primitive

\- ownership/lifecycle difference

\- confidence

\- evidence label

\#\# 6\. Smallest common primitive

\#\# 7\. What Agentica already appears to model correctly

\#\# 8\. Lessons Agentica may adopt without importing Nyx domain semantics

\#\# 9\. Concepts Agentica must not import

\#\# 10\. Simplifications: what can be deleted, collapsed, or left as metadata

\#\# 11\. Options analysis

Evaluate:

\- no core change; host/compiler pattern using existing concepts;

\- small optional metadata/contract extension;

\- host-side adapter/projection outside Agentica core;

\- defer shared extraction until a second concrete host proves reuse.

\#\# 12\. At most three recommended refinements

\#\# 13\. One minimal deterministic proof harness and acceptance tests

\#\# 14\. Repository-verification plan

List the smallest concrete evidence set a later repository-enabled agent should inspect. Do not perform that inspection now.

\#\# 15\. Unknown current state / deferred questions

\#\# 16\. Embedded evidence index

STOP RULE

Do not search for more context.

Do not attempt external access.

Do not ask the user for repository or document access.

Do not perform general architecture archaeology beyond the supplied evidence.

Begin writing once you can:

\- state the shared frame-to-transition contract;

\- distinguish host-owned from runtime-owned responsibilities;

\- explain at least one false equivalence caused by lifecycle/ownership;

\- identify at least one plausible simplification;

\- state what remains unknowable without code.

The purpose of this run is to make useful architecture progress despite a broken cloud execution environment, not to spend the run rediscovering that the environment cannot access anything.

* 