/** Browser-owned effects for an Agentica run. Transport progress never executes effects. */
export const PROTOCOL_VERSION = 1;

export function canonicalJson(value) {
  if (value === null || typeof value !== 'object') {
    const text = JSON.stringify(value);
    if (text === undefined) throw new TypeError('Protocol values must be JSON serializable.');
    return text;
  }
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonicalJson(value[key])}`).join(',')}}`;
}

const copy = value => JSON.parse(JSON.stringify(value));
const requiredText = (value, name) => {
  if (typeof value !== 'string' || !value.trim()) throw new TypeError(`${name} is required.`);
};
const boundedText = (value, name, maximum, identifier = false) => {
  requiredText(value, name);
  if (value.length > maximum || (identifier ? /[\u0000-\u001f\u007f-\u009f]/ : /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/).test(value))
    throw new TypeError(`Invalid ${name}.`);
};
const object = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const fields = (value, allowed, name) => {
  if (!object(value) || Object.keys(value).some(key => !allowed.includes(key))) throw new TypeError(`Invalid ${name} fields.`);
};
// These are wire-byte checks. The service also measures its own escaped JSON and
// materialized optional fields; it remains authoritative for those storage limits.
const jsonBytes = value => new TextEncoder().encode(JSON.stringify(value)).length;
const validateDepth = (value, depth = 1) => {
  if (value === null || typeof value !== 'object') return;
  // One additional object level belongs to either the WebSocket or recovery envelope.
  if (depth > 31) throw new Error('Host result exceeds the protocol nesting bound.');
  for (const child of Object.values(value)) validateDepth(child, depth + 1);
};

function validateObservation(observation, revision) {
  fields(observation, ['observationId', 'revision', 'observedAt', 'data', 'facts'], 'observation');
  boundedText(observation.observationId, 'observationId', 128, true);
  if (observation.revision !== revision || !Number.isSafeInteger(observation.revision) || observation.revision < 0)
    throw new Error('Observation must describe the resulting revision.');
  if (typeof observation.observedAt !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(observation.observedAt)
    || !Number.isFinite(Date.parse(observation.observedAt)) || Date.parse(observation.observedAt) === -62135596800000)
    throw new Error('Observation requires a valid timestamp.');
  if (!object(observation.data) || jsonBytes(observation) > 65536) throw new Error('Invalid or oversized observation data.');
  if (observation.facts != null && (!Array.isArray(observation.facts) || observation.facts.length > 64)) throw new Error('Invalid observation facts.');
  const keys = new Set();
  for (const fact of observation.facts ?? []) {
    fields(fact, ['key', 'summary', 'value', 'state', 'evidenceObservationIds', 'supersedes'], 'fact');
    boundedText(fact.key, 'fact key', 256); boundedText(fact.summary, 'fact summary', 1024);
    if (keys.has(fact.key)) throw new Error('Observation fact keys must be unique.');
    keys.add(fact.key);
    if (!Object.hasOwn(fact, 'value') || jsonBytes(fact.value) > 8192 ||
      !['observed', 'inferred', 'supported', 'refuted', 'stale'].includes(Object.hasOwn(fact, 'state') ? fact.state : 'observed')) throw new Error('Invalid observation fact.');
    if (fact.evidenceObservationIds != null && (!Array.isArray(fact.evidenceObservationIds) || fact.evidenceObservationIds.length > 8 ||
      fact.evidenceObservationIds.some(id => typeof id !== 'string'))) throw new Error('Invalid fact evidence references.');
    if (fact.supersedes != null && typeof fact.supersedes !== 'string') throw new Error('Invalid superseded fact identity.');
  }
}

function validateOperationUsage(usage) {
  if (usage != null && (!object(usage) || jsonBytes(usage) > 8192)) throw new Error('Invalid or oversized operation usage.');
}

