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

const hash = digit => `sha256:${digit.repeat(64)}`;
const observation = revision => ({ observationId: `observation-${revision}`, revision,
  observedAt: '2026-10-15T12:00:00Z', data: { revision, item: { id: 'item', eligible: true } }, facts: null });
const submission = () => ({ attemptId: 'initial-attempt', request: {
  protocolVersion: 1, hostId: 'host', sessionId: 'session', sessionEpoch: 'owner',
  scopeId: 'scope', perspectiveId: 'actor', objectiveId: 'objective', objective: 'Complete host work.',
  observation: observation(0), capabilities: [{ id: 'host.admit', name: 'Admit', description: 'Admit host work.',
    kind: 'action', effect: 'writesLocalState', durableHandoff: true,
    inputSchema: { fields: [{ name: 'itemId', type: 'string', required: true }], allowAdditionalProperties: false } }],
  provider: { provider: 'fixture', model: 'offline' }, limits: { maxSteps: 4 },
} });
const entry = (source = submission()) => ({ schema: 1, attemptId: source.attemptId, runId: 'initial-run',
  originalRequest: structuredClone(source.request), acceptedRequest: { ...structuredClone(source.request),
    provider: { provider: 'fixture', model: 'offline', thinkingEffortResolved: true },
    limits: { maxSteps: 4, maxRefinements: 32, maxPlanContinuations: 16, timeoutSeconds: 300,
      actionTimeoutSeconds: 45, maxRecentObservations: 6, maxRecentReceipts: 6 } },
  originalRequestHash: hash('a'), acceptedRequestHash: hash('b'), manifestHash: hash('c'),
  stopRequested: false, terminal: null, admittedAt: '2026-10-15T12:00:00Z' });
const key = () => ({ hostId: 'host', sessionId: 'session', attemptId: 'initial-attempt' });
const reference = () => ({ ...key(), runId: 'initial-run', acceptedRequestHash: hash('b') });
const terminal = (reason = 'stopped') => ({ reason, usage: reason === 'interrupted' ? null : {
  steps: 1, refinements: 0, continuations: 0, providerCalls: 1 }, usageUnknown: reason === 'interrupted', settledAt: '2026-10-15T12:00:01Z' });
const action = (patch = {}) => ({ actionId: 'action', runId: 'initial-run', runnerRunId: 'runner', stepId: 'step',
  sessionId: 'session', sessionEpoch: 'owner', capabilityId: 'host.admit', manifestHash: hash('c'),
  arguments: { itemId: 'item' }, expectedRevision: 0, deadlineAt: '2099-10-15T12:00:00Z', ...patch });
const result = action => ({ actionId: action.actionId, sessionId: action.sessionId, sessionEpoch: action.sessionEpoch,
  disposition: 'applied', beforeRevision: 0, afterRevision: 1, evidenceId: 'receipt', summary: 'Work admitted.',
  observation: observation(1), operation: { operationId: 'operation', summary: 'Host owns execution.' } });
const response = (body, status = 200) => ({ ok: status >= 200 && status < 300, status, text: async () => JSON.stringify(body) });
const host = (options = {}) => new AgenticaHost({ url: 'wss://example.invalid/api/host?ignored=yes#ignored',
  WebSocketImpl: Socket, requestTimeoutMs: 1000, onAction: () => assert.fail('Unexpected host effect.'), ...options });
const turn = () => new Promise(resolve => setImmediate(resolve));
async function connect(client) { const pending = client.connect(); const socket = Socket.instances.at(-1); socket.open(); await pending; return socket; }
function started(request, admission = entry()) {
  const accepted = admission.acceptedRequest;
  return { type: 'started', requestId: request.requestId, runId: admission.runId, payload: {
    runId: admission.runId, hostId: accepted.hostId, sessionId: accepted.sessionId, sessionEpoch: accepted.sessionEpoch,
    objectiveId: accepted.objectiveId, scopeId: accepted.scopeId, perspectiveId: accepted.perspectiveId,
    manifestHash: admission.manifestHash, startAdmission: admission } };
}
function deliver(socket, request = action()) { socket.receive({ type: 'action.request', runId: request.runId, payload: request }); }
async function admit(client, socket, admission = entry()) {
  const pending = client.startDurable({ attemptId: admission.attemptId, request: admission.originalRequest });
  await turn(); socket.receive(started(socket.sent.at(-1), admission)); return pending;
}

