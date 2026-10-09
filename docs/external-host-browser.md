# External browser hosts

**Contract:** version 1. Source of truth: [`HostProtocol.cs`](../Agentica.Lab.Web/Contracts/HostProtocol.cs), [`ProtocolValidation.cs`](../Agentica.Lab.Web/Contracts/ProtocolValidation.cs), [`HostRun.cs`](../Agentica.Lab.Web/Runtime/HostRun.cs), and [`LabWebApplication.cs`](../Agentica.Lab.Web/LabWebApplication.cs). Start and qualification commands are in [Lab web kickoff](lab-web-kickoff.md).

The Lab web service runs the existing bounded Agentica planner and execution loop. A browser or another application owns its authoritative state, observes its domain, and applies its bound capabilities. The service and browser exchange generic JSON over a browser-initiated WebSocket. No game vocabulary is required.

The dashboard at `/` lists runs and configured providers, follows provider progress, inspects context and receipts, and cancels runs. The scoped inventory example is an isolated browser-owned host. Its `demo` provider is a deterministic streaming fixture through the real planner/runtime path. Choosing another configured provider makes live API requests. Provider credentials remain on the service.

## Ownership and authority

| Owner | Responsibilities |
| --- | --- |
| External host | Canonical domain state, permitted perspective, rules, bound capabilities, single-writer ownership, revision fences, effect application, action custody and objective fulfillment evidence |
| Agentica service | Provider calls, bounded planning frames, plan validation, existing execution loop, awaited remote tool calls, derived knowledge, runtime receipts and outcome evaluation |
| Browser dashboard | Observational progress, context/evidence inspection, host connection status and explicit cancellation |

The host's capability list is a deliberate standing binding for this run. These registrations use `ToolApprovalRequirement.None`; routine bound calls do not request another approval. The service uses the supplied effect classification and binds capability provenance to the host and manifest hash. It still validates plans and arguments. Provider text, hypotheses and discovered semantic relevance cannot add a capability, change its binding or attest a domain effect.

The external host should execute its existing domain commands. Domain reflexes and simulation ticks can remain local; request cognition at meaningful decision boundaries. No second simulation or game-specific Agentica runtime is required.

### Identity rules

| Field | Meaning |
| --- | --- |
| `hostId` | Stable application/adapter identity |
| `sessionId` | Stable authoritative session, save or world identity |
| `sessionEpoch` | Ownership/incarnation fence for that session; not a way to evade unresolved actions |
| `scopeId`, `perspectiveId` | The bounded domain view used for context isolation |
| `objectiveId` | Stable objective identity, checked by host completion |
| `runId` | Service-created run identity; distinct from `runnerRunId` inside the core runtime |
| `actionId` | Service-created attempt identity. Keep it unchanged while reconciling or repeating a result |
| `observationId` | Identity of one immutable observation including its timestamp and contents |
| `revision` | Host-owned nonnegative, nondecreasing state version; browser clients use safe integers |

Durable context is isolated by the exact tuple `(hostId, sessionId, sessionEpoch, scopeId, perspectiveId)`. A new run can reuse that context with a fresh compatible observation. A new scope or perspective receives a different context. Changing an epoch does not resolve earlier effect custody.

The capability `manifestHash` is assigned by the service from canonical serialization of the capability list: object properties sort ordinally, array order is retained, then SHA-256 is encoded as lowercase hex prefixed with `sha256:`. Hosts should retain the returned binding and reject action requests outside it. Do not independently assume another JSON serializer produces identical digest bytes. The browser SDK's canonical action fingerprint is a local deduplication representation, not the server manifest digest.

## Wire format and exact shapes

Both directions use one UTF-8 JSON text message per WebSocket message:

```json
{
  "protocolVersion": 1,
  "type": "start",
  "requestId": "request-1",
  "runId": null,
  "payload": {}
}
```

`type` and `payload` are required; `requestId` and `runId` are nullable correlation fields. Always send `protocolVersion: 1` even though the envelope DTO has that default. A start payload separately requires `protocolVersion: 1`. Property names and enum values below use canonical camelCase. Unknown DTO members and numeric enum values are rejected. Arbitrary JSON inside `data`, `value`, and capability arguments follows the relevant host/tool contract.