function validateOperationEvent(event) {
  validateDepth(event);
  fields(event, ['hostId', 'sessionId', 'sessionEpoch', 'actionId', 'operationId', 'sequence', 'eventId', 'kind',
    'observation', 'summary', 'usage', 'completion', 'nextSessionEpoch'], 'operation event');
  for (const field of ['hostId', 'sessionId', 'sessionEpoch', 'actionId', 'operationId', 'eventId']) boundedText(event[field], field, 128, true);
  if (!Number.isSafeInteger(event.sequence) || event.sequence < 1) throw new Error('Operation sequence must be a positive safe integer.');
  if (!['progress', 'transfer', 'completed', 'blocked', 'cancelled', 'decision'].includes(event.kind)) throw new Error('Invalid operation event kind.');
  boundedText(event.summary, 'operation summary', 4000);
  validateObservation(event.observation, event.observation?.revision);
  validateOperationUsage(event.usage);
  if (event.kind === 'transfer') {
    boundedText(event.nextSessionEpoch, 'nextSessionEpoch', 128, true);
    if (event.nextSessionEpoch === event.sessionEpoch) throw new Error('Operation transfer requires a new epoch.');
  } else if (event.nextSessionEpoch != null) throw new Error('Only transfer may change the operation epoch.');
  if (event.kind === 'completed' && event.completion == null) throw new Error('A completed event requires objective completion evidence.');
  if (event.completion != null) {
    fields(event.completion, ['objectiveId', 'evidenceId', 'summary'], 'operation completion');
    if (event.kind !== 'completed') throw new Error('Only a completed operation event may carry completion evidence.');
    boundedText(event.completion.objectiveId, 'completion objectiveId', 128, true);
    boundedText(event.completion.evidenceId, 'completion evidenceId', 128, true);
    boundedText(event.completion.summary, 'completion summary', 4000);
  }
  if (jsonBytes(event) > 262144) throw new Error('Operation event exceeds the protocol size bound.');
}

const operationKey = value => canonicalJson([value.hostId, value.sessionId, value.actionId, value.operationId]);

export function actionKey(action) {
  return canonicalJson([action.sessionId, action.sessionEpoch, action.runId, action.actionId]);
}

export function actionFingerprint(action) {
  return canonicalJson({ actionId: action.actionId, runId: action.runId,
    runnerRunId: action.runnerRunId, stepId: action.stepId, sessionId: action.sessionId,
    sessionEpoch: action.sessionEpoch, capabilityId: action.capabilityId,
    manifestHash: action.manifestHash, arguments: action.arguments,
    expectedRevision: action.expectedRevision, deadlineAt: action.deadlineAt });
}

/**
 * loadAction(key) and saveAction(key, record) must use the host's durable action store.
 * Reserve before effects; atomically commit the effect and result in onAction where possible.
 * A pending record is never executed again, even after reconnect or process restart.
 * The in-memory fallback only protects this client instance; it is not restart durability.
 */
export class AgenticaHost {
  constructor({ url, onAction, loadAction, saveAction, reconcileAction, onMessage = () => {}, onStatus = () => {},
    WebSocketImpl = globalThis.WebSocket, maxMessages = 256, maxActions = 2048, maxQueuedActions = 64,
    maxMessageBytes = 262144, requestTimeoutMs = 15000 } = {}) {
    if (!url) throw new TypeError('A WebSocket URL is required.');
    if (typeof onAction !== 'function') throw new TypeError('onAction is required.');
    if (reconcileAction !== undefined && typeof reconcileAction !== 'function') throw new TypeError('reconcileAction must be a function.');
    if (!!loadAction !== !!saveAction) throw new TypeError('Supply both action persistence hooks.');
    for (const [name, value] of Object.entries({ maxMessages, maxActions, maxQueuedActions, maxMessageBytes, requestTimeoutMs })) {
      if (!Number.isSafeInteger(value) || value < 1) throw new TypeError(`${name} must be a positive integer.`);
    }
    Object.assign(this, { url, onAction, loadAction, saveAction, reconcileAction, onMessage, onStatus,
      WebSocketImpl, maxMessages, maxActions, maxQueuedActions, maxMessageBytes, requestTimeoutMs });
    this.messages = [];
    this.droppedMessages = 0;
    this.actions = new Map();
    this.pending = new Map();
    this.runs = new Map();
    this.operations = new Map();
    this.stoppedRuns = new Set();
    this.actionQueue = Promise.resolve();
    this.socket = null;
    this.connecting = null;
    this.counter = 0;
    this.queuedActions = 0;
  }

