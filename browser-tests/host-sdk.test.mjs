import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgenticaHost, actionKey, actionFingerprint } from '../Agentica.Lab.Web/wwwroot/sdk/agentica-host.mjs';

class Socket {
  static instances = [];
  constructor(url) { this.url = url; this.readyState = 0; this.listeners = new Map(); this.sent = []; Socket.instances.push(this); }
  addEventListener(type, handler) { const handlers = this.listeners.get(type) ?? []; handlers.push(handler); this.listeners.set(type, handlers); }
  emit(type, payload = {}) { for (const handler of this.listeners.get(type) ?? []) handler(payload); }
  open() { this.readyState = 1; this.emit('open'); }
  send(text) { assert.equal(this.readyState, 1); this.sent.push(JSON.parse(text)); }
  receive(message) { this.emit('message', { data: JSON.stringify({ protocolVersion: 1, ...message }) }); }
  close() { this.readyState = 3; this.emit('close'); }
}
const binding = { runId: 'run-1', sessionId: 'session-1', sessionEpoch: 'epoch-1' };
const actionDeadline = new Date(Date.now() + 60000).toISOString();
const action = changes => ({ ...binding, actionId: 'action-1', runnerRunId: 'runner-1', stepId: 'step-1', capabilityId: 'host.accept', manifestHash: 'manifest-1', arguments: { itemId: 'item-1' }, expectedRevision: 2, deadlineAt: actionDeadline, ...changes });
const applied = request => ({ actionId: request.actionId, sessionId: request.sessionId, sessionEpoch: request.sessionEpoch, disposition: 'applied', beforeRevision: 2, afterRevision: 3, evidenceId: 'receipt-1', summary: 'Applied once.', observation: { observationId: 'obs-3', revision: 3, observedAt: new Date().toISOString(), data: { accepted: true } } });
async function setup(options = {}) {
  const host = new AgenticaHost({ url: 'ws://localhost/api/host', WebSocketImpl: Socket, onAction: async request => applied(request), ...options });
  const connection = host.connect(); const socket = Socket.instances.at(-1); socket.open(); await connection;
  host.runs.set(binding.runId, binding);
  return { host, socket };
}
async function deliver(host, socket, type = 'action.request', request = action()) { socket.receive({ type, runId: request.runId, payload: request }); await host.actionQueue; return socket.sent.at(-1)?.payload; }

test('identical repeated action invokes the host once and replays the exact result', async () => {
  let calls = 0; const { host, socket } = await setup({ onAction: request => { calls++; return applied(request); } });
  const first = await deliver(host, socket); const second = await deliver(host, socket);
  assert.equal(calls, 1); assert.deepEqual(first, second); host.close();
});

test('argument key order is stable while changed arguments or deadline under the same action ID are refused', async () => {
  assert.equal(actionFingerprint(action({ arguments: { a: 1, b: 2 } })), actionFingerprint(action({ arguments: { b: 2, a: 1 } })));
  const statuses = []; let calls = 0;
  const { host, socket } = await setup({ onAction: request => { calls++; return applied(request); }, onStatus: status => statuses.push(status) });
  await deliver(host, socket); const second = await deliver(host, socket, 'action.request', action({ arguments: { itemId: 'item-2' } }));
  assert.equal(second.disposition, 'unresolved'); assert.equal(statuses.at(-1).state, 'action.error'); assert.match(statuses.at(-1).detail, /changed/);
  const changedDeadline = await deliver(host, socket, 'action.request', action({ deadlineAt: new Date(Date.parse(actionDeadline) + 1000).toISOString() }));
  assert.equal(changedDeadline.disposition, 'unresolved'); assert.equal(statuses.at(-1).state, 'action.error'); assert.match(statuses.at(-1).detail, /changed/);
  assert.equal(calls, 1); host.close();
});

