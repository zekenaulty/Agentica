# Codex Goal: Agentica Domain Router And Binding Harness

> Lifecycle: **Draft** · Completion: **35%** · Canonical status: [Agentica Product Status And Goal Xref](Agentica.ProductStatus.md)

Status: draft.

Source check date: May 21, 2026.

## Mission

Define the next architecture layer around Agentica: a domain-router-first harness that can compile scoped context, resolve dynamic capability bindings, expose a bounded tool surface, decide whether an agent is needed, and record auditable proof.

This goal formalizes the design direction discussed around:

- Agentica as a harness, not the agent itself.
- Domain of Domains style governance without importing a whole platform into the runtime.
- Bundled tool families that are not registered by default.
- Dynamic binding layers and workflow layers.
- A selector algebra that treats XPath, JSONPath, SQL filters, graph queries, globs, vector filters, and similar query surfaces as dialects of one broader concept.
- The rule for when a domain should become an active agent scope node.

The core operating definition is:

```text
Agentica is a governed agent/workflow harness where domains route intent,
scopes bound context, capabilities bind tools, and workflows execute safely.
```

Agentica should remain the bounded proof engine. The domain router and binding harness should sit around it first as host/platform infrastructure.

## Current Code Reality

This section records what exists in this repository at the source check date. It is intentionally concrete so future work does not confuse desired architecture with implemented runtime.

Current solution shape:

```text
Agentica.slnx
  Agentica/          central in-process planner/executor runtime package
  Agentica.Clients/  LLM provider SDK isolation
  Agentica.Lab/      console host and host-owned harnesses
  Agentica.Tests/    runtime, client, orchestration, and harness tests
```

Build check:

```powershell
dotnet build Agentica.slnx
```

Result on May 21, 2026:

```text
Build succeeded.
0 warnings.
0 errors.
```

Current git reality:

- The worktree was already dirty before this document was created.
- Several runtime, CLI, client, test, and ChessQuest files were modified.
- Some files were untracked, including chat host files and a workspace-context goal doc.
- This document must not be interpreted as validation of those unrelated changes.

Core Agentica runtime already has:

- `RunRequest`
- `AgenticaRun`
- `WorkflowPlan`
- `PlanStep`
- `ToolCatalog`
- `ToolDescriptor`
- `ToolInvocation`
- `ToolResult`
- `Observation`
- `Artifact`
- `Receipt`
- `ExecutionEvent`
- `ExecutionIntent`
- `AgenticaRunner`
- `ExecutionPolicy`
- `ToolEffectPolicy`
- `ICompletionEvaluator`
- `OutcomeEnvelope`
- `DetailEnvelope`
- `ToolSurfaceSnapshot`
- `PlanningFrame`
- `IPlanningFrameProjector`

Relevant current contracts:

- `ToolDescriptor` carries id, name, kind, effect, approval flag, input schema, description, context hint, and cooldown.
- `ToolCatalog` is an in-memory registry of `ToolRegistration` values visible to one run.
- `ToolEffect` currently includes `ReadOnly`, `WritesLocalState`, `ExternalSideEffect`, `Destructive`, and `Unknown`.
- `ExecutionPolicy` bounds steps, refinements, timeout, planning mode, continuations, blocked retries, read-only batches, and allowed effects.
- `RunRequest.Context` is the current generic place for host-projected scoped context.
- `PlanningRequest` includes request, tool descriptors, observations, receipts, execution context, optional `ToolSurfaceSnapshot`, and context frames.
- `PlanningFrame` can carry host-projected public context with evidence refs and a tool surface id.
- `ToolSurfaceSnapshot` records what Agentica actually exposed to the planner.

Incubating orchestration already has:

- `LargeTaskRequest`
- `TaskOrchestrator`
- `TaskGraphPlan`
- `TaskNode`
- task graph validation and mutation
- `WorkContextSnapshot`
- `IWorkContextCompiler`
- `DeterministicWorkContextCompiler`
- `OrchestrationOutcomeEnvelope`

Host-specific capability-surface work already exists outside core:

