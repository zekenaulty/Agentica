# Durable host operations

Agentica Lab can hand an admitted operation to a host, close the current bounded
execution window, and retain the objective while the host performs local work.
The host explicitly delivers progress or requests another cognition window when
a decision is needed. This extends HostProtocol v1; existing synchronous
capabilities keep their current behavior.

## Ownership and the three lifetimes

- **Objective:** the requested result and its completion evidence. A partial run
  outcome does not satisfy it.
- **Operation:** durable host work admitted for that objective. The host owns its
  domain state, checkpoint, continuation, cancellation, resource policy and effects.
- **Execution window:** one Agentica run and its bounded provider/tool activity.
  Action and run timeouts bound this window, not the parked operation's lifetime.

The service retains the operation binding and event state. It does not schedule
the host, execute its local steps, interpret its route, or renew host policy.
Host-local steps and progress require no model keepalive. A quiet interval is valid.

## Admit once and park

The host deliberately advertises `durableHandoff: true` on a capability in the
ordinary start request. Its `onAction` implementation atomically admits durable
work and saves the ordinary action result, using the same action custody rules
as any effect. The returned result adds an operation:

```json
{
  "actionId": "original-admission-action",
  "sessionId": "host-session",
  "sessionEpoch": "owner-1",
  "disposition": "applied",
  "beforeRevision": 12,
  "afterRevision": 13,
  "evidenceId": "admission-receipt",
  "summary": "Durable host work admitted.",
  "observation": {
    "observationId": "admission-observation",
    "revision": 13,
    "observedAt": "2026-10-10T12:00:00Z",
    "data": { "workState": "running" }
  },
  "operation": {
    "operationId": "host-operation-1",
    "summary": "The host owns local execution.",
    "usage": { "localSteps": 0, "stepLimit": null }
  }
}
```

Admission requires an applied result, a current resulting observation, a bound
durable capability, and no `completion`. It completes only the admission effect.
The original action result stays immutable: arrival or later completion is a
separate event, never a replacement result.

The service sends `operation.parked` before the original execution window's
partial outcome. The parked payload is the durable operation entry described
below. The SDK retains its binding for later wake validation; it still fences
new effects from the terminal original run. Host applications must distinguish
this partial execution outcome from objective completion and retain local work.

## Inventory and event transport

The browser SDK exposes three explicit methods. They never start a background
polling loop and never retry uncertain delivery automatically.

```js
const entries = await host.listOperations({ hostId, sessionId });
await host.signalOperation(progressEvent);
const started = await host.wakeOperation(decisionEvent);
```

Both HTTP methods accept an optional `fetchImpl` for a host transport or fixture:
`listOperations({ hostId, sessionId, fetchImpl })` and
`signalOperation(event, { fetchImpl })`. Their HTTP origin is derived from the
configured WebSocket URL. `wakeOperation` uses that WebSocket connection.

| Boundary | Contract |
| --- | --- |
| `GET /api/operations?hostId=…&sessionId=…` | Explicit durable inventory. Each entry includes original `request`, original `action`, Agentica `usage` at admission, nullable final `settledUsage`, nullable `admission`, `activeEpoch`, `latestEvent`, `wakeRunId`, `state`, `createdAt`, and `updatedAt`. |
| `POST /api/operations/events` | `progress`, `transfer`, `completed`, `blocked`, or `cancelled`. No cognition activation. |
| WebSocket `operation.wake` | A `decision` event. Returns correlated `started` with the new bounded run identity. |
| WebSocket `operation.parked` | Reliable admission handoff notification, before the original run outcome. |

Inventory states are `reserved`, `parked`, `waking`, `continued`, `completed`,
`blocked`, and `cancelled`. `continued` retains parent lineage when its successor
admits another operation. A reservation without an admission is not proof that
a host effect is absent; inspect original action custody before attempting
anything else.

### Event shape

```js
const progressEvent = {
  hostId, sessionId, sessionEpoch,
  actionId: originalAdmission.actionId,
  operationId: originalAdmission.operation.operationId,
  sequence: 1,
  eventId: 'host-event-1',
  kind: 'progress',
  observation: currentObservation,
  summary: 'The host advanced its durable checkpoint.',
  usage: { localSteps: 9, stepLimit: null }
};
```

Persist the exact event in the host's durable outbox before delivery. Every event
has a stable identity and a sequence exactly one greater than the last committed
event under its operation (the first is 1). An exact retry of the latest accepted
event is recognized; changed duplicates, sequence gaps, and stale sequence
numbers are rejected. Do not generate a new event identity
to work around an uncertain acknowledgment. After reconnect, read the durable
inventory and reconcile the retained event.

Each event contains a bounded observation with its source identity, resulting
revision, observation time and data. Host `usage` is an optional JSON object of
at most 8 KiB. It is opaque accounting, distinct from cumulative Agentica usage.
Retain configured null/unlimited values and cumulative counters across host
reload, exact replay and transfer. A technical timeout does not replenish them.