test('startDurable freezes the full submission before connecting and installs acceptance before immediate actions', async t => {
  let effects = 0;
  const events = [];
  const client = host({ onMessage: message => {
    if (message.type === 'started') {
      assert.equal(client.runs.get('initial-run').startAdmission.attemptId, 'initial-attempt');
      events.push('accepted');
      message.payload.startAdmission.acceptedRequest.capabilities.length = 0;
    }
  }, onAction: next => { effects++; events.push('effect'); return result(next); } });
  t.after(() => client.close());
  const source = submission(), before = structuredClone(source);
  const pending = client.startDurable(source);
  const socket = Socket.instances.at(-1);
  source.attemptId = 'changed'; source.request.sessionEpoch = 'changed';
  source.request.capabilities[0].inputSchema.fields[0].required = false;
  source.request.observation.data.item.eligible = false; source.request.provider.model = 'changed';
  socket.open(); await turn();
  assert.equal(socket.sent.length, 1);
  const sent = socket.sent[0];
  assert.equal(sent.type, 'start.durable'); assert.equal(sent.runId, undefined);
  assert.deepEqual(sent.payload, before);
  const admission = entry(before);
  socket.receive(started(sent, admission));
  assert.deepEqual(client.runs.get('initial-run').startAdmission, admission);
  deliver(socket);
  const accepted = await pending;
  accepted.startAdmission.acceptedRequest.capabilities.length = 0;
  await client.actionQueue;
  assert.equal(effects, 1); assert.deepEqual(events, ['accepted', 'effect']);
  assert.equal(socket.sent.at(-1).payload.disposition, 'applied');
  assert.deepEqual(client.runs.get('initial-run').capabilities, before.request.capabilities);
  assert.equal(accepted.pendingActions, undefined); assert.equal(accepted.context, undefined); assert.equal(accepted.outcome, undefined);
});

test('lost start reply is recovered by exact GET after SDK reload without opening a socket or starting again', async t => {
  const first = host(); t.after(() => first.close());
  const socket = await connect(first);
  const pending = first.startDurable(submission());
  const rejected = assert.rejects(pending, /unresolved/);
  await turn(); socket.close(); await rejected;
  assert.equal(first.runs.size, 0);
  assert.deepEqual(socket.sent.map(message => message.type), ['start.durable']);
  const next = host(); t.after(() => next.close());
  const connections = Socket.instances.length, requests = [];
  const recovered = await next.getStart(key(), { fetchImpl: async (url, options) => {
    requests.push({ url, options }); return response(entry());
  } });
  assert.deepEqual(recovered, entry()); assert.equal(next.runs.size, 0);
  assert.equal(Socket.instances.length, connections); assert.equal(requests.length, 1);
  const url = new URL(requests[0].url);
  assert.equal(url.protocol, 'https:'); assert.equal(url.pathname, '/api/starts'); assert.equal(url.hash, '');
  assert.deepEqual(Object.fromEntries(url.searchParams), key()); assert.equal(requests[0].options, undefined);
});

test('timed out start never retries and a late started reply cannot install an action binding', async t => {
  const client = host({ requestTimeoutMs: 25 }); t.after(() => client.close());
  const socket = await connect(client);
  const pending = client.startDurable(submission());
  await assert.rejects(pending, /timed out.*unresolved/);
  assert.equal(socket.sent.length, 1);
  socket.receive(started(socket.sent[0])); deliver(socket); await client.actionQueue;
  assert.equal(client.runs.size, 0); assert.equal(socket.sent.length, 1);
});

test('provider and limits may normalize while reordered object keys retain full payload equivalence', async t => {
  const client = host(); t.after(() => client.close()); const socket = await connect(client);
  const admitted = entry();
  admitted.originalRequest = Object.fromEntries(Object.entries(admitted.originalRequest).reverse());
  admitted.acceptedRequest.observation.data = { item: { eligible: true, id: 'item' }, revision: 0 };
  assert.deepEqual((await admit(client, socket, admitted)).startAdmission, admitted);
  assert.notEqual(admitted.originalRequestHash, admitted.acceptedRequestHash);
});