- `MazeQuestCapabilitySurfaceCompiler`
- `MazeQuestActiveCapabilitySurface`
- `MazeQuestContextSurfaceReceipt`
- `MazeQuestCapabilityBinding`
- `ChessQuestCapabilitySurfaceCompiler`
- `ChessQuestActiveCapabilitySurface`
- `ChessQuestContextSurfaceReceipt`
- `ChessQuestCapabilityBinding`

Those types prove the pattern is useful, but they are host vocabulary. They are not generic Agentica runtime contracts.

Current CLI chat work in the local worktree includes a host tool catalog with tools such as:

- `chat.context.read`
- `chat.context.append_note`
- `chat.memory.list`
- `chat.memory.summarize`
- `workspace.file.read`
- `workspace.file.search`
- `workspace.image.create`
- `workspace.image.generate`
- `chat.response.emit`

That chat host is a useful pressure test for default tool families, but it is not yet the generic domain router or standard tool library.

Not present in core Agentica today:

- Domain registry.
- Domain router.
- Domain manifests.
- Bounded context registry.
- Deterministic scope compiler.
- Context pack contract.
- Knowledge asset store.
- Citation resolver.
- Projection rebuild system.
- Selector abstraction.
- Capability catalog as a bundled standard library.
- Capability import and binding resolver.
- Policy resolver beyond `ToolEffectPolicy`.
- Approval workflow.
- Durable context manager.
- MCP tool adapter.
- Persistent run store.
- Vector, graph, or keyword retrieval engine.
- Runtime-owned memory.
- Agent scope node model.

The immediate architecture rule is:

```text
Do not pretend Agentica already owns DoD, storage, selectors, memory,
policy governance, or domain routing. It owns bounded run proof.
```

## Core Decision

Build toward this layered harness:

```text
request
  -> domain router
  -> scope compiler
  -> policy resolver
  -> context pack builder
  -> capability binding resolver
  -> execution mode selector
  -> workflow / tool / agent dispatch
  -> validation
  -> artifact and event capture
  -> projection update
```

Default to deterministic workflow or direct tool execution.

Escalate to an agent only when bounded judgment is required.

The practical rule is:

```text
Workflow nodes own execution.
Agent scope nodes own judgment.
Tools own side effects.
Domains own meaning.
Scopes own boundaries.
Assets own truth.
Selectors find things.
Bindings decide what can be called now.
Receipts prove what happened.
```

## Relationship To Existing Agentica

Agentica core should not become the Domain of Domains.

Agentica core should remain:

```text
RunRequest
  -> planner
  -> validated WorkflowPlan
  -> tool execution
  -> observations, receipts, artifacts
  -> OutcomeEnvelope
```

The domain router should produce the bounded surface that is handed to Agentica:

```text
DomainRouteDecision
  -> RunRequest.Context
  -> ToolCatalog / ToolDescriptor projection
  -> PlanningFrame records
  -> completion evaluator
  -> execution policy
  -> AgenticaRunner
```

The richer host/platform surface remains outside Agentica core:

```text
DomainSurfaceReceipt
ActiveCapabilitySurface
CapabilityBindingPlan
ContextPack
PolicyDecisionLog
SelectorPlan
```

Agentica may preserve ids and evidence refs for these records, but it must not need to interpret their full platform semantics in order to run.

## Terminology

### Bundled

A capability, adapter, or tool family is shipped with the system distribution.

Bundled does not mean available to every run.

### Installed

A bundled capability is enabled for a tenant, workspace, host, or domain.

Installed does not mean visible to a planner.

### Bound

A domain import has been resolved to a concrete implementation under scope, policy, actor, budget, and runtime profile.

Bound does not mean registered in Agentica.

### Registered

A tool descriptor is visible in the `ToolCatalog` for one Agentica run.

Only registered tools can be selected by the planner.

### Domain

A domain is an ownership boundary for meaning, vocabulary, policy overlay, evaluation, and capability imports.

Domain is not the same thing as a tool namespace.

### Scope

A scope is a deterministic address boundary used for context, retrieval, policy, assets, and audit.

