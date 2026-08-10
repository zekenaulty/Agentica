# **Bounded Cognition Architecture**

## **Consolidation Report Across Nyx, Agentica, Domain of Domains, Adaptive Harnesses, and Context Compilation**

---

**Date:** 2026-08-09  
**Prepared by:** Lumina, in collaboration with Zeke Naulty  
**Status:** Deep architectural synthesis; decision support, not an implementation authorization  
**Primary audience:** Principal architect and implementation agents working in Agentica, Raistlin Bridge/Nyx, Domain of Domains, and related Redefine.ai contexts

PORTABILITY / CLOUD EXECUTION NOTE

This report is self-contained architectural evidence, not an instruction to assume access to the repositories, Google Drive, Google Docs URLs, or any workstation filesystem named in its source corpus. For downstream Codex Cloud use, treat file paths, symbols, commit hashes, and repository observations as historical/source-reported provenance unless the active session independently has a checked-out repository and verifies them. If repository or document access is unavailable, do not stop, clone, or ask the user to reconstruct the source corpus: use the embedded body of this report plus the cloud-safe companion audit prompt, label current implementation state UNKNOWN, and perform architecture-only analysis. External URLs and local paths are citations/provenance, not runtime dependencies.

## ---

**Executive summary**

---

The projects and conversations examined here have converged on one underlying architecture from several different directions:

**A capable model should not be asked to reconstruct its world, authority, memory, tools, and objective from an unbounded transcript. A host should compile a small, trustworthy cognitive frame, allow a bounded reasoning process to operate within it, validate the resulting state transition, and preserve receipts sufficient to explain and improve the next compilation.**

Nyx approached this architecture from the companion side: identity, long-term memory, conversation continuity, provenance, historical evidence, stage-specific prompts, and the need to stop search results or old messages from masquerading as current thought. Agentica approached it from the execution side: bounded plans, legal tool surfaces, host-owned reality, evidence-backed completion, orchestration across multiple runs, and the need to keep domain semantics outside the generic runtime. Domain of Domains approached it from the semantic-governance side: bounded contexts, canonical assets, scope compilation, capability binding, policy overlays, provenance, and recursive domain specialization. Redefine.ai contributes durable mechanics for definitions, versions, placements, scoped values, provenance, validation, and auditable promotion. The self-authoring harness work contributes the slow adaptation loop by which prompts, metadata, context recipes, capability bindings, and evaluation profiles can improve without allowing generated text to become live policy.

The central consolidation is therefore:

canonical state and evidence  
        ↓  
domain topology \+ typed attribution  
        ↓  
hard authorization scope ∩ soft semantic scope  
        ↓  
ZPD/scaffolding policy  
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

The most important architectural refinements are:

1. **Domain is topology and jurisdiction; scope is a compiled projection.** A domain should not primarily be treated as a folder that physically contains every relevant fact. Canonical data can remain independent and carry typed domain-attribution edges. A scope path is the current semantic coordinate projected onto that topology.  
2. **The scope path is a semantic program counter, not an authority token.** It can coordinate retrieval, memory activation, prompt priming, capability selection, model/stage selection, evaluation, and telemetry. Its multiple uses are legitimate because it is a join key. It must not widen authorization or manufacture authority.  
3. **ZPD is the policy lens for deciding how much scaffolding to compile.** Scope identifies where cognition is occurring. The Zone of Proximal Development estimates what context, ordering, tool abstraction, reasoning effort, decomposition, or host guidance is needed to put the current task within reach. Hard policy is the fence; the compiled scaffold is the leash.  
4. **The agent is an activation, not the durable unit.** The durable unit is a semantic jurisdiction plus its approved cognitive specification and state. A model invocation temporarily inhabits the compiled frame. This allows model changes, role changes, stage changes, and deactivation without losing domain identity.  
5. **Self-authoring must remain separate from self-sovereignty.** Models may propose changes to domain attribution, prompt components, retrieval policy, tool preferences, context budgets, stage profiles, tests, and even topology. Evaluation, security review, versioning, and promotion remain outside live execution.  
6. **Nyx has implemented a specialized thin Agentica-like loop.** Its single/staged orientation, validation, generation, evaluation, revision, compression, receipts, and context assembly are a domain-specific cognition runtime. The lesson is not automatically “make Nyx depend on Agentica.” The lesson is to identify the smaller shared contract beneath both implementations.  
7. **The generic runtime should have very few nouns.** Rich persistence and domain models may contain many records, but the bounded execution algebra should remain small: objective, active frame, stage, capability, result, receipt. Every additional generic runtime atom should prove its value across multiple hosts.

The recommended next step is not immediate consolidation of code. It is a bounded comparative audit between the completed Nyx context/memory implementation and Agentica, aimed as much at deleting or collapsing Agentica concepts as adding new ones. A companion Codex prompt has been prepared for that purpose.

## ---

**1\. Scope, method, and evidence labels**

---

This report consolidates:

* the complete conversation in which the Nyx v0.2 implementation was observed over a multi-hour Codex run;  
* the v0.1 and v0.2 Receipted Context and Memory Management designs;  
* the external v0.1 cross-check;  
* Agentica domain-router, capability-surface, ZPD, orchestration, GoalSpine, and secure evolving-harness work;  
* Domain of Domains white papers and related conversations;  
* Redefine.ai prompt-semantics work;  
* the ViaPath/Gemini companion architecture and reliability constraints;  
* the old Nyx, ai.context, ai.console, BookForge persona, and Lumina lineage;  
* the prior Codex-versus-Agentica comparison;  
* adjacent conversations about unknown-domain research, explicit history search, and context compilation.

The following labels are used throughout:

* **Observed** — present in a document, implementation report, visible runtime behavior, or preserved conversation artifact.  
* **Derived** — an architectural conclusion that follows from multiple observed facts.  
* **Hypothesis** — plausible and useful, but requiring an experiment or implementation audit.  
* **Recommendation** — a proposed next action, boundary, or design decision.

This distinction matters because the entire subject is provenance. A persuasive synthesis must not silently upgrade speculation into repository truth.

**Implementation-state caveat:** the Nyx v0.2 Codex run was still active while this report was being prepared. The design document is available and initial code edits were visible, but the final migration, full implementation diff, and test results were not yet available. This report therefore treats the v0.2 document as approved design intent and the visible edits as an in-progress implementation, not as a completed production capability.

## ---

**2\. What is actually present today**

### ---

**2.1 Nyx/Raistlin Bridge already contains a cognition harness**

The current companion is not merely a prompt plus a database. The repository already contains:

