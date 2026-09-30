# Host binding and bounded work authority

This contract records the user's 2026-09-30 architecture notes, *Host/Application Authority — Standing Grants, Tool Selection, and Bounded Agency* and *Bounded Context Envelope — Recursive Authority, Delegation, and Objective Continuity in Domain of Domains*. The notes were supplied directly to this task. This document distinguishes target semantics from current Agentica support.

## Authority resolution

An operation may proceed when all of these hold:

1. The host deliberately bound the capability into this runtime's active surface. Discovery or the existence of code elsewhere is insufficient.
2. The proposed use serves the admitted objective or a valid child objective derived from it.
3. The installed binding allows the operation's scope, effect, data boundaries, and arguments.
4. Current resource and attempt fences allow this attempt to execute.

Tool selection is a planning decision. It does not create authority, and it does not require renewed human approval for an action already covered by host binding and objective authority. A host may attach an explicit per-invocation condition when it installs a tool. The effect category alone must not invent one.

The host binding and the objective are different authority inputs. A host grant can precede the user's current turn. The user's objective supplies purpose for using that grant. A tool bound in one host does not thereby become available in another host. Binding should carry the smallest applicable capability, scope, and constraints. Authority may have several attributable roots: user direction, deliberate host grant, and a valid ancestor delegation. Each required root and fence must hold for the particular effect.

## Recursive continuity

The durable work unit is a bounded context envelope: objective, scope, authority, delegation policy, constraints, resource limits, provenance, state, evidence, and completion criteria. A parent may derive a narrower child when its own delegation policy permits that child class and authority. A child must preserve its authority lineage and constraints. Composition cannot create authority without an attributable ancestor or host grant.

Standing work authority normally persists until governed completion, explicit higher-authority revocation, or a terminal failure with no viable recovery. New plans, child contexts, model/provider changes, restarts, and expired leases do not themselves require renewed owner authorization. A failed attempt may need a new lease, credential, budget reservation, or idempotency fence under the same standing authority.

The semantic authority state is `ACTIVE`, `COMPLETE`, `TERMINAL_FAILURE`, or `OUT_OF_SCOPE`. Lease expiry, missing holds, stale selections, and session renewal are execution states under `ACTIVE`: they can stop an immediate attempt and call for renewed execution controls while the original objective remains authorized.

For any proposed authority blocker, identify the invariant it protects, the materially new unauthorized effect it prevents, and the condition that removes it. An internal object transition alone is not a valid reason to ask the owner again.

## Current Agentica boundary

- `ToolCatalog` and the compiled manifest hold host registrations. Planning visibility is narrowed by effect policy, planner data boundaries, and any explicit grant condition.
- Registrations with `ApprovalRequirement.None` can execute within the active policy, including `ExternalSideEffect`. `ExplicitGrant` remains an installed per-invocation condition. The runtime still validates the manifest, planned kind and effect, input schema, and data boundaries at dispatch.
- MCP discovery alone is non-authoritative. The host must select a named server/tool, pin its schema, and bind a registration before the model can use it.
- `RunRequest.Objective` and `AuthorizationScopeId` do not by themselves represent a durable bounded context envelope. The runner passes the host-supplied `AuthorizationScopeId` to `ToolInvocation` so the bound tool can resolve the active host context at dispatch. The identifier is a lookup key, not authority proof. Agentica does not itself prove semantic fit between an arbitrary proposed action and the objective. The host must encode operation scope in its bindings and tools. A generic core classifier or English phrase grammar would be the wrong substitute.
- A host can now supply `IChildAuthorityDeriver` to `TaskOrchestrator`. When a `LargeTaskRequest` carries a parent authorization scope, child dispatch requires a distinct derived child scope and a derivation receipt ID. The derivation request includes the proposed child objective and typed context projection; these remain planner proposals for the host to check against its parent record. A missing deriver, an out-of-scope child, a temporarily unavailable derivation, or malformed proof stops before child dispatch. The host owns durable lineage, objective/scope validation, delegation policy, receipt storage, and renewal of execution controls. Unscoped legacy orchestrations retain their current behavior.
- `GoalSpine` is continuity context, not authority or proof. Receipts and authoritative host state establish effects and completion.

## Next implementation proof

The in-process `HostAuthorityVerticalTests` fixture now exercises a scripted model selection of deliberately bound `project.create` for a request phrased outside a command grammar. Its host derives a child scope from an attributable parent and structured task metadata, checks the lab namespace and one-project quota, executes once, and resolves completion from a receipt and state. It also proves that another namespace is refused at effect time and a parent without delegation cannot start the model or tool. Orchestrator tests prove missing, rejected, temporarily unavailable, and malformed child derivation cannot dispatch. The scripted model and in-memory registry make this a contract test; they do not prove a live model's semantic selection or durable lineage storage.

Next, use a separately operated host with a durable project registry and derivation receipt. Verify a live model selects the installed capability from a paraphrased objective; verify renewal after an expired execution lease without new owner authorization; and verify that unbound discovery, an unrelated namespace, and a parent lacking delegation authority cannot cause an effect.

Do not promote this sketch to a universal `BoundedContextEnvelope` class until two distinct hosts demonstrate compatible lineage and lifecycle requirements.
