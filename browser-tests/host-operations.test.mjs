import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgenticaHost, actionKey } from '../Agentica.Lab.Web/wwwroot/sdk/agentica-host.mjs';

class Socket {
  static instances = [];
  constructor(url) { this.url = url; this.readyState = 0; this.handlers = new Map(); this.sent = []; Socket.instances.push(this); }
  addEventListener(type, handler) { this.handlers.set(type, [...(this.handlers.get(type) ?? []), handler]); }
  emit(type, payload = {}) { for (const handler of this.handlers.get(type) ?? []) handler(payload); }
  open() { this.readyState = 1; this.emit('open'); }
  send(raw) { this.sent.push(JSON.parse(raw)); }
  receive(message) { this.emit('message', { data: JSON.stringify({ protocolVersion: 1, ...message }) }); }
  close() { this.readyState = 3; this.emit('close'); }
}

const observation = revision => ({ observationId: `observation-${revision}`, revision, observedAt: '2026-10-10T12:00:00Z', data: { progress: revision } });
const capability = { id: 'host.admit', effect: 'writesLocalState', durableHandoff: true };
const original = { protocolVersion: 1, hostId: 'host', sessionId: 'session', sessionEpoch: 'epoch-1',
  scopeId: 'scope', perspectiveId: 'actor', objectiveId: 'objective', objective: 'Complete host work.',
  observation: observation(1), capabilities: [capability] };
const action = { actionId: 'admission-action', runId: 'original-run', runnerRunId: 'runner', stepId: 'step',
  sessionId: original.sessionId, sessionEpoch: original.sessionEpoch, capabilityId: capability.id, manifestHash: 'manifest',
  arguments: {}, expectedRevision: 1, deadlineAt: '2099-10-10T12:00:00Z' };
const admission = { actionId: action.actionId, sessionId: action.sessionId, sessionEpoch: action.sessionEpoch,
  disposition: 'applied', beforeRevision: 1, afterRevision: 2, evidenceId: 'admission-evidence', summary: 'Host work admitted.',
  observation: observation(2), operation: { operationId: 'operation', summary: 'Host owns execution.', usage: { steps: 0, limit: null } } };
const entry = () => ({ request: structuredClone(original), action: structuredClone(action),
  usage: { steps: 1, refinements: 0, continuations: 0, providerCalls: 1 }, admission: structuredClone(admission),
  activeEpoch: original.sessionEpoch, latestEvent: null, wakeRunId: null, state: 'parked',
  createdAt: '2026-10-10T12:00:00Z', updatedAt: '2026-10-10T12:00:01Z' });
const event = (patch = {}) => ({ hostId: original.hostId, sessionId: original.sessionId, sessionEpoch: original.sessionEpoch,
  actionId: action.actionId, operationId: 'operation', sequence: 1, eventId: 'event-1', kind: 'progress',
  observation: observation(3), summary: 'Host progressed.', usage: { steps: 2, limit: null }, ...patch });
const response = (body, status = 200) => ({ ok: status >= 200 && status < 300, status, text: async () => JSON.stringify(body) });
const host = (options = {}) => new AgenticaHost({ url: 'ws://localhost:5078/api/host', WebSocketImpl: Socket,
  onAction: () => structuredClone(admission), requestTimeoutMs: 1000, ...options });
async function connect(client) { const pending = client.connect(); const socket = Socket.instances.at(-1); socket.open(); await pending; return socket; }
async function turn() { await new Promise(resolve => setImmediate(resolve)); }
function started(request, patch = {}) {
  return { type: 'started', requestId: request.requestId, runId: 'wake-run', payload: {
    runId: 'wake-run', hostId: original.hostId, sessionId: original.sessionId, sessionEpoch: original.sessionEpoch,
    objectiveId: original.objectiveId, ...patch } };
}