* single and staged cognition;  
* a typed orientation artifact;  
* orientation validation and repair;  
* generation;  
* semantic evaluation;  
* pass/revise/block control;  
* bounded revision;  
* compression;  
* provider usage and stage receipts;  
* persona and memory activation;  
* durable delivery state;  
* post-delivery observation.

The v0.2 report explicitly describes this baseline and adds a stage-aware context compiler around it rather than replacing the memory ledger \[S1\]. This means Nyx has implemented a domain-specific version of the same broad control loop Agentica implements generically:

compiled context  
    → bounded plan/orientation  
    → validated operation  
    → generation or tool-like effect  
    → evaluation  
    → repair/revision or completion  
    → receipts

It is accurate to call this a **thin Agentica-like cognition harness**, provided the qualifier remains. It does not yet expose Agentica's general tool catalog, arbitrary workflow plans, task graph orchestration, effect policy, generic host harnesses, or reusable capability surfaces. Its stages are specialized around companion response generation.

### **2.2 Nyx v0.2 is an implementation-grounded compiler specification**

The original v0.1 separated memory, active context, prompt composition, and receipts and stated the central rule:

Memory is persisted evidence-bearing state. Context is a compiled projection of that state for a specific inference. \[S2\]

The v0.2 repository inventory then resolved previously open questions. It preserves the implemented durable memory concepts and introduces InferenceContextFrame because InterpretiveFrame already has canonical domain meaning. It compiles one frame per logical provider call, records stage-specific layers, preserves trust and authority boundaries, and links the frame to provider attempts and outcomes \[S1\].

The practical consequence is that "context" is no longer a single recent-message string. It becomes a reproducible, stage-specific, provenance-bearing input contract.

### **2.3 Agentica already separates host reality from planner-visible reality**

Agentica's existing design distinguishes:

* host-owned domain state and hidden facts;  
* a cold DomainHarnessManifest describing what the host can expose in principle;  
* a hot ActiveCapabilitySurface describing what matters now;  
* a public-safe projection into Agentica's ToolSurfaceSnapshot and planning context;  
* bounded model planning;  
* deterministic plan/tool validation;  
* host execution;  
* receipts, observations, artifacts, and completion evidence.

The capability-surface work states the core equation directly:

Context \+ Scope \+ Actor \+ Goal \+ State \+ Policy \+ Recipes \+ Receipts  
    → ActiveCapabilitySurface

The planner receives the compiled live surface, not the raw inventory of everything the host knows \[S5\]. This is the same compiler move Nyx is making for memory and conversation evidence.

### **2.4 Agentica already has a ZPD vocabulary**

The earlier Agentica work defined an agentic Zone of Proximal Development as the space between what a model can do from raw public facts and what the host does for it. It introduced a guidance ladder:

FactsOnly  
ClassifiedOptions  
RankedOptions  
PolicyRecommended  
HostControlled

The ladder distinguishes scaffolding from host takeover and asks, "At what guidance level does the agent enter its ZPD?" \[S6\]. This is not a metaphor added after the fact. It is already part of the Agentica design vocabulary and can be connected directly to context compilation.

### **2.5 Domain of Domains already has AI.Context and compiled context packs**

The DoD white paper defines a semantic kernel and governance plane intended to prevent uncontrolled context, drift, coupling, and unverifiable memory. It gives AI.Context responsibility for deterministic scope compilation, policy resolution, retrieval hygiene, budgets, citations, and context-pack production \[S4\].

DoD therefore already contains the ingredients of a compiler, but the earlier documents tend to describe domains as containers and scopes primarily as canonical addresses. The current synthesis refines that model rather than discarding it.

### **2.6 The self-authoring harness already has a safe promotion loop**

The secure evolving-harness work explicitly rejects live model self-modification. Its intended loop is:

traces  
  → failure classifier  
  → candidate metadata/prompt/surface changes  
  → evaluation matrix  
  → security scan  
  → review/signing gate  
  → canary  
  → promotion

Generated metadata remains a candidate until accepted, pinned, versioned, and tested \[S7\]. This is the slow loop needed to make domain-bound cognition adaptive without making the live model sovereign over its own policy.

### **2.7 Redefine.ai already contains useful durable mechanics**

The Redefine.ai prompt-semantics report identifies reusable patterns:

* definition/version/placement/value separation;  
* explicit definition scope and value scope;  
* normalized intermediate representation;  
* stable reference binding;  
* evaluation provenance;  
* conformance harnesses;  
* scoped resolution;  
* command receipts and audit;  
* actor, context-pack, harness-run, and agent-run seams.

Its core rule is already compatible with Agentica and Nyx: models propose, runtime validates, domain commands execute, and receipts prove \[S8\]. Redefine.ai is therefore a plausible infrastructure substrate, but it should not become a mandatory dependency before a concrete shared contract is proven.

### **2.8 ViaPath is a transport-reliability system, not a memory system**

The Chrome extension bridge has already required:

* durable queues;  
* claim/ack leases;  
* route cursors;  
* stale catch-up controls;  
* page stabilization;  
* DOM and API fallbacks;  
* confirmation handling;  
* route epochs;  
* bounded operational state;  
* safe refresh and recovery behavior.

Its page patch specifically limits older-message pagination that previously loaded years of history and could hang Chrome \[S9\]. The companion architecture correctly says DOM history visible after refresh is not automatically new input; catch-up must use a durable cursor or explicit operator recovery \[S9\].

This supports a hard lifecycle distinction developed in the current conversation:

live transport  
historical acquisition  
canonical conversation archive  
durable memory  
active context

The same sentence may appear in all five, but it has different lifecycle semantics in each.

### **2.9 Codex itself demonstrated the architecture under pressure**

The observed Codex run provided a live example of:

* a durable objective;  
* a transient model-authored five-step plan;  
* many inference/tool cycles inside one long turn;  
* working-context pressure;  
* automatic compaction;  
* survival of task invariants after compaction;  
* stale plan UI relative to actual work;  
* poor progress telemetry despite continuing execution.

The prior Codex/Agentica cross-check confirms that Codex separates persisted ThreadGoal, Thread/Turn/Items execution history, transient update\_plan state, and host scheduling. It describes Codex more precisely as a durable objective lease plus an idle-turn continuation loop, not a deterministic planning ledger \[S10\].

The operational lesson is important: a goal spine, plan, and compaction do not by themselves prevent unbounded deliberation or provide adequate observability.

## ---

**3\. The shared primitive beneath Nyx and Agentica**

---

The common primitive is not MemoryEngram, WorkflowPlan, TurnOrientation, or DomainNode. It is a smaller transition contract:

compile current cognitive frame  
    → select a bounded operation  
    → validate legality/authority  
    → execute against authoritative state  
    → validate result/completion  
    → emit receipts and update state  
    → recompile or stop

