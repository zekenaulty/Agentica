# Durable Initial Host Starts

2026-10-15. This extends the Lab host protocol without importing game semantics.
Maze Battle owns its game, browser storage, local execution and explicit grants.
The service admission below is neither a host grant nor objective completion.

## Contract

Hosts retain an attempt ID and complete original request before sending:

```text
WebSocket start.durable { attemptId, request: HostRunRequest }
  -> started { runId, hostId, sessionId, sessionEpoch, scopeId,
               perspectiveId, objectiveId, manifestHash, startAdmission }

GET /api/starts?hostId=...&sessionId=...&attemptId=...
  -> HostStartEntry

POST /api/starts/stop
  { hostId, sessionId, attemptId, runId, acceptedRequestHash }
  -> HostStartEntry
```

`HostStartEntry` retains original and accepted typed requests, their independent
canonical digests, the capability digest, immutable run ID, admission timestamp,
Stop request and optional terminal receipt. Accepted input may only normalize
provider aliases, freeze unspecified provider model/thinking settings, and
materialize default limits. Explicit settings and all scope, identity, objective,
observation and capability data remain bound to the original request.

Admission is flushed before opening context or creating a run. A context failure
or lost socket reply leaves the original admission recoverable. An exact retry
returns the retained live run without another planner start. Changed input fails.
An admitted run missing after restart/eviction fails `start.interrupted`; it is
never recreated automatically. Missing lookup returns `start.not_found`, not a
proof that an earlier request was never attempted.

The original `start` protocol remains for existing non-durable SDK consumers.
It cannot bypass an open durable start or reuse its retained objective identity.
This is an Agentica API addition, not a second Maze save format or fallback.

## Stop And Recovery

Stop first retains a durable fence, then cancels matching live execution windows.
The fence rejects both repeat initial starts and subsequent operation wakes.
Original action results and host operation terminal evidence can still reconcile.
The response has `terminal: null` until all session windows are quiescent,
original dispatched actions are resolved, and host operations are closed.

Only then does the registry persist an immutable terminal receipt. Known usage
comes from the actual retained initial run. A missing process or handed-off
operation is conservatively `interrupted` with null/unknown usage, never zero
spend. This is not host objective success. Repeating a settled Stop returns the
same receipt without touching a newer objective. No lookup or Stop calls a model.

Cancellation before `Start()` prevents the socket handler's `finally` block from
starting it later. Durable attachment owns the socket send gate while sending
the binding response, preventing a running planner's action from overtaking it.
Rejected admissions do not evict unrelated retained execution windows.

## SDK

`AgenticaHost.startDurable(submission)`, `getStart(key, { fetchImpl })`, and
`stopStart(reference, { fetchImpl })` expose the contract. Callers retain all
durable intent, references and results in their own canonical storage.

The SDK freezes submission input, validates typed-default equivalence and the
complete authority payload, checks permitted settings normalization and terminal
consistency, and installs bindings synchronously before queued effects. It does
not claim JavaScript recomputation of the service's .NET request digests.
Typed normalization never strips arbitrary keys or normalizes opaque payloads.
Stop fences local effects before HTTP; an uncertain reply remains uncertain.
These methods never automatically retry, resume, wake, or issue replacement starts.

## Storage Bounds

The service uses one `HostStartStore` instance per process/storage directory.
The checksum-protected atomic ledger and initialization marker fail closed on
missing, partial or corrupt evidence. They do not authenticate malicious disk
edits or support concurrent service processes sharing the same directory.

Limits: 512 retained admissions/tombstones; 64 MiB logical envelope plus reserved
settlement allowance; each original and accepted typed request at most 64 KiB.
Every unsettled entry reserves 512 bytes for Stop settlement. RequestStop retains
that allowance; only settlement releases it. No source/tombstone eviction or
implicit reset is provided. These are logical limits, not physical disk guarantees.
Hosts must explicitly manage lifecycle/capacity; a full ledger fails admission
before a new planner can run. HTTP reads are exact-key, no-store responses.

Action custody now excludes operation-admission receipts from all three pruning
paths: per-session, global-count and byte-budget. The operation ledger still
requires those receipts for progress, cancellation and wake validation. Ordinary
completed receipts retain their existing pruning policy; protected capacity fails
closed instead of deleting required admission evidence. This prevents future
loss, not recovery of evidence pruned by an older build.

## Qualification Boundary

The focused store/registry/socket/SDK tests cover immutable replay, restart,
failed context opening after admission, cancelled-before-start, unknown usage,
unresolved custody and parked operation fences, quota rollback, typed defaults,
early action ordering, and capacity rejection without unrelated run eviction.
The real Node SDK test uses an isolated loopback service and a deterministic
planner: exactly one fixture host action, zero provider requests.

Local verification uses installed .NET SDK 10.0.401 from outside the repository;
the unchanged repository pin is 10.0.302. The project-aware format attempt could
not resolve that pinned SDK; targeted folder-mode whitespace formatting passed.
Node is 22.13.0. Hosted CI and the pinned-SDK build are not claimed.

Final checked candidate `9af8801`: **342/342 Lab Web Release tests** and
**328/328 SDK tests** with `AGENTICA_TEST_START_SERIALIZATION=1`. The default SDK
run passes 304 with one optional serializer test skipped; enabling it runs the
additional typed-serialization subcases. The Release suite includes the actual
Node SDK/loopback service proof and independent HTTP/WebSocket/restart cases.
Independent review found and repaired response ordering, rejected-start eviction,
unfunded terminal capacity, settings-normalization mismatches and custody pruning.
No external provider was invoked and no existing host data was reset.

Maze must still adopt this protocol through its canonical launch records and
GameSession, reconcile exact original request identity, settle the funded host
record, and mount the application controller. This service/SDK qualification does
not establish those host behaviors or qualify a live model.