test('action reservation is persisted before calling a host effect', async () => {
  const events = []; const { host, socket } = await setup({ loadAction: () => null,
    saveAction: (_, record) => events.push(record.status), onAction: request => { events.push('effect'); return applied(request); } });
  await deliver(host, socket); assert.deepEqual(events, ['pending', 'effect', 'completed']); host.close();
});

test('failed reservation storage prevents execution and returns unresolved', async () => {
  let calls = 0; const { host, socket } = await setup({ loadAction: () => null, saveAction: () => { throw new Error('storage unavailable'); }, onAction: request => { calls++; return applied(request); } });
  const result = await deliver(host, socket); assert.equal(calls, 0); assert.equal(result.disposition, 'unresolved');
  await deliver(host, socket); assert.equal(calls, 0); host.close();
});

test('deadline expiry during reservation prevents execution and retains the original attempt', async t => {
  let now = Date.now(); const deadline = now + 1000;
  t.mock.method(Date, 'now', () => now);
  const request = action({ deadlineAt: new Date(deadline).toISOString() });
  const records = new Map(), saves = []; let calls = 0;
  const { host, socket } = await setup({ loadAction: key => records.get(key),
    saveAction: (key, record) => { records.set(key, record); saves.push(record.status); now = deadline; },
    onAction: value => { calls++; return applied(value); } });
  const result = await deliver(host, socket, 'action.request', request);
  assert.equal(result.disposition, 'unresolved'); assert.match(result.summary, /deadline expired.*No effect was attempted/);
  assert.equal(calls, 0); assert.deepEqual(saves, ['pending']);
  assert.deepEqual(records.get(actionKey(request)), { fingerprint: actionFingerprint(request), status: 'pending' });
  now = deadline - 1000;
  assert.equal((await deliver(host, socket, 'action.request', request)).disposition, 'unresolved');
  assert.equal((await deliver(host, socket, 'action.reconcile', request)).disposition, 'unresolved');
  assert.equal(calls, 0); assert.deepEqual(saves, ['pending']); host.close();
});

test('cancellation during reservation reports no effect and preserves pending custody', async () => {
  const records = new Map(); let calls = 0;
  const { host, socket } = await setup({ loadAction: key => records.get(key),
    saveAction: (key, record) => { records.set(key, record); host.stoppedRuns.add(binding.runId); },
    onAction: request => { calls++; return applied(request); } });
  const result = await deliver(host, socket);
  assert.equal(result.disposition, 'unresolved'); assert.match(result.summary, /Run stopped.*No effect was attempted/);
  assert.equal(calls, 0); assert.equal(records.get(actionKey(action())).status, 'pending');
  assert.equal((await deliver(host, socket, 'action.reconcile')).disposition, 'unresolved');
  assert.equal(calls, 0); host.close();
});

for (const failingHook of ['loadAction', 'saveAction', 'onAction']) test(`${failingHook} exception details stay local and never enter action result messages`, async () => {
  const secret = `private-${failingHook}-credential`; const records = new Map(), statuses = []; let calls = 0;
  const { host, socket } = await setup({
    loadAction: key => { if (failingHook === 'loadAction') throw new Error(secret); return records.get(key); },
    saveAction: (key, record) => { if (failingHook === 'saveAction') throw new Error(secret); records.set(key, record); },
    onAction: request => { calls++; if (failingHook === 'onAction') throw new Error(secret); return applied(request); },
    onStatus: status => statuses.push(status) });
  const result = await deliver(host, socket);
  assert.equal(result.disposition, 'unresolved'); assert.match(result.summary, /Reconcile the original action/);
  assert.ok(statuses.some(status => status.state === 'action.error' && status.detail === secret));
  assert.equal((await deliver(host, socket)).disposition, 'unresolved');
  assert.equal(calls, failingHook === 'onAction' ? 1 : 0);
  assert.ok(socket.sent.length >= 2); assert.ok(socket.sent.every(message => message.type === 'action.result'));
  assert.ok(!JSON.stringify(socket.sent).includes(secret)); host.close();
});