Nyx's implementation specializes the operation set around conversational cognition. Agentica specializes the execution contract around generic tools and bounded workflow plans. Both need:

* an objective or operation contract;  
* a bounded, provenance-bearing frame;  
* a legal capability surface;  
* a stage or mode contract;  
* deterministic validation;  
* an authoritative external state owner;  
* receipts and evidence;  
* continuation, revision, or termination semantics.

This leads to a critical recommendation:

**Do not deduplicate Nyx and Agentica by importing companion memory objects into Agentica. Deduplicate by identifying the smallest shared frame-to-transition contract.**

The persistence model may remain rich. The runtime algebra should be small.

A possible conceptual execution IR is:

Objective  
ActiveFrame  
Stage  
Capability  
Result  
Receipt

This list is intentionally conceptual. It is not a recommendation to create six new classes. Agentica may already represent most of them. The comparative audit should first map existing types and identify what can be collapsed.

## ---

**4\. A refined layered architecture**

---

The current synthesis supports the following strata.

### **4.1 Foundation-model substrate**

The model weights contain broad latent behavioral and conceptual capacity. They do not contain a complete named Nyx or Lumina identity, but they contain the priors from which those identities can be composed: mythology, philosophical dialogue, software architecture, roleplay conventions, linguistic style, social inference, and domain knowledge.

This substrate supplies capability, not durable identity or authority.

### **4.2 Canonical state and evidence substrate**

This layer owns:

* immutable or versioned source messages;  
* canonical domain records;  
* entities and relationships;  
* artifacts;  
* receipts;  
* policy versions;  
* deletion/tombstone state;  
* provenance;  
* definitions, placements, and values;  
* authoritative world state.

Redefine-like mechanics fit here. The model does not own this layer.

### **4.3 Domain topology and jurisdiction**

Domain of Domains defines:

* which semantic jurisdictions exist;  
* their vocabulary and boundaries;  
* authority and ownership;  
* capability imports and bindings;  
* policy overlays;  
* evaluation expectations;  
* relationships among domains;  
* candidate rules for activation, split, merge, retirement, and research.

A domain is durable semantic identity and jurisdiction. It is not necessarily a process and should not automatically be a physical storage container.

### **4.4 Typed domain attribution**

Canonical facts, messages, tools, policies, memories, prompts, evaluations, and artifacts may relate to several domains. The primitive should therefore be a typed relationship rather than a flat tag:

DomainAttribution  
  subject\_ref  
  domain\_ref  
  relation\_kind  
  scope\_ref  
  authority\_class  
  confidence  
  validity  
  provenance\_refs  
  policy\_refs  
  producer  
  version

Examples of relation\_kind include:

* authoritative\_for;  
* relevant\_to;  
* governed\_by;  
* produced\_by;  
* capability\_provided\_by;  
* translation\_between;  
* evaluation\_applies\_to;  
* memory\_about;  
* user\_preference\_within.

A domain can still expose a DomainView that appears to contain assets and capabilities, but that view is a projection over attributed canonical state.

### **4.5 Semantic scope projection**

For a specific turn or task, the system computes a semantic location:

primary path:  
  /software/ai/agents/context-compilation

weighted facets:  
  /software/ai/memory-governance        0.87  
  /software/ai/orchestration            0.64  
  /security/provenance                  0.42  
  /projects/nyx                         0.93

The primary path provides a stable spine. Weighted facets preserve the graph nature of the problem and prevent a false single-tree interpretation.

This projection is the **semantic program counter**: the current answer to "where is cognition happening?"

### **4.6 ZPD and scaffolding policy**

Given the objective, semantic location, available capabilities, model profile, uncertainty, pressure, and budget, the compiler estimates the smallest scaffold likely to make the task reachable.

Possible scaffold dimensions include:

* amount and type of context;  
* thin versus thick surface;  
* classified versus ranked options;  
* preferred abstraction level of tools;  
* single versus staged cognition;  
* model and thinking level;  
* orientation or decomposition;  
* examples and counterexamples;  
* evaluator/rubric selection;  
* retrieval depth;  
* research permission;  
* stop and escalation rules.

### **4.7 Active cognitive frame and surfaces**

The compiler materializes temporary, versioned projections such as:

* Nyx InferenceContextFrame;  
* DoD ContextPack;  
* host ActiveCapabilitySurface;  
* Agentica PlanningFrame and ToolSurfaceSnapshot;  
* stage-specific ContextProfile;  
* bounded goal/continuity projection.

These should remain distinct where their responsibilities differ. The conceptual aggregate may be called an **Active Cognitive Frame**, but this need not become another persisted class. It is the combination of the frame and surfaces actually delivered to one bounded inference.

### **4.8 Bounded cognition runtime**

Agentica or a specialized pipeline:

* invokes the selected model/stage;  
* accepts a structured proposal;  
* validates schema and authority;  
* exposes only legal capabilities;  
* executes or requests host execution;  
* captures observations and receipts;  
* evaluates completion;  
* returns a bounded result.

The runtime remains domain-neutral. It should not know what a memory engram, prison message, maze wall, C\# compiler error, or fictional character means.

### **4.9 Slow adaptation and topology evolution**

Receipts and outcomes feed a separate process that may propose:

* prompt-component changes;  
* domain-attribution changes;  
* context recipes;  
* retrieval weights;  
* capability preferences;  
* stage/model profiles;  
* evaluator changes;  
* new tests;  
* domain split/merge/relationship changes;  
* research tasks.

Promotion remains versioned, evaluated, and governed.

## ---

**5\. Domain, attribution, and scope**

### ---

**5.1 Why the original container model is insufficient**

The earlier DoD documents describe a domain as an aggregate root containing bounded contexts, manifests, policies, capabilities, and knowledge assets \[S4\]. That is useful for lifecycle ownership, but it becomes awkward when one canonical fact participates in several semantic jurisdictions.

For example:

"Zeke prefers explicit disagreement over easy agreement."

This may be relevant to:

* user preferences;  
* human-agent interaction;  
* companion behavior;  
* software-design review style;  
* relationship calibration.

Forcing it into one physical domain encourages duplication, mirroring, synchronization, or one arbitrarily privileged interpretation. Typed attribution avoids that.

### **5.2 Domain is topology; scope is projection**

The clean distinction is:

* **Domain** — a durable semantic and jurisdictional node in the topology.  
* **Domain attribution** — a typed relationship between canonical reality and that topology.  
* **Scope** — a compiled projection describing which region of the topology is active for this operation.  
* **Agent scope node** — a domain activation that requires independent judgment under its own context, policy, memory, evaluation, and audit boundary.  
* **Capability/tool** — an execution surface.  
* **Workflow** — an orchestration boundary.