Scope is not a loose tag.

### Capability

A capability is an abstract need or affordance, such as:

```text
workspace.file.read
reporting.generate
messaging.outbound.send
asset.create
context.retrieve
selector.execute
```

Capabilities are imported by domains and resolved to bindings.

### Tool

A tool is one concrete callable implementation exposed to Agentica through `ToolDescriptor` and `ToolRegistration`.

### Workflow

A workflow is an orchestrated graph or sequence of node executions. It may call Agentica for bounded reasoning steps, but it is not itself a model.

### Agent Scope Node

An agent scope node is an active reasoning boundary with its own scoped context, policy constraints, evaluation expectations, and durable artifacts.

Do not create an agent scope node merely because a concept has a name.

### Selector

A selector is a declarative way to identify a subset of an addressable structure.

Examples:

```text
XPath
CSS selector
JSON Pointer
JSONPath
JMESPath
JSONata
SQL WHERE
Cypher MATCH
file glob
vector metadata filter
event predicate
```

### Address

An address is a stable location within a source or asset.

Selectors find candidates. Addresses identify what was found.

### Citation

A citation is a stable evidence reference to an asset, source, chunk, or result with enough metadata to resolve or audit it later.

### Projection

A projection is a derived query surface rebuilt from canonical truth.

Vector indexes, graph indexes, keyword indexes, read models, context graphs, and UI summaries are projections.

## Domain Router

The domain router is the layer that turns a request into an execution surface.

It answers:

```text
What is this request about?
Which domain owns the meaning?
Which bounded context is active?
What scope envelope is allowed?
What policy applies?
What evidence is needed?
What capabilities are needed?
Which bindings are legal now?
Should this run be a direct tool call, workflow, Agentica run, orchestration, or agent scope node?
What artifacts, receipts, approvals, and validations are required?
```

### Domain Router Inputs

```text
request id
request origin
user objective
actor identity
tenant/workspace/domain hints
active host/application
current scope hints
domain registry snapshot
capability catalog snapshot
policy snapshot
runtime profile
budget
recent receipts
available assets and projections
host state projection
```

### Domain Router Outputs

Proposed first contract:

```csharp
public sealed record DomainRouteDecision(
    string RouteDecisionId,
    string RequestId,
    string OwningDomainKey,
    string? BoundedContextKey,
    string CompiledScope,
    IReadOnlyList<string> ScopeEnvelope,
    ExecutionMode ExecutionMode,
    IReadOnlyList<CapabilityImportRequest> CapabilityImports,
    IReadOnlyList<CapabilityBindingDecision> BindingDecisions,
    IReadOnlyList<SelectorPlan> SelectorPlans,
    string? ContextPackId,
    string? ActiveCapabilitySurfaceId,
    string? ContextSurfaceReceiptId,
    IReadOnlyList<PolicyDecisionRef> PolicyDecisions,
    IReadOnlyList<string> RequiredArtifacts,
    IReadOnlyList<string> RequiredApprovals,
    IReadOnlyList<string> ValidationRequirements,
    IReadOnlyList<string> PublicReasons,
    IReadOnlyList<EvidenceRef> EvidenceRefs);
```

Execution mode values:

```text
DirectAnswer
DirectTool
Workflow
AgenticaRun
TaskOrchestration
AgentScopeNode
AskUser
Blocked
Denied
```

The route decision is an auditable artifact. It must be possible to inspect why a tool was exposed, hidden, demoted, denied, or escalated to agentic reasoning.

### Domain Router Pipeline

```text
1. Normalize request
2. Select candidate domains
3. Resolve active bounded context
4. Compile scope and allowed search envelope
5. Resolve policy
6. Build retrieval/context request
7. Resolve capability imports
8. Evaluate binding candidates
9. Compile selector plans
10. Select execution mode
11. Project public planner surface
12. Emit route decision and context surface receipt
```

The router may be deterministic in V1. It should not require an LLM to choose basic routing.

LLM classification can be added later as a bounded classifier behind validation, not as hidden authority.