test('each request fits the 64 KiB boundary and the compact started envelope fits the existing message limit', async t => {
  const source = submission(); source.request.observation.data.padding = '';
  source.request.observation.data.padding = 'x'.repeat(65536 - Buffer.byteLength(JSON.stringify(source.request)));
  assert.equal(Buffer.byteLength(JSON.stringify(source.request)), 65536);
  const admitted = entry(source); admitted.acceptedRequest = structuredClone(source.request);
  assert.ok(Buffer.byteLength(JSON.stringify(started({ requestId: 'request' }, admitted))) < 262144);
  const client = host(); t.after(() => client.close()); const socket = await connect(client);
  assert.deepEqual((await admit(client, socket, admitted)).startAdmission, admitted);
  source.request.observation.data.padding += 'x';
  await assert.rejects(client.startDurable(source), /64 KiB/);
  assert.equal(socket.sent.length, 1);
});

test('service start refusals preserve their code and never fall back to an ordinary start', async t => {
  const client = host(); t.after(() => client.close()); const socket = await connect(client);
  const pending = client.startDurable(submission());
  const rejected = assert.rejects(pending, error => error.code === 'start.conflict');
  await turn(); socket.receive({ type: 'error', requestId: socket.sent[0].requestId,
    payload: { code: 'start.conflict', message: 'Reconcile the original attempt.' } });
  await rejected; await turn();
  assert.equal(client.runs.size, 0); assert.deepEqual(socket.sent.map(message => message.type), ['start.durable']);
});

const invalidReplies = [
  ['response type', message => { message.type = 'resumed'; }],
  ['envelope run', message => { message.runId = 'foreign'; }],
  ...['runId', 'hostId', 'sessionId', 'sessionEpoch', 'objectiveId', 'scopeId', 'perspectiveId', 'manifestHash']
    .map(field => [`snapshot ${field}`, message => { message.payload[field] = 'foreign'; }]),
  ['missing admission', message => { delete message.payload.startAdmission; }],
  ['attempt', message => { message.payload.startAdmission.attemptId = 'foreign'; }],
  ['original objective', message => {
    message.payload.startAdmission.originalRequest.objective = 'Different work.';
    message.payload.startAdmission.acceptedRequest.objective = 'Different work.';
  }],
  ['original provider', message => { message.payload.startAdmission.originalRequest.provider.model = 'rewritten'; }],
  ['original limits', message => { message.payload.startAdmission.originalRequest.limits.maxSteps = 8; }],
  ...['hostId', 'sessionId', 'sessionEpoch', 'scopeId', 'perspectiveId', 'objectiveId', 'objective']
    .map(field => [`accepted ${field}`, message => { message.payload.startAdmission.acceptedRequest[field] = 'foreign'; }]),
  ['accepted observation', message => { message.payload.startAdmission.acceptedRequest.observation.data.item.eligible = false; }],
  ['accepted fact', message => { message.payload.startAdmission.acceptedRequest.observation.facts = []; }],
  ['accepted capability', message => { message.payload.startAdmission.acceptedRequest.capabilities[0].effect = 'destructive'; }],
  ['accepted schema', message => { message.payload.startAdmission.acceptedRequest.capabilities[0].inputSchema.fields[0].required = false; }],
  ['accepted handoff', message => { message.payload.startAdmission.acceptedRequest.capabilities[0].durableHandoff = false; }],
  ['accepted capability name', message => { message.payload.startAdmission.acceptedRequest.capabilities[0].name = 'Rewritten'; }],
];
for (const [name, change] of invalidReplies) test(`invalid ${name} reply rejects before registering or dispatching`, async t => {
  const messages = [], client = host({ onMessage: message => messages.push(message.type) });
  t.after(() => client.close()); const socket = await connect(client);
  const pending = client.startDurable(submission()), rejected = assert.rejects(pending);
  await turn(); const reply = started(socket.sent[0]); change(reply); socket.receive(reply);
  deliver(socket); await rejected; await client.actionQueue;
  assert.equal(client.runs.size, 0); assert.equal(client.actions.size, 0); assert.equal(socket.sent.length, 1);
  assert.ok(!messages.includes('started')); assert.ok(!messages.includes('resumed'));
});

