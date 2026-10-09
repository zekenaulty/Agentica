import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgenticaHost, actionKey, actionFingerprint } from '../Agentica.Lab.Web/wwwroot/sdk/agentica-host.mjs';

const action = { actionId: 'action-1', runId: 'run-1', runnerRunId: 'runner-1', stepId: 'step-1',
  sessionId: 'session-1', sessionEpoch: 'epoch-1', capabilityId: 'host.accept', manifestHash: 'manifest-1',
  arguments: {}, expectedRevision: 2, deadlineAt: '2099-01-01T00:00:00Z' };
const capability = { id: action.capabilityId, effect: 'writesLocalState' };
const binding = { sessionId: action.sessionId, sessionEpoch: action.sessionEpoch,
  objectiveId: 'objective-1', capabilities: [capability] };
const observation = revision => ({ observationId: `observation-${revision}`, revision,
  observedAt: '2026-10-09T00:00:00Z', data: { status: 'accepted' } });
const valid = () => ({ actionId: action.actionId, sessionId: action.sessionId, sessionEpoch: action.sessionEpoch,
  disposition: 'applied', beforeRevision: 2, afterRevision: 3, evidenceId: 'effect-1', summary: 'Applied once.',
  observation: observation(3), completion: { objectiveId: 'objective-1', evidenceId: 'completion-1', summary: 'Objective verified.' } });
const nonApplied = disposition => ({ ...valid(), disposition, afterRevision: 2, observation: observation(2), completion: undefined });
const invalid = [
  ['stale applied revision', () => ({ ...valid(), beforeRevision: 3, afterRevision: 4, observation: observation(4) })],
  ['stale refused revision', () => ({ ...nonApplied('refused'), beforeRevision: 3, afterRevision: 3, observation: observation(3) })],
  ...['refused', 'conflict', 'unavailable'].map(disposition => [`${disposition} claims mutation`, () => ({ ...valid(), disposition, completion: undefined })]),
  ...['applied', 'refused', 'conflict'].map(disposition => [`${disposition} lacks observation`, () => ({ ...nonApplied(disposition), observation: null })]),
  ['observation has another revision', () => ({ ...valid(), observation: observation(4) })],
  ['observation lacks identity', () => ({ ...valid(), observation: { ...observation(3), observationId: undefined } })],
  ['observation has invalid timestamp', () => ({ ...valid(), observation: { ...observation(3), observedAt: 'yesterday' } })],
  ['observation has array data', () => ({ ...valid(), observation: { ...observation(3), data: [] } })],
  ['observation exceeds bound', () => ({ ...valid(), observation: { ...observation(3), data: { text: 'x'.repeat(65536) } } })],
  ['observation has malformed facts', () => ({ ...valid(), observation: { ...observation(3), facts: [null] } })],
  ['observation fact has explicit null state', () => ({ ...valid(), observation: { ...observation(3), facts: [{ key: 'known', summary: 'Known.', value: true, state: null }] } })],
  ['result evidence identity exceeds bound', () => ({ ...valid(), evidenceId: 'x'.repeat(129) })],
  ['result summary exceeds bound', () => ({ ...valid(), summary: 'x'.repeat(4001) })],
  ['result has unsupported fields', () => ({ ...valid(), extraAuthority: true })],
  ['completion on refused action', () => ({ ...nonApplied('refused'), completion: valid().completion })],
  ['completion on unresolved action', () => ({ ...nonApplied('unresolved'), completion: valid().completion })],
  ['completion refers to another objective', () => ({ ...valid(), completion: { ...valid().completion, objectiveId: 'other' } })],
  ['completion lacks evidence identity', () => ({ ...valid(), completion: { ...valid().completion, evidenceId: '' } })],
  ['completion has invalid summary', () => ({ ...valid(), completion: { ...valid().completion, summary: '' } })],
];

function setup(makeResult, metadata = binding) {
  const durable = new Map(), saves = [], replies = []; let effects = 0;
  const host = new AgenticaHost({ url: 'ws://localhost/api/host', onAction: async () => { effects++; return makeResult(); },
    loadAction: key => durable.get(key), saveAction: (key, record) => { saves.push(record.status); durable.set(key, structuredClone(record)); } });
  host.runs.set(action.runId, structuredClone(metadata));
  host.socket = { readyState: 1, send: text => replies.push(JSON.parse(text).payload) };
  const deliver = async (type = 'action.request') => { await host._action({ type, runId: action.runId, payload: action }); return replies.at(-1); };
  return { host, durable, saves, deliver, effects: () => effects };
}

