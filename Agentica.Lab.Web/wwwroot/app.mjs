import { AgenticaHost, actionKey, actionFingerprint } from './sdk/agentica-host.mjs';

const $ = id => document.getElementById(id);
const state = { runs: [], providers: [], selected: null, events: [], droppedEvents: 0, source: null, summary: '', callId: null, client: null, busy: false };
const openaiSettings = { value: null, modelDirty: false, effortDirty: false, busy: false, generation: 0 };
let sampleThinkingOverride = false;
const terminal = value => ['completed', 'succeeded', 'planinvalid', 'partiallycomplete', 'waitingforapproval', 'failed', 'cancelled', 'canceled', 'stopped', 'indeterminate', 'blocked', 'timedout'].includes(String(value).toLowerCase());
const json = value => JSON.stringify(value ?? null, null, 2);
const text = (id, value) => { $(id).textContent = value ?? ''; };
const node = (tag, className, value) => { const element = document.createElement(tag); if (className) element.className = className; if (value !== undefined) element.textContent = value; return element; };
const label = value => String(value ?? 'unknown').replaceAll('_', ' ').replaceAll('.', ' ');
const statusClass = status => /completed|succeeded|accepted/i.test(status) ? 'success' : /fail|cancel|indeterminate/i.test(status) ? 'error' : /disconnect|pending|blocked|unresolved/i.test(status) ? 'warning' : /running|planning|active/i.test(status) ? 'running' : 'neutral';
const badge = value => node('span', `badge ${statusClass(value)}`, label(value));
function notice(message) { $('notice').hidden = !message; text('notice', message); }
async function api(path, options) {
  const response = await fetch(path, options);
  if (!response.ok) { let details = ''; try { const body = await response.json(); details = body.message ?? body.error ?? ''; } catch {} throw new Error(`${response.status} ${response.statusText}${details ? `: ${details}` : ''}`); }
  return response.status === 204 ? null : response.json();
}

async function refresh() {
  const settingsGeneration = openaiSettings.generation;
  const results = await Promise.allSettled([api('/api/runs'), api('/api/providers'), openaiSettings.busy ? Promise.resolve(null) : api('/api/providers/openai/settings')]);
  if (results[0].status === 'fulfilled') {
    state.runs = results[0].value;
    renderRuns(); text('service-status', 'Service online'); $('service-dot').className = 'status-dot live';
    if (state.selected) await refreshSelected();
  } else { text('service-status', 'Service unavailable'); $('service-dot').className = 'status-dot'; notice(`Cannot reach the Lab service. ${results[0].reason.message}`); }
  if (results[1].status === 'fulfilled' && settingsGeneration === openaiSettings.generation) { state.providers = results[1].value; renderProviders(); }
  if (results[2].status === 'fulfilled' && results[2].value && settingsGeneration === openaiSettings.generation) renderOpenaiSettings(results[2].value);
  else if (results[2].status === 'rejected' && !openaiSettings.value && settingsGeneration === openaiSettings.generation) text('openai-credential-state', 'Credential status unavailable. Refresh to retry.');
}

function renderRuns() {
  state.runs.sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt));
  text('total-runs', state.runs.length); text('run-count', state.runs.length);
  text('active-runs', state.runs.filter(run => !terminal(run.status)).length);
  text('connected-runs', state.runs.filter(run => run.connected).length);
  text('pending-total', state.runs.reduce((sum, run) => sum + (run.pendingActions?.length ?? 0), 0));
  if (!state.runs.length) return;
  $('run-list').replaceChildren(...state.runs.map(run => {
    const item = node('button', `run-item${state.selected === run.runId ? ' selected' : ''}`);
    item.setAttribute('aria-pressed', String(state.selected === run.runId));
    item.append(badge(run.status), node('span', 'run-item-title', run.objective));
    const meta = node('span', 'run-item-meta'); meta.append(node('span', '', run.hostId), node('span', '', run.connected ? 'Host connected' : 'Host disconnected')); item.append(meta);
    item.addEventListener('click', () => selectRun(run.runId));
    return item;
  }));
}