## Agent Scope Node Rule

The secret rule is:

```text
A domain becomes an agent scope node only when it has independent judgment
under its own context, policy, memory, evaluation boundary, and artifact trail.
```

Create an agent scope node when the candidate domain needs most of these:

- Its own durable knowledge or memory.
- Its own policy overlay.
- Its own scope envelope.
- Its own bounded vocabulary.
- Its own planning behavior.
- Its own recurring workflows.
- Its own approval rules.
- Its own evaluation harness or golden queries.
- Its own audit trail.
- Its own failure modes.
- Its own capability imports.
- Its own artifact lineage.
- Its own validation rules.

Do not create an agent scope node when the thing is only:

- A parser.
- A renderer.
- A transport adapter.
- A data source.
- A formatting step.
- A deterministic calculator.
- A simple tool wrapper.
- A transient task.
- A namespace used only for code organization.
- A technical capability with no independent judgment.

Examples:

```text
RealEstate.MarketAnalytics
  likely agent scope node
  because it has domain vocabulary, recurring ingestion/reporting, policies,
  evaluation, durable evidence, and planning behavior.

PersonalOps.PreferenceMemory
  likely agent scope node
  because durable memory claims require scope, policy, citations, and evaluation.

PDFRenderer
  not an agent scope node
  because it is a deterministic technical capability.

CSVParser
  not an agent scope node
  because it is a parser.

Email.OutboundDelivery
  usually not an agent scope node
  because delivery is an execution surface, though it may require approval gates.

WorkspaceContextDiscovery
  not an agent scope node in V1
  because it should be a read-only discovery capability that produces evidence.
```

### Agent Scope Node Decision Gate

Before promoting a domain or bounded context to agent scope node, require a written decision artifact:

```text
candidate id
owning domain
scope envelope
reasoning responsibilities
durable assets produced
policy overlay
capability imports
approval needs
evaluation/golden queries
failure modes
why workflow/tool execution is insufficient
rollback or demotion plan
```

If that artifact cannot be filled in concretely, it is not ready to be an agent scope node.

## Standard Tool Library

Agentica should eventually ship with a tool standard library, but the action surface must stay narrow per run.

The state model is:

```text
Bundled -> Installed -> Bound -> Registered
```

No standard-library tool should be registered for a run merely because it is bundled.

### Kernel Capability Families

These are closest to operating-system capabilities. They should still be mediated by policy and scope.

```text
context.compile_scope
context.assemble_pack
context.retrieve
context.resolve_citation
context.summarize
context.request_more

asset.create
asset.read
asset.revise
asset.link_provenance
asset.hash
asset.export
asset.import

policy.evaluate
policy.classify_risk
policy.redact
policy.explain_denial
approval.request
approval.status

workflow.start
workflow.resume
workflow.pause
workflow.cancel
workflow.inspect
event.emit
signal.wait

validation.schema
validation.citations
validation.policy
validation.invariants
validation.golden_query
```

### Knowledge And Retrieval

```text
keyword.search
vector.search
graph.query
citation.resolve
document.chunk
document.embed
rerank.results
dedupe.assets
extract.entities
classify.asset
```

### Selectors

```text
selector.parse
selector.validate
selector.estimate_cost
selector.execute
selector.explain
address.resolve
address.compare
```

### Document Processing

```text
markdown.parse
html.extract
pdf.extract
docx.extract
xlsx.read
csv.read
json.parse
yaml.parse
text.normalize
ocr.image
```

### Data And Analysis

```text
sql.query.readonly
sql.query.write.gated
dataframe.transform
statistics.describe
statistics.compare
chart.render
report.generate
metric.compute
```

### Integration And Coupling

```text
http.fetch
http.call_api
webhook.receive
webhook.send.gated
polling.job
change.detect
schema.snapshot
payload.normalize
rate_limit.apply
```

### Human Interaction

```text
notification.send
email.compose
email.send.gated
calendar.read
calendar.create_event.gated
slack.send.gated
teams.send.gated
```

### Browser And Web