for (const [name, makeResult] of invalid) test(`invalid ${name} never becomes completed custody or a repeated effect`, async () => {
  const fixture = setup(makeResult);
  assert.equal((await fixture.deliver()).disposition, 'unresolved');
  assert.deepEqual(fixture.saves, ['pending']);
  assert.equal(fixture.host.actions.get(actionKey(action)).status, 'pending');
  assert.equal((await fixture.deliver()).disposition, 'unresolved');
  assert.equal(fixture.effects(), 1);
  const original = valid(); fixture.host.reconcileAction = () => original;
  assert.deepEqual(await fixture.deliver('action.reconcile'), original);
  assert.deepEqual(await fixture.deliver(), original);
  assert.equal(fixture.effects(), 1);
  assert.equal(fixture.durable.get(actionKey(action)).status, 'completed');
});

test('read-only capability metadata rejects claimed mutations before completed persistence', async () => {
  const fixture = setup(valid, { ...binding, capabilities: [{ ...capability, effect: 'readOnly' }] });
  assert.equal((await fixture.deliver()).disposition, 'unresolved');
  assert.deepEqual(fixture.saves, ['pending']); assert.equal(fixture.effects(), 1);
  const result = { ...valid(), afterRevision: 2, observation: observation(2) };
  fixture.host.reconcileAction = () => result;
  assert.deepEqual(await fixture.deliver('action.reconcile'), result);
  assert.equal(fixture.effects(), 1);
});

test('valid non-applied dispositions and observation-free uncertainty preserve their semantics', async () => {
  for (const disposition of ['refused', 'conflict', 'unavailable', 'unresolved']) {
    const result = nonApplied(disposition);
    if (['unavailable', 'unresolved'].includes(disposition)) delete result.observation;
    delete result.completion;
    const fixture = setup(() => result);
    assert.deepEqual(await fixture.deliver(), result);
    assert.equal(fixture.durable.get(actionKey(action)).status, disposition === 'unresolved' ? 'pending' : 'completed');
    assert.equal(fixture.effects(), 1);
  }
});

test('invalid durable completion is rejected without overwriting it or invoking the action', async () => {
  const fixture = setup(() => { throw new Error('Must not execute.'); });
  const bad = { fingerprint: actionFingerprint(action), status: 'completed', result: { ...valid(), beforeRevision: 1 } };
  fixture.durable.set(actionKey(action), structuredClone(bad));
  assert.equal((await fixture.deliver('action.reconcile')).disposition, 'unresolved');
  assert.deepEqual(fixture.saves, []); assert.deepEqual(fixture.durable.get(actionKey(action)), bad);
  assert.equal(fixture.effects(), 0);
  fixture.durable.set(actionKey(action), { ...bad, result: valid() });
  assert.deepEqual(await fixture.deliver('action.reconcile'), valid());
  assert.equal(fixture.effects(), 0);
});

test('invalid WebSocket reconciliation cannot replace an unresolved reservation', async () => {
  const fixture = setup(() => { throw new Error('Original effect acknowledgement unknown.'); });
  await fixture.deliver();
  fixture.host.reconcileAction = () => ({ ...valid(), completion: { ...valid().completion, objectiveId: 'other' } });
  assert.equal((await fixture.deliver('action.reconcile')).disposition, 'unresolved');
  assert.deepEqual(fixture.saves, ['pending']); assert.equal(fixture.effects(), 1);
});