function renderProviders() {
  $('provider-list').replaceChildren(...state.providers.map(provider => {
    const card = node('div', 'provider-card'); const id = provider.provider;
    card.append(node('h3', '', id === 'demo' ? 'Demo / deterministic fixture' : label(id)), badge(id === 'demo' ? 'fixture' : provider.configured ? 'configured' : 'not configured'));
    card.append(node('p', '', `${provider.api ?? 'Provider adapter'} · ${provider.streams ? 'Streaming' : 'Streaming unavailable'}`));
    card.append(node('p', '', provider.configurationIssue ?? (id === 'demo' ? 'Runs the real loop with scripted provider output. No live model.' : 'Live verification is not established by configuration.')));
    return card;
  }));
  const previous = $('sample-provider').value;
  $('sample-provider').replaceChildren(...state.providers.map(provider => {
    const option = node('option', '', provider.provider === 'demo' ? 'Demo · deterministic fixture' : `${label(provider.provider)}${provider.configured ? '' : ' · not configured'}`);
    option.value = provider.provider; option.disabled = !provider.configured; return option;
  }));
  if (state.providers.some(p => p.provider === previous)) $('sample-provider').value = previous;
  else $('sample-provider').value = 'demo';
  syncProviderControls();
}

function syncProviderControls(resetTransport = false) {
  const provider = $('sample-provider').value;
  const metadata = state.providers.find(p => p.provider === provider);
  const gemini = provider === 'gemini' || provider === 'google';
  $('gemini-transport-field').hidden = !gemini;
  $('sample-gemini-api').disabled = !gemini;
  if (resetTransport) $('sample-gemini-api').value = 'interactions';
  $('sample-model').placeholder = metadata?.defaultModel ?? 'Provider default';
  $('sample-reasoning-field').hidden = provider !== 'openai';
  $('sample-thinking-effort').disabled = provider !== 'openai';
  const defaultEffort = provider === 'openai' && openaiSettings.value ? openaiSettings.value.thinkingEffort : metadata && Object.hasOwn(metadata, 'defaultThinkingEffort') ? metadata.defaultThinkingEffort : metadata ? null : 'high';
  if (resetTransport || !sampleThinkingOverride) $('sample-thinking-effort').value = defaultEffort ?? '';
  text('sample-thinking-source', sampleThinkingOverride ? 'per-run override' : 'saved default');
}

function renderOpenaiSettings(settings) {
  openaiSettings.value = settings;
  if (!openaiSettings.modelDirty) $('openai-model').value = settings.model;
  if (!openaiSettings.effortDirty) $('openai-thinking-effort').value = settings.thinkingEffort ?? '';
  const source = { serviceMemory: 'Service memory · cleared when the service restarts', environment: 'Service environment', none: 'No credential configured' }[settings.credentialSource] ?? 'Unavailable';
  text('openai-credential-state', `Credential: ${source}. ${settings.configured ? 'Configured; live verification is pending.' : 'Add a key or configure the service environment.'}`);
  syncProviderControls();
}

