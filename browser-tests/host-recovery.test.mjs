import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgenticaHost, actionKey, actionFingerprint } from '../Agentica.Lab.Web/wwwroot/sdk/agentica-host.mjs';

const request = { actionId: 'action-1', runId: 'run-1', runnerRunId: 'runner-1', stepId: 'step-1',
  sessionId: 'session-1', sessionEpoch: 'old-epoch', capabilityId: 'host.accept', manifestHash: 'manifest-1',
  arguments: { itemId: 'item-1' }, expectedRevision: 1, deadlineAt: '2000-01-01T00:00:00Z' };
const entry = { hostId: 'host-1', sessionId: request.sessionId, sessionEpoch: request.sessionEpoch, runId: request.runId, request };
const result = { actionId: request.actionId, sessionId: request.sessionId, sessionEpoch: request.sessionEpoch,
  disposition: 'applied', beforeRevision: 1, afterRevision: 2, evidenceId: 'receipt-1', summary: 'Original effect established.',
  observation: { observationId: 'obs-2', revision: 2, observedAt: '2026-10-09T00:00:00Z', data: { accepted: true } } };
const completed = { fingerprint: actionFingerprint(request), status: 'completed', result };
const response = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
const client = options => new AgenticaHost({ url: 'ws://localhost:5078/api/host', onAction: () => { throw new Error('Recovery must not invoke effects.'); }, ...options });

test('service restart recovery posts the retained exact result without executing or calling the reconciler', async () => {
  const sent = []; let reconciled = false;
  const host = client({ loadAction: () => completed, saveAction: () => {} });
  const results = await host.recover({ hostId: 'host-1', sessionId: 'session-1', reconcile: () => { reconciled = true; },
    fetchImpl: async (url, options) => { sent.push({ url, options }); return response(options ? { actionId: 'action-1', resolved: true, duplicate: false } : [entry]); } });
  assert.equal(reconciled, false); assert.equal(results[0].resolved, true); assert.equal(sent.length, 2);
  assert.deepEqual(JSON.parse(sent[1].options.body), { hostId: 'host-1', sessionId: 'session-1', result });
  assert.match(sent[0].url, /^http:\/\/localhost:5078\/api\/recovery\?/); assert.equal(host.connected, false);
});

test('missing or pending host result remains unresolved without posting invented evidence', async () => {
  for (const retained of [null, { fingerprint: actionFingerprint(request), status: 'pending' }]) {
    let calls = 0; const host = client({ loadAction: () => retained, saveAction: () => {} });
    const results = await host.recover({ hostId: 'host-1', sessionId: 'session-1', fetchImpl: async () => { calls++; return response([entry]); } });
    assert.equal(calls, 1); assert.equal(results[0].status, 'unresolved');
  }
});

test('explicit state reconciler preserves original action and epoch and persists before resolution POST', async () => {
  const order = []; const retained = { fingerprint: actionFingerprint(request), status: 'pending' };
  const host = client({ loadAction: () => retained, saveAction: (_, value) => { order.push('persist'); assert.deepEqual(value.result, result); } });
  const results = await host.recover({ hostId: 'host-1', sessionId: 'session-1', reconcile: (action, reservation) => {
    assert.deepEqual(action, request); assert.deepEqual(reservation, retained); order.push('inspect'); return result;
  }, fetchImpl: async (_, options) => { if (!options) return response([entry]); order.push('post'); return response({ actionId: 'action-1', resolved: true }); } });
  assert.deepEqual(order, ['inspect', 'persist', 'post']); assert.equal(results[0].sessionEpoch, 'old-epoch');
});

test('changed action bindings cannot reach a reconciler or resolution POST', async () => {
  let calls = 0; let inspections = 0;
  const host = client({ loadAction: () => ({ ...completed, fingerprint: 'different' }), saveAction: () => {} });
  const results = await host.recover({ hostId: 'host-1', sessionId: 'session-1', reconcile: () => { inspections++; return result; }, fetchImpl: async () => { calls++; return response([entry]); } });
  assert.equal(calls, 1); assert.equal(inspections, 0); assert.equal(results[0].status, 'error');
});

test('a retained live run is reported for WebSocket resume without automatic retries', async () => {
  let calls = 0; const host = client({ loadAction: () => completed, saveAction: () => {} });
  const results = await host.recover({ hostId: 'host-1', sessionId: 'session-1', fetchImpl: async (_, options) => {
    calls++; return options ? response({ code: 'recovery.live_run', message: 'Resume the retained run.' }, 409) : response([entry]);
  } });
  assert.equal(calls, 2); assert.equal(results[0].status, 'live_run'); assert.equal(results[0].resolved, false);
});

test('different sessions and failed persistence leave durable service custody unresolved', async () => {
  for (const fixture of ['session', 'persistence']) {
    let calls = 0; const host = client({ loadAction: () => completed, saveAction: () => { if (fixture === 'persistence') throw new Error('storage failed'); } });
    const results = await host.recover({ hostId: 'host-1', sessionId: 'session-1', fetchImpl: async () => {
      calls++; return response([{ ...entry, sessionId: fixture === 'session' ? 'foreign' : entry.sessionId }]);
    } });
    assert.equal(calls, 1); assert.equal(results[0].status, 'error'); assert.equal(results[0].resolved, false);
  }
});

test('legacy completed-unresolved records can use explicit recovery inspection', async () => {
  const unresolved = { ...completed, result: { ...result, disposition: 'unresolved' } }; let inspections = 0; let posted = false;
  const host = client({ loadAction: () => unresolved, saveAction: () => {} });
  const results = await host.recover({ hostId: 'host-1', sessionId: 'session-1', reconcile: (action, retained) => {
    inspections++; assert.deepEqual(action, request); assert.equal(retained.status, 'unresolved'); return result;
  }, fetchImpl: async (_, options) => { if (!options) return response([entry]); posted = true; return response({ actionId: 'action-1', resolved: true }); } });
  assert.equal(inspections, 1); assert.equal(posted, true); assert.equal(results[0].resolved, true);
});

test('HTTP recovery reloads durable completion even when this client cached an earlier pending record', async () => {
  let inspections = 0; const host = client({ loadAction: () => completed, saveAction: () => {} });
  host.actions.set(actionKey(request), { fingerprint: actionFingerprint(request), status: 'pending' });
  const results = await host.recover({ hostId: 'host-1', sessionId: 'session-1', reconcile: () => { inspections++; return null; },
    fetchImpl: async (_, options) => response(options ? { actionId: 'action-1', resolved: true } : [entry]) });
  assert.equal(inspections, 0); assert.equal(results[0].resolved, true);
});