test('operation inventory is explicit, scoped HTTP and retains the original capability binding', async () => {
  const client = host(); const calls = []; const before = Socket.instances.length;
  const entries = await client.listOperations({ hostId: 'host', sessionId: 'session', fetchImpl: async (...args) => {
    calls.push(args); return response([entry()]);
  } });
  assert.deepEqual(entries, [entry()]); assert.equal(calls.length, 1); assert.equal(Socket.instances.length, before);
  const url = new URL(calls[0][0]); assert.equal(url.protocol, 'http:'); assert.equal(url.pathname, '/api/operations');
  assert.equal(url.searchParams.get('hostId'), 'host'); assert.equal(url.searchParams.get('sessionId'), 'session');
  entries[0].request.capabilities[0].durableHandoff = false;
  assert.equal([...client.operations.values()][0].request.capabilities[0].durableHandoff, true);
});

test('foreign, malformed and oversized operation inventories are rejected without caching partial data', async () => {
  const foreign = entry(); foreign.request.hostId = 'another';
  const badAdmission = entry(); badAdmission.request.capabilities[0].durableHandoff = false;
  const badEpoch = entry(); badEpoch.request.sessionEpoch = 'another';
  const missingCapabilities = entry(); delete missingCapabilities.request.capabilities;
  for (const body of [[entry(), foreign], [foreign], [badAdmission], [badEpoch], [missingCapabilities], {}, [entry(), entry()]]) {
    const client = host({ maxActions: 1 });
    await assert.rejects(client.listOperations({ hostId: 'host', sessionId: 'session', fetchImpl: async () => response(body) }));
    assert.equal(client.operations.size, 0);
  }
});

test('a continued operation remains readable as lineage after a successor admits new work', async () => {
  const client = host(); const continued = { ...entry(), state: 'continued', wakeRunId: 'wake-run' };
  assert.deepEqual(await client.listOperations({ hostId: 'host', sessionId: 'session', fetchImpl: async () => response([continued]) }), [continued]);
});

test('progress, transfer, completion and terminal signals use HTTP without connecting or invoking host effects', async () => {
  let effects = 0; const client = host({ onAction: () => effects++ }); const before = Socket.instances.length; const calls = [];
  const signals = [event(), event({ kind: 'transfer', nextSessionEpoch: 'epoch-2' }),
    event({ kind: 'completed', completion: { objectiveId: 'objective', evidenceId: 'arrival-evidence', summary: 'Verified.' } }),
    event({ kind: 'blocked' }), event({ kind: 'cancelled' })];
  for (const value of signals) {
    const result = await client.signalOperation(value, { fetchImpl: async (url, options) => { calls.push({ url, options }); return response({ accepted: true }); } });
    assert.deepEqual(result, { accepted: true });
  }
  assert.equal(effects, 0); assert.equal(Socket.instances.length, before);
  assert.deepEqual(calls.map(call => JSON.parse(call.options.body)), signals);
  assert.ok(calls.every(call => call.url === 'http://localhost:5078/api/operations/events' && call.options.method === 'POST'));
  assert.equal(JSON.parse(calls[0].options.body).usage.limit, null);
});

test('event validation rejects malformed routing, transfer, completion and accounting before HTTP', async () => {
  let calls = 0; const client = host(); const fetchImpl = async () => { calls++; return response({}); };
  for (const patch of [{ kind: 'decision' }, { kind: 'transfer' }, { kind: 'completed' }, { nextSessionEpoch: 'epoch-2' },
    { kind: 'transfer', nextSessionEpoch: 'epoch-1' }, { sequence: 0 }, { sequence: 1.5 }, { eventId: '' },
    { operationId: 'bad\nidentity' }, { usage: [] }, { usage: { value: 'x'.repeat(8192) } },
    { observation: { ...observation(3), revision: -1 } }, { unexpected: true },
    { completion: { objectiveId: 'objective', evidenceId: 'evidence', summary: 'Not yet.' } }]) {
    await assert.rejects(client.signalOperation(event(patch), { fetchImpl }));
  }
  assert.equal(calls, 0);
});

