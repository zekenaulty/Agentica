import assert from 'node:assert/strict';
import { AgenticaHost } from '../Agentica.Lab.Web/wwwroot/sdk/agentica-host.mjs';

// Started only by the .NET test's isolated, provider-free host. No ambient endpoint or credentials.
const endpoint = new URL(process.argv[2]);
assert.equal(endpoint.hostname, '127.0.0.1');
assert.equal(endpoint.protocol, 'http:');
const records = new Map();
const statuses = [];
let actions = 0;
let complete;
const completion = new Promise(resolve => { complete = resolve; });
const client = new AgenticaHost({ url: `${endpoint.origin.replace('http:', 'ws:')}/api/host`,
  loadAction: key => records.get(key), saveAction: (key, value) => records.set(key, structuredClone(value)),
  onStatus: status => statuses.push(status),
  onMessage: message => { if (message.type === 'outcome') complete(message); },
  onAction: action => {
    actions++;
    return { actionId: action.actionId, sessionId: action.sessionId, sessionEpoch: action.sessionEpoch,
      disposition: 'applied', beforeRevision: 0, afterRevision: 1, evidenceId: 'proof', summary: 'Fixture transition applied.',
      observation: { observationId: 'after', revision: 1, observedAt: '2026-10-15T00:00:01.000Z', data: { done: true } },
      completion: { objectiveId: 'objective', evidenceId: 'proof', summary: 'Fixture proved completion.' } };
  },
});
const submission = { attemptId: 'attempt', request: { protocolVersion: 1, hostId: 'sdk-host', sessionId: 'sdk-session',
  sessionEpoch: 'epoch', scopeId: 'scope', perspectiveId: 'actor', objectiveId: 'objective', objective: 'Run a fixture action.',
  observation: { observationId: 'initial', revision: 0, observedAt: '2026-10-15T00:00:00.000Z', data: { done: false } },
  capabilities: [{ id: 'host.operate', name: 'Operate', description: 'One explicit fixture action.', kind: 'action',
    effect: 'writesLocalState', inputSchema: { fields: [] } }], provider: { provider: 'fixture' } } };
try {
  const started = await client.startDurable(submission);
  const outcome = await completion;
  assert.equal(outcome.runId, started.runId);
  assert.equal(actions, 1);
  const entry = await client.getStart({ hostId: 'sdk-host', sessionId: 'sdk-session', attemptId: 'attempt' });
  assert.equal(entry.runId, started.runId);
  assert.equal(entry.originalRequest.observation.facts, null);
  assert.equal(entry.acceptedRequest.provider.model, 'offline-model');
  assert.equal(entry.acceptedRequest.limits.maxSteps, 32);
  const reference = { hostId: 'sdk-host', sessionId: 'sdk-session', attemptId: 'attempt',
    runId: entry.runId, acceptedRequestHash: entry.acceptedRequestHash };
  let stopped;
  // The outcome frame can precede task quiescence. Stop replay is exact, not a second execution.
  for (let attempt = 0; attempt < 20; attempt++) {
    stopped = await client.stopStart(reference);
    if (stopped.terminal) break;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  assert.equal(stopped.terminal?.reason, 'stopped');
  assert.deepEqual(await client.stopStart(reference), stopped);
  await assert.rejects(client.startDurable(submission), error => error.code === 'start.stopped');
  assert.equal(actions, 1);
  assert.equal(statuses.some(status => status.type === 'protocol.error' || status.type === 'action.error'), false);
  console.log('PASS: real SDK typed start, host effect, exact lookup and settled Stop; one fixture action.');
} finally { client.close(); }
