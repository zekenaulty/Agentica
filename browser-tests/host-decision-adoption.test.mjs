import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgenticaHost } from '../Agentica.Lab.Web/wwwroot/sdk/agentica-host.mjs';

class Socket {
  static instances = [];
  constructor() { this.readyState = 0; this.handlers = new Map(); this.sent = []; Socket.instances.push(this); }
  addEventListener(type, handler) { this.handlers.set(type, [...(this.handlers.get(type) ?? []), handler]); }
  emit(type, payload = {}) { for (const handler of this.handlers.get(type) ?? []) handler(payload); }
  open() { this.readyState = 1; this.emit('open'); }
  send(raw) { this.sent.push(JSON.parse(raw)); }
  receive(message) { this.emit('message', { data: JSON.stringify({ protocolVersion: 1, ...message }) }); }
  close() { this.readyState = 3; this.emit('close'); }
}

const observation = revision => ({ observationId: `observation-${revision}`, revision,
  observedAt: '2026-10-12T12:00:00Z', data: { revision, decisionRequired: true } });
const capability = { id: 'host.admit', effect: 'writesLocalState', durableHandoff: true };
const original = { protocolVersion: 1, hostId: 'host', sessionId: 'session', sessionEpoch: 'old-owner',
  scopeId: 'scope', perspectiveId: 'actor', objectiveId: 'objective', objective: 'Complete host work.',
  observation: observation(0), capabilities: [capability], provider: { provider: 'fixture', model: 'offline-model' },
  limits: { maxSteps: 4, maxRefinements: 4, maxPlanContinuations: 4 } };
const action = { actionId: 'original-action', runId: 'original-run', runnerRunId: 'runner', stepId: 'step',
  sessionId: 'session', sessionEpoch: 'old-owner', capabilityId: capability.id, manifestHash: 'manifest',
  arguments: {}, expectedRevision: 0, deadlineAt: '2099-10-12T12:00:00Z' };
const admission = { actionId: action.actionId, sessionId: action.sessionId, sessionEpoch: action.sessionEpoch,
  disposition: 'applied', beforeRevision: 0, afterRevision: 1, evidenceId: 'original-receipt', summary: 'Host work admitted.',
  observation: observation(1), operation: { operationId: 'original-operation', summary: 'Host owns execution.', usage: { ticks: 0, limit: null } } };
const entry = () => ({ request: structuredClone(original), action: structuredClone(action), admission: structuredClone(admission),
  usage: { steps: 1, refinements: 0, continuations: 0, providerCalls: 1 }, activeEpoch: 'old-owner', state: 'parked',
  latestEvent: null, wakeRunId: null, createdAt: '2026-10-12T12:00:00Z', updatedAt: '2026-10-12T12:00:01Z' });
const decision = (patch = {}) => ({ hostId: 'host', sessionId: 'session', sessionEpoch: 'old-owner',
  actionId: action.actionId, operationId: 'original-operation', sequence: 1, eventId: 'original-decision', kind: 'decision',
  observation: observation(2), summary: 'The original owner requires a decision.', usage: { ticks: 100, limit: null },
  completion: null, nextSessionEpoch: null, ...patch });
const adoption = (patch = {}) => ({ adoptionId: 'original-adoption', nextSessionEpoch: 'new-owner', observation: observation(5), ...patch });
const response = body => ({ ok: true, status: 200, text: async () => JSON.stringify(body) });
const host = (options = {}) => new AgenticaHost({ url: 'ws://localhost:5078/api/host', WebSocketImpl: Socket,
  requestTimeoutMs: 1000, onAction: () => assert.fail('Unexpected host action.'), ...options });
async function connect(client) { const pending = client.connect(); const socket = Socket.instances.at(-1); socket.open(); await pending; return socket; }
async function turn() { await new Promise(resolve => setImmediate(resolve)); }
async function inventory(client, retained = entry()) {
  return client.listOperations({ hostId: 'host', sessionId: 'session', fetchImpl: async () => response([retained]) });
}
function started(request, patch = {}) {
  return { type: 'started', requestId: request.requestId, runId: 'adopted-run', payload: {
    runId: 'adopted-run', hostId: 'host', sessionId: 'session', sessionEpoch: 'new-owner', objectiveId: 'objective', ...patch } };
}