Messages are bounded to 262,144 bytes with JSON depth at most 32. Binary WebSocket messages are rejected. Unless a tighter limit is listed, identity fields are nonblank strings of at most 128 characters with no control characters. `observedAt` and `deadlineAt` are ISO 8601 date-time strings with offset. No credentials or provider endpoint can be supplied in the start payload.

The following type notation states the exact JSON fields; `?` means optional, and nullable values may also be supplied as `null`. These shapes correspond to the current C# DTOs, rather than a separately generated JSON Schema file.

```ts
type HostRunRequest = {
  protocolVersion: 1;
  hostId: string; sessionId: string; sessionEpoch: string;
  scopeId: string; perspectiveId: string;
  objectiveId: string; objective: string;
  observation: HostObservation;
  capabilities: HostCapability[];
  provider?: ProviderSettings | null;
  limits?: HostRunLimits | null;
};

type HostCapability = {
  id: string; name: string; description: string;
  kind: 'query' | 'action' | 'plannerAssist' | 'validation' | 'synthesis';
  effect: 'readOnly' | 'writesLocalState' | 'externalSideEffect' | 'destructive';
  inputSchema: {
    fields: ToolInputField[];
    allowAdditionalProperties?: boolean; // default false
  };
};

type ToolInputField = {
  name: string;
  type?: 'any' | 'string' | 'integer' | 'number' | 'boolean' | 'object' | 'array'; // default string
  required?: boolean; // default false
  description?: string | null;
  allowedValues?: string[] | null;
  example?: unknown;
  minimum?: number | null;
  maximum?: number | null;
};

type HostObservation = {
  observationId: string; revision: number; observedAt: string;
  data: Record<string, unknown>;
  facts?: KnowledgeFact[] | null;
};

type KnowledgeFact = {
  key: string; summary: string; value: unknown;
  state?: 'observed' | 'inferred' | 'supported' | 'refuted' | 'stale'; // default observed
  evidenceObservationIds?: string[] | null;
  supersedes?: string | null;
};

type ProviderSettings = {
  provider?: string; // default gemini
  model?: string | null;
  thinkingEffort?: 'none' | 'minimal' | 'low' | 'medium' | 'high' | 'xhigh' | 'max' | null;
  maxOutputTokens?: number; // default 4096
  contextWindowTokens?: number; // default 131072
  includeThoughtSummaries?: boolean; // default false
  geminiApi?: 'interactions' | 'generatecontent' | 'legacy' | null; // default interactions; legacy aliases generatecontent
};

type HostRunLimits = {
  maxSteps?: number; // default 32; 1..128
  maxRefinements?: number; // default 32; 0..128
  maxPlanContinuations?: number; // default 16; 0..64
  timeoutSeconds?: number; // default 300; 1..3600
  actionTimeoutSeconds?: number; // default 45; 1..300
  maxRecentObservations?: number; // default 6; 1..32
  maxRecentReceipts?: number; // default 6; 1..32
};

type HostActionRequest = {
  actionId: string; runId: string; runnerRunId: string; stepId: string;
  sessionId: string; sessionEpoch: string; capabilityId: string;
  manifestHash: string; arguments: Record<string, unknown>;
  expectedRevision: number; deadlineAt: string;
};

type HostActionResult = {
  actionId: string; sessionId: string; sessionEpoch: string;
  disposition: 'applied' | 'refused' | 'conflict' | 'unavailable' | 'unresolved';
  beforeRevision: number; afterRevision: number;
  evidenceId: string; summary: string;
  observation?: HostObservation | null;
  completion?: {
    objectiveId: string; evidenceId: string; summary: string;
  } | null;
};
```

`ToolInputSchema` is Agentica's existing flat field contract. It is not arbitrary JSON Schema: nested `properties`, `items`, combinators and `$ref` are not accepted fields. An object or array field can carry host-defined structured data; the host must enforce any deeper domain rules.

Additional bounds:

| Value | Bound |
| --- | --- |
| Objective | 8,000 characters, no control characters |
| Capabilities | 1–32, unique IDs, `lab.` namespace reserved |
| Capability name / description | 128 / 2,000 characters, no control characters |
| Input fields / allowed values | At most 32 unique fields / 64 allowed values per field |
| Entire observation | 65,536 serialized bytes, object `data`, at most 64 facts |
| Fact key / summary / value | 256 characters / 1,024 characters / 8,192 serialized bytes |
| Fact evidence references | 1–8 retained sources when explicit; omission or empty array uses the current observation |
| Action result / completion summary | 4,000 characters each, no control characters |
| Provider output budget | 256–65,536 tokens |
| Provider context budget | 8,192–1,048,576 tokens; more than 4,096 tokens must remain after reserving output |

Two facts in one observation cannot share a key. A reused observation ID must have exactly the same content, and an old observation cannot become current again. A new observation may retain the same revision if no state changed; give it a new observation ID when its timestamp or contents change.

### Client messages and correlation

| Client type | Payload | Envelope requirements | Direct response |
| --- | --- | --- | --- |
| `start` | `HostRunRequest` | New `requestId`; omit `runId` | `started` with run snapshot and assigned `runId` |
| `resume` | `{runId, sessionId, sessionEpoch}` | New `requestId`; send matching `runId` | `resumed` with snapshot, then any pending reconciliation |
| `action.result` | `HostActionResult` | `runId` must match the run attached to this connection; optional `requestId` | `action.accepted` with `{actionId, duplicate}` |
| `cancel` | `{runId}` | `runId` must match the attached run; new `requestId` | `cancelled` with `{stopRequested:true}` |

Control responses echo the caller's `requestId`; asynchronous events normally omit it. An action is correlated by `actionId`, not by the control `requestId`. The service may also publish `action.accepted` telemetry with disposition/evidence and no `requestId`; it is distinct from the direct acknowledgment. Clients should not count either acknowledgment as a second host effect.

`resumed` may appear again without a `requestId` as a current-state notification. The SDK correlates only the response carrying its request ID. One socket owns at most one active run; one retained run accepts one attached host socket. Explicitly close the old connection before reconnecting elsewhere.

## Connect and start

Import `/sdk/agentica-host.mjs`, or copy that dependency-free ES module into your host application. Cross-origin module imports depend on the origin's CORS configuration; bundling the module avoids that dependency. A WebSocket connection is initiated by the host application:

```js
import { AgenticaHost } from './agentica-host.mjs';

const host = new AgenticaHost({
  url: 'ws://localhost:PORT/api/host',
  loadAction: key => actionStore.load(key),
  saveAction: (key, record) => actionStore.save(key, record),
  onAction: action => applyBoundAction(action),
  // Optional: inspect the original effect; never invoke the capability again.
  reconcileAction: (action, retainedRecord) => inspectOriginalEffect(action, retainedRecord),
  onMessage: message => displayProgress(message),
  onStatus: status => displayConnectionState(status)
});

const started = await host.start({
  protocolVersion: 1,
  hostId: 'inventory-app',
  sessionId: 'inventory-session-1',
  sessionEpoch: 'epoch-from-authoritative-store',
  scopeId: 'inventory-1',
  perspectiveId: 'operator',
  objectiveId: 'objective-1',
  objective: 'Accept one eligible record',
  observation: {
    observationId: 'observation-1', revision: 1,
    observedAt: new Date().toISOString(),
    data: { items: [{ id: 'item-1', eligible: true, status: 'pending' }] },
    facts: []
  },
  capabilities: [{
    id: 'inventory.accept', name: 'Accept record',
    description: 'Accept one eligible pending record in this inventory.',
    kind: 'action', effect: 'writesLocalState',
    inputSchema: {
      fields: [{ name: 'itemId', type: 'string', required: true }],
      allowAdditionalProperties: false
    }
  }],
  provider: { provider: 'gemini', includeThoughtSummaries: true },
  limits: {
    maxSteps: 32, maxRefinements: 32, maxPlanContinuations: 16,
    timeoutSeconds: 300, actionTimeoutSeconds: 45,
    maxRecentObservations: 6, maxRecentReceipts: 6
  }
});
```