test('HTTP errors retain the service error code and never retry an uncertain event', async () => {
  let calls = 0; const client = host(); const value = event();
  await assert.rejects(client.signalOperation(value, { fetchImpl: async () => {
    calls++; return response({ code: 'operation.sequence_conflict', message: 'Changed duplicate.' }, 409);
  } }), error => error.code === 'operation.sequence_conflict');
  assert.equal(calls, 1);
  await assert.rejects(client.signalOperation(value, { fetchImpl: async () => { calls++; throw new Error('Connection lost after delivery.'); } }));
  assert.equal(calls, 2);
});

test('admission is durably retained and replayed exactly without becoming completion', async () => {
  let effects = 0; const records = new Map(); const client = host({ onAction: () => { effects++; return structuredClone(admission); },
    loadAction: key => records.get(key), saveAction: (key, value) => records.set(key, value) });
  client.runs.set(action.runId, structuredClone(original)); const socket = await connect(client);
  for (let index = 0; index < 2; index++) {
    socket.receive({ type: 'action.request', runId: action.runId, payload: action }); await client.actionQueue;
    assert.deepEqual(socket.sent.at(-1).payload, admission);
  }
  assert.equal(effects, 1); assert.deepEqual(records.get(actionKey(action)).result, admission);
  assert.equal(records.get(actionKey(action)).result.completion, undefined); client.close();
});

test('admission requires the bound durable capability, fresh observation, applied result and no completion', async () => {
  const cases = [
    { capability: { ...capability, durableHandoff: false } },
    { capability: { ...capability, id: 'another-capability' } },
    { result: { ...admission, observation: undefined } },
    { result: { ...admission, disposition: 'unresolved' } },
    { result: { ...admission, completion: { objectiveId: 'objective', evidenceId: 'evidence', summary: 'Done.' } } },
    { result: { ...admission, operation: { ...admission.operation, usage: { value: 'x'.repeat(8192) } } } },
  ];
  for (const fixture of cases) {
    const client = host({ onAction: () => structuredClone(fixture.result ?? admission) });
    client.runs.set(action.runId, { ...original, capabilities: [fixture.capability ?? capability] });
    const socket = await connect(client); socket.receive({ type: 'action.request', runId: action.runId, payload: action }); await client.actionQueue;
    assert.equal(socket.sent.at(-1).payload.disposition, 'unresolved'); client.close();
  }
});

test('recovery accepts the original admission only when the recovered capability permits handoff', async () => {
  for (const durableHandoff of [true, false]) {
    let effects = 0, posts = 0; const client = host({ onAction: () => effects++ });
    const recovery = { ...original, runId: action.runId, request: action, capability: { ...capability, durableHandoff } };
    const results = await client.recover({ hostId: 'host', sessionId: 'session', reconcile: () => structuredClone(admission),
      fetchImpl: async (_url, options) => {
        if (!options) return response([recovery]); posts++; assert.deepEqual(JSON.parse(options.body).result, admission);
        return response({ actionId: action.actionId, resolved: true });
      } });
    assert.equal(effects, 0); assert.equal(posts, Number(durableHandoff)); assert.equal(results[0].resolved, durableHandoff);
  }
});

test('parked notification preserves operation binding while the original execution window becomes terminal', async () => {
  const seen = []; const client = host({ onMessage: value => seen.push(value.type) });
  client.runs.set(action.runId, structuredClone(original)); const socket = await connect(client);
  socket.receive({ type: 'operation.parked', runId: action.runId, payload: entry() });
  socket.receive({ type: 'outcome', runId: action.runId, payload: { outcome: 'partiallyComplete' } });
  assert.deepEqual(seen, ['operation.parked', 'outcome']); assert.equal(client.operations.size, 1);
  assert.ok(client.stoppedRuns.has(action.runId)); assert.equal(socket.sent.length, 0); client.close();
});