### Transfer and completion

- `transfer` carries `nextSessionEpoch`. `sessionEpoch` names the current owner;
  the service compares that epoch before accepting the successor. The host must
  already have reconciled original pending effects and fenced the previous owner
  at its transaction boundary. Service transfer does not perform that host work.
- `completed` requires `completion` binding `objectiveId`, `evidenceId` and
  `summary`: the host must prove the governing objective. If the local operation
  arrives but the larger objective needs another decision, send a `decision`
  event instead. Local arrival does not implicitly fulfill that larger objective.
  The completion evidence identity must differ from the admission evidence.
- `blocked` and `cancelled` record terminal host state without model calls.
  Progress, pause, a transport timeout, and an observation update must not be
  misreported as verified completion.

Only a `completed` event may carry `completion`; only `transfer` may carry
`nextSessionEpoch`.

A transfer cannot revive a previously used ownership epoch. Stopping a bounded
Lab run does not cancel host-local work; the host records semantic cancellation
with a `cancelled` event after fencing its executor and reconciling original effects.

## Wake for a decision

Persist a `decision` event and call `wakeOperation` only when another cognition
decision is needed. The service uses the original objective and bound capability
surface, the event's fresh host observation, and remaining cumulative budgets.
The returned `started` identifies a new run; the SDK binds it before processing
early action requests. Retain the new binding in the host's durable state before
admitting its effects, using the normal host binding barrier.

An exact wake retry returns the existing run while it is still available; it
does not invoke the planner twice. If the service stops after durably claiming
a wake but before that execution can be recovered, it returns
`operation.wake_interrupted`. Reconcile that original wake and any resulting
action custody. There is no automatic fresh run after a claimed crash.

Parked entries survive a service restart. This is a durable handoff boundary,
not arbitrary serialization and resumption of the in-process runner stack.
The SDK's operation cache supports current-client validation only; call
`listOperations` explicitly after creating a replacement client.

## Accounting and host adoption

Agentica records cumulative `steps` (actual tool invocations), `refinements`,
`continuations`, and `providerCalls`. The original configured step/refinement/
continuation limits apply across successor execution windows, including the
continuation consumed by waking. Provider calls remain accounted; this seam
does not introduce a new monetary, token, or provider-call ceiling. Other
provider limits keep their existing behavior. Run and action timeouts apply
per execution window and exclude time parked at the host.

An entry's `usage` is the immutable cumulative baseline at its admission. When
its successor window settles, `settledUsage` retains the final cumulative totals
across service restart. A child admission commits the parent's `continued` state
and settlement totals in the same operation-ledger write. An interrupted wake
without a settlement has unknown final totals; the admission baseline does not
claim to include work from the lost process.

A host adapter must:

1. Bind its durable admission capability and retain original action evidence.
2. Run local work from its own checkpoint without model polling.
3. Keep operation identity separate from attempt and ownership epoch.
4. Reconcile unknown effects before another attempt or ownership transfer.
5. Preserve configured resource limits and cumulative use.
6. Persist and deduplicate progress/wake delivery independently of action results.
7. Supply explicit objective evidence when fulfillment is established.

No host-specific capability, map, scheduler, clock, authentication, or origin
restriction is introduced by this contract. Existing local Lab access behavior
remains the integration environment.

## Verification boundary

`browser-tests/host-operations.test.mjs` exercises explicit HTTP transport,
admission validation and immutable replay, recovery capability binding, parked
notification, wake response binding, and no automatic retry after error or
disconnect using mocked HTTP/WebSocket fixtures. Run it with:

```powershell
node --test browser-tests/host-operations.test.mjs browser-tests/host-sdk.test.mjs
```

These tests establish SDK behavior. They do not establish host checkpoint
continuation, service durability, paid-model intelligence, or cross-runtime
temporal continuity. Paired proof must name both exact source heads, SDK
identity, fixture and acceptance revision, original effect lineage, provider
calls per intention, local steps, wall time versus simulation time, retained
usage, and exact observation/context evidence.

### Service qualification: 10 October 2026

- Lab web .NET suite: **129 passed**, including real Kestrel HTTP/WebSocket
  admission, progress, decision wake, duplicate acknowledgment and completion.
- Browser SDK Node suite: **83 passed** across protocol, recovery, result
  validation and durable-operation fixtures.
- Focused cases include parked storage restart, interrupted claimed wake,
  transfer and retired epochs, invalid context before claim, original admission
  recovery, missing/corrupt ledger fencing, cumulative allowances and final
  usage, atomic child admission, and preservation of native planner sessions.
- Provider-default fixture checks retain an omitted reasoning effort across a
  later service configuration change. These are HTTP fixtures with no paid calls.

Maze Battle adoption and exact-head paired cross-runtime proof remain separate
acceptance work. These service results do not qualify the ordinary game, long
travel with reload/takeover, paid-model performance, or complete NS-6 behavior.
