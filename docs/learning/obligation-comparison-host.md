# Obligation projection and isolated comparative host

Status: opt-in C-02/N-02 implementation. Based on the
[v1 contracts](../contracts/obligation-activation-v1.md) and
[synthetic reference corpus](nyx-reference-baseline.md). Exact test/CLI execution
evidence must be recorded at the merged source head; this design description is
not a test-pass claim.

Tracking: [C-02 #19](https://github.com/zekenaulty/Agentica/issues/19),
[N-02 #29](https://github.com/zekenaulty/Agentica/issues/29).

## Existing runtime integration

`ObligationPlanningFrameProjector` implements the existing
`IPlanningFrameProjector`. Its host callback returns a current
`ObligationFulfillment`, reusing the C-01 container for manifest, residual work
and original attempt custody instead of adding another activation state type.
The runner calls it through the existing planning request factory on initial
planning and refinement.

The frame includes schema version and the frozen fulfillment, exact presented
text/source coordinates, current consumer acceptance and attempts. Evidence refs
refer to those original sources; they do not invent current-run receipts.
A canonically serialized full-payload digest identifies the frame, distinct from
the reusable interpretation ContentIdentity. Changing consumer acceptance changes
the frame even when reusable interpretation inputs remain equal.
The existing tool-surface identity is retained. Full payload bytes are checked
against the host's configured 1 KiB–1 MiB limit before returning the frame.
No automatic compacted or omitted form is invented.

`ObligationFulfillmentAdapter` maps into existing `TaskAcceptanceResult`.
The host explicitly supplies its current authoritative fulfillment ledger,
semantic decision and real `OutcomeEnvelope`. A proposed fulfillment is a
separate argument; it cannot supply the current ledger on the host's behalf.
The adapter:

- requires current obligation/acceptance identity and interpretation content hash;
- resolves candidate citations in the current presented manifest and verifies
  the exact source, revision, attribution, segment range and text binding;
- blocks pending/unknown original custody or indeterminate interpretation;
- requires original attempts, residual requirements and fulfillment disposition
  to match the current host ledger, including attempt owner and realization;
- preserves partial/rejected obligation dispositions;
- requires Accepted evidence to resolve to canonical receipts, observations or
  artifacts within this exact returned outcome;
- freezes bounded host decision reasons and evidence refs.

It never infers satisfaction from a successful provider/tool return. It does not
replace an orchestration acceptance evaluator, create a grant, accept a raw
provider claim, resolve external source authority, or discharge a host ledger.
Host rejection, blocking or plan invalidation remains a veto even when the
candidate is structurally Satisfied or PartiallySatisfied. Only the host's own
acceptance check can discharge its residual work. Execution status and accepted
fulfillment remain separate facts.

Local segment labels are excluded from reusable content identity. Reuse with
different labels requires an exact source/revision/span rebind before acceptance;
equal content hash alone does not authorize an old citation label.

## Two source-neutral host cases

The independent
[learning host](../../samples/Agentica.NyxLearningHost/README.md) contains domain
fixtures outside core. Both use the same optional contracts, projector, existing
runner, canonical evidence and acceptance adapter. No companion or Book branch
is added to core.

### Exact grounding with a preferred dependency outage

The installed primary capability returns unavailable without erasing its binding.
The next model refinement chooses a separate installed mirror. A legitimate
mirror returns the exact required primary document and revision. The next
refinement submits a quoted candidate; the host independently checks source,
revision, text and perspective before completion.

Both routes unavailable or a mirror returning a different source/revision
produce an honest unresolved obligation. A successful read or matching words in
a secondary document cannot replace exact-source acceptance.

### Effectful continuation after a lost response

The parent host already owns one original attempt whose execution was cancelled
with effect outcome unknown. An accepted state observation does not prove that
attempt caused it. The model chooses a read-only exact-attempt reconciliation.
A known result can confirm the original effect; an unknown result remains
indeterminate. The next refinement submits the corresponding fulfillment.

No original effect dispatch capability is installed in this recovery run.
Original attempt ID/owner, parent objective, acceptance and cumulative allowance
survive. State and custody appear in the exact source manifest and canonical
reconciliation observation. New model narration cannot confirm them.

## Inert comparison and honest measurement

The expected reference is authored synthetic acceptance semantics, not a claim
that historical Nyx code was executed. Negative cases retain explicit coverage,
residuals and custody. No current participant data or production observer is used.

Shadow uses the real planning bridge only to generate a typed proposal; it never
constructs a runner/executor or canonical tool result. It cannot promote memory,
deliver speech or dispatch an effect.

Each actual candidate run records fixture calls, planner user-prompt characters,
canonical receipts, actual stream order/first JSON text timestamp, elapsed wall
duration and cumulative parent steps. This qualifies the fixture plumbing and
comparison boundary. Provider tokens/cost remain unknown/null, and warm reuse
remains NOT-MEASURED. No percentage savings, live-model quality or latency
improvement is claimed. `PlannerUserPromptCharacters` counts the last user
message sent on each planner call; it excludes system instructions, structured
output schema and transport overhead and is not a complete provider-input size.

The host derives the run's remaining step allowance from its supplied cumulative
usage and refuses a depleted parent budget. Existing runtime policies enforce
the bounded window. Durable budget/attempt persistence, multi-process custody
and future routing remain host-owned follow-on gates.

## Reproduction and qualification

```powershell
dotnet test Agentica.Tests/Agentica.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~ObligationHostConformanceTests|FullyQualifiedName~NyxLearningHarnessTests"
dotnet run --project samples/Agentica.NyxLearningHost/Agentica.NyxLearningHost.csproj -c Release --no-restore
```

Use the pinned SDK/restored locks. Record actual results and source head after
execution. The source-level loop is not installed Nyx, live provider judgment,
N-03 MCP faculty conformance, N-04 independently restartable cognition, R-01 reuse
or NL-05/NL-06 operational adoption/retirement.