test('adoptDecision sends the exact old decision plus explicit adoption, preserving cached admission and early-action binding', async t => {
  let effects = 0;
  const client = host({ onAction: next => {
    effects++;
    assert.equal(next.sessionEpoch, 'new-owner');
    assert.equal(next.expectedRevision, 5);
    return { actionId: next.actionId, sessionId: next.sessionId, sessionEpoch: next.sessionEpoch,
      disposition: 'applied', beforeRevision: 5, afterRevision: 6, evidenceId: 'child-admission', summary: 'Child work admitted.',
      observation: observation(6), operation: { operationId: 'child-operation', summary: 'Host owns child work.' } };
  } });
  t.after(() => client.close());
  assert.equal(typeof client.adoptDecision, 'function');
  const retained = entry();
  await inventory(client, retained);
  const socket = await connect(client);
  const source = decision(), next = adoption();
  const originalSource = structuredClone(source), originalNext = structuredClone(next);
  const pending = client.adoptDecision(source, next);
  await turn();
  const sent = socket.sent.at(-1);
  assert.equal(sent.type, 'operation.adopt');
  assert.equal(sent.runId, undefined);
  assert.deepEqual(sent.payload, { decision: originalSource, adoption: originalNext });
  assert.equal(socket.sent.length, 1);
  socket.receive(started(sent));
  socket.receive({ type: 'action.request', runId: 'adopted-run', payload: {
    ...action, actionId: 'child-action', runId: 'adopted-run', sessionEpoch: 'new-owner', expectedRevision: 5 } });
  await client.actionQueue;
  assert.equal((await pending).runId, 'adopted-run');
  assert.equal(client.runs.get('adopted-run').sessionEpoch, 'new-owner');
  assert.equal(client.runs.get('adopted-run').objectiveId, 'objective');
  assert.deepEqual(client.runs.get('adopted-run').capabilities, [capability]);
  assert.equal(effects, 1);
  assert.equal(socket.sent.at(-1).type, 'action.result');
  assert.equal(socket.sent.at(-1).payload.disposition, 'applied');
  assert.deepEqual([...client.operations.values()], [retained]);
  assert.deepEqual(source, originalSource);
  assert.deepEqual(next, originalNext);
});

for (const cached of [false, true]) {
  test(`adoptDecision validates the new-epoch started response with cached inventory ${cached}`, async t => {
    for (const patch of [{}, { sessionEpoch: 'old-owner' }, { sessionEpoch: 'foreign' }, { hostId: 'foreign' },
      { sessionId: 'foreign' }, { objectiveId: undefined }, ...(cached ? [{ objectiveId: 'foreign' }] : [])]) {
      const client = host();
      t.after(() => client.close());
      assert.equal(typeof client.adoptDecision, 'function');
      if (cached) await inventory(client);
      const socket = await connect(client);
      const pending = client.adoptDecision(decision(), adoption());
      const rejected = Object.keys(patch).length ? assert.rejects(pending) : null;
      await turn();
      socket.receive(started(socket.sent.at(-1), patch));
      if (rejected) { await rejected; assert.equal(client.runs.size, 0); }
      else { assert.equal((await pending).runId, 'adopted-run'); assert.equal(client.runs.get('adopted-run').sessionEpoch, 'new-owner'); }
      assert.equal(socket.sent.length, 1);
      client.close();
    }
  });
}

test('adoptDecision rejects a non-start response without retaining a run', async t => {
  const client = host();
  t.after(() => client.close());
  assert.equal(typeof client.adoptDecision, 'function');
  const socket = await connect(client);
  const pending = client.adoptDecision(decision(), adoption());
  const rejected = assert.rejects(pending);
  await turn();
  socket.receive({ ...started(socket.sent.at(-1)), type: 'resumed' });
  await rejected;
  assert.equal(client.runs.size, 0);
});