  get connected() { return this.socket?.readyState === 1; }

  _status(state, detail) {
    try { this.onStatus({ state, detail }); } catch { /* Observers do not own execution. */ }
  }

  connect() {
    if (this.connected) return Promise.resolve();
    if (this.connecting) return this.connecting;
    const socket = new this.WebSocketImpl(this.url);
    this.socket = socket;
    this._status('connecting');
    this.connecting = new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        reject(new Error('Connection timed out.'));
        socket.close();
      }, this.requestTimeoutMs);
      socket.addEventListener('open', () => {
        clearTimeout(timer);
        this._status('connected');
        resolve();
      });
      socket.addEventListener('message', event => { if (this.socket === socket) this._receive(event.data); });
      socket.addEventListener('error', () => {
        clearTimeout(timer);
        reject(new Error('WebSocket connection failed.'));
        this._status('connection.error');
        socket.close();
      });
      socket.addEventListener('close', () => {
        clearTimeout(timer);
        if (this.socket !== socket) return;
        this.socket = null;
        this.connecting = null;
        const error = new Error('Disconnected; request outcome may be unresolved. Reconnect and reconcile.');
        for (const request of this.pending.values()) { clearTimeout(request.timer); request.reject(error); }
        this.pending.clear();
        this._status('disconnected');
        reject(error);
      });
    }).finally(() => { this.connecting = null; });
    return this.connecting;
  }

  close() { this.socket?.close(); }

  _send(message) {
    if (!this.connected) throw new Error('Host is disconnected.');
    const text = JSON.stringify({ protocolVersion: PROTOCOL_VERSION, ...message });
    if (new TextEncoder().encode(text).length > this.maxMessageBytes) throw new Error('Outgoing protocol message exceeds limit.');
    this.socket.send(text);
  }

  /** Request IDs correlate control responses. Timeouts never automatically repeat a request. */
  async request(type, payload, { runId, timeoutMs = this.requestTimeoutMs, binding } = {}) {
    await this.connect();
    const requestId = `request-${Date.now()}-${++this.counter}`;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(requestId);
        reject(new Error(`${type} response timed out; outcome is unresolved. Do not automatically repeat it.`));
      }, timeoutMs);
      this.pending.set(requestId, { resolve, reject, timer, binding, type });
      try { this._send({ type, payload, requestId, ...(runId ? { runId } : {}) }); }
      catch (error) { clearTimeout(timer); this.pending.delete(requestId); reject(error); }
    });
  }

  async start(request) {
    for (const field of ['hostId', 'sessionId', 'sessionEpoch', 'scopeId', 'perspectiveId', 'objectiveId', 'objective']) requiredText(request[field], field);
    const binding = { sessionId: request.sessionId, sessionEpoch: request.sessionEpoch,
      objectiveId: request.objectiveId, ...(request.capabilities ? { capabilities: copy(request.capabilities) } : {}) };
    const response = await this.request('start', { ...request, protocolVersion: PROTOCOL_VERSION }, { binding });
    const runId = response.payload.runId ?? response.runId;
    requiredText(runId, 'runId');
    this.runs.set(runId, binding);
    return { ...response.payload, runId };
  }

  async resume({ runId, sessionId, sessionEpoch }) {
    for (const [name, value] of Object.entries({ runId, sessionId, sessionEpoch })) requiredText(value, name);
    const retained = this.runs.get(runId);
    const binding = retained?.sessionId === sessionId && retained?.sessionEpoch === sessionEpoch
      ? { ...retained } : { sessionId, sessionEpoch };
    this.runs.set(runId, binding);
    return this.request('resume', { runId, sessionId, sessionEpoch }, { runId, binding });
  }

  async stop(runId) { requiredText(runId, 'runId'); this.stoppedRuns.add(runId); return this.request('cancel', { runId }, { runId }); }

  async _operationHttp(path, fetchImpl, options) {
    if (typeof fetchImpl !== 'function') throw new TypeError('fetchImpl is required.');
    const endpoint = new URL(this.url);
    endpoint.protocol = endpoint.protocol === 'wss:' ? 'https:' : 'http:';
    endpoint.pathname = path; endpoint.search = ''; endpoint.hash = '';
    if (options?.query) for (const [key, value] of Object.entries(options.query)) endpoint.searchParams.set(key, value);
    const response = await fetchImpl(endpoint.href, options?.body === undefined ? undefined : {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(options.body),
    });
    const raw = await response.text();
    if (new TextEncoder().encode(raw).length > 4194304) throw new Error('Operation response exceeds the supported size.');
    const body = raw ? JSON.parse(raw) : null;
    if (!response.ok) throw Object.assign(new Error(body?.message ?? `Operation request failed (${response.status}).`), { code: body?.code });
    return body;
  }

  _operationEntry(entry, hostId, sessionId) {
    if (!object(entry) || !object(entry.request) || !object(entry.action)
      || entry.request.hostId !== hostId || entry.request.sessionId !== sessionId || entry.action.sessionId !== sessionId
      || entry.action.sessionEpoch !== entry.request.sessionEpoch
      || !Array.isArray(entry.request.capabilities)
      || !['reserved', 'parked', 'waking', 'continued', 'completed', 'blocked', 'cancelled'].includes(entry.state)) throw new Error('Invalid operation inventory entry.');
    for (const name of ['hostId', 'sessionId', 'sessionEpoch', 'scopeId', 'perspectiveId', 'objectiveId']) boundedText(entry.request[name], name, 128, true);
    for (const name of ['actionId', 'runId', 'runnerRunId', 'stepId', 'sessionId', 'sessionEpoch', 'capabilityId', 'manifestHash']) boundedText(entry.action[name], name, 128, true);
    boundedText(entry.activeEpoch, 'activeEpoch', 128, true);
    if (entry.admission != null) {
      this._validateResult(entry.action, entry.admission, entry.request);
      if (!entry.admission.operation) throw new Error('Operation inventory is missing its admission.');
    }
    return copy(entry);
  }

  _rememberOperation(entry) {
    if (!entry.admission?.operation) return;
    const key = operationKey({ ...entry.request, actionId: entry.action.actionId, operationId: entry.admission.operation.operationId });
    if (this.operations.has(key) || this.operations.size < this.maxActions) this.operations.set(key, copy(entry));
  }

  /** Read durable operation state explicitly. This never polls, executes an effect, or activates cognition. */
  async listOperations({ hostId, sessionId, fetchImpl = globalThis.fetch } = {}) {
    boundedText(hostId, 'hostId', 128, true); boundedText(sessionId, 'sessionId', 128, true);
    const entries = await this._operationHttp('/api/operations', fetchImpl, { query: { hostId, sessionId } });
    if (!Array.isArray(entries) || entries.length > this.maxActions) throw new Error('Invalid or oversized operation inventory.');
    const checked = entries.map(entry => this._operationEntry(entry, hostId, sessionId));
    for (const entry of checked) this._rememberOperation(entry);
    return checked;
  }

  /** Deliver one immutable progress, transfer, or terminal event. Never invokes the planner. */
  async signalOperation(event, { fetchImpl = globalThis.fetch } = {}) {
    event = copy(event); validateOperationEvent(event);
    if (event.kind === 'decision') throw new Error('A decision event must use wakeOperation.');
    return this._operationHttp('/api/operations/events', fetchImpl, { body: event });
  }

  /** Request one new bounded cognition window. The host persists the event first; no automatic retry occurs. */
  async wakeOperation(event) {
    event = copy(event); validateOperationEvent(event);
    if (event.kind !== 'decision') throw new Error('Only a decision event may wake cognition.');
    const original = this.operations.get(operationKey(event))?.request;
    const binding = { hostId: event.hostId, sessionId: event.sessionId, sessionEpoch: event.sessionEpoch,
      ...(original ? { objectiveId: original.objectiveId, capabilities: copy(original.capabilities) } : {}) };
    const response = await this.request('operation.wake', event, { binding });
    return { ...response.payload, runId: response.payload.runId ?? response.runId };
  }

  /**
   * Reconcile durable custody after a service restart. This never invokes onAction.
   * reconcile(action, retainedRecord, entry) may inspect authoritative host state to
   * return the original result. It must never execute or repeat the requested effect.
   */
  async recover({ hostId, sessionId, reconcile, fetchImpl = globalThis.fetch } = {}) {
    requiredText(hostId, 'hostId'); requiredText(sessionId, 'sessionId');
    const endpoint = new URL(this.url); endpoint.protocol = endpoint.protocol === 'wss:' ? 'https:' : 'http:';
    endpoint.pathname = '/api/recovery'; endpoint.search = '';
    const read = async (url, options) => {
      const response = await fetchImpl(url, options);
      const raw = await response.text();
      if (new TextEncoder().encode(raw).length > 4194304) throw new Error('Recovery response exceeds the supported size.');
      const body = raw ? JSON.parse(raw) : null;
      if (!response.ok) throw Object.assign(new Error(body?.message ?? `Recovery request failed (${response.status}).`), { code: body?.code });
      return body;
    };
    const query = new URL(endpoint); query.searchParams.set('hostId', hostId); query.searchParams.set('sessionId', sessionId);
    const entries = await read(query.href);
    if (!Array.isArray(entries) || entries.length > this.maxActions) throw new Error('Invalid or oversized recovery inventory.');
    const outcomes = [];
    for (const entry of entries) {
      const action = entry.request;
      const identity = { actionId: action?.actionId, runId: action?.runId, sessionEpoch: action?.sessionEpoch };
      try {
        if (!action || entry.hostId !== hostId || entry.sessionId !== sessionId || action.sessionId !== sessionId || (entry.runId && entry.runId !== action.runId) || (entry.sessionEpoch && entry.sessionEpoch !== action.sessionEpoch)) throw new Error('Recovery entry does not match the requested host/session identity.');
        for (const field of ['actionId', 'runId', 'runnerRunId', 'stepId', 'sessionId', 'sessionEpoch', 'capabilityId', 'manifestHash']) requiredText(action[field], field);
        const retained = await this._retainedAction(action, true, entry);
        const inspect = reconcile ?? this.reconcileAction;
        let result = this._resolved(retained) ? retained.result : await inspect?.(copy(action), retained ? copy(retained) : null, copy(entry));
        if (!result || result.disposition === 'unresolved') { outcomes.push({ ...identity, status: 'unresolved', resolved: false }); continue; }
        result = await this._persistResolvedAction(action, result, entry);
        const response = await read(endpoint.href, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ hostId, sessionId, result }) });
        if (response.actionId !== action.actionId) throw new Error('Recovery response action identity differs.');
        outcomes.push({ ...identity, status: response.resolved ? 'resolved' : 'unresolved', resolved: response.resolved === true, duplicate: response.duplicate === true });
      } catch (error) {
        outcomes.push({ ...identity, status: error.code === 'recovery.live_run' ? 'live_run' : 'error', resolved: false, code: error.code ?? null, message: error.message });
      }
    }
    return outcomes;
  }

  _receive(text) {
    let message;
    try {
      if (typeof text !== 'string' || new TextEncoder().encode(text).length > this.maxMessageBytes) throw new Error('Incoming message is too large or is not text.');
      message = JSON.parse(text);
      if (message.protocolVersion !== PROTOCOL_VERSION || typeof message.type !== 'string' || !message.payload || typeof message.payload !== 'object') throw new Error('Invalid service protocol envelope.');
    } catch (error) { this._status('protocol.error', error.message); return; }
    this.messages.push(copy(message));
    if (this.messages.length > this.maxMessages) { this.messages.shift(); this.droppedMessages++; }
    if (['outcome', 'run.terminated', 'cancelled', 'cancel.requested'].includes(message.type) && message.runId) this.stoppedRuns.add(message.runId);
    const pending = message.requestId && this.pending.get(message.requestId);
    if (pending) {
      clearTimeout(pending.timer);
      this.pending.delete(message.requestId);
      if (message.type === 'error') pending.reject(Object.assign(new Error(message.payload.message ?? message.payload.error ?? 'Service rejected request.'), { code: message.payload.code }));
      else {
        if (pending.type === 'operation.wake' && (message.type !== 'started'
          || typeof (message.payload.runId ?? message.runId) !== 'string'
          || !message.payload.objectiveId || message.payload.hostId !== pending.binding.hostId
          || message.payload.sessionId !== pending.binding.sessionId || message.payload.sessionEpoch !== pending.binding.sessionEpoch
          || (pending.binding.objectiveId !== undefined && message.payload.objectiveId !== pending.binding.objectiveId))) {
          pending.reject(new Error('Operation wake response binding differs from the requested host session.'));
          return;
        }
        if (['started', 'resumed'].includes(message.type) && pending.binding) {
          const binding = { ...pending.binding };
          if (binding.objectiveId === undefined && message.payload.objectiveId !== undefined) binding.objectiveId = message.payload.objectiveId;
          this.runs.set(message.payload.runId ?? message.runId, binding);
        }
        pending.resolve(message);
      }
    }
    if (message.type === 'operation.parked') {
      try {
        const entry = this._operationEntry(message.payload, message.payload.request?.hostId, message.payload.request?.sessionId);
        const run = this.runs.get(message.runId);
        if (entry.action.runId !== message.runId || !run || run.sessionId !== entry.request.sessionId
          || run.sessionEpoch !== entry.request.sessionEpoch || (run.objectiveId !== undefined && run.objectiveId !== entry.request.objectiveId))
          throw new Error('Parked operation run binding differs.');
        this._rememberOperation(entry);
      } catch (error) { this._status('protocol.error', error.message); return; }
    }
    try { this.onMessage(copy(message)); } catch { /* Display failures must not repeat effects. */ }
    if (message.type === 'action.request' || message.type === 'action.reconcile') {
      if (this.queuedActions >= this.maxQueuedActions) { this._status('protocol.error', 'Action queue limit reached. Reconnect to reconcile retained actions.'); this.close(); return; }
      this.queuedActions++;
      this.actionQueue = this.actionQueue.then(() => this._action(message)).catch(error => this._status('action.error', error.message)).finally(() => this.queuedActions--);
    }
  }

  _unresolved(action, summary) {
    return { actionId: action.actionId, sessionId: action.sessionId, sessionEpoch: action.sessionEpoch,
      disposition: 'unresolved', beforeRevision: action.expectedRevision, afterRevision: action.expectedRevision,
      evidenceId: `unresolved:${action.actionId}`, summary };
  }

  _resolved(record) { return record?.status === 'completed' && record.result && record.result.disposition !== 'unresolved'; }

  _cacheAction(action, record) {
    const key = actionKey(action);
    if (this.actions.has(key) || this.actions.size < this.maxActions) this.actions.set(key, copy(record));
  }

  async _retainedAction(action, refresh = false, context) {
    const fingerprint = actionFingerprint(action);
    const checked = record => {
      if (!record) return null;
      if (record.fingerprint !== fingerprint) throw new Error('Action identity was reused with changed arguments or bindings. No effect was attempted.');
      if (!['pending', 'unresolved', 'completed'].includes(record.status)) throw new Error('Invalid retained action status.');
      if (record.result) this._validateResult(action, record.result, context);
      if (record.status === 'completed' && !record.result) throw new Error('Completed custody is missing its result.');
      // Older SDK records could mark an unresolved response as completed.
      return copy(record.result?.disposition === 'unresolved' ? { ...record, status: 'unresolved' } : record);
    };
    const memory = checked(this.actions.get(actionKey(action)));
    const durable = this.loadAction && (refresh || !this._resolved(memory))
      ? checked(await this.loadAction(actionKey(action))) : null;
    if (this._resolved(memory) && this._resolved(durable) && canonicalJson(memory.result) !== canonicalJson(durable.result))
      throw new Error('A completed original action result changed in durable storage.');
    const retained = this._resolved(memory) ? memory : this._resolved(durable) ? durable
      : durable?.status === 'unresolved' ? durable : memory?.status === 'unresolved' ? memory : durable ?? memory;
    if (retained) this._cacheAction(action, retained);
    return retained;
  }

  async _persistResolvedAction(action, result, context) {
    this._validateResult(action, result, context);
    if (result.disposition === 'unresolved') throw new Error('Unresolved custody cannot be committed as completed.');
    const retained = await this._retainedAction(action, true, context);
    if (this._resolved(retained) && canonicalJson(retained.result) !== canonicalJson(result))
      throw new Error('A completed original action result cannot be replaced.');
    const record = { fingerprint: actionFingerprint(action), status: 'completed', result: copy(result) };
    this._cacheAction(action, record);
    await this.saveAction?.(actionKey(action), copy(record));
    return copy(record.result);
  }

  async _action(message) {
    const action = message.payload;
    for (const field of ['actionId', 'runId', 'runnerRunId', 'stepId', 'sessionId', 'sessionEpoch', 'capabilityId', 'manifestHash']) requiredText(action[field], field);
    if (message.runId !== action.runId || !Number.isSafeInteger(action.expectedRevision) || action.expectedRevision < 0 || typeof action.arguments !== 'object' || action.arguments === null || Array.isArray(action.arguments)) throw new Error('Invalid action identity or revision.');
    const run = this.runs.get(action.runId);
    if (!run || run.sessionId !== action.sessionId || run.sessionEpoch !== action.sessionEpoch) throw new Error('Action belongs to an unknown run or another host session.');
    const key = actionKey(action);
    const fingerprint = actionFingerprint(action);
    let record;
    let result;
    try {
      record = await this._retainedAction(action, message.type === 'action.reconcile');
      if (this._resolved(record)) {
        result = await this._persistResolvedAction(action, record.result);
      } else if (record || message.type === 'action.reconcile') {
        const inspected = message.type === 'action.reconcile'
          ? await this.reconcileAction?.(copy(action), record ? copy(record) : null) : null;
        if (inspected && inspected.disposition !== 'unresolved') result = await this._persistResolvedAction(action, inspected);
        else {
          if (inspected) this._validateResult(action, inspected);
          result = inspected ?? this._unresolved(action, 'No completed retained result exists. Reconcile authoritative host state; the action was not invoked again.');
        }
      } else if (this.stoppedRuns.has(action.runId)) {
        result = this._unresolved(action, 'Run has stopped or cancellation was requested. No effect was attempted.');
      } else if (this.actions.size >= this.maxActions) {
        result = this._unresolved(action, 'Action retention limit reached. No effect was attempted.');
      } else if (!Number.isFinite(Date.parse(action.deadlineAt)) || Date.parse(action.deadlineAt) <= Date.now()) {
        result = this._unresolved(action, 'Action deadline expired before execution. No effect was attempted.');
      } else {
        record = { fingerprint, status: 'pending' };
        this.actions.set(key, copy(record));
        await this.saveAction?.(key, copy(record));
        if (this.stoppedRuns.has(action.runId)) {
          result = this._unresolved(action, 'Run stopped while the action reservation was being saved. No effect was attempted.');
        } else if (Date.parse(action.deadlineAt) <= Date.now()) {
          result = this._unresolved(action, 'Action deadline expired while the reservation was being saved. No effect was attempted.');
        } else {
          result = await this.onAction(copy(action));
          this._validateResult(action, result);
          if (result.disposition === 'unresolved') {
            record = await this._retainedAction(action, true);
            if (this._resolved(record)) result = await this._persistResolvedAction(action, record.result);
            else this._cacheAction(action, { fingerprint, status: 'unresolved', result: copy(result) });
            // Leave the durable reservation intact. A late host commit may already be
            // writing its concrete result; uncertainty must not overwrite that result.
          } else result = await this._persistResolvedAction(action, result);
        }
      }
    } catch (error) {
      result = this._unresolved(action, 'Action outcome could not be established. Reconcile the original action before further effects.');
      this._status('action.error', error.message);
    }
    if (this.connected) this._send({ type: 'action.result', runId: action.runId, payload: result });
    else this._status('action.retained', action.actionId);
  }

  _validateResult(action, result, context) {
    // Validate the exact JSON representation that persistence and transport will retain.
    result = copy(result);
    validateDepth(result);
    fields(result, ['actionId', 'sessionId', 'sessionEpoch', 'disposition', 'beforeRevision', 'afterRevision', 'evidenceId', 'summary', 'observation', 'completion', 'operation'], 'host result');
    if (result.actionId !== action.actionId || result.sessionId !== action.sessionId || result.sessionEpoch !== action.sessionEpoch) throw new Error('Host result identity does not match the action.');
    if (!['applied', 'refused', 'conflict', 'unavailable', 'unresolved'].includes(result.disposition)) throw new Error('Unsupported action disposition.');
    if (!Number.isSafeInteger(result.beforeRevision) || !Number.isSafeInteger(result.afterRevision) || result.beforeRevision < 0 || result.afterRevision < result.beforeRevision) throw new Error('Invalid host result revisions.');
    if (['applied', 'refused'].includes(result.disposition) && result.beforeRevision !== action.expectedRevision)
      throw new Error('A stale action must be reported as a conflict.');
    if (['refused', 'conflict', 'unavailable'].includes(result.disposition) && result.beforeRevision !== result.afterRevision)
      throw new Error('A non-applied result cannot claim an effect.');
    const metadata = { ...this.runs.get(action.runId), ...context };
    const capability = metadata.capability ?? metadata.capabilities?.find(item => item.id === action.capabilityId);
    if (capability && capability.id !== action.capabilityId) throw new Error('Result capability binding differs from the original action.');
    if (capability?.effect === 'readOnly' && result.beforeRevision !== result.afterRevision)
      throw new Error('A read-only capability cannot change the host revision.');
    boundedText(result.evidenceId, 'evidenceId', 128, true); boundedText(result.summary, 'summary', 4000);
    if (['applied', 'refused', 'conflict'].includes(result.disposition) && result.observation == null)
      throw new Error('A resolved host action requires an observation.');
    if (result.observation != null) validateObservation(result.observation, result.afterRevision);
    if (result.operation != null) {
      fields(result.operation, ['operationId', 'summary', 'usage'], 'operation admission');
      if (result.disposition !== 'applied' || result.observation == null || result.completion != null
        || (capability && capability.durableHandoff !== true)
        || (metadata.capabilities && !capability)) throw new Error('Operation admission requires an applied, observed, durably bound capability without completion.');
      boundedText(result.operation.operationId, 'operationId', 128, true);
      boundedText(result.operation.summary, 'operation summary', 4000);
      validateOperationUsage(result.operation.usage);
    }
    if (result.completion != null) {
      fields(result.completion, ['objectiveId', 'evidenceId', 'summary'], 'completion');
      if (result.disposition !== 'applied' || result.observation == null ||
        (metadata.objectiveId !== undefined && result.completion.objectiveId !== metadata.objectiveId))
        throw new Error('Completion must bind an applied result to the active objective.');
      boundedText(result.completion.objectiveId, 'completion objectiveId', 128, true);
      boundedText(result.completion.evidenceId, 'completion evidenceId', 128, true);
      boundedText(result.completion.summary, 'completion summary', 4000);
    }
    if (jsonBytes(result) > 262144 || jsonBytes({ protocolVersion: PROTOCOL_VERSION, type: 'action.result', runId: action.runId, payload: result }) > this.maxMessageBytes)
      throw new Error('Host result exceeds the protocol size bound.');
  }
}