test('decision wake binds the returned run before early action delivery and inherits original capability metadata', async () => {
  const client = host(); const socket = await connect(client);
  await client.listOperations({ hostId: 'host', sessionId: 'session', fetchImpl: async () => response([entry()]) });
  const wake = client.wakeOperation(event({ kind: 'decision' })); await turn(); const sent = socket.sent.at(-1);
  assert.equal(sent.type, 'operation.wake'); assert.equal(sent.payload.kind, 'decision'); assert.equal(sent.runId, undefined);
  socket.receive(started(sent));
  const nextAction = { ...action, runId: 'wake-run', actionId: 'next-action' };
  client.onAction = () => ({ ...admission, actionId: 'next-action', operation: { ...admission.operation, operationId: 'next-operation' } });
  socket.receive({ type: 'action.request', runId: 'wake-run', payload: nextAction }); await client.actionQueue;
  assert.equal((await wake).runId, 'wake-run'); assert.equal(client.runs.get('wake-run').objectiveId, 'objective');
  assert.deepEqual(client.runs.get('wake-run').capabilities, [capability]);
  assert.equal(socket.sent.at(-1).payload.disposition, 'applied'); client.close();
});

test('wake can bind a recovered service response without locally cached inventory and rejects foreign responses', async () => {
  for (const patch of [{}, { sessionEpoch: 'foreign' }, { hostId: 'foreign' }, { objectiveId: undefined }]) {
    const client = host(); const socket = await connect(client); const waking = client.wakeOperation(event({ kind: 'decision' })); await turn();
    socket.receive(started(socket.sent.at(-1), patch));
    if (Object.keys(patch).length) { await assert.rejects(waking); assert.equal(client.runs.size, 0); }
    else { assert.equal((await waking).runId, 'wake-run'); assert.equal(client.runs.get('wake-run').objectiveId, 'objective'); }
    client.close();
  }
});

test('wake interruption and disconnect do not automatically create another cognition window', async () => {
  const client = host(); const socket = await connect(client);
  const waking = client.wakeOperation(event({ kind: 'decision' })); await turn(); const sent = socket.sent.at(-1);
  socket.receive({ type: 'error', requestId: sent.requestId, payload: { code: 'operation.wake_interrupted', message: 'Claimed wake requires reconciliation.' } });
  await assert.rejects(waking, error => error.code === 'operation.wake_interrupted'); assert.equal(socket.sent.length, 1);
  const other = client.wakeOperation(event({ kind: 'decision', eventId: 'event-2', sequence: 2 })); await turn(); socket.close();
  await assert.rejects(other, /Disconnected/); assert.equal(socket.sent.length, 2); assert.equal(client.runs.size, 0);
});

test('a host-confirmed epoch transfer keeps original admission immutable and binds the wake to its new epoch', async () => {
  const client = host(); const socket = await connect(client); const transferred = { ...entry(), activeEpoch: 'epoch-2' };
  await client.listOperations({ hostId: 'host', sessionId: 'session', fetchImpl: async () => response([transferred]) });
  const waking = client.wakeOperation(event({ kind: 'decision', sessionEpoch: 'epoch-2', sequence: 2 })); await turn();
  const sent = socket.sent.at(-1); assert.equal(sent.payload.sessionEpoch, 'epoch-2');
  socket.receive(started(sent, { sessionEpoch: 'epoch-2' })); await waking;
  assert.equal(client.runs.get('wake-run').sessionEpoch, 'epoch-2');
  assert.equal([...client.operations.values()][0].admission.sessionEpoch, 'epoch-1'); client.close();
});

test('nondecision wakes are rejected before any WebSocket connection', async () => {
  const client = host(); const before = Socket.instances.length;
  await assert.rejects(client.wakeOperation(event()), /Only a decision/); assert.equal(Socket.instances.length, before);
});