Use stable host/session identities. Change the session epoch when ownership or the underlying authoritative session changes; reconnecting the same session does not change its epoch. Save the returned `runId` with the session. If `start` times out, inspect `GET /api/runs` for the matching session/objective before starting again. A timeout does not prove that no run was created.

The sample at `/` uses `demo.inspect` and `demo.accept`; those names belong to the sample. Real hosts supply their own capabilities and an appropriate provider.

## Apply and receipt actions

`onAction` receives:

```js
{
  actionId, runId, runnerRunId, stepId,
  sessionId, sessionEpoch, capabilityId, manifestHash,
  arguments, expectedRevision, deadlineAt
}
```

The host validates the session, revision, capability, arguments and domain rules against its current authoritative state. It returns:

```js
{
  actionId, sessionId, sessionEpoch,
  disposition: 'applied', // applied | refused | conflict | unavailable | unresolved
  beforeRevision: 1,
  afterRevision: 2,
  evidenceId: 'receipt-1',
  summary: 'The eligible item was accepted.',
  observation: {
    observationId: 'observation-2', revision: 2,
    observedAt: new Date().toISOString(),
    data: { items: [{ id: 'item-1', eligible: true, status: 'accepted' }] },
    facts: []
  },
  completion: {
    objectiveId: 'objective-1', evidenceId: 'receipt-1',
    summary: 'One eligible item is accepted in authoritative state.'
  }
}
```

Only attest completion when authoritative state fulfills the exact objective. An applied result with a fresh observation can carry completion evidence; a model's progress text cannot. An observation revision must equal the result's `afterRevision`. Refusal, conflicts and unresolved effects remain explicit. `unresolved` means that the outcome cannot be established; it never means that retrying an effect is safe.

Result validation is explicit:

- `beforeRevision >= 0` and `afterRevision >= beforeRevision`.
- An `applied` or `refused` result must start at the action's `expectedRevision`; a stale action is `conflict`.
- An applied `readOnly` capability cannot change the revision.
- `refused`, `conflict`, and `unavailable` do not mutate state and must have equal before/after revisions.
- `applied`, `refused`, and `conflict` require an observation. Any supplied observation must match `afterRevision` and cannot rewind the current context.
- Another action cannot reuse an existing `evidenceId`.
- A resolved action accepts an identical repeated result and rejects a changed result. `unresolved` can later receive its original resolved result through reconciliation.
- Completion must accompany `applied` and a fresh observation and must use the current `objectiveId`.

The service turns accepted host results into runtime receipts and observations. Completion becomes a `host.objective.completed` artifact referenced by the core completion evaluator. It remains the host's attestation about host state; the generic service does not independently simulate the host's rules. Any outstanding unresolved action blocks successful completion.

### Persistent attempt custody

The SDK serializes incoming action processing and reserves the action identity before invoking `onAction`. Its canonical fingerprint binds run, session, epoch, step, capability, manifest, arguments, expected revision and deadline. Repeated matching actions return their retained result. Reusing an identity with changed bindings produces an unresolved response and no second invocation.

Supply **both** persistence hooks for restart protection:

- `loadAction(key)` returns a retained record or `null`.
- `saveAction(key, record)` must complete only after the reservation or result is stored.
- A record is `{ fingerprint, status: 'pending' }`, `{ fingerprint, status: 'unresolved', result }`, or `{ fingerprint, status: 'completed', result }`. Only a concrete non-unresolved result can be completed. Legacy `completed` records carrying an unresolved result are read as unresolved.
- `actionKey(action)` and `actionFingerprint(action)` are exported for hosts that atomically persist their world transition and completed result in `onAction`.

For effects, the authoritative host should atomically commit its state transition and completed action result. If the process crashes between dispatch and recording the result, the pending reservation prevents re-execution. Reconcile authoritative state before resolving that pending attempt. The SDK does not infer whether the effect happened.

An unresolved `onAction` response stays uncertain in the client. Its original durable reservation remains intact, so storing uncertainty cannot overwrite a concurrent host commit of the exact completed result. Storage hooks should enforce that completed results are immutable, including against concurrent writers. Reconciliation reloads durable custody even when the same SDK instance has cached an older pending/unresolved record. Changed fingerprints or changed completed results are rejected; known completed evidence is preserved.