const invalidEntries = [
  ['schema', item => { item.schema = 2; }], ['extra field', item => { item.unexpected = true; }],
  ['stop state', item => { item.stopRequested = 'false'; }], ['missing terminal', item => { delete item.terminal; }],
  ['admission timestamp', item => { item.admittedAt = 'not-a-time'; }],
  ...['originalRequestHash', 'acceptedRequestHash', 'manifestHash'].flatMap(field =>
    [hash('A'), 'sha256:abc', 'a'.repeat(64)].map(value => [`${field} ${value.slice(0, 12)}`, item => { item[field] = value; }])),
  ['terminal reason', item => { item.terminal = { ...terminal(), reason: 'completed' }; }],
  ['terminal usage', item => { item.terminal = { ...terminal(), usage: { steps: 0 } }; }],
  ['negative usage', item => { item.terminal = terminal(); item.terminal.usage.steps = -1; }],
  ['fractional usage', item => { item.terminal = terminal(); item.terminal.usage.providerCalls = 0.5; }],
  ['unsafe usage', item => { item.terminal = terminal(); item.terminal.usage.steps = Number.MAX_SAFE_INTEGER + 1; }],
  ['unknown usage', item => { item.terminal = { ...terminal(), usageUnknown: undefined }; }],
  ['null known usage', item => { item.terminal = { ...terminal(), usage: null }; }],
  ['terminal timestamp', item => { item.terminal = { ...terminal(), settledAt: '' }; }],
  ['original request size', item => { item.originalRequest.observation.data.large = 'x'.repeat(65536); }],
  ['accepted request size', item => { item.acceptedRequest.observation.data.large = 'x'.repeat(65536); }],
  ['accepted authority on GET', item => { item.acceptedRequest.capabilities[0].description = 'Changed authority.'; }],
];
for (const [name, change] of invalidEntries) test(`getStart rejects invalid ${name} without registration or a socket`, async () => {
  const client = host(), connections = Socket.instances.length, admitted = entry(); change(admitted);
  let requests = 0;
  await assert.rejects(client.getStart(key(), { fetchImpl: async () => { requests++; return response(admitted); } }));
  assert.equal(requests, 1); assert.equal(client.runs.size, 0); assert.equal(Socket.instances.length, connections);
});

for (const state of ['pending stop', 'stopped', 'interrupted']) test(`${state} started response fences actions already queued before acceptance`, async t => {
  const client = host(); t.after(() => client.close()); const socket = await connect(client);
  const admitted = entry(); admitted.stopRequested = state !== 'interrupted';
  if (state !== 'pending stop') admitted.terminal = terminal(state);
  const pending = client.startDurable(submission()); await turn();
  deliver(socket); socket.receive(started(socket.sent[0], admitted));
  assert.ok(client.stoppedRuns.has(admitted.runId)); assert.ok(client.runs.has(admitted.runId));
  const received = await pending; await client.actionQueue;
  assert.deepEqual(received.startAdmission.terminal, admitted.terminal);
  assert.equal(socket.sent.at(-1).payload.disposition, 'unresolved'); assert.equal(client.actions.size, 0);
});

test('stopStart freezes its exact reference and fences effects before HTTP completion, even when settlement is pending', async t => {
  const client = host(); t.after(() => client.close()); const socket = await connect(client); await admit(client, socket);
  let complete; const requests = [];
  const target = reference(), before = structuredClone(target);
  deliver(socket);
  const pending = client.stopStart(target, { fetchImpl: (url, options) => {
    requests.push({ url, options }); return new Promise(resolve => { complete = resolve; });
  } });
  target.attemptId = 'changed'; target.runId = 'changed'; target.acceptedRequestHash = hash('d');
  assert.ok(client.stoppedRuns.has(before.runId));
  await client.actionQueue; assert.equal(socket.sent.at(-1).payload.disposition, 'unresolved');
  const admitted = { ...entry(), stopRequested: true };
  complete(response(admitted)); assert.deepEqual(await pending, admitted);
  assert.equal(requests.length, 1);
  const url = new URL(requests[0].url);
  assert.equal(url.pathname, '/api/starts/stop'); assert.equal(url.search, ''); assert.equal(url.hash, '');
  assert.equal(requests[0].options.method, 'POST'); assert.equal(requests[0].options.headers['Content-Type'], 'application/json');
  assert.deepEqual(JSON.parse(requests[0].options.body), before);
  assert.equal(admitted.terminal, null); assert.equal(socket.sent.filter(item => item.type === 'start.durable').length, 1);
});