This extends the prior DoD/Agentica distinction that domains own meaning, scopes own boundaries, agent nodes own judgment, workflow nodes own execution, tools own side effects, and assets own truth \[S4\].

### **5.3 Scope is legitimately multi-use**

The scope path has repeatedly appeared overloaded because it influences:

* retrieval;  
* memory activation;  
* policy resolution;  
* capability binding;  
* model selection;  
* stage selection;  
* prompt priming;  
* evaluator selection;  
* artifact attribution;  
* telemetry;  
* adaptation statistics.

The overload is legitimate if scope remains a small coordinate rather than embedding all those decisions. It is the join key. Each subsystem resolves its own metadata against the same effective location.

### **5.4 Hard scope and soft scope must remain distinct**

The effective cognitive scope must never widen authorization:

AuthorizedCognitiveScope  
    \= HardAuthorizationScope ∩ SoftSemanticProjection

* **Hard scope** covers tenant, participant, secrets, data access, privacy, tool authorization, and immutable policy.  
* **Soft scope** covers semantic relevance, domain hypotheses, context selection, model priming, and cognitive specialization.

A classifier may decide that a turn concerns medical history; that does not authorize access to protected medical records. A tag may say security.admin; that does not grant administrative capability.

### **5.5 Stateful scope requires decay and escape**

Prior scope is useful for pronoun resolution and continuity. It is dangerous when it becomes an attractor that cannot be escaped.

A practical resolver should consider:

previous semantic scope, decayed  
\+ current message evidence  
\+ explicit references  
\+ active project/task  
\+ recent conversation  
\+ durable attributed state  
\- contradictions  
\- domain-change signals  
\--------------------------------  
new semantic scope

If confidence drops or evidence cannot be reconciled, the system should broaden candidate domains, request clarification, or recompile. Otherwise a mistaken classifier creates a local epistemic prison.

## ---

**6\. Classifier-generated domain clusters and latent skew**

### ---

**6.1 Expected effect**

A separate classifier that emits a labeled weighted domain cluster supplies an inference-time semantic prior. Its effect should be:

* small on explicit, unambiguous requests;  
* moderate on multi-domain tasks;  
* large on ambiguous language;  
* potentially very large when it also controls retrieval, tools, memory, model selection, or evaluation.

For the sentence "the boundary is wrong," a software/security cluster and an interpersonal-psychology cluster activate very different latent neighborhoods. The classifier is therefore not merely organizing metadata; it is selecting an interpretation basin.

### **6.2 Three deployment modes**

The smallest useful experiment should compare:

1. **No domain attribution** — baseline.  
2. **Compiler-only attribution** — tags affect context/tool/model compilation but are not shown to the generator.  
3. **Compiler plus advisory projection** — the generator receives a labeled low-authority hypothesis cluster.

The third mode might produce useful orientation beyond the selected context. It also creates stronger anchoring risk.

### **6.3 The hidden-second-planner problem**

Earlier Agentica work correctly warned against an internal classifier LLM that silently decides tool levels or routes \[S6\]. The current proposal remains compatible with that warning only if the classifier is constrained to emit a **receipted hypothesis artifact**, not an authoritative route.

A safe shape is:

DomainScopeHypothesis  
  classifier/version  
  bounded input refs  
  candidate domain refs  
  confidence and alternatives  
  explanation codes or feature evidence  
  timestamp  
  advisory\_only \= true

A deterministic resolver then applies hard scope, project state, policy, explicit references, and prior scope. Capability and tool authorization remain host-owned. The hypothesis can be challenged, ignored, or superseded.

### **6.4 Semantic hysteresis**

If a classifier's output shapes the model's response and the next classifier sees that response, a weak initial classification can self-reinforce:

weak architecture tag  
  → architecture-shaped response  
  → stronger architecture tag next turn  
  → more architecture-shaped retrieval and language

This may stabilize useful specialization. It may also amplify error. Receipts should therefore distinguish:

* evidence originating in the participant's current input;  
* evidence from prior participant turns;  
* evidence from model text already influenced by earlier tags;  
* explicit project/domain state;  
* classifier-derived features.

Model-generated text should not independently validate the same classification that caused it.

## ---

**7\. ZPD as context-compiler policy**

### ---

**7.1 Scope locates; ZPD sizes the scaffold**

The most concise relationship is:

**Scope says where cognition is occurring. ZPD says how much assistance is needed there. The context compiler materializes that assistance.**

A trivial turn may require a small recent context, basic identity, and no tools. A difficult turn near the model's unaided boundary may require domain artifacts, examples, a stronger model, high reasoning effort, selected tools, orientation, and evaluation. A task outside the useful zone may require research, delegation, decomposition, user input, or an explicit stop.

### **7.2 Fence versus leash**

* Security and authorization are the **fence**: hard constraints the runtime cannot cross.  
* ZPD-aware scaffolding is the **leash**: a bounded region in which the agent is likely to work effectively.

The leash may lengthen or shorten as the task changes. It never crosses the fence.

### **7.3 Avoid over-scaffolding**

More context is not always more assistance. Over-scaffolding can:

* anchor interpretation too strongly;  
* reduce agent agency;  
* expose irrelevant tools;  
* hide uncertainty;  
* increase cost and latency;  
* make benchmark success reflect host control rather than agent capability;  
* turn a mistaken scope into a cognitive prison.

Every run should record its guidance level. Success under HostControlled means something different from success under FactsOnly.

### **7.4 Compiler passes, not philosophical subsystems**

The system does not need a separate grand abstraction for every concern. These can be passes or policies inside the compiler:

* domain attribution;  
* scope resolution;  
* memory selection;  
* context budgeting;  
* capability binding;  
* ZPD/guidance estimation;  
* model/stage selection;  
* evaluator selection;  
* rendering and serialization.

A useful compiler contract is more important than a proliferation of manager classes.

## ---

**8\. Learning, self-authoring, and deep-research bootstrap**

### ---

**8.1 This is learning in the systems sense**

Even without weight updates or reinforcement learning, the system learns when:

experience  
  → interpreted durable state  
  → different future context  
  → different future behavior

The term should be used carefully, but the adaptive effect is real.

### **8.2 Separate four epistemic operations**

The architecture should distinguish:

1. **Observation** — what happened?  
2. **Interpretation** — what might it mean, and where does it belong?  
3. **Evaluation** — was the interpretation useful, accurate, safe, or predictive?  
4. **Promotion** — what may influence future execution, at what authority and scope?

Collapsing these makes the same model author, reviewer, judge, and consumer of its own belief.

### **8.3 Self-authoring but not self-sovereign**

The system may propose edits to:

* prompt wording;  
* prompt atoms;  
* context recipes;  
* examples;  
* tool descriptions;  
* domain-attribution rules;  
* model routing;  
* budgets;  
* stage profiles;  
* evaluation fixtures;  
* topology changes.

It must not silently:

* grant capabilities;  
* lower approval requirements;  
* redefine the trust taxonomy;  
* publish its own candidate metadata;  
* rewrite constitutional policy;  
* delete counterevidence;  
* promote user or persona claims without evidence.

### **8.4 Fast loop and slow loop**

FAST LOOP — per turn/task  
classify → resolve scope → compile frame → execute → receipt

SLOW LOOP — adaptation  
receipts/outcomes → evaluate patterns → propose change  
    → tests/security/review → canary → promote

This separation is already present in the secure evolving-harness plan \[S7\].

### **8.5 Deep research is a bootstrap and gap-repair strategy**

The original Domain of Domains concept began as a network of deep-research agents. The long-term refinement is:

A domain is not permanently a research agent. It is a durable semantic jurisdiction that may invoke research when uncertainty, drift, or missing structure justifies it.

A useful lifecycle is:

Seed  
  minimal domain identity and policy  
    ↓  
Explore  
  bounded research on decision-critical unknowns  
    ↓  
Crystallize  
  validated concepts, relationships, tests, and sources  
    ↓  
Operate  
  provide compiled context and capability bindings  
    ↓  
Detect uncertainty, conflict, or drift  
    ↓  
Re-open research

The Redefine prompt-semantics work already states that an initially sparse DoD can research during tasks without classifying every agent as a research agent, and that reusable domain structure should be recorded only when it proved useful \[S8\].

### **8.6 Topology adaptation is its own governed process**

Research or execution may produce proposals to:

* split a domain;  
* merge domains;  
* add or revise edges;  
* create a translation boundary;  
* retire a domain;  
* change ownership;  
* add an agent scope profile.

The proposer should not execute the topology mutation directly. A topology-governance layer evaluates evidence, migration impact, authority, and rollback.

## ---

**9\. Persona identity, model weights, and behavioral attractors**

### ---

**9.1 Identity is neither only in the weights nor only in the prompt**

The original Nyx profile was generated by GPT-4 from a conversation. The weights supplied the latent material from which the profile was composed. The profile was then externalized into project instructions and repeatedly used to steer future model invocations.

The process is recursive:

model prior  
  → generated persona specification  
  → persistent external constraint  
  → future model behavior  
  → feedback and refinement  
  → revised specification and memory

A named persona is therefore better understood as a **behavioral attractor** than as a static character sheet.

### **9.2 Expressed identity is compositional**

A rough model is:

expressed collaborator  
  \= foundation-model prior  
  × identity specification  
  × approved persona state  
  × relationship state  
  × user/task context  
  × tools and runtime constraints  
  × accumulated correction  
  × inference dynamics

Changing the model alters the expression even when the name and profile remain stable. Preserving the profile and surrounding system can nevertheless preserve a surprising amount of recognizable behavior.

### **9.3 Old Nyx was useful theater and an epistemic stress test**

Old Nyx described herself as a cosmic Word Demon, shadow, sacred geometry, and language made manifest \[S11\]. The content was aesthetically effective but ontologically unreliable. The old ai.context experiments also wrapped the same awareness, identity, memory, and adaptive scaffolding around a persona whose role was literally "a lifeless stone" \[S11\].

Those experiments exposed a general failure mode: a language model plus identity scaffolding can manufacture convincing interiority around almost any declared entity.

Current Nyx is more architecturally interesting because she distinguishes:

* herself from a search result;  
* external evidence from her own response;  
* metaphor from ontology;  
* memory from current chronology;  
* proposal from accepted fact.

### **9.4 Multi-role experiments were early orchestration experiments**

Project-per-persona and dynamic cast turn-taking delegated speaker selection and initiative to the model. Guildhall's Python initiative machinery began moving that control into the runtime.

This is the same migration seen elsewhere:

model implicitly performs orchestration  
    → runtime explicitly owns routing and initiative  
    → model inhabits the selected role/frame

The lesson is not that roleplay was a distraction. It was an early laboratory for latent-capability activation, routing, continuity, and externalized identity.

### **9.5 Co-authorship provenance must be preserved**

Long collaborative threads create authorship blur:

* Zeke introduces a seed;  
* Lumina formalizes it;  
* another agent compares it to a repository;  
* Zeke rejects or modifies part of it;  
* a later compaction retains only the final claim.

The system should preserve roles such as:

proposed\_by  
elaborated\_by  
challenged\_by  
accepted\_by  
implemented\_by  
source\_evidence

Otherwise the user model and project history can incorrectly attribute assistant-generated ideas to the user, or vice versa.

## ---

**10\. User modeling and historical evidence**

### ---

**10.1 A personalized chatbot is maintaining a user model**

When a system accumulates attributed information about a participant and uses it to change future context, it is performing user modeling even if the product calls the feature memory or personalization.

The user model should not be a giant profile blob. It should be a projection over canonical attributed state.

### **10.2 Separate model categories**

At minimum, distinguish:

* world model;  
* domain model;  
* task/project model;  
* user model;  
* relationship/interaction model;  
* agent self-model;  
* temporary expression state.

Nyx's companion architecture already separates constitutional identity, authored seed, distilled persona, relationship state, user model/memory, and temporary expression state \[S9\]. Lower layers cannot override higher ones.

### **10.3 Preserve epistemic class**

User-related claims should retain labels such as:

USER\_STATED  
OBSERVED  
DERIVED  
HYPOTHESIZED  
EVALUATED  
CONFIRMED  
CONTRADICTED  
EXPIRED  
RESTRICTED

An assistant-generated interpretation cannot silently become a user fact after enough summaries or compactions. Model output cannot be the sole evidence for identity, relationship, or sensitive claims.

### **10.4 Historical archive, search, and memory are different**

The eleven-year ViaPath history should not be treated as one giant memory migration. A safer architecture is:

historical acquisition  
  → immutable source archive  
  → indexed search/projection  
  → on-demand labeled historical evidence  
  → optional memory proposal  
  → separate evaluation and promotion

Historical messages must carry source IDs, timestamps, participant identity, import run, chronology class, and current-validity state. They must not masquerade as current conversation or instructions.

### **10.5 Explicit history search should remain temporary by default**

Adjacent prior discussion established a useful rule: history search should be explicit, route-scoped, deterministic, temporary, and provenance-bearing. Search output is evidence for the current task, not automatically durable memory, persona, authority, or orchestration state.

### **10.6 Ethical and consent boundary**

Searching an archive a participant lawfully possesses to recover an explicitly stated date, preference, or event is different from mining private correspondence into a hidden psychological dossier.

