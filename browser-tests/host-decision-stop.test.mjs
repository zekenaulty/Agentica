import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgenticaHost } from '../Agentica.Lab.Web/wwwroot/sdk/agentica-host.mjs';

class ForbiddenSocket {
  static attempts = 0;
  constructor() {
    ForbiddenSocket.attempts++;
    throw new Error('Stopping a decision must not connect or activate cognition.');
  }
}

const observation = revision => ({ observationId: `observation-${revision}`, revision,
  observedAt: '2026-10-12T12:00:00Z', data: { revision, decisionRequired: true } });
const decision = (patch = {}) => ({ hostId: 'host', sessionId: 'session', sessionEpoch: 'epoch',
  actionId: 'original-action', operationId: 'original-operation', sequence: 2, eventId: 'original-decision',
  kind: 'decision', observation: observation(3), summary: 'The host requires a decision.',
  usage: { ticks: 200, limit: null }, completion: null, nextSessionEpoch: null, ...patch });
const entry = (source, wakeRunId) => ({
  request: { protocolVersion: 1, hostId: 'host', sessionId: 'session', sessionEpoch: 'epoch',
    scopeId: 'scope', perspectiveId: 'actor', objectiveId: 'objective', objective: 'Complete host work.',
    observation: observation(0), capabilities: [{ id: 'host.admit', effect: 'writesLocalState', durableHandoff: true }] },
  action: { actionId: 'original-action', runId: 'original-run', runnerRunId: 'runner', stepId: 'step',
    sessionId: 'session', sessionEpoch: 'epoch', capabilityId: 'host.admit', manifestHash: 'manifest',
    arguments: {}, expectedRevision: 0, deadlineAt: '2099-10-12T12:00:00Z' },
  admission: { actionId: 'original-action', sessionId: 'session', sessionEpoch: 'epoch', disposition: 'applied',
    beforeRevision: 0, afterRevision: 1, evidenceId: 'original-admission', summary: 'Host work admitted.',
    observation: observation(1), operation: { operationId: 'original-operation', summary: 'Host owns execution.' } },
  usage: { steps: 1, refinements: 0, continuations: 0, providerCalls: 1 }, activeEpoch: 'epoch',
  state: 'cancelled', wakeRunId, latestEvent: structuredClone(source), stopDecision: structuredClone(source),
  createdAt: '2026-10-12T12:00:00Z', updatedAt: '2026-10-12T12:00:01Z',
});
const response = (body, status = 200) => ({ ok: status >= 200 && status < 300, status,
  text: async () => JSON.stringify(body) });
const host = (url = 'ws://localhost:5078/api/host') => {
  const client = new AgenticaHost({ url, WebSocketImpl: ForbiddenSocket,
    onAction: () => { assert.fail('Decision Stop must not execute a host action.'); } });
  assert.equal(typeof client.stopDecision, 'function', 'AgenticaHost.stopDecision must implement the proposed API.');
  return client;
};

for (const wakeRunId of [null, 'already-claimed-wake']) {
  test(`stopDecision posts the exact original decision and retains wake identity ${wakeRunId}`, async () => {
    const client = host();
    const source = decision();
    const original = structuredClone(source);
    const stopped = entry(source, wakeRunId);
    const calls = [];
    const connections = ForbiddenSocket.attempts;
    const result = await client.stopDecision(source, { fetchImpl: async (url, options) => {
      calls.push({ url, options });
      return response(stopped);
    } });

    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, 'http://localhost:5078/api/operations/stop-decision');
    assert.equal(calls[0].options.method, 'POST');
    assert.equal(calls[0].options.headers['Content-Type'], 'application/json');
    assert.deepEqual(JSON.parse(calls[0].options.body), original);
    assert.deepEqual(source, original);
    assert.deepEqual(result, stopped);
    assert.equal(result.latestEvent.kind, 'decision');
    assert.equal(result.latestEvent.sequence, original.sequence);
    assert.equal(result.wakeRunId, wakeRunId);
    assert.equal(client.runs.size, 0);
    assert.equal(ForbiddenSocket.attempts, connections);
    client.close();
  });
}

test('an explicit Stop retry after SDK reload uses the same decision, never a synthetic cancellation event', async () => {
  const source = decision();
  const stopped = entry(source, 'original-wake');
  const bodies = [];
  const fetchImpl = async (_url, options) => {
    bodies.push(options.body);
    return response(stopped);
  };
  for (let reload = 0; reload < 2; reload++) {
    const client = host();
    assert.deepEqual(await client.stopDecision(structuredClone(source), { fetchImpl }), stopped);
    assert.deepEqual(await client.stopDecision(structuredClone(source), { fetchImpl }), stopped);
    assert.equal(client.runs.size, 0);
    client.close();
  }
  assert.equal(bodies.length, 4);
  assert.equal(new Set(bodies).size, 1);
  assert.deepEqual(JSON.parse(bodies[0]), source);
});

test('Stop uses HTTPS for a secure host endpoint without forwarding its WebSocket query or fragment', async () => {
  const client = host('wss://host.invalid:7443/api/host?stale=1#socket');
  const source = decision();
  let calls = 0;
  await client.stopDecision(source, { fetchImpl: async (url, options) => {
    calls++;
    assert.equal(url, 'https://host.invalid:7443/api/operations/stop-decision');
    assert.deepEqual(JSON.parse(options.body), source);
    return response(entry(source, null));
  } });
  assert.equal(calls, 1);
  client.close();
});

test('nondecision and malformed Stop requests are refused before HTTP or WebSocket activity', async () => {
  const client = host();
  const connections = ForbiddenSocket.attempts;
  let calls = 0;
  const fetchImpl = async () => { calls++; return response({}); };
  for (const patch of [
    { kind: 'progress' }, { kind: 'cancelled' }, { kind: 'blocked' },
    { kind: 'transfer', nextSessionEpoch: 'next-epoch' },
    { kind: 'completed', completion: { objectiveId: 'objective', evidenceId: 'proof', summary: 'Completed.' } },
    { sequence: 0 }, { sequence: 1.5 }, { eventId: '' }, { operationId: 'wrong\nidentity' },
    { nextSessionEpoch: 'next-epoch' },
    { completion: { objectiveId: 'objective', evidenceId: 'proof', summary: 'Not completion.' } },
    { observation: { ...observation(3), revision: -1 } }, { usage: [] },
    { usage: { oversized: 'x'.repeat(8192) } }, { unexpected: true },
  ]) {
    await assert.rejects(client.stopDecision(decision(patch), { fetchImpl }));
  }
  assert.equal(calls, 0);
  assert.equal(ForbiddenSocket.attempts, connections);
  assert.equal(client.runs.size, 0);
  client.close();
});

test('Stop preserves service refusal codes and never retries an uncertain delivery automatically', async () => {
  const client = host();
  const source = decision();
  const original = structuredClone(source);
  let calls = 0;
  for (const code of ['operation.event_changed', 'operation.admission_unsettled', 'session.busy', 'session.unresolved']) {
    const before = calls;
    await assert.rejects(client.stopDecision(source, { fetchImpl: async () => {
      calls++;
      return response({ code, message: 'Decision Stop refused.' }, 409);
    } }), error => error.code === code);
    assert.equal(calls, before + 1);
  }
  const before = calls;
  await assert.rejects(client.stopDecision(source, { fetchImpl: async () => {
    calls++;
    throw new Error('Lost acknowledgement after possible delivery.');
  } }), /Lost acknowledgement/);
  assert.equal(calls, before + 1);
  assert.deepEqual(source, original);
  assert.equal(client.runs.size, 0);
  client.close();
});