```text
browser.navigate
browser.click
browser.type
browser.screenshot
browser.extract
browser.download
web.search
web.fetch
```

### Local Workspace

```text
workspace.file.read
workspace.file.search
workspace.context.discover
file.write.gated
archive.create
process.run.gated
sandbox.exec
```

Raw filesystem writes and process execution are high-risk capabilities. They must never be casual defaults.

### Developer And Repository

These are useful even when Agentica is not a coding agent:

```text
git.status
git.diff
git.branch
git.commit.gated
github.issue.read
github.issue.write.gated
github.pr.read
github.pr.create.gated
ci.status
```

### Model Utility

These should be harness-level model capabilities, not arbitrary recursive model calls:

```text
model.generate
model.classify
model.extract_structured
model.embed
model.rerank
model.critique
model.compare
```

Every model utility call must have a runtime profile, budget, purpose, and output schema where possible.

### System And Operations

```text
secrets.resolve
config.read
capability.list
binding.resolve
runtime.profile.get
audit.write
event.publish
job.schedule
job.cancel
health.check
```

## Capability Descriptor

The standard library should be described as a capability catalog, not as a flat tool list.

Proposed descriptor shape:

```yaml
capability: reporting.generate
ownerDomain: Reporting
riskTier: medium
sideEffects:
  - creates_asset
inputSchema: ReportRequest.v1
outputSchema: ReportArtifact.v1
requires:
  - asset.create
  - citation.resolve
  - policy.evaluate
gates:
  - approval.required_if_external_distribution
bindings:
  - pdf.renderer.v1
  - html.renderer.v2
  - json.report.v1
```

A business domain imports meaning:

```yaml
imports:
  - capability: reporting.generate
    constraints:
      formats: [pdf, json]
      evidenceRequired: true
```

The binding resolver maps that import to concrete machinery:

```text
RealEstate.MarketAnalytics
  -> reporting.generate
  -> ReportingDomain.ReportRenderer.v4
  -> requires citations
  -> output stored as immutable asset
```

## Binding Resolver

The binding resolver decides which installed capabilities can be used now.

Inputs:

```text
domain import
actor
compiled scope
policy result
runtime profile
available adapters
host state
budget
recent receipts
```

Outputs:

```text
binding id
capability id
concrete tool or workflow id
binding state
public reason
required approvals
required evidence
input mapping
output mapping
fallbacks
evidence refs
```

Binding states:

```text
Preferred
Available
Demoted
Blocked
Denied
Hidden
Unavailable
```

These states already appear in host-specific MazeQuest and ChessQuest surfaces. The generic shape should be promoted only after at least two non-game host scenarios need the same state machine.

## Selector Algebra

The selector abstraction should be first-class, but dialect-neutral.

Definition:

```text
selector = declarative query intent over an addressable source
```

Required selector adapter operations:

```text
parse
validate
estimate_cost
enforce_scope
enforce_policy
execute
return_addresses
explain_result
```

Selector descriptors should say what they target:

```yaml
selector:
  dialect: jsonpath
  expression: $.domains[?(@.kind == "Business")]
  targetModel: DomainRegistry.v1
  expectedCardinality: many
  scopePolicy: current-domain
  resultType: Domain[]
```

Selector execution should return typed selections:

```csharp
public sealed record SelectionResult(
    object? Value,
    CanonicalAddress Address,
    string Dialect,
    string Expression,
    string? SourceAssetId,
    string? ContentHash,
    DateTimeOffset MatchedAt,
    IReadOnlyList<EvidenceRef> EvidenceRefs);
```

Important invariant:

```text
Selectors find things.
Canonical addresses identify what was found.
Citations prove where the evidence came from.
```

Use layers instead of one universal selector language:

```text
JSON Pointer
  exact canonical address in JSON-like data

JSONPath
  simple query selection over JSON-like data

JMESPath / JSONata
  projection and transformation

SQL / Cypher / XPath / CSS / glob / vector filter
  dialect adapters over their native stores

Workflow node
  complex branching, loops, or domain logic
```

