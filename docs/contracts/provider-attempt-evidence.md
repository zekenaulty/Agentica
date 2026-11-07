# Provider attempt evidence

P01 introduces additive observation contracts in `Agentica.Clients.Llm`. Existing
generate/stream entrypoints, positional constructors and deconstruction remain.
Adapter population is P02; preparation and required admission are P03.

## Request ownership

A host can supply `LlmRequest.AttemptCapture` containing `LlmAttemptReferences`.
The references identify one physical attempt and optionally its operation, phase,
route fingerprint and captured execution bundle. Agentica treats these as opaque
correlation values. A route fingerprint and a complete bundle reference have
different scopes; neither grants authority or selects a route.

The capture is single-use, including concurrent use and use after completion.
Rejection of a second invocation must never change the original owner's evidence.
Without an explicit capture, an adapter may use a local observation identity; it
is not a durable host attempt identity. Request captures are excluded from JSON.

## Facts and outcomes

`LlmAttemptEvidence` is a detached immutable snapshot. Increasing sequence numbers
order observations within one capture. It contains bounded structural identifiers,
HTTP status where available, provider status, finish reason, nullable usage and a
stable reason code. It excludes prompts, output text, summaries, credentials and
private native continuation payloads.

Dispatch certainty is conservative at the configured transport boundary:

| Value | Meaning |
| --- | --- |
| `NotDispatched` | The adapter has not entered its transport send. |
| `MayHaveDispatched` | A send began; execution cannot be ruled out. |
| `ResponseObserved` | Some provider response was received. |

This does not measure physical requests through arbitrary injected retry or redirect
handlers. P02 qualifies the supported handler chains. A missing response does not
prove zero cost or no effect.

Client `Outcome` is separate from `ProviderStatus`. For example, a provider may
report completed usage while client validation fails, cancellation wins delivery,
or a late stream error invalidates the result. `Succeeded` establishes client
delivery/validation only. Host acceptance, objective completion, budget settlement
and durable receipts remain host decisions.

Usage updates replace cumulative counters; absent fields preserve prior facts.
Counters are never added as stream deltas. Conflicting known output-inclusion
semantics become `Unknown`, preserving raw counters while disabling unsupported
derived arithmetic. Missing usage remains null, never zero.

## Delivery and cancellation

Successful responses, provider exceptions and terminal stream events can expose
`AttemptEvidence`. A caller needing evidence after ordinary cancellation or early
stream disposal retains its request's capture and reads `Latest`. Cancellation
continues to use `OperationCanceledException` and the original caller token.
Dispose or cancellation does not implicitly retry an unresolved send.

The stream completion helper buffers terminal success until enumeration ends.
Best-effort UI callbacks are isolated: a recoverable callback failure disables
further notifications without changing provider execution or completion. This
lossy observer is never an admission, journal or settlement mechanism.

P03 adds the required host admission boundary: an admission/reservation failure
must prevent inference dispatch. Hosts own durable storage, recovery and cumulative
budgets; the capture is an in-memory observation holder, not that ledger.

## Proof boundary

P01 contract fixtures cover serialization, compatibility, cumulative usage,
single-owner capture, cancellation, disposal and observer isolation. They do not
qualify all provider transports, native reasoning replay, paid usage settlement,
installed Lab behavior or Bridge migration. Follow issues #49–#56 for those gates.