test('stop during durable action reservation prevents dispatch after the persistence await', async t => {
  let saved, release;
  const saving = new Promise(resolve => { saved = resolve; });
  const client = host({ loadAction: () => null, saveAction: async () => { saved(); await new Promise(resolve => { release = resolve; }); } });
  t.after(() => client.close()); const socket = await connect(client); await admit(client, socket);
  deliver(socket); await saving;
  await client.stopStart(reference(), { fetchImpl: async () => response({ ...entry(), stopRequested: true }) });
  release(); await client.actionQueue;
  assert.equal(socket.sent.at(-1).payload.disposition, 'unresolved');
});

for (const field of ['hostId', 'sessionId', 'attemptId', 'runId', 'acceptedRequestHash'])
  test(`stopStart rejects changed ${field} response binding without dispatch or retry`, async () => {
    const client = host(), connections = Socket.instances.length, admitted = { ...entry(), stopRequested: true };
    if (['hostId', 'sessionId'].includes(field)) {
      admitted.originalRequest[field] = 'foreign'; admitted.acceptedRequest[field] = 'foreign';
    } else admitted[field] = field.endsWith('Hash') ? hash('d') : 'foreign';
    let requests = 0;
    await assert.rejects(client.stopStart(reference(), { fetchImpl: async () => { requests++; return response(admitted); } }));
    assert.equal(requests, 1); assert.equal(client.runs.size, 0); assert.equal(Socket.instances.length, connections);
    assert.ok(client.stoppedRuns.has('initial-run'));
  });

test('getStart snapshots the lookup identity and stopped recovery fences an already bound run', async t => {
  const client = host(); t.after(() => client.close()); const socket = await connect(client); await admit(client, socket);
  let complete; const lookup = key();
  const pending = client.getStart(lookup, { fetchImpl: () => new Promise(resolve => { complete = resolve; }) });
  lookup.hostId = 'changed'; lookup.attemptId = 'changed';
  const admitted = { ...entry(), stopRequested: true, terminal: terminal() };
  complete(response(admitted)); assert.deepEqual(await pending, admitted);
  deliver(socket); await client.actionQueue; assert.equal(socket.sent.at(-1).payload.disposition, 'unresolved');
  await client.getStart(key(), { fetchImpl: async () => response(entry()) });
  assert.ok(client.stoppedRuns.has('initial-run'));
});

for (const method of ['getStart', 'stopStart']) test(`${method} not-found and HTTP failures stay unknown without retry or start`, async () => {
  const client = host(), connections = Socket.instances.length;
  for (const status of [404, 409, 500]) {
    let requests = 0;
    const error = { code: status === 404 ? 'start.not_found' : 'start.unavailable', message: 'Reconcile original attempt.' };
    await assert.rejects(client[method](method === 'getStart' ? key() : reference(), { fetchImpl: async () => {
      requests++; return response(error, status);
    } }), failure => failure.code === error.code);
    assert.equal(requests, 1);
  }
  let requests = 0;
  await assert.rejects(client[method](method === 'getStart' ? key() : reference(), { fetchImpl: async () => {
    requests++; throw new Error('Lost HTTP response.');
  } }), /Lost HTTP response/);
  assert.equal(requests, 1); assert.equal(client.runs.size, 0); assert.equal(Socket.instances.length, connections);
});

test('HTTP reads use the existing bounded response helper', async () => {
  const client = host();
  await assert.rejects(client.getStart(key(), { fetchImpl: async () => ({ ok: true, text: async () => ' '.repeat(4194305) }) }), /exceeds/);
  await assert.rejects(client.getStart(key(), { fetchImpl: async () => ({ ok: true, text: async () => '{invalid' }) }));
  await assert.rejects(client.getStart(key(), { fetchImpl: async () => response(null) }));
  await assert.rejects(client.stopStart(reference(), { fetchImpl: async () => response(entry()) }), /did not acknowledge/);
});