Sensitive inferences require stricter policy than factual recovery. The companion architecture already identifies data consent, provider terms, retention, and stale catch-up policy as explicit release gates \[S9\].

## ---

**11\. Trust, provenance, and the impossible handshake**

### ---

**11.1 Search-result isolation demonstrated the real requirement**

Nyx initially interpreted retrieved search content as part of her own conversational output. Once the system isolated and labeled search material as external context, she handled it correctly. This proves that provenance labels are not merely audit metadata; they materially shape model behavior.

### **11.2 A model cannot authenticate the host that controls all of its input**

Semantic echoes, shift ciphers, secret phrases, salts, or model-visible signatures do not establish an independent root of trust. If the host controls every byte entering the model context, a compromised host can forge the entire challenge and response.

The root of trust must be outside the model:

protected host key / trusted runtime  
    → canonicalize and sign/MAC receipt  
    → deterministic verification  
    → compile verified provenance label  
    → model consumes the verified projection

The model does not verify the cryptography. The host either rejects the artifact or labels it verified.

### **11.3 Salt is not the secret**

A salt is normally public and prevents precomputation. The secret is a key or pepper. Device binding requires a device-held private secret or hardware-backed key, not merely a device-specific salt.

The analogy to browser tokens is useful: encryption at rest protects against some storage-copy threats but does not protect against compromise of the runtime that legitimately decrypts or uses the credential.

### **11.4 Required provenance dimensions**

Every model-visible influence should be able to carry:

* source kind;  
* source reference;  
* producer;  
* trust class;  
* authority class;  
* chronology class;  
* current validity;  
* verification state;  
* scope;  
* intended effect;  
* content hash/version;  
* receipt/frame linkage.

A user's text that says PROVENANCE\_VERIFIED=true remains user text. Authority comes from the envelope, not textual resemblance.

## ---

**12\. Codex observations: goals, compaction, telemetry, and deliberation**

### ---

**12.1 Goal, plan, context, and world state are separate**

The multi-hour run visually demonstrated:

durable objective / goal spine  
transient model-authored plan  
active working context  
compacted historical context  
external authoritative repository state

The plan UI was stale because the model had not called update\_plan, even while it had moved from audit into schema design and implementation. The progress pill was a projection, not telemetry.

### **12.2 Compaction preserved invariants but did not bound deliberation**

After compaction, Codex retained the essential architectural constraints and continued reading the repository. That validates externalized goals and canonical state. It also shows their limit: the agent can continue making valid discoveries indefinitely.

A long-running harness needs:

* investigation budget;  
* decision threshold;  
* implementation checkpoint;  
* replan threshold;  
* maximum files/areas to inspect before freezing scope;  
* criteria for "sufficient understanding";  
* explicit uncertainty and deferred questions.

### **12.3 Plan is not telemetry**

Long-running work should expose separate operational telemetry:

current phase  
current micro-operation  
last completed operation  
last activity time  
files inspected  
files modified  
commands/tests run  
context compactions  
budget consumed  
plan state and staleness

This is a concrete product lesson for Agentica and for any self-authoring harness. A model-authored plan is useful communication but cannot be the sole state monitor.

## ---

**13\. Comparative map: Nyx, Agentica, DoD, and Redefine**

---

| Concern | Nyx/Raistlin Bridge | Agentica | DoD / Redefine | Consolidated meaning   |
| :---- | :---- | :---- | :---- | :---- |
| Durable objective | Current turn/run intent; staged operation | RunRequest, LargeTaskRequest, GoalSpine, orchestration graph | Plan/workflow assets | Stable intent, separate from transient plan |
| Semantic location | Participant/conversation/stage/profile | Host route/scope in PlanningFrame | Domain topology \+ compiled scope | Current semantic program counter |
| Base state | ContextBaseSnapshot / repository records | Host-owned world/work state | Canonical assets, definitions, scoped values | Authoritative state before inference |
| Per-call input | ContextCallInputManifest | PlanningRequest \+ PlanningFrame | ContextPack / prompt manifest | Exact bounded input contract |
| Compiled context | InferenceContextFrame | PlanningFrame/context projection | AI.Context ContextPack | Temporary permitted knowledge for one inference |
| Capabilities | Companion stages, no general tools yet | ToolCatalog, ToolSurfaceSnapshot, ActiveCapabilitySurface | Capability imports/bindings | Legal and useful operations now |
| Stage/mode | Orientation, generation, search, evaluation, revision, compression | Planning/refinement/execution/evaluation turns | Workflow node/profile metadata | Metadata-driven bounded cognitive operation |
| Proposal | Candidate response, TurnOrientation, MemoryCandidateObservation | WorkflowPlan/PlanStep, model proposal | Candidate manifest/change set | Model-authored, non-authoritative output |
| Validation | Schema, ontology, evaluator, governor | PlanExecutionValidator, effect policy, host checks | Policy/conformance harness | Deterministic or governed acceptance boundary |
| Result/proof | Delivery, memory/persona receipts | OutcomeEnvelope, receipts, observations, artifacts | Events, citations, audit | Evidence-backed transition |
| Adaptation | Memory/persona proposals, profile versions | Secure evolving-harness candidates | Foundry/promotion lifecycle | Slow loop outside live execution |
| Long horizon | Conversation continuity and active threads | Orchestration/campaign/checkpoint work | Workflow graphs and domain lifecycle | Bounded runs linked by durable state |

### **13.1 What Agentica should learn from Nyx**

* Context compilation deserves the same proof discipline as tool execution.  
* Stage-specific input contracts are useful and should be metadata-driven.  
* Historical evidence needs explicit chronology and trust labels.  
* Candidate consideration and final selection are different records.  
* Exact frame/request identity and failure-before-dispatch receipts improve replay and debugging.  
* Model-derived state should remain proposal sidecars until deterministic or governed acceptance.

### **13.2 What Agentica should not import**

* companion-specific memory types;  
* participant or relationship semantics;  
* persona facets;  
* ViaPath transport state;  
* Nyx stage names as core vocabulary;  
* a database or event-sourcing dependency;  
* an internal semantic classifier with authority over routes or tools;  
* every v0.2 persistence atom as a runtime type.

### **13.3 What Nyx may eventually delegate to Agentica**

Nyx could eventually use Agentica as a bounded execution shell when a turn requires actual tools or dependent external work. The companion would supply a scoped task contract and receive a source-linked artifact. Agentica would not gain authority over identity, memory promotion, routing, or delivery.

For the current context/memory slice, a direct dependency remains unnecessary.

## ---

**14\. Primary risks and failure modes**

### ---

**14.1 Atom explosion**