Do not turn selectors into a hidden programming language. If a selector needs branching, loops, retries, or side effects, it belongs in a workflow node or technical domain implementation.

## Context Pack Surface

The harness should expose explicit context packs to agents and workflows.

Agents should not own the context manager. They should receive mediated context and request more through controlled operations.

Proposed context pack:

```csharp
public sealed record ContextPack(
    string ContextPackId,
    string RequestId,
    string CompiledScope,
    IReadOnlyList<string> ScopeEnvelope,
    IReadOnlyList<PolicyDecisionRef> PolicyDecisions,
    IReadOnlyList<CitationRef> Citations,
    IReadOnlyList<SelectionResult> Selections,
    IReadOnlyList<CapabilityBindingDecision> ActiveBindings,
    IReadOnlyDictionary<string, object?> Budget,
    string RuntimeProfileId,
    IReadOnlyList<EvidenceRef> EvidenceRefs,
    DateTimeOffset CreatedAt);
```

Allowed agent-facing context operations:

```text
context.request_more
context.resolve_citation
context.summarize
context.search
context.write_artifact
context.record_decision
```

Avoid exposing operations that imply hidden permanent memory control, such as arbitrary invisible pinning. If something must persist, make it an asset, summary, receipt, note proposal, or decision record.

## Workflow Harness

The harness should use the following decision rule:

```text
Use direct tool execution for deterministic bounded work.
Use workflow execution for known sequences or graphs.
Use Agentica runs for bounded planning over a visible tool surface.
Use task orchestration for larger goals decomposed into bounded Agentica runs.
Use agent scope nodes only for independent judgment boundaries.
```

The basic loop:

```text
observe
  -> route
  -> compile scope
  -> bind capabilities
  -> assemble context
  -> execute
  -> validate
  -> capture
  -> project
```

This is the generalized version of the coding-agent loop:

```text
inspect -> plan -> act -> verify -> record
```

## Non-Negotiable Boundaries

1. Do not add Domain of Domains platform ownership to Agentica core.
2. Do not add storage, vector memory, graph storage, keyword storage, durable assets, or projections to Agentica core in this slice.
3. Do not make every domain an agent.
4. Do not register bundled tools by default.
5. Do not let selectors bypass scope or policy.
6. Do not let model-authored summaries become proof.
7. Do not let retrieved content override governance.
8. Do not expose hidden host state through binding reasons, selector results, or context packs.
9. Do not use `ToolSurfaceSnapshot` as a substitute for the richer host/platform `ActiveCapabilitySurface`.
10. Do not make the router an opaque LLM prompt.
11. Do not introduce runtime project sprawl just to name this layer.
12. Do not weaken the existing Agentica proof rule: receipts, observations, artifacts, validation issues, and terminal state prove reality.

## Explicit Non-Goals

Do not build in the first implementation slice:

- Full DoD platform.
- Domain registry database.
- Canonical asset store.
- Vector index.
- Graph store.
- Keyword index.
- Browser automation.
- MCP adapter.
- Approval UI.
- Tool marketplace.
- Plugin installer.
- Autonomous tool generation.
- Cross-run autonomous learning.
- Multi-agent swarm runtime.
- Hidden memory manager.
- Universal selector language.
- Public API service.
- Queue worker.
- Scheduler.
- Authentication system.

The first slice should produce explicit route and binding artifacts using existing Agentica run surfaces.

## First Implementation Shape

V1 should be host/platform-side and conservative.

Allowed first-slice locations:

```text
docs/
Agentica.Lab/Scenarios/      host-specific probes if needed
Agentica.Tests/              targeted host/runtime boundary tests
```

Avoid new core runtime contracts until the same shape is proven by multiple hosts.

If code is needed after this draft, prefer a host-owned prototype:

```text
DomainRouterPrototype
CapabilityCatalogPrototype
SelectorPrototype
ContextPackPrototype
```

Do not put those names into `Agentica` core until their boundary is proven.

## Milestones

### Milestone 0: Reality Baseline

- Record current repo shape.
- Record dirty worktree status.
- Run `dotnet build Agentica.slnx`.
- Identify existing core contracts this layer must reuse.
- Identify which concepts are only host-specific today.

