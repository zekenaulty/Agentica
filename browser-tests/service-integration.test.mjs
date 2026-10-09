import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AgenticaHost } from '../Agentica.Lab.Web/wwwroot/sdk/agentica-host.mjs';

// Opt in against an isolated running Lab service. Never select a live provider here.
const baseUrl = process.env.AGENTICA_LAB_TEST_URL;
const provider = process.env.AGENTICA_LAB_TEST_PROVIDER ?? 'demo';
if (!['demo', 'fixture'].includes(provider)) throw new Error('Integration qualification accepts only demo or fixture providers.');
test('browser SDK drives a complete streamed inspect/accept loop with host completion evidence', { skip: !baseUrl, timeout: 20000 }, async () => {
  const id = crypto.randomUUID();
  const session = { hostId: 'sdk-integration', sessionId: `session-${id}`, sessionEpoch: `epoch-${id}`, objectiveId: `objective-${id}` };
  let revision = 0; let accepted = false; let observations = 0;
  const observe = () => ({ observationId: `observation-${id}-${++observations}`, revision, observedAt: new Date().toISOString(), data: { items: [{ id: 'sample-1', status: accepted ? 'accepted' : 'pending', eligible: true }] } });
  const records = new Map(); const messages = []; const calls = [];
  let finish; let timeout;
  const outcome = new Promise((resolve, reject) => { finish = resolve; timeout = setTimeout(() => reject(new Error('Demo did not reach a terminal outcome in 15 seconds.')), 15000); });
  const client = new AgenticaHost({ url: `${baseUrl.replace(/^http/, 'ws')}/api/host`,
    loadAction: key => records.get(key), saveAction: (key, record) => records.set(key, structuredClone(record)),
    onMessage: message => { messages.push(message); if (message.type === 'outcome') finish(message); },
    onAction: request => {
      calls.push(request.capabilityId); assert.equal(request.expectedRevision, revision);
      const result = { actionId: request.actionId, sessionId: session.sessionId, sessionEpoch: session.sessionEpoch,
        disposition: 'applied', beforeRevision: revision, afterRevision: revision,
        evidenceId: `receipt-${request.actionId}`, summary: 'Scoped inventory observed.' };
      if (request.capabilityId === 'demo.accept') {
        assert.equal(accepted, false); assert.equal(request.arguments.itemId, 'sample-1'); accepted = true; revision++;
        result.afterRevision = revision; result.summary = 'Eligible record accepted.';
        result.completion = { objectiveId: session.objectiveId, evidenceId: result.evidenceId, summary: 'Exactly one eligible record is accepted.' };
      } else assert.equal(request.capabilityId, 'demo.inspect');
      result.observation = observe(); return result;
    }
  });
  try {
    const providers = await (await fetch(`${baseUrl}/api/providers`)).json();
    assert.ok(providers.some(entry => entry.provider === provider && entry.configured));
    const started = await client.start({ ...session, scopeId: 'integration-inventory', perspectiveId: 'operator', objective: 'Accept one eligible record', observation: observe(), provider: { provider },
      capabilities: [
        { id: 'demo.inspect', name: 'Inspect', description: 'Observe inventory.', kind: 'query', effect: 'readOnly', inputSchema: { fields: [] } },
        { id: 'demo.accept', name: 'Accept', description: 'Accept an eligible pending record.', kind: 'action', effect: 'writesLocalState', inputSchema: { fields: [{ name: 'itemId', type: 'string', required: true }] } }
      ] });
    const terminal = await outcome;
    assert.equal(terminal.runId, started.runId); assert.deepEqual(calls, ['demo.inspect', 'demo.accept']); assert.equal(revision, 1);
    assert.ok(messages.some(message => message.type === 'progress'));
    assert.ok(messages.findIndex(message => message.type === 'progress') < messages.findIndex(message => message.type === 'outcome'));
    assert.equal(messages.filter(message => message.type === 'error').length, 0);
    const response = await fetch(`${baseUrl}/api/runs/${started.runId}`); assert.equal(response.status, 200);
    const snapshot = await response.json(); assert.equal(snapshot.status, 'succeeded'); assert.equal(snapshot.pendingActions.length, 0);
    assert.ok(snapshot.outcome); assert.ok(snapshot.context);
    assert.equal(records.size, 2); assert.ok([...records.values()].every(record => record.status === 'completed'));
  } finally { clearTimeout(timeout); client.close(); }
});
