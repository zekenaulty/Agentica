# Isolated Nyx learning host

An independently runnable, disposable **synthetic comparison host** for Agentica.
It uses the actual `LlmWorkflowPlanner`, strict plan/refinement parsing,
`AgenticaRunner`, registered tools, canonical results, projected context and
explicit host completion. The `ILlmStreamingClient` is a deterministic fixture.
It reads the current real planner prompt and latest canonical observation to
choose each next tool; it does not access the host object.

No credentials, environment-provider selection, production stores, HTTP clients
or live provider calls are used. Current Nyx/Bridge and the existing Lab are
unmodified. This is N-02/C-02 qualification, not a contemporary Nyx replay or
N-04 production candidate.

## Run

Use the repository-pinned SDK and restored dependency graph:

```powershell
dotnet run --project samples/Agentica.NyxLearningHost/Agentica.NyxLearningHost.csproj -c Release --no-restore
```

The command runs five bounded cases and returns a nonzero exit code when a
candidate disagrees with its declared reference acceptance:

1. Preferred source unavailable; select an alternate returning the exact same
   required document/revision, then submit the quoted interpretation.
2. Read the original lost-response attempt; confirm its existing effect, then
   submit fulfillment. No new domain effect capability is installed.
3. Both exact-source routes unavailable; preserve the exact-source residual.
4. Original effect remains unknown; preserve original attempt and residual.
5. Inert shadow creates a typed proposal without constructing an executor.

Library consumers/tests can additionally select a wrong source/revision,
wrong perspective, or a depleted cumulative parent allowance through
`LearningOptions`.

## What actually loops

```text
current host obligation + exact evidence/custody frame
 -> streaming fixture model chooses a bounded plan
 -> existing schema and runtime plan validation
 -> bound source/result capability
 -> canonical receipt + observation
 -> current frame rebuilt with the actual new evidence
 -> streaming model refinement citing the current canonical observation
 -> candidate submission artifact
 -> independent host semantic completion
 -> canonical outcome + optional obligation acceptance mapping
```

A source request can succeed operationally while returning unavailable evidence.
A ready interpretation or successful proposal submission is not host acceptance.
The source case requires the primary source identity, revision and exact quote;
a secondary note with matching words cannot satisfy it. The effect case requires
the exact original attempt result; an accepted state observation alone cannot
settle causation or authorize another effect.

The fixture parser uses only the current last user prompt, required projected
frame sections, current execution context and exact refinement observation ID.
It rejects missing prompt sections. There is no narration-to-proof shortcut.

## Comparative evidence and metrics

The reference is **declared synthetic semantics**, grounded in the pinned
[reference baseline](../../docs/learning/nyx-reference-baseline.md). It is not
execution of historical or installed Nyx source. Each report compares expected
satisfaction against candidate host acceptance while retaining the original
outcome, exact manifest, residuals and attempts.

Reported measurements are actual fixture calls, planner user-prompt characters, canonical
receipt count, consumed parent step allowance, first fixture JSON text event and
wall duration. `PlannerUserPromptCharacters` sums the last user-message text of
each planner call, excluding system instructions, structured-output schema and
transport overhead. It is not complete provider-input size. No token usage or
dollars are fabricated; both are null. First
text is a fixture stream event, not a measure of live model thought. Warm reuse
is NOT-MEASURED because no cache is enabled. Repeat runs construct independent
state; they do not prove production persistence or warm savings.

The parent step allowance is supplied by the host. The run receives only the
remaining count and reports cumulative usage afterward. This sample does not
supply a durable ledger or schedule renewed work. Provider-call fixture ceilings
and runtime deadlines are bounded separately.

All non-submit tools are read-only. The only local write records an inert
candidate artifact in disposable host state. Original-effect reconciliation
changes the retained synthetic custody disposition after its exact result;
it never invokes the original action. Shadow work constructs no runner, records
no canonical tool outcome and has no route to effect dispatch.

## Gates and remaining scope

`ObligationHostConformanceTests` checks bounded projection, input/acceptance
freshness, canonical evidence resolution, consumer-local citation labels,
unknown custody and mutation isolation. `NyxLearningHarnessTests` checks both
complete loops and negative cases, actual refinement evidence, streaming,
inert shadow and cumulative allowance.

The acceptance adapter receives the current host ledger separately from a
candidate. Candidate attempts cannot omit, reassign or confirm host custody;
residual work clears only after the independent host check. Citation reuse
requires an exact source/range binding, even when existing local labels collide.
Explicit host vetoes also apply to partial candidates. Host completion reads
this ledger and canonical source/result proof rather than trusting a candidate's
declared satisfaction.

These are deterministic offline tests. Live model intelligence, live provider
parity, current Bridge behavior, restart custody, historical-faculty MCP transport,
warm interpretation reuse and operational Nyx adoption remain separately
qualified work. See
[comparison and integration](../../docs/learning/obligation-comparison-host.md).

