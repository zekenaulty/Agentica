# Semantic projection in Agentica

`Agentica.Projection` is an opt-in, host-neutral projection system for inspecting
concepts, workflow and plan references, directed relations, and optional vectors.
It produces bounded planning context from a host-admitted graph. It does not own
domain truth, persistence, tool registration, authorization, or execution.

## Source and interpretation

The concrete seed was [AI.Forge at `fdb3ee2`](https://github.com/zekenaulty/AI.Forge/tree/fdb3ee29b499d401c0772084189ffd12ba12868e):
`KnowledgeItem` carries typed content, keywords, relations, source, and optional
embedding; `ConceptualWhatTool` and `ConceptualWhyTool` use conceptual question
lenses. The old `KnowledgeGraphTool` explicitly says it has no semantic vector
search, and some conceptual and relation operations are stubs. The new system
implements these reusable graph and projection behaviors in Agentica rather
than treating the older code as a finished runtime.

[Semantic Work — 02 Research](https://docs.google.com/document/d/15zhqziPF0ltV9dXEZPvWaJ43l7NiF4l3l8fclzFpJ3g)
describes the sparse What/Why/How/When/Where/Who coordinate idea. In this
contract, the keys mean subject/result, purpose/cause, mechanism, timing,
applicability, and attribution/perspective respectively. A concept may omit any
key, including `how`. Hosts may add their own facet keys. No fixed chain of six
model calls is implied.

## Data and authority

- `SemanticProjectionCatalog` snapshots host-supplied nodes, perspectives,
  relations, bindings, and vectors. IDs must be exact and unique; relations and
  bindings must resolve to known nodes. Input size, facet length, graph size,
  vector dimensions, and perspective depth are bounded.
- `DomainPerspective` can inherit another perspective. Base facets are copied
  first, then inherited overlays in parent-to-child order. Every returned facet
  names its source perspective. A lens changes interpretation; it does not
  expand authorization.
- Each facet, relation, and binding has an explicit `Unknown`, `Observed`,
  `Inferred`, or `Proposed` status. An `Observed` item needs a source reference.
  A reference is an assertion of provenance, not proof that the host evidence
  exists; the host remains responsible for resolving it. An unparsed relation
  should remain absent or `Unknown`, never inferred as a false relation.
- `SemanticBinding` links a node to an opaque host workflow, task, step, or
  other target. Both the node and target domain must be host-admitted. It is a
  semantic reference. It does not activate a task, bind a
  tool, mint a grant, or prove an effect. Agentica's compiled tool manifest and
  host policy remain the authority boundary.
- Optional `SemanticVector.SpaceId` identifies the **exact** embedding space,
  including the host's model and preprocessing version. The compiler compares
  vectors only within one space and equal dimensions. It exposes separate text
  and cosine scores for inspection; it does not generate embeddings or treat a
  score as truth or authority.

## Compile a scoped perspective

The host constructs a `SemanticProjectionQuery` with `AllowedDomainIds` from
authoritative policy, a perspective, explicit seeds or a text/vector search,
and recursion limits. The compiler filters domains *before* ranking or graph
traversal. A perspective whose parent chain leaves the admitted domains is
rejected. Hidden-domain node IDs, relations, bindings, exclusion counts, and
global catalog hash never enter the returned projection. `ScopedSourceHash`
changes only with admitted source data in the selected perspective chain.

```csharp
var projection = new SemanticProjectionCompiler(catalog).Compile(
    new SemanticProjectionQuery(
        AllowedDomainIds: ["operations", "shared"],
        PerspectiveId: "operations.planner",
        SeedNodeIds: ["workflow.ship"],
        MaxDepth: 2,
        MaxNodes: 24,
        FacetKeys: [SemanticFacets.What, SemanticFacets.Why, SemanticFacets.How]));
```

Traversal follows incoming and outgoing typed relations, tracks visited nodes,
and stops at the supplied depth, node, and relation bounds. The output carries
exact IDs, status, provenance references, relevance scores, source perspective,
and a deterministic projection hash. Reordering the same catalog input does not
change that hash. No matching seed/search result yields an empty projection.

`SemanticPlanningFrameProjector` adapts this result to the existing
`IPlanningFrameProjector` boundary. Its host callback builds the query from
the current planning request and policy. The adapter binds `ToolSurfaceId`,
uses the projection hash as frame ID, and rejects a payload above its byte
budget (256 KiB by default) before Agentica's own snapshot limit. It is not
installed by default; hosts opt in by passing it to their runner.

## Adjust a concept

An LLM or user may propose `ConceptAdjustmentProposal`. Applying it requires
the expected whole-catalog hash, expected node revision, a reason and evidence
references, plus a non-null decision from the host's
`IConceptAdjustmentAuthority`. An approved adjustment returns a new immutable
catalog and a receipt with prior/new revision, hashes, decision, issuer, and
evidence. The prior catalog remains unchanged. A stale or denied proposal does
not change the graph. The host owns durable storage, evidence resolution,
retention, deletion, and the authority policy.

## Verification and limits

`SemanticProjectionTests` cover recursive cycles, inherited and sparse facets,
exact scope isolation and stable hashes, vector-space checks, question-lens
selection, host-approved revision, mutable-input detachment, malformed graphs,
and planning-frame size limits. This is a useful in-process projection contract;
it is not a persistent graph database, embedding service, autonomous ontology
learner, or permission engine. A host must supply the catalog and scope policy.