### Milestone 1: Vocabulary Lock

- Lock the difference between domain, scope, capability, tool, workflow, selector, address, citation, projection, binding, context pack, and agent scope node.
- Update related docs only if terms conflict.
- Do not code new abstractions until vocabulary is stable.

### Milestone 2: Bundled Capability Catalog Draft

- Define the standard library as capability descriptors.
- Mark each capability with risk tier, side-effect profile, required gates, input schema, output schema, and allowed binding states.
- Prove that bundled, installed, bound, and registered are separate states.

### Milestone 3: Selector Contract Draft

- Define selector descriptors and selection results.
- Require canonical addresses and evidence refs.
- Define at least JSON Pointer, JSONPath, file glob, and object-property selector adapters as conceptual examples.
- Keep selectors read-only in V1.

### Milestone 4: Domain Route Decision Draft

- Define `DomainRouteDecision`.
- Define execution modes.
- Define required route decision evidence.
- Show how a route decision maps to existing `RunRequest.Context`, `ToolCatalog`, `PlanningFrame`, and `ToolSurfaceSnapshot`.

### Milestone 5: Agent Scope Node Gate

- Implement or document the decision artifact required before promoting a domain to an agent scope node.
- Add examples for one accepted and one rejected candidate.

### Milestone 6: Host Prototype

- Pick one host scenario that is not core runtime vocabulary.
- Compile a route decision, active capability surface, selector plan, and context pack for one request.
- Feed only the public projection into Agentica.
- Ensure `OutcomeEnvelope` preserves enough ids/evidence refs to connect back to host/platform artifacts.

### Milestone 7: Boundary Tests

- Prove a bundled capability is not registered unless explicitly bound.
- Prove a denied binding is not exposed in `ToolCatalog`.
- Prove a hidden binding does not leak its hidden value through public reasons.
- Prove selector execution refuses to cross scope.
- Prove route decisions can choose `DirectTool`, `Workflow`, `AgenticaRun`, and `Blocked` without LLM routing.
- Prove Agentica core remains free of host/domain vocabulary.

### Milestone 8: Promotion Decision

- Decide whether any generic type belongs in Agentica core.
- Require evidence from at least two hosts before promoting.
- If promotion is justified, document the exact contract and why `RunRequest.Context` plus `PlanningFrame` is insufficient.

## Acceptance Criteria

This goal is complete when:

1. The domain router contract is explicit enough to implement without guessing.
2. The standard tool library is described as bundled capabilities, not registered tools.
3. Bundled, installed, bound, and registered states are unambiguous.
4. The selector abstraction has dialect-neutral responsibilities and output requirements.
5. The agent scope node rule is written as a decision gate with concrete acceptance and rejection criteria.
6. The first implementation target is host/platform-side, not a core runtime takeover.
7. The relationship to existing `ToolDescriptor`, `ToolCatalog`, `ToolSurfaceSnapshot`, `PlanningFrame`, `RunRequest.Context`, and `OutcomeEnvelope` is explicit.
8. The document lists what currently exists in code and what does not.
9. Verification commands are named.
10. Non-goals prevent storage, memory, DoD, vector, MCP, browser, and swarm scope creep.

## Verification Commands

Run before implementation work is considered complete:

```powershell
dotnet build Agentica.slnx
dotnet test Agentica.slnx
```

If a host prototype is added, also run the smallest affected harness commands and tests. Do not declare this goal complete based on documentation alone if code was changed.

## Completion Condition

This document is a draft goal. It is complete as a planning artifact when it accurately reflects the current repository, formalizes the router/binding/selector/agent-scope design, and leaves a concrete implementation path.

The feature is complete only when a host/platform prototype proves:

```text
request
  -> route decision
  -> scoped context pack
  -> capability binding decisions
  -> registered Agentica tool surface
  -> execution
  -> validation
  -> outcome envelope with evidence refs back to route/context/binding artifacts
```

without adding domain routing, storage, memory, or DoD platform ownership to Agentica core.