const response = value => new Response(JSON.stringify(value), { status: 200 });
for (const source of ['retained', 'inspected']) test(`HTTP recovery validates ${source} results with original capability and objective metadata`, async () => {
  for (const [name, makeResult, effect] of [
    ['stale', () => ({ ...valid(), beforeRevision: 1 }), 'writesLocalState'],
    ['read-only mutation', valid, 'readOnly'],
    ['wrong objective', () => ({ ...valid(), completion: { ...valid().completion, objectiveId: 'other' } }), 'writesLocalState'],
    ['missing observation', () => ({ ...valid(), observation: null }), 'writesLocalState'],
  ]) {
    let saves = 0, posts = 0, effects = 0;
    const retained = { fingerprint: actionFingerprint(action), status: source === 'retained' ? 'completed' : 'pending',
      ...(source === 'retained' ? { result: makeResult() } : {}) };
    const host = new AgenticaHost({ url: 'ws://localhost/api/host', onAction: () => { effects++; throw new Error('Must not execute.'); },
      loadAction: () => retained, saveAction: () => { saves++; }, reconcileAction: () => makeResult() });
    const outcomes = await host.recover({ hostId: 'host-1', sessionId: action.sessionId, fetchImpl: async (_, options) => {
      if (options) { posts++; return response({ actionId: action.actionId, resolved: true }); }
      return response([{ hostId: 'host-1', sessionId: action.sessionId, sessionEpoch: action.sessionEpoch, runId: action.runId,
        objectiveId: binding.objectiveId, capability: { ...capability, effect }, request: action }]);
    } });
    assert.equal(outcomes[0].status, 'error', name); assert.equal(outcomes[0].resolved, false, name);
    assert.equal(saves, 0, name); assert.equal(posts, 0, name); assert.equal(effects, 0, name);
    assert.equal(host.runs.size, 0, 'Recovery metadata must not admit a live run.');
  }
});

test('start and same-client resume retain independent validation metadata', async () => {
  const fixture = setup(valid);
  fixture.host.request = async (_type, _payload, { binding: captured }) => {
    assert.equal(captured.objectiveId, binding.objectiveId);
    assert.equal(captured.capabilities[0].effect, 'readOnly');
    return { type: 'started', payload: { runId: action.runId } };
  };
  const capabilities = [{ ...capability, effect: 'readOnly' }];
  await fixture.host.start({ ...binding, hostId: 'host-1', scopeId: 'scope-1', perspectiveId: 'party', objective: 'Test', capabilities });
  capabilities[0].effect = 'writesLocalState';
  assert.equal(fixture.host.runs.get(action.runId).capabilities[0].effect, 'readOnly');
  await fixture.host.resume({ runId: action.runId, sessionId: action.sessionId, sessionEpoch: action.sessionEpoch });
  assert.equal((await fixture.deliver()).disposition, 'unresolved');
  assert.deepEqual(fixture.saves, ['pending']);
});

test('a resumed envelope supplies the objective before an immediate action is validated', async () => {
  const fixture = setup(() => ({ ...valid(), completion: { ...valid().completion, objectiveId: 'wrong' } }),
    { sessionId: action.sessionId, sessionEpoch: action.sessionEpoch });
  fixture.host.pending.set('resume-1', { resolve() {}, binding: { sessionId: action.sessionId, sessionEpoch: action.sessionEpoch } });
  fixture.host._receive(JSON.stringify({ protocolVersion: 1, type: 'resumed', requestId: 'resume-1', runId: action.runId,
    payload: { runId: action.runId, objectiveId: binding.objectiveId } }));
  assert.equal((await fixture.deliver()).disposition, 'unresolved');
  assert.deepEqual(fixture.saves, ['pending']);
});

test('result nesting is bounded before completed persistence', async () => {
  let data = {};
  for (let index = 0; index < 32; index++) data = { nested: data };
  const fixture = setup(() => ({ ...valid(), observation: { ...observation(3), data } }));
  assert.equal((await fixture.deliver()).disposition, 'unresolved');
  assert.deepEqual(fixture.saves, ['pending']);
});

test('wire byte validation counts UTF-8 while service serialization remains authoritative', async () => {
  const result = valid();
  // The service's escaping of these characters can require more bytes than this
  // wire representation. The SDK does not estimate .NET canonical storage size.
  result.observation.data = { text: 'é<&'.repeat(100) };
  const fixture = setup(() => result);
  assert.deepEqual(await fixture.deliver(), result);
  assert.equal(fixture.durable.get(actionKey(action)).status, 'completed');
  const oversized = setup(() => ({ ...valid(), observation: { ...observation(3), data: { text: '😀'.repeat(17000) } } }));
  assert.equal((await oversized.deliver()).disposition, 'unresolved');
  assert.deepEqual(oversized.saves, ['pending']);
});