Without persistence hooks, duplicate protection covers only the current `AgenticaHost` instance. It does not establish restart durability or protect separate host writers. The host must enforce a single writer or a transactional revision fence for the underlying world/save. The browser sample uses a Web Lock and one localStorage write for its small isolated state and exact action result; its storage implementation is an example, not a general database adapter.

## Disconnect, resume and cancel

```js
await host.resume({ runId, sessionId, sessionEpoch });
await host.stop(runId);
host.close(); // Disconnects transport; does not imply cancellation.
```

Reconnection is explicit. The service requests `action.reconcile` for a previously dispatched unresolved action. The SDK refreshes durable custody and returns an exact retained concrete result, without calling `onAction`. This picks up a late result committed by the host after the SDK originally observed pending or unresolved custody.

If no completed result exists, the optional constructor callback `reconcileAction(action, retainedRecord)` may inspect authoritative state and the original attempt journal. It returns the original `HostActionResult`, or `null`/`undefined`/an unresolved result if the outcome remains unknown. It must not execute or repeat the capability. The SDK validates and persists concrete resolution before sending it; a failed persistence write keeps the service response unresolved. Missing evidence without a callback stays unresolved. A cached completed result cannot be replaced by a later changed record.

`stop` requests cancellation and prevents queued, unstarted actions from invoking the host. An action already executing may still finish; the host must retain its result. Cancellation does not roll back an external effect. Control-request timeouts are reported without automatic repetition.

Each client uses one connection, one action-processing queue, bounded display messages (256), bounded queued actions (64) and an action retention limit (2,048). Action records are never evicted to make room for another effect. Hitting the limit refuses further new execution until the host deliberately starts a fresh client/store lifecycle with appropriate retained evidence. Change limits through the constructor when the host has a defined retention policy.

### Service restart reconciliation

A WebSocket reconnect can resume an in-memory run. A service restart cannot restore its planner conversation or runtime stack. Durable effect custody fences the original host/session across epochs so that starting another run cannot silently repeat an unresolved effect.

Recovery uses a separate read/reconcile path:

```text
GET /api/recovery?hostId=inventory-app&sessionId=inventory-session-1

POST /api/recovery
Content-Type: application/json

{ "hostId": "inventory-app", "sessionId": "inventory-session-1", "result": { ...HostActionResult } }

Response: { "actionId": "original-action-id", "resolved": true, "duplicate": false }
```

GET returns an array of durable custody entries containing the original `.request: HostActionRequest` plus its host/session/run bindings. POST resolves only that original action; it does not invoke a capability. An unresolved result keeps the fence. If the original run remains retained in this process, POST reports `recovery.live_run`: reconnect via WebSocket and submit `action.result` so the live runner receives the result.

The GET array contains only unresolved entries with this exact shape:

```ts
type ActionCustodyEntry = {
  hostId: string; sessionId: string; sessionEpoch: string;
  runId: string; objectiveId: string;
  request: HostActionRequest; capability: HostCapability;
  requestHash: string; capabilityHash: string;
  reservedAt: string; updatedAt: string;
  result?: HostActionResult | null; resultHash?: string | null;
};
```

The service reserves and flushes the original request/capability binding before possible delivery. The custody ledger allows at most 128 unresolved entries, 1,024 total entries, 256 retained completed entries per host/session, and 64 MiB on disk. Completed history may be pruned; unresolved entries are never pruned to make room. The ledger's content hash and initialization marker are checked on reads. A missing/corrupt ledger or changed marker fences admission instead of creating a replacement store. Keep the ledger and marker together; manually deleting all storage cannot prove old effects absent.

The SDK helper uses completed host records first:

```js
const recovery = await host.recover({
  hostId: 'inventory-app',
  sessionId: 'inventory-session-1',
  // Optional. Only inspect original custody and authoritative state here.
  // Never call the capability again from this callback.
  reconcile: (action, retainedRecord, durableServiceEntry) =>
    lookupOriginalEffectResult(action, retainedRecord, durableServiceEntry)
});

const outstanding = recovery.filter(item => !item.resolved);
// Keep the same session fenced while anything is outstanding.
```

