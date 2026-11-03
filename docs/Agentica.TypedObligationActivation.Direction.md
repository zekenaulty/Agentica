# Typed obligation activation direction

Status: **approved architecture direction, planning only** (2026-10-04). This note records decisions from the [approved refinement](https://docs.google.com/document/d/1VEKHUVs9pSkJZIs4ODgFclXjoEsN73KHb4EKmIXw5ew/edit) and relates them to this checkout. It does not claim an obligation compiler, durable activation store, or performance improvement exists. [Product Status](Agentica.ProductStatus.md) remains the authority for product lifecycle and completion.

## Contract to preserve

The durable recursive unit is an obligation to satisfy a versioned acceptance contract for a subject within a scope and perspective, using admissible evidence and, when needed, a compatible realization. The result carries typed evidence and unresolved obligations. Obligation identity and governing objective lineage survive a change in workflow, tool, model, provider, or execution substrate. A child can narrow the parent's purpose but cannot silently weaken its acceptance criteria.

The first semantic method signature is `requires / produces / applies / effects`. Identity, version, integrity, provenance, recovery rules, and operational guidance belong in a richer SkillEnvelope around that signature. `Perform / Visit / Choose / Gather / Repeat / Await` are the initial composition forms. Do not add another form without a cross-domain case that cannot be represented honestly by these six and typed returns.

Capability discovery is a bounded realization for an unresolved HOW. It returns candidate handshake or SkillEnvelope identities. Discovery does not bind, authorize, or invoke a capability. Candidate use proceeds through integrity and compatibility checks, host binding policy, scope applicability, runtime readiness, current authority and effect admission, invocation, and reevaluation of the original obligation. Preserve distinct states for registered, bound, applicable, ready, authorized, selected, and invoked capabilities. Temporary dependency loss or integrity failure can block execution while retaining the durable binding.

Keep obligation satisfaction, execution disposition, evidence quality and coverage, continuation, and effect custody as separate result dimensions. A successful invocation can leave the obligation unsatisfied. A failed or cancelled effect can have an unknown external outcome that requires reconciliation before retry. Open-ended inquiry needs an explicit horizon so a result can say what was satisfied through that horizon without claiming permanent completion.

## Relationship to existing Agentica work

| Current local seam | Direction and boundary |
| --- | --- |
| [Runtime Contracts](Agentica.RuntimeContracts.md), [Design Decisions](Agentica.Design.Decisions.md) | Keep the bounded runner, compiled registration manifest, exact effect checks, receipts, and evidence-gated outcome. A new activation contract must compile into or compose with these seams before adding core types. |
| [Host Binding Authority](host-binding-authority.md), [MCP Runtime Acceptance](mcp-runtime-acceptance.md) | Generalize the existing host-authored, schema-pinned handshake. MCP discovery alone does not grant a tool. The SDK read-only fixture proves one bound path, not general dynamic discovery or mutation safety. |
| [Semantic Projection](semantic-projection.md), [Domain Router Binding Harness](CodexGoal.Agentica.DomainRouterBindingHarness.md) | Domain of Domains and hosts own meaning, canonical state, scope policy, storage, and binding. `SemanticBinding` is a graph reference, not a host capability grant. The router remains a host-side draft; do not promote its vocabulary into core from one host. |
| [Run Continuity Spine](CodexGoal.Agentica.RunContinuitySpine.md), [Adaptive Supervisor](CodexGoal.Agentica.OrchestrationAdaptiveSupervisor.md) | `GoalSpine` is evidence-grounded planning context, not obligation identity, authority, or proof. Existing task graph and acceptance seams are candidates for compilation; durable wake/resume and aggregate recursive budgets remain proposed. |
| [Workflow Routing Ontology](CodexGoal.Agentica.WorkflowRoutingOntology.md), [Secure Evolving Harness](CodexGoal.Agentica.SecureEvolvingHarnessToolSystem.md) | Reuse structured tool semantics, decision attribution, and compiled security declarations where demonstrated. Avoid a parallel router or a second unreviewed effect taxonomy. |

At checkout `e075d93` (2026-09-30), `SkillEnvelope`, `SkillHandshake`, `capability.discover`, and a typed obligation contract are not code symbols in `Agentica`, `Agentica.Clients`, `Agentica.Lab`, or `Agentica.Tests`. These are target concepts, not shipped APIs. Recheck the checkout before using this observation later.

## First falsification and economy test

Prototype a host-owned, versioned obligation artifact that compiles into existing execution contracts. Reject unsupported constructs explicitly. Test the same gather/reconcile shape in two materially different hosts before promoting shared core types:

1. **Exact-source grounding:** an exact passage from a specified revision is needed while the preferred connector is unavailable. Preserve the binding, show readiness failure, accept a substitute only if source identity, revision, and coverage satisfy the same contract, and leave unresolved work visible otherwise.
2. **Effectful continuation:** a Book continuation or reconciliation has child obligations, budget and authority limits, restart, and a possible unknown effect outcome. Preserve attempt custody and parent acceptance through recovery.

Resolve stable definition meaning when admitted revisions change. At every activation, recheck subject freshness, evidence, bindings, runtime health, revocation, authority fences, budgets, attempts, effect custody, wake conditions, and source or topology changes that could invalidate an absence claim. Filter type, scope, effect, and readiness incompatibilities deterministically before invoking a model. Mount only the context needed for the unresolved decision. Cache semantics; reevaluate circumstances.

Compare direct workflow authoring with obligation authoring under cold and warm activations. Record authoring effort, mounted context bytes or tokens, planning/provider calls, latency, compilation and invalidation cost, and **evidence-qualified** completion. Roughly 25–30% less mounted context on representative recursive activations and elimination of an otherwise needed planning call in deterministic cases are early targets from the approved direction, not measured results. Reject an apparent gain if it weakens evidence, authority, or freshness.

Source context: [architecture direction](https://docs.google.com/document/d/1oN3jA-c8vRU_KH-7qQGYY-VWpwKQY2LtlLTPVMxnn2k/edit), [recursive semantic routing](https://docs.google.com/document/d/1QpjRY9LI27qCvnQP9CjXEcej67nNojZhM3QEUoOXJzo/edit), [typed-obligation thesis](https://docs.google.com/document/d/1niO0f44u2k2L1ZPnPybHyEjbh6suh8N3Qgiax-MYrtA/edit), and the [approved refinement](https://docs.google.com/document/d/1VEKHUVs9pSkJZIs4ODgFclXjoEsN73KHb4EKmIXw5ew/edit). The approved refinement governs where these proposals differ.