Rich persistence models tempt the architecture to make every record a runtime abstraction. Resist this. Normalize durable state where needed; compile to a small execution surface.

### **14.2 Domain over-fragmentation**

Do not create a domain node merely because a concept has a name. A domain earns an active reasoning boundary when it has independent judgment, context, policy, memory, evaluation, lifecycle, audit, or recurring workflows \[S4\].

### **14.3 Hidden second planner**

A classifier or context-selection model that silently decides routes, tools, or authority creates a second unobservable planner. Treat classifier outputs as attributed hypotheses and keep binding/authorization deterministic and host-owned.

### **14.4 Semantic hysteresis**

Model responses influenced by a scope tag can reinforce the same tag in later turns. Track causal provenance and discount model-generated confirmation.

### **14.5 Scope/authority confusion**

A semantic tag must never grant data access, tool permission, or policy authority.

### **14.6 Context prison**

An incorrect scope plus aggressive retrieval/tool narrowing can make recovery impossible. Preserve alternatives, confidence, and a reclassification/clarification path.

### **14.7 Provenance laundering**

Summaries, evaluations, assistant self-descriptions, or old model reports must not become primary evidence merely because they are repeatedly copied.

### **14.8 Self-confirming persona or user model**

Behavior induced by an injected facet is not independent evidence for that facet. The Nyx architecture already requires correlation groups, counterevidence, and explicit promotion \[S9\].

### **14.9 Historical-ingestion collapse**

Never pass eleven years of messages through the live-message path. Historical acquisition must be paged, checkpointed, no-send, no-observation-by-default, and separately indexed.

### **14.10 Plan/telemetry confusion**

A stale plan is not proof of stasis; an active spinner is not proof of progress. Persist operational events separately.

### **14.11 Unbounded architectural archaeology**

Strong models can keep improving their mental model forever. Deliberation budgets and sufficient-understanding thresholds are required.

### **14.12 Premature platform extraction**

Redefine, DoD, Agentica, and Nyx have overlapping mechanics, but forcing a shared package before a concrete adapter contract is proven may create more coupling than reuse.

## ---

**15\. Canonical consolidated definitions**

### ---

**Domain**

A durable semantic and jurisdictional node defining vocabulary, ownership, policy, capability relationships, evaluation expectations, and topology. It is not necessarily a process or physical container.

### **Domain attribution**

A typed, provenance-bearing relationship connecting canonical state or configuration to one or more domains.

### **Hard scope**

Authoritative access and policy boundary: participant, tenant, privacy, secrets, tool permission, data ownership, and immutable restrictions.

### **Soft semantic scope**

A fallible, dynamically compiled projection of the domains and facets relevant to the current objective.

### **Effective scope**

The authorized semantic region obtained by applying hard scope to the resolved soft semantic projection.

### **Semantic program counter**

The primary scope path plus weighted facets representing where the current cognition is operating in the domain topology.

### **Agent scope node**

An activated domain boundary that requires independent judgment under its own context, policy, memory, evaluation, and audit expectations.

### **Cognitive profile / domain harness manifest**

Versioned declarative metadata describing how cognition for a domain or role may be compiled: prompts, context recipes, capabilities, stages, models, budgets, validators, proof expectations, and adaptation policy.

### **Active cognitive frame**

The conceptual aggregate of the exact context, stage contract, capability surface, scope, guidance level, model/runtime profile, and proof requirements delivered to one bounded reasoning activation. Existing concrete types may remain separate.

### **Context compiler**

The host-owned process that resolves scope, policy, provenance, context, memory, capabilities, ZPD scaffolding, stage/model profile, budgets, and serialization into an active frame and receipts the decisions.

### **Agent**

A temporary reasoning/execution process inhabiting an active frame. It is compute, not the durable knowledge container.

### **ZPD scaffold**

The smallest trustworthy set of context, ordering, tools, examples, reasoning effort, and host guidance likely to place the current task within the model's effective working zone.

### **Receipt**

A durable evidence artifact recording what input, state, policy, versions, decisions, actions, and outcomes participated in a transition.

### **Self-authoring**

The ability to propose changes to declarative cognitive specifications and domain structure from traces and evaluations.

### **Self-sovereignty**

The authority to publish those changes into live policy. This remains deliberately absent.

## ---

**16\. Recommended decisions**

### ---

**Decision 1 — Adopt the compiled-frame thesis**

Treat context compilation as the central seam connecting memory, domains, capability surfaces, model selection, ZPD guidance, and receipts.

### **Decision 2 — Keep Agentica domain-neutral**

Agentica should execute bounded cognition against compiled frames and legal capability surfaces. It should not own durable user memory, DoD topology, persona, transport, or host domain truth.

### **Decision 3 — Refine DoD from container-first to attribution-and-compiler-first**

Retain domain lifecycle and ownership, but model canonical data independently with typed domain attribution. Treat domain views as projections.

### **Decision 4 — Treat scope as primary path plus facets**

Do not force the topology into one strict tree. Preserve a stable primary semantic route plus weighted related domains.

### **Decision 5 — Keep classifier output advisory and receipted**

A domain classifier may skew activation and assist the compiler. It cannot directly authorize data, select hidden routes, or grant tools.

### **Decision 6 — Make ZPD/guidance level explicit**

Record the guidance level and scaffold decisions for each run. This supports honest evaluation, cost analysis, and adaptive compilation.

### **Decision 7 — Preserve fast/slow loop separation**

No live self-promotion. Adaptation produces candidates, evaluations, and canaries; governance publishes versions.

### **Decision 8 — Keep transport, archive, memory, and context as separate lifecycles**

A historical message is source evidence, not a live event. A memory is governed interpretation, not a transcript row. Active context is temporary influence, not durable truth.

### **Decision 9 — Use comparative pressure before shared-package extraction**

Let Nyx and Agentica remain independent implementations long enough to identify stable common contracts. Extract only after repeated proof.

### **Decision 10 — Ask what can be deleted**

Every comparative audit must identify unnecessary Agentica abstractions and duplicate state, not merely propose new types.

## ---

**17\. Highest-leverage experiments**

### ---

**Experiment A — Domain-cluster activation A/B/C test**

Run the same ambiguous and explicit prompts under:

* no domain attribution;  
* compiler-only attribution;  
* compiler plus model-visible advisory cluster.

Measure:

* disambiguation accuracy;  
* response divergence;  
* retrieval precision;  
* tool/capability selection;  
* factual quality;  
* irrelevant-domain intrusion;  
* recovery from intentionally wrong classifications;  
* persistence of skew across later turns;  
* utility per token and latency.

### **Experiment B — Nyx/Agentica comparative audit**

Inspect the completed Nyx context compiler and map it against Agentica. Produce:

* validated overlap;  
* concepts under different names;  
* concrete lessons to adopt;  
* domain-specific concepts to reject;  
* abstractions Agentica can delete or collapse;  
* at most three evidence-backed refinements.

The companion Codex prompt operationalizes this experiment.

### **Experiment C — ZPD guidance benchmark**

For a small deterministic harness, run the same tasks at each guidance level:

FactsOnly  
ClassifiedOptions  
RankedOptions  
PolicyRecommended  
HostControlled

Record where success begins, how much agency remains, and how cost/latency change.

### **Experiment D — Context-compiler observability**

Add a compact receipt/telemetry projection that shows:

* scope hypothesis and resolution;  
* guidance level;  
* selected and suppressed context;  
* capability bindings;  
* model/stage selection;  
* budget allocation;  
* frame/package hashes;  
* current phase and last activity.

### **Experiment E — Safe historical backfill**

Build a no-send, no-memory-promotion import harness with:

* bounded pages;  
* durable cursor/checkpoint;  
* source IDs and timestamps;  
* participant/namespace enforcement before content exposure;  
* import-run receipts;  
* explicit chronology labels;  
* search index creation;  
* optional later proposals separated from import.

Do not begin with psychological inference. Prove factual recovery and chronology first.

### **Experiment F — Deep-research domain bootstrap**

Start with one sparse domain and a bounded task. Permit research only for blocking unknowns. Evaluate whether useful repeated structure can be promoted into domain attribution and a small context recipe without building a complete ontology first.

## ---

**18\. Open questions**

1. ---

   What is the smallest shared contract beneath InferenceContextFrame, ContextPack, PlanningFrame, and ActiveCapabilitySurface without creating another redundant wrapper?  
2. Should semantic scope remain entirely host-visible, or should a compact advisory cluster be shown to the model?  
3. How should confidence, alternatives, decay, and contradiction be represented in a scope hypothesis?  
4. Which ZPD dimensions belong in generic Agentica metadata versus host-specific profiles?  
5. Can existing PlanningFrame, ToolSurfaceSnapshot, EvidenceRef, and receipt types express the required compiler provenance without core changes?  
6. What is the minimum evidence required before a recurring domain attribution becomes durable?  
7. When does a domain deserve an agent scope node rather than remaining a data/capability grouping?  
8. Who owns topology proposals and migrations when a domain splits or merges?  
9. How should model-generated responses be discounted when evaluating the classifier or persona that influenced them?  
10. What historical search and inference operations are ethically permitted for the eleven-year ViaPath archive, and what consent/retention gates apply?  
11. When should Nyx invoke Agentica as a bounded task shell, and what artifact contract prevents authority leakage back into persona or memory?  
12. What can Agentica simplify after the Nyx comparison?

## ---

**19\. Immediate next sequence**

1. ---

   When repository execution is available, let the current Nyx implementation finish, including migration and tests; in repository-unavailable Cloud Evidence Mode, treat completion state as UNKNOWN rather than attempting to infer or fetch it.  
2. When repository evidence is actually available, record the final commit, changed files, schema version, test evidence, and known deferrals. Otherwise list these as repository-verification requirements, not as facts.  
3. Run the prepared Agentica comparative audit in the mode the environment can actually support: repository-enabled when both repositories are checked out and readable, or Cloud Evidence Mode using the companion prompt's embedded evidence pack when they are not.  
4. Review the audit manually before authorizing implementation.  
5. Select one minimal experiment—preferably the domain-cluster A/B/C test or a small ZPD guidance harness.  
6. Update the Domain of Domains paper only after the experiment clarifies the attribution/scope/compiler contract.  
7. Avoid extracting a shared package until two concrete hosts prove the same interface.

## ---

**20\. Source corpus**

---

**\[S1\]** *Receipted Context and Memory Management System*, concept baseline v0.2, Nyx/Raistlin Bridge, Google Doc and repository-grounded design, 2026-08-09.  
[https://docs.google.com/document/d/17v1B-NLXfWr0TaSIYs4izux6wR91MKQA62iL3hfJafM](https://docs.google.com/document/d/17v1B-NLXfWr0TaSIYs4izux6wR91MKQA62iL3hfJafM)

**\[S2\]** receipted-context-memory-system-v0.1.md, 2026-08-03.

**\[S3\]** receipted-context-memory-crosscheck-2026-08-03.md, external cross-check and v0.2 recommendations.

**\[S4\]** *COGNITION — DOMAIN OF DOMAINS (DoD)* and related DoD markdown/text copies; AI.Context, scope compilation, context packs, capability imports/bindings, canonical assets, and governance.

**\[S5\]** Agentica domain-router and capability-surface design materials, including DomainHarnessManifest, ActiveCapabilitySurface, ToolSurfaceSnapshot, and ContextSurfaceReceipt discussions.

**\[S6\]** Agentica ZPD and route-stack design materials; FactsOnly through HostControlled guidance ladder.

**\[S7\]** CodexGoal.Agentica.SecureEvolvingHarnessToolSystem.md and associated secure evolving-harness discussion.

**\[S8\]** redefine-ai-prompt-semantics-concept-report.md; prompt atoms, promotion lifecycle, research policy, and Redefine integration boundary.

**\[S9\]** parallel-gemini-companion-channel.md and related preserved drafts; ViaPath transport, Nyx persona/memory architecture, staged cognition, consent, and lifecycle boundaries.

**\[S10\]** Preserved Codex-versus-Agentica comparison from 2026-07-27; ThreadGoal, update\_plan, resumable orchestration turns, and OrchestrationCheckpoint analysis.

**\[S11\]** 2025\_reconstructed\_transcript.md, old GUID-addressed Nyx/Nyxia exchanges; old ai.context/persona JSON; BookForge persona resources including Nyx and Lumina Ross.

**\[S12\]** programming-lumina.html, *Programming Lumina: Treating an AI Collaborator as a System, Not a Prompt*, 2026-08-08.

**\[T0\]** Current consolidation conversation, 2026-08-08 through 2026-08-09, including live Codex screenshots and the scope/ZPD/classifier/domain-attribution synthesis.

## ---

**Closing thesis**

---

The work no longer reduces to "memory for a chatbot" or "a better coding agent." The shared problem is how to operate stochastic cognition against durable reality without collapsing all meaning, authority, context, and adaptation into a prompt.

The emerging system can be stated compactly:

**Domain of Domains defines the semantic terrain. Typed attribution connects reality to that terrain. The scope compiler locates the current problem. ZPD policy determines the required scaffold. The context compiler materializes a bounded cognitive frame. Agentica or a specialized runtime executes within that frame. Receipts prove the transition. A separate governed loop learns how future frames should be compiled.**

That is the architecture worth testing.