`recover()` never calls `onAction`. It refreshes durable records, compares original fingerprints, preserves action/session/epoch identities, persists an established host result before POST, and returns per-entry `resolved`, `unresolved`, `live_run`, or `error` outcomes. Its explicit `reconcile` callback takes precedence over the constructor's `reconcileAction` callback; if neither exists, missing evidence stays unresolved. A prior unresolved response, including a legacy record marked completed, does not prevent this state inspection. An expired original deadline does not prevent reporting a previously established result; it still prevents a new invocation. Recovering an effect does not retroactively turn the lost run into a successful completed run. Recovery resolves custody only; a subsequent new run must supply a fresh observation, since the HTTP recovery route does not rebuild lost planner context or update its old completion outcome.

The browser example checks recovery before replacing an earlier sample session. Its Reconnect button switches to this recovery path when the old service run is no longer retained. Pending host reservations without a proven result remain visible as a block to starting another example.

## Streaming and inspection

One Agentica run continues across provider calls and tool awaits. The host does not restart the planner on every action result. The service emits:

| Type | Meaning |
| --- | --- |
| `started`, `resumed`, `cancelled` | Control response, correlated by `requestId` |
| `progress` | Provider activity, output counts, optional available thought summaries |
| `execution.event` | Runtime observation of planning/execution transitions |
| `action.request` | Awaited bound host capability call |
| `action.reconcile` | Resolve a previously dispatched action from retained host evidence |
| `action.accepted` | Service accepted a host action result |
| `outcome` | Terminal run outcome |
| `run.terminated` | Reliable exceptional terminality when the run could not produce a normal runtime outcome |
| `error` | Rejected request or service error |
| `telemetry.gap` | Progress replay exceeded retained history |

The service also publishes `connection` (`{connected}`), `status` (`{status}`), `action.pending` (the pending action request for inspection), and `cancel.requested` (`{stopFutureDispatch:true,reconcileDispatchedActions:true}`). `action.pending` is observational; only the awaited WebSocket `action.request` invokes a capability. Transport `error` payloads use `{code,message}`; unexpected run failures may add `exceptionType`.

Every host consumer should handle both terminal message types. `run.terminated` is an awaited reliable message with `{status:"failed"|"cancelled",code:"run.failed",message,snapshotUrl}`. It is retained as `snapshot.termination` and replayed on resume. A setup/runtime exception can leave `outcome:null` because no completed core outcome envelope exists. The termination record explicitly ends the run without inventing one. Pending effects still require reconciliation. The SDK stops new action invocation on either `outcome` or `run.terminated` while allowing exact retained results to be reconciled.

The dashboard separately reads `GET /api/runs/{runId}/events` as ordinary SSE `data` envelopes with event IDs. EventSource reconnects with `Last-Event-ID`; gaps are explicit. `GET /api/runs/{runId}` returns current run state, context, pending actions and any outcome. Provider display telemetry is bounded and may drop progress while effects are reconciled through separate action results. Native provider signatures and opaque continuation data are not browser display payloads.

Progress payloads contain `{record,droppedRecords,context}`. The optional correlation `context` records service `runId`, `runnerRunId`, `objectiveId`, `planningOperation` (`create` or `refine`), `currentPlanId`, `planVersionCount`, `afterStepId`, and identity-only `frames` (`frameId`, `kind`, `version`, `toolSurfaceId`). Each provider call retains the planning binding captured when it began, including repair calls. At most 512 call associations are kept; unavailable bindings are explicitly null. This metadata connects live call IDs to the existing execution events and retained frames without exposing frame content or private provider state. Run guidance is labeled `lab-host/1` in the planning request.

An outcome fits the normal core envelope when its serialized wire payload is at most 128 KiB. Larger outcomes use `{outcome:{runId,status,stopReason},truncated:true,snapshotUrl,receiptCount,observationCount}`; retrieve the full outcome from `snapshotUrl`. Oversized observational messages preserve their message type with `{truncated:true,snapshotUrl}`. Actions are never truncated: an oversized action fails before dispatch.

The SDK invokes `onMessage` for display only. Exceptions in display callbacks do not execute or repeat effects. `messages` holds bounded recent messages and `droppedMessages` counts evictions.