async function saveOpenaiSettings(credentialAction) {
  if (openaiSettings.busy || !$('openai-settings-form').reportValidity()) return;
  const payload = { model: $('openai-model').value.trim(), thinkingEffort: $('openai-thinking-effort').value || null, credentialAction };
  if (credentialAction === 'set') payload.apiKey = $('openai-api-key').value;
  // Retain a submitted credential only for this request, never in browser persistence or run configuration.
  $('openai-api-key').value = '';
  openaiSettings.busy = true; openaiSettings.generation++;
  const controls = [...$('openai-settings-form').querySelectorAll('input, select, button')];
  controls.forEach(control => { control.disabled = true; });
  text('openai-settings-status', 'Saving configuration…');
  try {
    const settings = await api('/api/providers/openai/settings', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
    openaiSettings.modelDirty = false; openaiSettings.effortDirty = false;
    renderOpenaiSettings(settings);
    const provider = state.providers.find(provider => provider.provider === 'openai');
    if (provider) { provider.defaultModel = settings.model; provider.defaultThinkingEffort = settings.thinkingEffort; provider.configured = settings.configured; if (settings.configured) provider.configurationIssue = null; }
    renderProviders();
    text('openai-settings-status', 'Settings saved for new runs. No provider call was made.');
  } catch {
    // Do not render a server error that could contain submitted credential material.
    text('openai-settings-status', 'Could not save settings. Check the model, reasoning effort and credential, then retry. No run was started.');
  } finally {
    $('openai-api-key').value = '';
    delete payload.apiKey;
    openaiSettings.busy = false; openaiSettings.generation++;
    controls.forEach(control => { control.disabled = false; });
  }
}

async function selectRun(runId) {
  state.source?.close(); state.selected = runId; state.events = []; state.droppedEvents = 0; state.summary = ''; state.callId = null;
  text('output-count', '0'); text('thought-count', '0'); text('elapsed', '0'); text('activity', 'Waiting for provider activity'); text('provider-kind', '');
  $('thought-summary').hidden = true; text('thought-summary', ''); text('telemetry-loss', 'Progress is observational. Action receipts establish effects.'); renderEvents(); renderRuns();
  $('inspector-empty').hidden = true; $('inspector-content').hidden = false;
  await refreshSelected();
  if (state.selected !== runId) return;
  const source = new EventSource(`/api/runs/${encodeURIComponent(runId)}/events`); state.source = source;
  source.onopen = () => { if (state.source === source) { text('stream-state', 'Live stream'); $('stream-state').className = 'badge success'; } };
  source.onerror = () => { if (state.source === source) { text('stream-state', 'Stream disconnected'); $('stream-state').className = 'badge warning'; $('thinking-dot').className = 'status-dot'; } };
  source.onmessage = event => {
    try { const message = JSON.parse(event.data); handleEvent(message); }
    catch { notice('The service sent an unreadable progress event. Execution state will be refreshed.'); }
  };
}

async function refreshSelected() {
  const runId = state.selected;
  try {
    const run = await api(`/api/runs/${encodeURIComponent(runId)}`);
    if (state.selected !== runId) return;
    text('objective', run.objective); text('run-identity', run.runId);
    $('cancel-run').disabled = terminal(run.status);
    const entries = [['Host / session', `${run.hostId} / ${run.sessionId}`], ['Status / connection', `${label(run.status)} · ${run.connected ? 'host connected' : 'host disconnected'}`], ['Session epoch', run.sessionEpoch], ['Objective identity', run.objectiveId]];
    $('identity-grid').replaceChildren(...entries.map(([key, value]) => { const entry = node('div', '', value); entry.prepend(node('span', '', key)); return entry; }));
    text('context-json', json(run.context)); text('actions-json', json(run.pendingActions ?? [])); text('outcome-json', run.outcome ? json(run.outcome) : run.termination ? json(run.termination) : 'No terminal outcome.');
    if (run.termination) text('activity', `${label(run.termination.status)} · ${run.termination.message ?? 'Run terminated.'}`);
    if (terminal(run.status)) { $('thinking-dot').className = 'status-dot'; $('start-sample').disabled = state.busy; }
  } catch (error) { notice(`Could not refresh the selected run: ${error.message}`); }
}

function handleEvent(message) {
  if (message.payload?.truncated) text('telemetry-loss', 'This display event was truncated. The current run snapshot retains the full execution evidence.');
  if (message.type === 'progress') {
    const payload = message.payload; const record = payload.record ?? payload;
    if (record.callId !== state.callId) { state.callId = record.callId; state.summary = ''; }
    if (record.thoughtSummaryDelta) state.summary = (state.summary + record.thoughtSummaryDelta).slice(-8192);
    text('activity', record.activity ?? label(record.kind)); text('provider-kind', record.provider);
    text('output-count', record.outputCharacters ?? 0); text('thought-count', record.thoughtSummaryCharacters ?? 0); text('elapsed', record.elapsedMs ?? 0);
    $('thinking-dot').className = /completed|failed|cancelled/.test(record.kind) ? 'status-dot live' : 'status-dot busy';
    $('thought-summary').hidden = !state.summary; text('thought-summary', state.summary);
    if (payload.droppedRecords || record.thoughtSummaryTruncated) text('telemetry-loss', `${payload.droppedRecords ?? 0} provider progress records dropped. ${record.thoughtSummaryTruncated ? 'Summary was truncated.' : ''} Effects are tracked separately by receipts.`);
    if (!['started', 'completed', 'failed', 'cancelled'].includes(record.kind)) return;
  }
  if (message.type === 'telemetry.gap') text('telemetry-loss', `Some progress is no longer retained. ${json(message.payload)} Effects are tracked separately by receipts.`);
  state.events.unshift({ ...message, receivedAt: new Date() });
  if (state.events.length > 150) { state.events.pop(); state.droppedEvents++; }
  renderEvents();
  if (['outcome', 'run.terminated', 'action.request', 'action.accepted', 'action.reconcile', 'execution.event'].includes(message.type)) void refreshSelected();
  if (['outcome', 'run.terminated'].includes(message.type)) {
    $('thinking-dot').className = 'status-dot';
    if (message.type === 'run.terminated') text('activity', `${label(message.payload.status)} · ${message.payload.message ?? 'Run terminated.'}`);
    void refresh(); renderSample();
  }
}

function renderEvents() {
  $('event-list').replaceChildren(...state.events.map(event => {
    const row = node('li'); row.append(node('time', '', event.receivedAt.toLocaleTimeString([], { hour12: false })));
    const value = event.payload;
    const detail = node('div'); detail.append(node('strong', '', label(event.type === 'execution.event' ? value.type ?? event.type : event.type)));
    const summary = value.summary ?? value.message ?? value.report?.summary ?? value.data?.reason?.summary ?? value.data?.context?.summary ?? value.record?.kind ?? value.status ?? value.disposition ?? value.capabilityId ?? (typeof value.connected === 'boolean' ? `Host ${value.connected ? 'connected' : 'disconnected'}.` : value.actionId ? `Action ${value.actionId}` : event.type === 'execution.event' ? 'Runtime evidence recorded.' : JSON.stringify(value));
    detail.append(node('p', '', String(summary).slice(0, 350))); row.append(detail); return row;
  }));
  text('event-retention', `${state.events.length} retained display events${state.droppedEvents ? ` · ${state.droppedEvents} older events omitted` : ''}. Provider deltas update the counters above.`);
}

// The example is intentionally local. A production host should use its authoritative
// transactional store for action reservation, effect, result and world revision.
const STORAGE_KEY = 'agentica.lab.scoped-inventory.v1';
function readSample() { const stored = localStorage.getItem(STORAGE_KEY); return stored ? JSON.parse(stored) : null; }
function writeSample(sample) { localStorage.setItem(STORAGE_KEY, JSON.stringify(sample)); }
function freshSample() {
  return { sessionId: crypto.randomUUID(), sessionEpoch: crypto.randomUUID(), revision: 0, objectiveId: crypto.randomUUID(), runId: null, actions: {},
    items: [{ id: 'sample-1', label: 'Eligible record', status: 'pending', eligible: true }, { id: 'sample-2', label: 'Deferred record', status: 'pending', eligible: false }] };
}
function observation(sample) {
  const observationId = `${sample.sessionId}:r${sample.revision}:${crypto.randomUUID()}`;
  return { observationId, revision: sample.revision, observedAt: new Date().toISOString(),
    data: { items: sample.items }, facts: sample.items.map(item => ({ key: item.id, summary: `${item.label} is ${item.status}.`, value: item, state: 'observed', evidenceObservationIds: [observationId] })) };
}
async function sampleLock(callback) {
  if (!navigator.locks) throw new Error('This sample needs the Web Locks API to keep one browser writer. Use localhost or another supported origin.');
  return navigator.locks.request(STORAGE_KEY, callback);
}
function renderSample() {
  try {
    const sample = readSample(); const items = sample?.items ?? freshSample().items;
    $('inventory').replaceChildren(...items.map(item => { const element = node('div', `inventory-item ${item.status}`, item.label); element.append(node('span', '', `${item.status}${item.eligible ? ' · eligible' : ' · ineligible'}`)); return element; }));
    $('resume-sample').disabled = !sample?.runId || state.client?.connected || state.busy;
  } catch (error) { notice(`Sample storage is unavailable: ${error.message}`); }
}

function createSampleClient() {
  state.client?.close();
  const client = new AgenticaHost({ url: `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/host`,
    loadAction: key => sampleLock(() => readSample()?.actions[key]),
    saveAction: (key, record) => sampleLock(() => { const sample = readSample(); if (!sample) throw new Error('Sample session no longer exists.'); sample.actions[key] = record; writeSample(sample); }),
    onAction: action => sampleLock(() => {
      const sample = readSample();
      if (!sample || sample.sessionId !== action.sessionId || sample.sessionEpoch !== action.sessionEpoch) throw new Error('Sample session changed.');
      const result = { actionId: action.actionId, sessionId: sample.sessionId, sessionEpoch: sample.sessionEpoch, beforeRevision: sample.revision, afterRevision: sample.revision, evidenceId: `sample:${action.actionId}`, disposition: 'applied', summary: 'Inventory observed.' };
      if (action.expectedRevision !== sample.revision) { result.disposition = 'conflict'; result.summary = 'Sample revision changed; request a fresh observation.'; }
      else if (action.capabilityId === 'demo.accept') {
        const item = sample.items.find(item => item.id === action.arguments.itemId);
        if (!item?.eligible || item.status !== 'pending') { result.disposition = 'refused'; result.summary = 'The requested record is not eligible and pending.'; }
        else { item.status = 'accepted'; sample.revision++; result.afterRevision = sample.revision; result.summary = `${item.id} accepted.`; result.completion = { objectiveId: sample.objectiveId, evidenceId: result.evidenceId, summary: 'One eligible record is accepted in the authoritative browser inventory.' }; }
      } else if (action.capabilityId !== 'demo.inspect') { result.disposition = 'refused'; result.summary = 'Unknown sample capability.'; }
      result.observation = observation(sample);
      // One storage write commits both the browser state and its exact result.
      sample.actions[actionKey(action)] = { fingerprint: actionFingerprint(action), status: 'completed', result };
      writeSample(sample); renderSample(); return result;
    }),
    onStatus: status => { text('sample-state', status.detail ? `${label(status.state)} · ${status.detail}` : `Host ${label(status.state)}`); renderSample(); },
    onMessage: message => {
      if (message.type === 'started') void sampleLock(() => { const sample = readSample(); sample.runId = message.payload.runId ?? message.runId; writeSample(sample); }).catch(error => notice(error.message));
      if (message.type === 'outcome') { text('sample-state', 'Run reached a terminal outcome. Inspect its receipts above.'); renderSample(); }
      if (message.type === 'run.terminated') { text('sample-state', `${label(message.payload.status)} · ${message.payload.message ?? 'Run terminated.'}`); renderSample(); }
    }
  });
  state.client = client; return client;
}

async function recoverSample(sample, client) {
  const results = await client.recover({ hostId: 'lab-browser-sample', sessionId: sample.sessionId });
  const unresolved = results.filter(result => !result.resolved);
  if (unresolved.length) throw new Error(`Original sample actions still require reconciliation (${unresolved.length}). ${unresolved[0].message ?? 'No retained result proves the outcome.'} No new example was started.`);
  return results;
}

async function startSample(event) {
  event.preventDefault(); if (state.busy) return;
  state.busy = true; $('start-sample').disabled = true; notice('');
  try {
    const previous = readSample();
    if (previous && !previous.runId) {
      const runs = await api('/api/runs');
      const existing = runs.find(run => run.sessionId === previous.sessionId && run.sessionEpoch === previous.sessionEpoch);
      if (existing && (!terminal(existing.status) || existing.pendingActions?.length)) {
        previous.runId = existing.runId; await sampleLock(() => writeSample(previous));
        throw new Error('The previous start created an active run. Reconnect to it or cancel it before starting another.');
      }
    }
    if (previous?.runId) {
      const previousRun = await api(`/api/runs/${encodeURIComponent(previous.runId)}`).catch(error => { if (error.message.startsWith('404 ')) return null; throw error; });
      if (previousRun && (!terminal(previousRun.status) || previousRun.pendingActions?.length)) throw new Error('An example is active or has unresolved actions. Reconnect and reconcile it before starting another.');
    }
    if (previous) await recoverSample(previous, createSampleClient());
    const sample = freshSample(); await sampleLock(() => writeSample(sample)); renderSample();
    const client = createSampleClient();
    const provider = { provider: $('sample-provider').value, includeThoughtSummaries: $('include-thoughts').checked };
    if (provider.provider === 'gemini' || provider.provider === 'google') provider.geminiApi = $('sample-gemini-api').value;
    if (provider.provider === 'openai' && $('sample-thinking-effort').value) provider.thinkingEffort = $('sample-thinking-effort').value;
    if ($('sample-model').value.trim()) provider.model = $('sample-model').value.trim();
    const started = await client.start({ hostId: 'lab-browser-sample', sessionId: sample.sessionId, sessionEpoch: sample.sessionEpoch, scopeId: 'scoped-inventory', perspectiveId: 'inventory-operator', objectiveId: sample.objectiveId, objective: 'Accept one eligible record', observation: observation(sample), provider,
      capabilities: [
        { id: 'demo.inspect', name: 'Inspect inventory', description: 'Observe the current scoped inventory and revision.', kind: 'query', effect: 'readOnly', inputSchema: { fields: [], allowAdditionalProperties: false } },
        { id: 'demo.accept', name: 'Accept item', description: 'Accept one eligible pending item. The browser verifies and receipts fulfillment.', kind: 'action', effect: 'writesLocalState', inputSchema: { fields: [{ name: 'itemId', type: 'string', required: true, description: 'The item identity from the current observation.' }], allowAdditionalProperties: false } }
      ], limits: { maxSteps: 8, maxRefinements: 8, maxPlanContinuations: 4, timeoutSeconds: 90, actionTimeoutSeconds: 20, maxRecentObservations: 4, maxRecentReceipts: 4 } });
    await sampleLock(() => { const latest = readSample(); latest.runId = started.runId; writeSample(latest); });
    await refresh(); await selectRun(started.runId);
  } catch (error) { notice(error.message); }
  finally { state.busy = false; $('start-sample').disabled = false; renderSample(); }
}

$('sample-form').addEventListener('submit', startSample);
$('openai-model').addEventListener('input', () => { openaiSettings.modelDirty = true; });
$('openai-thinking-effort').addEventListener('change', () => { openaiSettings.effortDirty = true; });
$('openai-settings-form').addEventListener('invalid', () => { $('openai-api-key').value = ''; }, true);
$('openai-settings-form').addEventListener('submit', event => { event.preventDefault(); void saveOpenaiSettings($('openai-api-key').value ? 'set' : 'keep'); });
$('use-openai-environment').addEventListener('click', () => { void saveOpenaiSettings('environment'); });
$('resume-sample').addEventListener('click', async () => {
  if (state.busy) return; state.busy = true;
  try {
    const sample = readSample(); const client = createSampleClient();
    try { await client.resume({ runId: sample.runId, sessionId: sample.sessionId, sessionEpoch: sample.sessionEpoch }); await selectRun(sample.runId); }
    catch (error) {
      if (error.code !== 'run.not_found') throw error;
      await recoverSample(sample, client); client.close();
      await sampleLock(() => { const latest = readSample(); latest.runId = null; writeSample(latest); });
      text('sample-state', 'Original effects reconciled after service restart. Start a new example; the old planner conversation is not resumed.');
    }
  }
  catch (error) { notice(error.message); } finally { state.busy = false; renderSample(); }
});
$('cancel-run').addEventListener('click', async () => { try { await api(`/api/runs/${encodeURIComponent(state.selected)}/cancel`, { method: 'POST' }); await refresh(); } catch (error) { notice(error.message); } });
$('refresh').addEventListener('click', () => { notice(''); void refresh(); });
$('sample-provider').addEventListener('change', () => { $('sample-model').value = ''; sampleThinkingOverride = false; syncProviderControls(true); });
$('sample-thinking-effort').addEventListener('change', () => { sampleThinkingOverride = true; syncProviderControls(); });
const tabs = [...document.querySelectorAll('[data-pane]')];
function activateTab(tab) { for (const item of tabs) { const selected = item === tab; item.setAttribute('aria-selected', String(selected)); item.tabIndex = selected ? 0 : -1; $(`pane-${item.dataset.pane}`).hidden = !selected; } }
for (const tab of tabs) { tab.addEventListener('click', () => activateTab(tab)); tab.addEventListener('keydown', event => { if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) { event.preventDefault(); const index = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 : (tabs.indexOf(tab) + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length; activateTab(tabs[index]); tabs[index].focus(); } }); }
window.addEventListener('storage', event => { if (event.key === STORAGE_KEY) renderSample(); });
window.addEventListener('pagehide', () => { state.source?.close(); state.client?.close(); });
renderSample(); void refresh(); setInterval(() => { if (!document.hidden) void refresh(); }, 4000);
