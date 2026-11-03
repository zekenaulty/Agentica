# Obligation activation contract v1

Status: opt-in contract and synthetic fixtures. Implements the bounded C-01/OA-01
slice; runner projection and host acceptance integration are C-02 work. This
document does not establish a cache, scheduler, persistent obligation ledger or
installed Nyx integration.

Tracking: [C-01 #18](https://github.com/zekenaulty/Agentica/issues/18),
[N-01 #28](https://github.com/zekenaulty/Agentica/issues/28).

## Small expression

```text
satisfy acceptance-contract@revision(subject-reference@revision)
within scope / perspective
given exact admissible presented evidence
when current validity and readiness conditions permit
using a compatible realization when resolved
-> fulfillment, evidence and residual requirements
```

The host owns identity, acceptance, current authority, source eligibility,
objective lineage, resource ledger and original attempts. A different provider
or realization does not create a new objective, acceptance revision or budget.
A changed acceptance is a host-owned explicit versioned decision.

## API and ownership

All types live in `Agentica.Projection`; no existing constructor or runtime
default changes.

- `ObligationIdentity`: host obligation ID, governing objective ID, consumer
  acceptance contract/revision, subject ID/revision and optional parent obligation.
- `PresentedEvidenceSegment`: consumer-local segment label, existing `EvidenceRef`
  identifying the original source, exact source revision, start in UTF-16 code
  units, exact presented text and attributable speaker/owner identity.
- `ObligationCoverage`: Unknown, Partial or Complete through an explicit required
  horizon and optional corpus revision/watermark.
- `ObligationInputManifest`: freezes the identity, question, scope/perspective,
  ordered presented segments, coverage, compiler revision, input/output schema
  revisions and generation-policy revision. Its content identity is derived.
- `ObligationFulfillment`: freezes one consumer's satisfaction, interpretation
  resolution, exact citations, residual requirement IDs and original attempt
  references. It retains the manifest and consumer acceptance identity.
- `ObligationAttemptReference`: original attempt, owning host and realization
  identity, execution state and effect custody. It references the authoritative
  host ledger; it does not replace, reconcile or authorize that ledger.

Input records become validated, detached snapshots when constructing the
manifest or fulfillment. Returned lists are read-only shells, nested values
contain only immutable strings/scalars/records, and no input collection escapes.
Malformed or oversized values fail at construction. These are representation
checks, not semantic admission.

```csharp
var identity = new ObligationIdentity(
    "obligation.42", "objective.7", "exact-passage", "1",
    "document.9", "4", ParentObligationId: "parent.3");

var manifest = new ObligationInputManifest(
    identity,
    "Which exact passage supports this claim?",
    "scope.documents", "perspective.reader",
    [new PresentedEvidenceSegment(
        "selected.1", new EvidenceRef("document", "document.9"),
        "4", 120, "An exact presented passage.", "speaker.author")],
    new ObligationCoverage(
        ObligationCoverageStatus.Complete, "document.9@4:120-147"),
    "compiler.v1", "input.v1", "interpretation.v1", "grounded-policy.v1");

var evidence = new PresentedEvidenceCitation("selected.1", 120, 27);
var fulfillment = new ObligationFulfillment(
    manifest, ObligationSatisfaction.Satisfied, ObligationResolutionState.Ready,
    [evidence], []);
```

The example's structural fulfillment is not a host verdict that the passage
actually supports the claim. The current host acceptance evaluator must establish
that relation and resolve source authority.

## Reusable interpretation identity

`ContentIdentity` is `sha256-v1:` followed by the lowercase SHA-256 of one fixed
UTF-8 JSON encoding. Object fields and supplied segment order are explicit.
Each segment contributes original source kind/ID/revision, attribution, start,
text length and SHA-256 of its exact UTF-8 text. No text normalization occurs.
Ill-formed UTF-16 is rejected rather than silently replaced before hashing.

Included meaning-changing coordinates:

- contract encoding version;
- subject identity and revision;
- exact question, semantic scope and perspective;
- compiler, input schema, output schema and generation-policy revisions;
- coverage class, required horizon and corpus revision/watermark;
- exact ordered attributable presented material and its source coordinates.

Excluded consumer coordinates:

- obligation ID, governing objective ID and parent obligation ID;
- consumer acceptance identity/revision;
- local citation join labels (`SegmentId`);
- provider-call/activation IDs and receipt timestamps.

The interpretation's input/output schema and generation policy must change when
its actual interpretation contract changes. A consumer acceptance change alone
need not force recomputation of otherwise identical interpretation inputs.
Every consumer must still evaluate the available interpretation against its own
current acceptance. Reusing a hash is never evidence of current eligibility,
authorization, fulfillment or inspected model attention.

Segment labels are local citation joins, not semantic content. Reusing an
interpretation with different labels requires an explicit exact-source/range
rebind into the consumer's current manifest. Do not reuse an old label without
validating the current source, revision and coordinates.

A corpus watermark matters for completeness/absence questions: newly arriving
relevant evidence can invalidate an earlier conclusion without changing any
selected source. The host chooses an honest horizon/watermark and revalidates it.
A missing watermark is not an automatic certificate of exhaustive absence.

## Presentation and citation integrity

The host/compiler must send the exact manifest text and retain the actual
presentation receipt. Constructing this object merely declares presentation;
it proves neither network dispatch nor model attention.

`PresentedEvidenceCitation` uses absolute UTF-16 coordinates in its segment's
original source revision. `ReadCitation` returns only a nonempty range wholly
inside that exact presented text. Unknown/omitted segment labels, negative or
overflowing coordinates, out-of-range spans and surrogate-splitting ranges fail.
A source retained elsewhere is not implicitly part of the presented manifest.
Source attribution remains explicit when perspective changes.

The contract stores exact snippets for deterministic later projection. Hosts
remain responsible for scoped source selection before construction, current
eligibility, retention/deletion, provenance resolution and avoiding disclosure
of hidden IDs or exclusion counts.

## Independent result axes and conservative satisfaction

Interpretation resolution: Ready, Pending, Unavailable, Indeterminate.
A Ready artifact can have partial coverage and leave an obligation unsatisfied.

Consumer satisfaction: Unsatisfied, PartiallySatisfied, Satisfied, Indeterminate.

Execution: NotAttempted, Active, Succeeded, Failed, Cancelled, OutcomeUnknown.

Effect custody: NotAttempted, Admitted, Confirmed, Failed, OutcomeUnknown.

A Satisfied snapshot requires Ready interpretation, Complete coverage of the
host-declared required horizon, at least one valid presented citation, no residual
requirements, and no active/unknown original execution or admitted/unknown effect.
Other satisfaction states require at least one residual requirement ID. An
unattempted execution cannot claim an effect. Duplicate original attempt IDs or
residual IDs fail.

Confirmed effect and failed/cancelled execution can coexist. Observing a desired
state does not prove the original attempted transition caused it. A similar warm
interpretation or successful new read cannot discharge an unresolved old effect.
Missing or indeterminate evidence preserves original attempt references and
residual work.

These guards intentionally do not infer semantic acceptance. Host policy may
reject an otherwise well-formed Satisfied claim. C-02 must map this data through
existing evidence/acceptance boundaries without manufacturing a receipt or
collapsing unknown custody into execution success.

## Bounds and compatibility

- Exact identifiers: 256 UTF-16 units, no surrounding whitespace/control characters.
- Question: 8,192 units; coverage horizon: 4,096 units.
- Presented evidence: 128 segments; 32,768 units per segment; 131,072 total units.
- Fulfillment: 128 citations, 128 residual IDs and 128 original attempts.
- Count and enumeration are both bounded; a collection reporting an inaccurate
  Count cannot bypass the snapshot's actual iteration ceilings.
- Native provider state, credentials, arbitrary object graphs and mutable payload
  dictionaries are absent.

These limits bound the contract. They do not promise every manifest fits every
planner/provider projection budget. C-02 or a host compiler must apply its existing
complete-input limits and preserve explicit omissions/required-floor failure.
No runtime JSON or durable database migration is introduced. SchemaVersion 1
names this contract/identity encoding; future incompatible encodings require an
explicit version change.

## Qualification

`ObligationActivationContractTests` tests meaningful identity changes,
cross-consumer equivalence and distinct acceptance, immutable snapshots, exact
citations/Unicode, missing coverage, independent resolution/result states,
realization changes preserving unknown custody, bounds including misreported
collection counts, and the source-neutral synthetic corpus.

The [reference baseline](../learning/nyx-reference-baseline.md) defines three
scenarios and golden manifest identities. Fixture declaration and contract tests
qualify their own boundaries. They are not the comparative host, complete runner
integration, live-provider judgment, or current Bridge/Nyx operational proof.