test('reconcile missing or pending retained result never re-executes', async () => {
  for (const retained of [null, { fingerprint: actionFingerprint(action()), status: 'pending' }]) {
    let calls = 0; const { host, socket } = await setup({ loadAction: () => retained, saveAction: () => {}, onAction: request => { calls++; return applied(request); } });
    const result = await deliver(host, socket, 'action.reconcile'); assert.equal(calls, 0); assert.equal(result.disposition, 'unresolved'); host.close();
  }
});

test('reconcile after a new client instance loads and returns the committed result', async () => {
  const records = new Map(); const options = { loadAction: key => records.get(key), saveAction: (key, record) => records.set(key, record) };
  const first = await setup(options); const result = await deliver(first.host, first.socket); first.host.close();
  let calls = 0; const second = await setup({ ...options, onAction: () => { calls++; throw new Error('Must not run.'); } });
  assert.deepEqual(await deliver(second.host, second.socket, 'action.reconcile'), result); assert.equal(calls, 0); second.host.close();
});

test('effect failure leaves a reservation and cannot cause a duplicate invocation', async () => {
  let calls = 0; const { host, socket } = await setup({ onAction: () => { calls++; throw new Error('Unknown after effect dispatch.'); } });
  assert.equal((await deliver(host, socket)).disposition, 'unresolved'); assert.equal((await deliver(host, socket)).disposition, 'unresolved'); assert.equal(calls, 1); host.close();
});

test('a disconnected effect result is retained and returned on reconciliation', async () => {
  let finish; const { host, socket } = await setup({ onAction: request => new Promise(resolve => { finish = () => resolve(applied(request)); }) });
  socket.receive({ type: 'action.request', runId: binding.runId, payload: action() });
  await new Promise(resolve => setImmediate(resolve)); socket.close(); finish(); await host.actionQueue;
  assert.equal(socket.sent.length, 0);
  const connecting = host.connect(); const resumed = Socket.instances.at(-1); resumed.open(); await connecting;
  assert.equal((await deliver(host, resumed, 'action.reconcile')).disposition, 'applied'); host.close();
});

test('different session epoch, mismatched envelope, and unknown run do not invoke effects', async () => {
  let calls = 0; const { host, socket } = await setup({ onAction: request => { calls++; return applied(request); } });
  await deliver(host, socket, 'action.request', action({ sessionEpoch: 'other' }));
  await deliver(host, socket, 'action.request', action({ runId: 'unknown' }));
  socket.receive({ type: 'action.request', runId: 'other', payload: action() }); await host.actionQueue;
  assert.equal(calls, 0); assert.equal(socket.sent.length, 0); host.close();
});

test('expired deadline, invalid result identity, and regressing revision never become applied', async () => {
  const first = await setup(); assert.equal((await deliver(first.host, first.socket, 'action.request', action({ deadlineAt: '2000-01-01T00:00:00Z' }))).disposition, 'unresolved'); first.host.close();
  for (const patch of [{ sessionEpoch: 'other' }, { afterRevision: 1 }, { observation: { revision: 99 } }]) {
    const fixture = await setup({ onAction: request => ({ ...applied(request), ...patch }) });
    assert.equal((await deliver(fixture.host, fixture.socket)).disposition, 'unresolved'); fixture.host.close();
  }
});

test('retained display history and action registry are bounded without evicting effect deduplication', async () => {
  const { host, socket } = await setup({ maxMessages: 2, maxActions: 1 });
  for (let index = 0; index < 4; index++) socket.receive({ type: 'progress', payload: { sequence: index } });
  assert.equal(host.messages.length, 2); assert.equal(host.droppedMessages, 2);
  await deliver(host, socket);
  assert.equal((await deliver(host, socket, 'action.request', action({ actionId: 'action-2' }))).disposition, 'unresolved');
  assert.equal((await deliver(host, socket)).disposition, 'applied'); assert.equal(host.actions.size, 1); host.close();
});

