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
  constructor({ url, onAction, loadAction, saveAction, onMessage = () => {}, onStatus = () => {},
    WebSocketImpl = globalThis.WebSocket, maxMessages = 256, maxActions = 2048, maxQueuedActions = 64,
    maxMessageBytes = 262144, requestTimeoutMs = 15000 } = {}) {
    if (!url) throw new TypeError('A WebSocket URL is required.');
    if (typeof onAction !== 'function') throw new TypeError('onAction is required.');
    if (!!loadAction !== !!saveAction) throw new TypeError('Supply both action persistence hooks.');
    for (const [name, value] of Object.entries({ maxMessages, maxActions, maxQueuedActions, maxMessageBytes, requestTimeoutMs })) {
      if (!Number.isSafeInteger(value) || value < 1) throw new TypeError(`${name} must be a positive integer.`);
    }
    Object.assign(this, { url, onAction, loadAction, saveAction, onMessage, onStatus,
      WebSocketImpl, maxMessages, maxActions, maxQueuedActions, maxMessageBytes, requestTimeoutMs });
    this.messages = [];
    this.droppedMessages = 0;
    this.actions = new Map();
    this.pending = new Map();
    this.runs = new Map();
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
      this.pending.set(requestId, { resolve, reject, timer, binding });
      try { this._send({ type, payload, requestId, ...(runId ? { runId } : {}) }); }
      catch (error) { clearTimeout(timer); this.pending.delete(requestId); reject(error); }
    });
  }

  async start(request) {
    for (const field of ['hostId', 'sessionId', 'sessionEpoch', 'scopeId', 'perspectiveId', 'objectiveId', 'objective']) requiredText(request[field], field);
    const binding = { sessionId: request.sessionId, sessionEpoch: request.sessionEpoch };
    const response = await this.request('start', { ...request, protocolVersion: PROTOCOL_VERSION }, { binding });
    const runId = response.payload.runId ?? response.runId;
    requiredText(runId, 'runId');
    this.runs.set(runId, binding);
    return { ...response.payload, runId };
  }

  async resume({ runId, sessionId, sessionEpoch }) {
    for (const [name, value] of Object.entries({ runId, sessionId, sessionEpoch })) requiredText(value, name);
    this.runs.set(runId, { sessionId, sessionEpoch });
    return this.request('resume', { runId, sessionId, sessionEpoch }, { runId });
  }

  async stop(runId) { requiredText(runId, 'runId'); this.stoppedRuns.add(runId); return this.request('cancel', { runId }, { runId }); }

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
        const key = actionKey(action); const fingerprint = actionFingerprint(action);
        const retained = this.actions.get(key) ?? await this.loadAction?.(key);
        if (retained && retained.fingerprint !== fingerprint) throw new Error('Recovery action fingerprint differs from retained host custody.');
        const result = retained?.status === 'completed' ? retained.result : await reconcile?.(copy(action), retained ? copy(retained) : null, copy(entry));
        if (!result || result.disposition === 'unresolved') { outcomes.push({ ...identity, status: 'unresolved', resolved: false }); continue; }
        this._validateResult(action, result);
        const record = { fingerprint, status: 'completed', result: copy(result) };
        if (this.actions.has(key) || this.actions.size < this.maxActions) this.actions.set(key, record);
        await this.saveAction?.(key, copy(record));
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
        if (message.type === 'started' && pending.binding) this.runs.set(message.payload.runId ?? message.runId, pending.binding);
        pending.resolve(message);
      }
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

  async _action(message) {
    const action = message.payload;
    for (const field of ['actionId', 'runId', 'runnerRunId', 'stepId', 'sessionId', 'sessionEpoch', 'capabilityId', 'manifestHash']) requiredText(action[field], field);
    if (message.runId !== action.runId || !Number.isSafeInteger(action.expectedRevision) || action.expectedRevision < 0 || typeof action.arguments !== 'object' || action.arguments === null || Array.isArray(action.arguments)) throw new Error('Invalid action identity or revision.');
    const run = this.runs.get(action.runId);
    if (!run || run.sessionId !== action.sessionId || run.sessionEpoch !== action.sessionEpoch) throw new Error('Action belongs to an unknown run or another host session.');
    const key = actionKey(action);
    const fingerprint = actionFingerprint(action);
    let record = this.actions.get(key);
    let result;
    try {
      record ??= await this.loadAction?.(key);
      if (record && record.fingerprint !== fingerprint) {
        result = this._unresolved(action, 'Action identity was reused with changed arguments or bindings. No effect was attempted.');
      } else if (record?.status === 'completed' && record.result) {
        this._validateResult(action, record.result);
        result = copy(record.result);
      } else if (record || message.type === 'action.reconcile') {
        result = this._unresolved(action, 'No completed retained result exists. Reconcile authoritative host state; the action was not invoked again.');
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
        if (this.stoppedRuns.has(action.runId)) throw new Error('Run stopped while the action reservation was being saved.');
        result = await this.onAction(copy(action));
        this._validateResult(action, result);
        record = { fingerprint, status: 'completed', result: copy(result) };
        this.actions.set(key, record);
        try { await this.saveAction?.(key, copy(record)); }
        catch (error) { this._status('persistence.error', `Result is known in this session but durable storage failed: ${error.message}`); }
      }
    } catch (error) {
      result = this._unresolved(action, `Action outcome could not be established: ${error.message}`);
      this._status('action.error', error.message);
    }
    if (this.connected) this._send({ type: 'action.result', runId: action.runId, payload: result });
    else this._status('action.retained', action.actionId);
  }

  _validateResult(action, result) {
    if (!result || result.actionId !== action.actionId || result.sessionId !== action.sessionId || result.sessionEpoch !== action.sessionEpoch) throw new Error('Host result identity does not match the action.');
    if (!['applied', 'refused', 'conflict', 'unavailable', 'unresolved'].includes(result.disposition)) throw new Error('Unsupported action disposition.');
    if (!Number.isSafeInteger(result.beforeRevision) || !Number.isSafeInteger(result.afterRevision) || result.beforeRevision < 0 || result.afterRevision < result.beforeRevision) throw new Error('Invalid host result revisions.');
    requiredText(result.evidenceId, 'evidenceId'); requiredText(result.summary, 'summary');
    if (result.observation && result.observation.revision !== result.afterRevision) throw new Error('Observation must describe the resulting revision.');
  }
}