test('malformed adoption and rewritten or nondecision events fail before opening a socket', async t => {
  const client = host();
  t.after(() => client.close());
  assert.equal(typeof client.adoptDecision, 'function');
  const connections = Socket.instances.length;
  for (const invalid of [null, {}, adoption({ adoptionId: '' }), adoption({ adoptionId: 'bad\nidentity' }),
    adoption({ nextSessionEpoch: 'old-owner' }), adoption({ nextSessionEpoch: '' }), adoption({ nextSessionEpoch: 'bad\nowner' }),
    adoption({ observation: undefined }), adoption({ observation: observation(1) }),
    adoption({ observation: { ...observation(2), data: { changed: true } } }),
    adoption({ observation: { ...observation(5), revision: -1 } }), adoption({ unexpected: true })]) {
    await assert.rejects(client.adoptDecision(decision(), invalid));
  }
  for (const patch of [{ kind: 'progress' }, { kind: 'cancelled' }, { kind: 'blocked' },
    { kind: 'transfer', nextSessionEpoch: 'third-owner' }, { nextSessionEpoch: 'new-owner' },
    { completion: { objectiveId: 'objective', evidenceId: 'proof', summary: 'Not completion.' } },
    { sequence: 0 }, { eventId: '' }, { usage: [] }, { unexpected: true }]) {
    await assert.rejects(client.adoptDecision(decision(patch), adoption()));
  }
  assert.equal(Socket.instances.length, connections);
  assert.equal(client.runs.size, 0);
});

test('explicit duplicate adoption retries retain identical payloads across SDK reload without synthetic transfer events', async t => {
  const source = decision(), next = adoption();
  const before = structuredClone({ decision: source, adoption: next });
  const payloads = [];
  for (let reload = 0; reload < 2; reload++) {
    const client = host();
    t.after(() => client.close());
    assert.equal(typeof client.adoptDecision, 'function');
    const retained = { ...entry(), state: 'waking', activeEpoch: next.nextSessionEpoch, wakeRunId: 'adopted-run',
      latestEvent: structuredClone(source), wakeAdoption: structuredClone(next) };
    await inventory(client, retained);
    const socket = await connect(client);
    for (let duplicate = 0; duplicate < 2; duplicate++) {
      const pending = client.adoptDecision(structuredClone(source), structuredClone(next));
      await turn();
      const sent = socket.sent.at(-1);
      assert.equal(sent.type, 'operation.adopt');
      payloads.push(sent.payload);
      socket.receive(started(sent));
      await pending;
    }
    assert.deepEqual([...client.operations.values()], [retained]);
    assert.equal(socket.sent.length, 2);
    client.close();
  }
  assert.equal(payloads.length, 4);
  assert.ok(payloads.every(payload => JSON.stringify(payload) === JSON.stringify(before)));
  assert.deepEqual({ decision: source, adoption: next }, before);
});

test('lost-process refusal and uncertain disconnect never retry adoption or fall back to ordinary wake', async t => {
  const client = host();
  t.after(() => client.close());
  assert.equal(typeof client.adoptDecision, 'function');
  const socket = await connect(client);
  const source = decision(), next = adoption();
  const before = structuredClone({ decision: source, adoption: next });
  const pending = client.adoptDecision(source, next);
  const refused = assert.rejects(pending, error => error.code === 'operation.wake_interrupted');
  await turn();
  socket.receive({ type: 'error', requestId: socket.sent.at(-1).requestId,
    payload: { code: 'operation.wake_interrupted', message: 'Original claim requires reconciliation.' } });
  await refused;
  assert.equal(socket.sent.length, 1);
  const retry = client.adoptDecision(source, next);
  const disconnected = assert.rejects(retry, /Disconnected/);
  await turn();
  socket.close();
  await disconnected;
  await turn();
  assert.deepEqual(socket.sent.map(message => message.type), ['operation.adopt', 'operation.adopt']);
  assert.deepEqual(socket.sent.map(message => message.payload), [before, before]);
  assert.equal(client.runs.size, 0);
  assert.deepEqual({ decision: source, adoption: next }, before);
});

test('ordinary wakeOperation still sends the bare old decision and binds its original epoch', async t => {
  const client = host();
  t.after(() => client.close());
  const socket = await connect(client);
  const source = decision();
  const pending = client.wakeOperation(source);
  await turn();
  const sent = socket.sent.at(-1);
  assert.equal(sent.type, 'operation.wake');
  assert.deepEqual(sent.payload, source);
  assert.equal(sent.payload.adoption, undefined);
  socket.receive(started(sent, { sessionEpoch: source.sessionEpoch }));
  assert.equal((await pending).runId, 'adopted-run');
  assert.equal(client.runs.get('adopted-run').sessionEpoch, source.sessionEpoch);
  assert.equal(socket.sent.length, 1);
});