### HTTP inspection and retention

| Route | Result |
| --- | --- |
| `GET /api/health` | `{status:"ready",protocolVersion:1}`; process readiness only |
| `GET /api/providers` | Array of `{provider,defaultModel,configured,configurationIssue,api,streams}` |
| `GET /api/runs` | Retained snapshots, newest first; `context:null` |
| `GET /api/runs/{runId}` | Full snapshot including context |
| `GET /api/runs/{runId}/events` | SSE observational envelopes |
| `POST /api/runs/{runId}/cancel` | `{runId,status:"cancel_requested"}`; stop requested, effects may require reconciliation |
| `GET /api/recovery` / `POST /api/recovery` | Durable custody inspection and original-attempt resolution |

A run snapshot is `{runId,hostId,sessionId,sessionEpoch,objectiveId,objective,scopeId,perspectiveId,status,createdAt,updatedAt,connected,manifestHash,pendingActions,outcome,termination,context}`. Each pending item is `{request:HostActionRequest,deliveryAttempted,unresolved}`. `outcome` is the existing core [`OutcomeEnvelope`](../Agentica/Outcomes/OutcomeEnvelope.cs), or `null`; `termination` is the exceptional terminal record described above or `null`.

Run statuses include `starting`, `running`, `awaiting_host`, and terminal core status names in lowercase: `succeeded`, `planinvalid`, `blocked`, `failed`, `waitingforapproval`, `cancelled`, `partiallycomplete`. Inspect `pendingActions` even when a run is terminal. `connected` describes the host socket; it is independent of the dashboard SSE connection.

The process retains at most 64 run objects. At capacity it can evict the oldest terminal run without unresolved custody; it never evicts an unresolved run to make room. An unretained run produces `run.not_found` (HTTP 404); that is not permission to replay its effects. Other handled HTTP contract errors use 409 with `{code,message}`. Persistent storage read/write failures use HTTP 503 with `code:"storage.unavailable"`; resolve storage before dispatching more work. HTTP request bodies use the same 256 KiB ceiling as host messages.

SSE retains 512 events per run and up to 128 queued events per observer, with at most 32 observers per run. Pass `Last-Event-ID` or `?after=N` to resume retained display history. Event IDs are per-run monotonic sequence numbers; `telemetry.gap` includes `{after,next,snapshotUrl}` when history or the observer queue has a gap. The WebSocket progress queue holds 64 messages and may drop its oldest progress; its `telemetry.gap` payload uses `{dropped,snapshotUrl}`. Use SSE for sequence-visible operator replay. Required action/control/terminal sends use the separate awaited send path with a five-second send timeout. Receipt custody and result correlation resolve an effect beyond transport delivery.

## Bounded context and learned knowledge

The host sends only the perspective it permits the planner to know. Its canonical world/save remains host-owned. `data` holds the current scoped view; `facts` can preserve known locations, resources, obligations or other domain concepts without requiring a game-specific schema.

The context store retains at most 128 exact observations and 256 knowledge entries per context identity. It preferentially retains current knowledge; omission/pruning counts and source availability remain visible. Projection selects up to 48 current facts within an 81,920-byte frame target. The compact representation keeps up to 12 facts within 16,384 bytes and omits current observation data and fact values while retaining references. The client prompt compiler may compact or further trim bounded recent observations/receipts under its own input budget.

Context snapshots contain:

```ts
type HostContextSnapshot = {
  identity: { hostId: string; sessionId: string; sessionEpoch: string; scopeId: string; perspectiveId: string };
  currentObservation: HostObservation;
  facts: Array<{
    id: string; key: string; summary: string; value: unknown;
    state: string; source: 'host' | 'model';
    evidence: HostEvidenceReference[];
    supersedes: string | null; change: string;
    revision: number; updatedAt: string; isCurrent: boolean;
  }>;
  evidence: Array<{ reference: HostEvidenceReference; available: boolean }>;
  retainedObservationCount: number; prunedObservationCount: number; prunedFactCount: number;
  thoughtTests: HostThoughtTest[];
  actionEvidence: HostActionEvidence[];
};
type HostEvidenceReference = {
  observationId: string; revision: number; observedAt: string; contentHash: string;
};
type HostThoughtTest = {
  testId: string; hypothesisId: string; capabilityId: string;
  expectedResult: string; falsifier: string; plannedAt: string;
  plannedObservation: HostEvidenceReference; actionId: string | null;
  assessment: null | {
    assessment: 'supported' | 'refuted' | 'inconclusive'; summary: string;
    actionId: string; hostEvidenceId: string; observationId: string;
    resultHash: string; observationHash: string; assessedHypothesisId: string; assessedAt: string;
  };
};
type HostActionEvidence = {
  actionId: string; capabilityId: string; runId: string; requestHash: string;
  intentRecordedAt: string; thoughtTestIds: string[];
  result: null | {
    evidenceId: string; disposition: string; beforeRevision: number; afterRevision: number;
    resultHash: string; observation: HostEvidenceReference | null;
  };
};
```

Context content hashes use the context store's canonical SHA-256 lowercase hex representation without the manifest's `sha256:` prefix. Treat hashes as opaque exact source identities. Context files contain host public observations and derived knowledge, not provider-native reasoning state.

Four service-local capabilities are added beside host capabilities:

| Tool | Input | Contract |
| --- | --- | --- |
| `lab.evidence.read` | Required `observationId`; optional `contentHash` | Returns exact source or explicit `unknown` / `not_retained`. An expected-hash mismatch is refused |
| `lab.knowledge.query` | Optional `key` or `keyPrefix` (mutually exclusive), `state`, `limit` (1–32, default 16), `cursor` | Bounded current knowledge with source availability, total/remaining counts and next cursor. Changed knowledge/filter identity invalidates the cursor |
| `lab.hypothesis.record` | Required `key`, `summary`, `value`, `evidenceObservationIds`; optional `state`, `supersedes`; optional `expectedResult`, `falsifier`, `capabilityId` together | Records model `inferred`, `refuted` or `stale` knowledge against 1–8 retained observations; the optional triple registers a prediction before the next matching host action |
| `lab.hypothesis.assess` | Required `hypothesisId`, `actionId`, `hostEvidenceId`, `observationId`, `assessment`, `summary` | Records a model assessment (`supported`, `refuted`, `inconclusive`) only against the exact pre-action prediction and resolved host result; no promotion to host truth or completion |

Host facts may be observed, inferred, supported, refuted or stale. Repeating a current host key refreshes or corrects it; `supersedes` can identify one current host fact by key or ID. Model corrections can supersede only current model hypotheses. A retired entry becomes stale unless already refuted. Missing retained source content is never recreated from its summary or hash.

These capabilities execute inside the Lab context store and do not send `action.request` to the host. They still pass through the normal tool/receipt path. [Lab thought testing](lab-thought-testing.md) describes prediction chronology, original result binding, model assessment, retention and schema-1 migration. Up to 128 thought tests and 128 action-evidence records are retained separately from authoritative effect custody. A result's `supported` assessment remains `source:model`.

## Verification

Run the dependency-free SDK seam tests:

```text
node --test browser-tests/host-sdk.test.mjs
node --test browser-tests/host-recovery.test.mjs
```

They exercise duplicate action results, changed arguments, reservation ordering, durable-store failures, new-client reconciliation, disconnected completion, session and epoch mismatch, invalid results, bounds and control correlation. These tests do not establish live-provider success or a connected external game integration.

For an isolated running Lab service, set `AGENTICA_LAB_TEST_URL` to its HTTP origin and run `node --test browser-tests/service-integration.test.mjs`. That opt-in test uses only the deterministic demo provider and verifies the complete streamed SDK/service loop and host completion evidence. It is skipped when no service URL is supplied.

`node browser-tests/ui-smoke.mjs` also exercises the actual browser dashboard with the deterministic example, context inspection, progress counters, and a 390 px mobile layout. It requires Playwright locally; set `AGENTICA_PLAYWRIGHT_MODULE` to a module URL if the package is installed elsewhere. Optional `AGENTICA_BROWSER_ARTIFACTS` selects a screenshot directory. Generated screenshots under `browser-tests/artifacts` are ignored by Git.
