# Host binding and bounded work authority

This contract records the user's 2026-09-30 architecture notes, *Host/Application Authority — Standing Grants, Tool Selection, and Bounded Agency* and *Bounded Context Envelope — Recursive Authority, Delegation, and Objective Continuity in Domain of Domains*. The notes were supplied directly to this task. This document distinguishes target semantics from current Agentica support.

## Authority resolution

An operation may proceed when all of these hold:

1. The host deliberately bound the capability into this runtime's active surface. Discovery or the existence of code elsewhere is insufficient.
2. The proposed use serves the admitted objective or a valid child objective derived from it.
3. The installed binding allows the operation's scope, effect, data boundaries, and arguments.
4. Current resource and attempt fences allow this attempt to execute.

Tool selection is a planning decision. It does not create authority, and it does not require renewed human approval for an action already covered by host binding and objective authority. A host may attach an explicit per-invocation condition when it installs a tool. The effect category alone must not invent one.

The host binding and the objective are different authority inputs. A host grant can precede the user's current turn. The user's objective supplies purpose for using that grant. A tool bound in one host does not thereby become available in another host. Binding should carry the smallest applicable capability, scope, and constraints.

## Recursive continuity

The durable work unit is a bounded context envelope: objective, scope, authority, delegation policy, constraints, resource limits, provenance, state, evidence, and completion criteria. A parent may derive a narrower child when its own delegation policy permits that child class and authority. A child must preserve its authority lineage and constraints. Composition cannot create authority without an attributable ancestor or host grant.

Standing work authority normally persists until governed completion, explicit higher-authority revocation, or a terminal failure with no viable recovery. New plans, child contexts, model/provider changes, restarts, and expired leases do not themselves require renewed owner authorization. A failed attempt may need a new lease, credential, budget reservation, or idempotency fence under the same standing authority.

For any proposed authority blocker, identify the invariant it protects, the materially new unauthorized effect it prevents, and the condition that removes it. An internal object transition alone is not a valid reason to ask the owner again.

## Current Agentica boundary

- `ToolCatalog` and the compiled manifest hold host registrations. Planning visibility is narrowed by effect policy, planner data boundaries, and any explicit grant condition.
- Registrations with `ApprovalRequirement.None` can execute within the active policy, including `ExternalSideEffect`. `ExplicitGrant` remains an installed per-invocation condition. The runtime still validates the manifest, planned kind and effect, input schema, and data boundaries at dispatch.
- MCP discovery alone is non-authoritative. The host must select a named server/tool, pin its schema, and bind a registration before the model can use it.
- `RunRequest.Objective` and `AuthorizationScopeId` do not yet represent a durable, recursively derived bounded context envelope. Agentica does not currently prove semantic fit between an arbitrary proposed action and the objective. The host must encode operation scope in its bindings and tools. A generic core classifier or English phrase grammar would be the wrong substitute.
- `GoalSpine` is continuity context, not authority or proof. Receipts and authoritative host state establish effects and completion.

## Next implementation proof

Use one host with a deliberately bound `project.create` tool, constrained namespace and quota, and a user request for a fresh workspace phrased outside a deterministic command grammar. The model should select `project.create`; host validation should verify the objective, binding, arguments, quota, and current state; execution should emit a durable receipt. Repeat inside a derived child context and after a renewed execution lease without asking the owner to authorize the same objective again. Also prove that an unbound discovered tool, an unrelated project namespace, and a child whose parent lacks delegation authority cannot execute.

Do not promote this sketch to a universal `BoundedContextEnvelope` class until two distinct hosts demonstrate compatible lineage and lifecycle requirements.