test('connect is shared and start registers identity before immediate service action', async () => {
  let calls = 0; const host = new AgenticaHost({ url: 'ws://localhost/api/host', WebSocketImpl: Socket, onAction: request => { calls++; return applied(request); } });
  const before = Socket.instances.length; const one = host.connect(); const two = host.connect(); assert.equal(one, two); assert.equal(Socket.instances.length, before + 1);
  const socket = Socket.instances.at(-1); socket.open(); await one;
  const starting = host.start({ ...binding, hostId: 'host-1', scopeId: 'scope-1', perspectiveId: 'view-1', objectiveId: 'objective-1', objective: 'Test' });
  await new Promise(resolve => setImmediate(resolve)); const request = socket.sent.at(-1);
  socket.receive({ type: 'started', requestId: request.requestId, runId: binding.runId, payload: { runId: binding.runId } });
  socket.receive({ type: 'action.request', runId: binding.runId, payload: action() });
  await starting; await host.actionQueue; assert.equal(calls, 1); host.close();
});

test('control request disconnect rejects without resending the control request', async () => {
  const { host, socket } = await setup(); const pending = host.stop(binding.runId);
  await new Promise(resolve => setImmediate(resolve)); socket.close();
  await assert.rejects(pending, /unresolved/); assert.equal(socket.sent.filter(item => item.type === 'cancel').length, 1);
});

test('malformed and oversized inbound envelopes are rejected before effects', async () => {
  const statuses = []; const { host, socket } = await setup({ onStatus: status => statuses.push(status), maxMessageBytes: 1024 });
  socket.emit('message', { data: '{bad' }); socket.receive({ protocolVersion: 999, type: 'progress', payload: {} }); socket.emit('message', { data: 'x'.repeat(1025) });
  assert.equal(host.messages.length, 0); assert.equal(statuses.filter(status => status.state === 'protocol.error').length, 3); host.close();
});

test('observer exceptions do not interrupt action processing', async () => {
  const { host, socket } = await setup({ onMessage: () => { throw new Error('broken display'); }, onStatus: () => { throw new Error('broken display'); } });
  assert.equal((await deliver(host, socket)).disposition, 'applied'); host.close();
});

test('action keys separate run, epoch and session identities', () => {
  const original = actionKey(action());
  for (const field of ['runId', 'sessionId', 'sessionEpoch', 'actionId']) assert.notEqual(actionKey(action({ [field]: 'different' })), original);
});

test('reliable exceptional termination stops new effects while retaining exact reconciliation', async () => {
  let calls = 0; const seen = [];
  const { host, socket } = await setup({ onAction: request => { calls++; return applied(request); }, onMessage: message => seen.push(message.type) });
  const result = await deliver(host, socket);
  socket.receive({ type: 'run.terminated', runId: binding.runId, payload: { status: 'failed', code: 'run.failed', message: 'Provider setup failed.', snapshotUrl: '/api/runs/run-1' } });
  assert.ok(seen.includes('run.terminated'));
  assert.equal((await deliver(host, socket, 'action.request', action({ actionId: 'new-action' }))).disposition, 'unresolved');
  assert.deepEqual(await deliver(host, socket, 'action.reconcile'), result);
  assert.equal(calls, 1); host.close();
});

test('same client refreshes a pending reservation after the host commits a late durable result', async () => {
  const store = new Map(); let calls = 0;
  const { host, socket } = await setup({ loadAction: key => store.get(key), saveAction: (key, record) => store.set(key, record),
    onAction: () => { calls++; throw new Error('Host dispatch was interrupted.'); } });
  assert.equal((await deliver(host, socket)).disposition, 'unresolved');
  const request = action(); const result = applied(request);
  store.set(actionKey(request), { fingerprint: actionFingerprint(request), status: 'completed', result });
  assert.deepEqual(await deliver(host, socket, 'action.reconcile'), result);
  assert.equal(calls, 1); assert.equal(host.actions.get(actionKey(request)).status, 'completed'); host.close();
});