test('invalid submissions and references fail before any transport', async () => {
  const client = host(), connections = Socket.instances.length;
  for (const change of [value => { value.attemptId = ''; }, value => { value.attemptId = 'bad\nidentity'; },
    value => { value.unexpected = true; }, value => { value.request.protocolVersion = 2; },
    value => { value.request.capabilities = []; }, value => { value.request.provider = []; },
    value => { value.request.limits = 'unbounded'; }, value => { value.request.observation.data.large = 'x'.repeat(65536); },
    value => { let nested = value.request.observation.data; for (let i = 0; i < 32; i++) nested = nested.child = {}; }]) {
    const value = submission(); change(value); await assert.rejects(client.startDurable(value));
  }
  for (const value of [null, {}, { ...key(), attemptId: '' }, { ...key(), extra: true }])
    await assert.rejects(client.getStart(value, { fetchImpl: () => assert.fail('Unexpected HTTP request.') }));
  for (const value of [key(), { ...reference(), runId: '' }, { ...reference(), acceptedRequestHash: 'invalid' }])
    await assert.rejects(client.stopStart(value, { fetchImpl: () => assert.fail('Unexpected HTTP request.') }));
  assert.equal(Socket.instances.length, connections); assert.equal(client.runs.size, 0);
});

test('retained admission cannot be rebound, and explicit identical replies cannot remove a local stop fence', async t => {
  const client = host(); t.after(() => client.close()); const socket = await connect(client); await admit(client, socket);
  await client.stopStart(reference(), { fetchImpl: async () => response({ ...entry(), stopRequested: true }) });
  const retained = structuredClone(client.runs.get('initial-run'));
  const changed = entry(); changed.acceptedRequestHash = hash('d');
  const pending = client.startDurable(submission()), rejected = assert.rejects(pending, /retained run binding/);
  await turn(); socket.receive(started(socket.sent.at(-1), changed)); await rejected;
  assert.deepEqual(client.runs.get('initial-run'), retained);
  await assert.rejects(client.getStart(key(), { fetchImpl: async () => response(changed) }), /retained run binding/);
  await admit(client, socket); assert.ok(client.stoppedRuns.has('initial-run'));
  deliver(socket); await client.actionQueue; assert.equal(socket.sent.at(-1).payload.disposition, 'unresolved');
});

test('a wrong local stop reference is rejected before fencing or HTTP', async t => {
  const client = host(); t.after(() => client.close()); const socket = await connect(client); await admit(client, socket);
  await assert.rejects(client.stopStart({ ...reference(), acceptedRequestHash: hash('d') }, {
    fetchImpl: () => assert.fail('Unexpected HTTP request.') }));
  assert.equal(client.stoppedRuns.size, 0);
});

test('a retained attempt cannot change run ID on either a started reply or a reconciliation read', async t => {
  const client = host(); t.after(() => client.close()); const socket = await connect(client); await admit(client, socket);
  const admitted = entry(); admitted.runId = 'different-run';
  const pending = client.startDurable(submission()), rejected = assert.rejects(pending, /retained attempt/);
  await turn(); socket.receive(started(socket.sent.at(-1), admitted)); await rejected;
  await assert.rejects(client.getStart(key(), { fetchImpl: async () => response(admitted) }), /retained attempt/);
  assert.deepEqual([...client.runs.keys()], ['initial-run']);
});

test('durable actions require the admitted manifest and capability before invoking host effects', async t => {
  const client = host(); t.after(() => client.close()); const socket = await connect(client); await admit(client, socket);
  for (const patch of [{ manifestHash: hash('d') }, { capabilityId: 'unbound' }]) { deliver(socket, action(patch)); await client.actionQueue; }
  assert.equal(client.actions.size, 0); assert.equal(socket.sent.length, 1);
});

test('durable run retention refuses overflow without evicting existing admission bindings', async t => {
  const client = host({ maxActions: 1 }); t.after(() => client.close()); const socket = await connect(client); await admit(client, socket);
  const source = submission(); source.attemptId = 'second-attempt';
  const admitted = entry(source); admitted.runId = 'second-run';
  const pending = client.startDurable(source), rejected = assert.rejects(pending, /retention limit/);
  await turn(); socket.receive(started(socket.sent.at(-1), admitted)); await rejected;
  assert.deepEqual([...client.runs.keys()], ['initial-run']);
});