test('an unresolved host response remains uncertain and explicit reconciliation persists before replying', async () => {
  const store = new Map(); let calls = 0; let inspections = 0; let persistedBeforeReply = false;
  const { host, socket } = await setup({ loadAction: key => store.get(key), saveAction: (key, record) => store.set(key, record),
    onAction: request => { calls++; return { ...applied(request), disposition: 'unresolved', summary: 'Awaiting original effect confirmation.' }; },
    reconcileAction: (request, retained) => { inspections++; assert.equal(retained.status, 'unresolved'); return applied(request); } });
  await deliver(host, socket);
  assert.equal(host.actions.get(actionKey(action())).status, 'unresolved');
  assert.equal(store.get(actionKey(action())).status, 'pending');
  const originalSend = socket.send.bind(socket);
  socket.send = text => { const message = JSON.parse(text); if (message.payload.disposition === 'applied') persistedBeforeReply = store.get(actionKey(action())).status === 'completed'; originalSend(text); };
  assert.equal((await deliver(host, socket, 'action.reconcile')).disposition, 'applied');
  assert.equal(persistedBeforeReply, true); assert.equal(calls, 1); assert.equal(inspections, 1); host.close();
});

test('changed durable custody is rejected before state inspection or another effect', async () => {
  const store = new Map(), statuses = []; let calls = 0; let inspections = 0;
  const { host, socket } = await setup({ loadAction: key => store.get(key), saveAction: (key, record) => store.set(key, record),
    onAction: () => { calls++; throw new Error('Unknown original outcome.'); }, reconcileAction: () => { inspections++; return applied(action()); },
    onStatus: status => statuses.push(status) });
  await deliver(host, socket);
  store.set(actionKey(action()), { fingerprint: 'changed-binding', status: 'completed', result: applied(action()) });
  const reply = await deliver(host, socket, 'action.reconcile');
  assert.equal(reply.disposition, 'unresolved'); assert.equal(statuses.at(-1).state, 'action.error'); assert.match(statuses.at(-1).detail, /changed/);
  assert.equal(calls, 1); assert.equal(inspections, 0);
  assert.equal(host.actions.get(actionKey(action())).status, 'pending'); host.close();
});

test('a known completed result stays immutable if durable storage later contains another result', async () => {
  const store = new Map(); let calls = 0;
  const { host, socket } = await setup({ loadAction: key => store.get(key), saveAction: (key, record) => store.set(key, record), onAction: request => { calls++; return applied(request); } });
  const original = await deliver(host, socket); const key = actionKey(action());
  store.set(key, { ...store.get(key), result: { ...original, summary: 'Changed evidence.' } });
  assert.equal((await deliver(host, socket, 'action.reconcile')).disposition, 'unresolved');
  assert.deepEqual(host.actions.get(key).result, original); assert.equal(calls, 1); host.close();
});

test('failed concrete reconciliation persistence stays unresolved and retry preserves known evidence', async () => {
  const store = new Map(); let calls = 0; let inspections = 0; let failSave = true;
  const { host, socket } = await setup({ loadAction: key => store.get(key), saveAction: (key, record) => {
    if (record.status === 'completed' && failSave) throw new Error('Durable result write failed.'); store.set(key, record);
  }, onAction: () => { calls++; throw new Error('Unknown original outcome.'); }, reconcileAction: request => { inspections++; return applied(request); } });
  await deliver(host, socket);
  assert.equal((await deliver(host, socket, 'action.reconcile')).disposition, 'unresolved');
  failSave = false;
  assert.equal((await deliver(host, socket, 'action.reconcile')).disposition, 'applied');
  assert.equal(calls, 1); assert.equal(inspections, 1); host.close();
});
