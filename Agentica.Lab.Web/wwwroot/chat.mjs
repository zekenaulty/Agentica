// Conversation state belongs to Lab. This page never stores provider credentials or
// promotes diagnostic events into the conversation's input history.
export const isTerminal = status => !['queued', 'pending', 'running', 'active', 'starting', 'cancelling'].includes(String(status ?? '').toLowerCase());
export const depthPresets = Object.freeze({
  minimal: { maxHistoryMessages: 4, maxInputCharacters: 12000 },
  focused: { maxHistoryMessages: 12, maxInputCharacters: 32000 },
  normal: { maxHistoryMessages: 32, maxInputCharacters: 80000 },
  deep: { maxHistoryMessages: 64, maxInputCharacters: 160000 },
  exhaustive: { maxHistoryMessages: 128, maxInputCharacters: 256000 }
});
export function classifyEvent(event) {
  const type = String(event?.type ?? 'unknown');
  if (/fail|error|interrupt|cancel|gap|drop|loss/i.test(type)) return 'error';
  if (/summary|reasoning/i.test(type)) return 'summary';
  if (/tool|action|receipt|harness/i.test(type)) return 'tool';
  return 'activity';
}
export function summarizeTrace(events) {
  const ordered = [...new Map(events.map(event => [event.sequence, event])).values()].sort((a, b) => a.sequence - b.sequence);
  const groups = new Map();
  for (const event of ordered) {
    // Native trace preserves the original OpenAI event envelope. Older fixtures
    // and adapters can also supply a bare reasoning item.
    const item = event.data?.item ?? event.data;
    const delta = event.type === 'provider.summary.delta';
    const completed = event.type === 'provider.item' && item?.type === 'reasoning';
    if (!delta && !completed) continue;
    const key = `${event.callId ?? ''}|${event.itemId ?? (completed ? item.id : null) ?? ''}`;
    const group = groups.get(key) ?? { delta: '', complete: '' };
    if (delta) group.delta += event.data?.text ?? '';
    else {
      const text = (item.summary ?? []).filter(part => part.type === 'summary_text').map(part => part.text ?? '').join('\n\n');
      if (text) group.complete = text;
    }
    groups.set(key, group);
  }
  // A completed native item and streamed deltas describe the same artifact.
  // Keep one version per call/item, including completion-only summaries.
  return [...groups.values()].map(group => group.delta || group.complete).filter(Boolean).join('\n\n');
}
export const instructionValue = (original, editorValue, edited) => edited ? editorValue : original ?? '';
// Exact official IDs verified 2026-11-02. This is a fallback catalog, never an
// assertion of account access and never a version x family product.
export const knownFamilyModelIds = Object.freeze([
  'gpt-6.1-sol', 'gpt-6.1-terra', 'gpt-6-astra', 'gpt-6-sol', 'gpt-6-luna',
  'gpt-5.6-sol', 'gpt-5.6-terra', 'gpt-5.6-luna'
]);
export function familyModelCatalog(ids) {
  const versions = (a, b) => {
    const left = a.split('.').map(Number); const right = b.split('.').map(Number);
    for (let index = 0; index < Math.max(left.length, right.length); index++) {
      const delta = (right[index] ?? 0) - (left[index] ?? 0); if (delta) return delta;
    }
    return 0;
  };
  const models = [...new Set(ids)].flatMap(id => {
    if (typeof id !== 'string') return [];
    const match = /^gpt-(\d+(?:\.\d+)*)-(terra|luna|sol|astra)(?:-(\d{4}-\d{2}-\d{2}))?$/.exec(id);
    if (!match || versions(match[1], '5.6') > 0) return [];
    return [{ id, version: match[1], family: match[2], date: match[3] ?? null }];
  }).sort((a, b) => versions(a.version, b.version) || b.version.localeCompare(a.version) || a.family.localeCompare(b.family) || (a.date && b.date ? b.date.localeCompare(a.date) : a.date ? 1 : b.date ? -1 : 0));
  const groups = [];
  for (const model of models) {
    let group = groups.at(-1);
    if (!group || group.version !== model.version) { group = { version: model.version, label: `GPT ${model.version}`, models: [] }; groups.push(group); }
    group.models.push(model);
  }
  return groups;
}
export const catalogSelection = (groups, exactModel) => groups.some(group => group.models.some(model => model.id === exactModel)) ? exactModel : '';
export function safeHttpUrl(value) {
  try { const url = new URL(value); return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password ? url.href : null; } catch { return null; }
}
export function citationPresentation(text, events) {
  const sources = []; const inline = []; const sourceKeys = new Map(); const placements = new Set(); let unplaced = 0;
  const asciiOffsets = /^[\x00-\x7f]*$/.test(text);
  function addSource(value, target, callId) {
    const url = safeHttpUrl(value?.url); if (!url) return null;
    const key = `${target}|${url}`;
    if (!sourceKeys.has(key)) {
      const source = { url, title: typeof value.title === 'string' && value.title ? value.title : new URL(url).hostname, target, callId: callId ?? null, number: sources.filter(item => item.target === target).length + 1 };
      sources.push(source); sourceKeys.set(key, source);
    }
    return sourceKeys.get(key);
  }
  for (const event of [...events].sort((a, b) => a.sequence - b.sequence)) {
    if (event.type !== 'provider.citations' || !['assistant', 'tool'].includes(event.data?.target)) continue;
    const target = event.data.target;
    for (const citation of event.data.citations ?? []) {
      const source = addSource(citation, target, event.callId); if (!source || target !== 'assistant') continue;
      const { startIndex, endIndex } = citation;
      // Provider documentation does not establish Unicode offset units. ASCII
      // ranges are unambiguous; other text keeps clickable sources without a
      // guessed conversion or alteration of the stored answer.
      if (!asciiOffsets || !Number.isSafeInteger(startIndex) || !Number.isSafeInteger(endIndex) || startIndex < 0 || endIndex < startIndex || endIndex > text.length) { unplaced++; continue; }
      const key = `${source.url}|${endIndex}`;
      if (!placements.has(key)) { inline.push({ ...source, offset: endIndex }); placements.add(key); }
    }
    for (const source of event.data.sources ?? []) addSource(source, target, event.callId);
  }
  inline.sort((a, b) => a.offset - b.offset || a.number - b.number);
  return { inline, sources, unplaced };
}
export function validateSettings(settings) {
  if (!settings.model?.trim()) return 'Enter an exact model ID.';
  if (!['clean-room', 'harness'].includes(settings.mode)) return 'Choose a valid execution mode.';
  if (settings.mode === 'clean-room' && settings.enabledTools.length) return 'Clean Room cannot offer tools.';
  if (settings.memoryEnabled) return 'Memory is disabled for this feature.';
  if (settings.mode === 'harness' && settings.ambientWebSearch) return 'Use the explicit web.search capability in Harness mode; ambient search is Clean Room only.';
  if (!Number.isSafeInteger(settings.maxWebSearchCalls ?? 3) || (settings.maxWebSearchCalls ?? 3) < 1 || (settings.maxWebSearchCalls ?? 3) > 20) return 'Web search call budget must be 1–20 calls per turn.';
  for (const name of ['maxOutputTokens', 'contextWindowTokens', 'maxInputCharacters', 'maxToolResultCharacters', 'maxToolEvidenceCharacters', 'maxSteps', 'maxProviderCalls', 'timeoutSeconds']) {
    if (!Number.isSafeInteger(settings[name]) || settings[name] < 1) return `${name} must be a positive whole number.`;
  }
  for (const name of ['maxHistoryMessages', 'maxContinuations']) {
    if (!Number.isSafeInteger(settings[name]) || settings[name] < 0) return `${name} must be a nonnegative whole number.`;
  }
  return null;
}
export function mergeTrace(current, incoming, limit = 1000) {
  const map = new Map(current.map(event => [event.sequence, event]));
  for (const event of incoming) if (Number.isSafeInteger(event.sequence) && event.sequence > 0) map.set(event.sequence, event);
  const events = [...map.values()].sort((a, b) => a.sequence - b.sequence);
  return { events: events.slice(-limit), omitted: Math.max(0, events.length - limit) };
}

if (typeof document !== 'undefined') startChat();

function startChat() {
  const $ = id => document.getElementById(id);
  const state = { conversations: [], conversation: null, capabilities: [], models: null, dirty: false, instructionsDirty: false, busy: false, generation: 0, streams: new Map(), traces: new Map(), nodes: new Map(), pendingSend: null };
  const json = value => JSON.stringify(value ?? null, null, 2);
  const node = (tag, className, text) => { const value = document.createElement(tag); if (className) value.className = className; if (text !== undefined) value.textContent = text; return value; };
  const label = value => String(value ?? 'unknown').replaceAll('_', ' ').replaceAll('.', ' ');
  const timestamp = value => { const date = new Date(value); return Number.isNaN(date.valueOf()) ? String(value ?? '') : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }); };
  const statusClass = value => /complete|succeed/i.test(value) ? 'success' : /fail|error|cancel|interrupt|indeterminate/i.test(value) ? 'error' : /running|queued|active/i.test(value) ? 'running' : 'neutral';
  const badge = value => node('span', `badge ${statusClass(value)}`, label(value));
  const notice = message => { $('chat-notice').hidden = !message; $('chat-notice').textContent = message ?? ''; };
  const activeTurn = () => state.conversation?.turns?.find(turn => !isTerminal(turn.status));
  const nearBottom = () => $('transcript').scrollHeight - $('transcript').scrollTop - $('transcript').clientHeight < 140;
  const scrollLatest = force => { if (force || nearBottom()) $('transcript').scrollTop = $('transcript').scrollHeight; else $('jump-container').hidden = false; };
  async function api(path, method = 'GET', body) {
    const response = await fetch(path, { method, headers: body === undefined ? {} : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
    if (!response.ok) {
      let data; try { data = await response.json(); } catch { /* An HTML proxy error is not a JSON API error. */ }
      const error = new Error(data?.message ?? data?.errorMessage ?? data?.error ?? `${response.status} ${response.statusText}`);
      error.status = response.status; error.code = data?.code; throw error;
    }
    return response.status === 204 ? null : response.json();
  }
  const base = () => `/api/chats/${encodeURIComponent(state.conversation.id)}`;
  const turnBase = id => `${base()}/turns/${encodeURIComponent(id)}`;
  const numericFields = {
    maxOutputTokens: 'max-output', contextWindowTokens: 'context-window', maxInputCharacters: 'max-input', maxHistoryMessages: 'max-history',
    maxToolResultCharacters: 'max-tool-result', maxToolEvidenceCharacters: 'max-tool-evidence', maxSteps: 'max-steps', maxContinuations: 'max-continuations',
    maxProviderCalls: 'max-provider-calls', timeoutSeconds: 'timeout-seconds', maxWebSearchCalls: 'max-web-search-calls'
  };
  function settingsFromForm() {
    return {
      ...state.conversation.settings,
      mode: $('mode').value, model: $('model').value.trim(), reasoningEffort: $('reasoning-effort').value || null,
      instructions: instructionValue(state.conversation.settings.instructions, $('instructions').value, state.instructionsDirty), reasoningSummary: $('reasoning-summary').checked,
      ambientWebSearch: $('mode').value === 'clean-room' && $('ambient-web-search').checked,
      historyPolicy: $('history-policy').value, contextDepth: $('context-depth').value,
      ...Object.fromEntries(Object.entries(numericFields).map(([key, id]) => [key, Number($(id).value)])),
      enabledTools: $('mode').value === 'harness' ? [...$('capability-list').querySelectorAll('input:checked')].map(input => input.value) : [],
      memoryEnabled: false
    };
  }
  function fillSettings() {
    const settings = state.conversation.settings;
    $('mode').value = settings.mode; $('model').value = settings.model;
    syncModelSelection();
    if (settings.reasoningEffort && ![...$('reasoning-effort').options].some(option => option.value === settings.reasoningEffort)) $('reasoning-effort').append(new Option(settings.reasoningEffort, settings.reasoningEffort));
    $('reasoning-effort').value = settings.reasoningEffort ?? ''; $('instructions').value = settings.instructions ?? '';
    $('reasoning-summary').checked = settings.reasoningSummary; $('history-policy').value = settings.historyPolicy;
    $('ambient-web-search').checked = Boolean(settings.ambientWebSearch);
    $('context-depth').value = settings.contextDepth;
    for (const [key, id] of Object.entries(numericFields)) $(id).value = settings[key] ?? (key === 'maxWebSearchCalls' ? 3 : '');
    for (const input of $('capability-list').querySelectorAll('input')) input.checked = settings.enabledTools?.includes(input.value) ?? false;
    state.dirty = false; state.instructionsDirty = false; updateMode(); updateControls();
  }
  function updateMode() {
    const harness = $('mode').value === 'harness';
    $('harness-settings').hidden = !harness;
    if (harness) $('ambient-web-search').checked = false;
    $('ambient-web-search').disabled = harness;
    $('web-search-help').textContent = harness ? 'Harness uses the explicit web.search capability below. Ambient provider search is disabled for this mode.' : 'Optional in Clean Room. When enabled, the provider may search the public web. This changes the no-tools baseline.';
    $('mode-description').textContent = harness
      ? 'Uses the real Agentica planner, bounded execution and tool evidence. Application planning instructions are an explicit experimental variable.'
      : $('ambient-web-search').checked ? "Only this conversation's context is sent. Provider-native web search is enabled; no application planner or memory is added." : "Only this conversation's history and your exact instructions are sent. No application planner or tools.";
    $('instruction-state').textContent = $('instructions').value.length ? `${$('instructions').value.length.toLocaleString()} characters · saved exactly as entered.` : 'Blank means the instructions field is omitted.';
  }
  function updateControls() {
    const selected = Boolean(state.conversation); const running = Boolean(activeTurn());
    $('settings-fields').disabled = !selected || state.busy || running;
    $('save-settings').disabled = !selected || !state.dirty || state.busy || running;
    $('reload-settings').disabled = !selected || state.busy || running;
    $('rename-chat').disabled = !selected || state.busy;
    $('message').disabled = !selected || state.busy || running;
    $('send-message').disabled = !selected || state.busy || running || !$('message').value.trim();
    $('new-chat').disabled = state.busy;
    $('cancel-turn').hidden = !running; $('cancel-turn').disabled = state.busy;
    $('transcript').setAttribute('aria-busy', String(running));
    $('save-status').textContent = selected ? state.dirty ? 'Unsaved changes · saved before sending or switching.' : `Saved · revision ${state.conversation.settingsRevision}` : 'No conversation selected.';
    $('composer-status').textContent = running ? 'Turn running · settings are frozen for this invocation.' : selected ? state.dirty ? 'The next turn will use your edited configuration.' : 'Saved conversation · only its own context is eligible.' : 'Select or create a conversation.';
    if (selected) $('conversation-subtitle').textContent = `${state.conversation.settings.mode === 'harness' ? 'Agentica Harness' : 'Clean Room'} · ${state.conversation.settings.model} · ${state.conversation.settings.reasoningEffort ?? 'model default'} effort · memory off`;
  }
  async function saveSettings() {
    if (!state.dirty || !state.conversation) return;
    const settings = settingsFromForm(); const problem = validateSettings(settings); if (problem) throw new Error(problem);
    if (!$('settings-form').reportValidity()) throw new Error('Check the highlighted configuration fields.');
    const updated = await api(`${base()}/settings`, 'PUT', { expectedRevision: state.conversation.settingsRevision, settings });
    // Keep the durable transcript, which may have advanced while the settings saved.
    state.conversation = updated; fillSettings(); upsertConversation(updated);
  }
  function upsertConversation(conversation) {
    const index = state.conversations.findIndex(item => item.id === conversation.id);
    if (index < 0) state.conversations.push(conversation); else state.conversations[index] = conversation;
    renderConversations();
  }
  function renderConversations() {
    const values = [...state.conversations].sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt));
    $('conversation-list').replaceChildren(...(values.length ? values.map(conversation => {
      const button = node('button', 'conversation-button'); button.type = 'button'; button.setAttribute('aria-current', String(state.conversation?.id === conversation.id));
      button.append(node('strong', '', conversation.title), node('small', '', `${conversation.settings?.mode === 'harness' ? 'Harness' : 'Clean Room'} · ${new Date(conversation.updatedAt).toLocaleDateString([], { month: 'short', day: 'numeric' })}`));
      button.addEventListener('click', () => runAction(() => selectConversation(conversation.id)));
      return button;
    }) : [node('p', 'micro', 'No experiments yet. Start a new clean room.')]));
  }
  async function refreshList() { const result = await api('/api/chats'); state.conversations = Array.isArray(result) ? result : result.conversations ?? []; renderConversations(); }
  function stopStreams() { for (const entry of state.streams.values()) entry.source.close(); state.streams.clear(); }
  async function selectConversation(id, skipSave = false) {
    if (!skipSave) await saveSettings();
    const generation = ++state.generation;
    const conversation = await api(`/api/chats/${encodeURIComponent(id)}`);
    if (generation !== state.generation) return;
    stopStreams(); state.conversation = conversation; state.traces.clear(); state.nodes.clear(); state.pendingSend = null;
    $('message').value = ''; $('conversation-title').textContent = conversation.title;
    history.replaceState(null, '', `${location.pathname}?chat=${encodeURIComponent(id)}`);
    fillSettings(); renderTranscript(); upsertConversation(conversation); notice(null);
    for (const turn of conversation.turns ?? []) if (!isTerminal(turn.status)) connectTurn(turn);
    scrollLatest(true);
  }
  async function newConversation() {
    await saveSettings();
    const conversation = await api('/api/chats', 'POST', {});
    upsertConversation(conversation); await selectConversation(conversation.id, true); $('message').focus();
  }
  function newTrace(turn) {
    return { events: [], citationEvents: [], citationsLoading: false, citationsHydrated: false, citationCoverage: '', after: 0, hasMore: true, loaded: false, loading: false, liveLast: turn.traceCount ?? 0, windowOmitted: 0, streamError: '', gap: '', renderPending: false, payloads: new Map() };
  }
  function renderTranscript() {
    $('transcript').replaceChildren();
    const turns = state.conversation.turns ?? [];
    if (!turns.length) {
      const empty = node('div', 'chat-empty'); empty.append(node('span', 'empty-symbol', '◇'), node('h2', '', 'A clear starting point'), node('p', '', 'Your conversation stays isolated. You control its instructions.'), node('p', 'micro', 'Configure an experiment, then send a message. Each turn uses this conversation’s saved settings. Memory is disabled.'));
      $('transcript').append(empty);
    } else for (const turn of turns) renderTurn(turn);
  }
  function renderTurn(turn) {
    const article = node('article', 'turn'); article.dataset.turnId = turn.id;
    const user = node('div', 'user-message'); user.append(node('div', 'message-label', 'You'), node('pre', 'message-content', turn.userText)); article.append(user);
    const assistant = node('div', 'assistant-message'); const assistantLabel = node('div', 'message-label', 'Agentica Lab');
    const content = node('pre', 'message-content', turn.assistantText); const placeholder = node('div', 'assistant-placeholder');
    const meta = node('div', 'turn-meta'); const error = node('div', 'turn-error'); const continuity = node('div', 'continuity-label'); const answerSources = node('div', 'answer-sources');
    assistant.append(assistantLabel, content, placeholder, answerSources, meta, error, continuity); article.append(assistant);
    const trace = node('details', 'turn-trace'); const traceLabel = node('summary'); trace.append(traceLabel);
    const traceContent = node('div', 'trace-content'); const activity = node('div', 'trace-activity');
    const summary = node('section', 'trace-summary'); summary.append(node('h4', '', 'Provider reasoning summary')); const summaryText = node('pre'); summary.append(summaryText);
    const absence = node('p', 'trace-absence'); const coverage = node('p', 'trace-coverage');
    const requestInspector = makeInspector('Effective provider request'); const contextInspector = makeInspector('Context manifest'); const nativeInspector = makeInspector('Native output items · sanitized'); const settingsInspector = makeInspector('Invocation settings snapshot');
    const searchInspector = makeInspector('Web search & sources');
    const sourceInspector = makeInspector('Provider source records'); sourceInspector.body.remove();
    sourceInspector.section.append(node('p', 'trace-coverage', 'Allowed provider source events, with sensitive fields redacted. Private reasoning and opaque continuation carriers are not exposed. These records are separate from the normalized timeline and are not a replay carrier.'));
    sourceInspector.records = node('ol', 'typed-timeline'); sourceInspector.section.append(sourceInspector.records);
    settingsInspector.body.textContent = json({ settingsRevision: turn.settingsRevision, settings: turn.settings });
    const timelineSection = node('details', 'trace-inspector'); const timelineLabel = node('summary', '', 'Typed timeline'); timelineSection.append(timelineLabel); const timeline = node('ol', 'typed-timeline'); timelineSection.append(timeline);
    const pageControls = node('div', 'trace-page-controls'); const load = node('button', 'button secondary', 'Load retained trace'); load.type = 'button';
    const restart = node('button', 'button secondary', 'Read from start'); restart.type = 'button'; restart.title = 'Reset only the browser trace window and reread retained pages. Makes no provider call.';
    const pageInfo = node('span'); pageControls.append(load, restart, pageInfo);
    traceContent.append(activity, summary, absence, coverage, requestInspector.section, contextInspector.section, searchInspector.section, nativeInspector.section, sourceInspector.section, settingsInspector.section, timelineSection, pageControls); trace.append(traceContent); article.append(trace);
    const entry = { article, content, placeholder, answerSources, meta, error, continuity, trace, traceLabel, activity, summary, summaryText, absence, coverage, timeline, timelineSection, timelineLabel, requestInspector, contextInspector, nativeInspector, sourceInspector, searchInspector, load, pageInfo };
    state.nodes.set(turn.id, entry); state.traces.set(turn.id, newTrace(turn));
    trace.addEventListener('toggle', () => { if (trace.open && !state.traces.get(turn.id)?.loaded) loadTrace(turn.id); });
    for (const inspector of [timelineSection, requestInspector.section, contextInspector.section, nativeInspector.section, sourceInspector.section, searchInspector.section]) inspector.addEventListener('toggle', () => { if (inspector.open) renderTrace(turn.id); });
    load.addEventListener('click', () => loadTrace(turn.id));
    restart.addEventListener('click', () => {
      const retained = state.traces.get(turn.id); if (!retained || retained.loading) return;
      retained.events = []; retained.after = 0; retained.loaded = false; retained.hasMore = true; retained.windowOmitted = 0;
      loadTrace(turn.id);
    });
    $('transcript').append(article); updateTurn(turn); renderTrace(turn.id);
    if (!isTerminal(turn.status)) trace.open = true;
  }
  function makeInspector(title) { const section = node('details', 'trace-inspector'); section.append(node('summary', '', title)); const body = node('pre', 'json-view', 'No retained record loaded.'); section.append(body); return { section, body }; }
  function turnById(id) { return state.conversation?.turns?.find(turn => turn.id === id); }
  function sourceLink(source, inline = false) {
    const anchor = node('a', inline ? 'answer-citation' : 'source-link', inline ? `[${source.number}]` : `[${source.target === 'tool' ? 'T' : ''}${source.number}] ${source.title}`);
    anchor.href = source.url; anchor.target = '_blank'; anchor.rel = 'noopener noreferrer'; anchor.title = `${source.title} · ${source.url}`;
    if (inline) anchor.setAttribute('aria-label', `Citation ${source.number}: ${source.title}`);
    return anchor;
  }
  function renderAnswer(turn, elements) {
    const retained = state.traces.get(turn.id);
    const events = [...new Map([...(retained?.events ?? []), ...(retained?.citationEvents ?? [])].map(event => [event.sequence, event])).values()];
    const text = turn.assistantText ?? ''; const presentation = citationPresentation(text, events);
    if (!presentation.inline.length) elements.content.textContent = text;
    else {
      const fragments = []; let cursor = 0;
      for (const citation of presentation.inline) { fragments.push(document.createTextNode(text.slice(cursor, citation.offset)), sourceLink(citation, true)); cursor = citation.offset; }
      fragments.push(document.createTextNode(text.slice(cursor))); elements.content.replaceChildren(...fragments);
    }
    elements.content.hidden = !text; elements.answerSources.replaceChildren(); elements.answerSources.hidden = !presentation.sources.length && !retained?.citationCoverage;
    if (retained?.citationCoverage) elements.answerSources.append(node('p', 'micro', retained.citationCoverage));
    for (const target of ['assistant', 'tool']) {
      const sources = presentation.sources.filter(source => source.target === target); if (!sources.length) continue;
      const section = node('section', `citation-sources ${target}-sources`); section.append(node('h4', '', target === 'tool' ? 'Tool research sources' : 'Answer sources'));
      if (target === 'tool') section.append(node('p', 'micro', 'Search-tool evidence. These source ranges do not annotate the final answer.'));
      if (target === 'assistant' && presentation.unplaced) section.append(node('p', 'micro', 'Source links are preserved below; inline placement is unavailable for some provider offsets.'));
      const list = node('ul'); for (const source of sources) { const item = node('li'); item.append(sourceLink(source), node('span', 'source-domain', new URL(source.url).hostname)); list.append(item); }
      section.append(list); elements.answerSources.append(section);
    }
  }
  function updateTurn(turn) {
    const elements = state.nodes.get(turn.id); if (!elements) return;
    renderAnswer(turn, elements);
    elements.placeholder.hidden = Boolean(turn.assistantText); elements.placeholder.textContent = isTerminal(turn.status) ? 'No assistant text emitted.' : 'Waiting for provider output…';
    elements.meta.replaceChildren(badge(turn.status), node('span', '', `${turn.settings?.model ?? 'OpenAI'} · ${turn.settings?.reasoningEffort ?? 'default'} effort`), node('span', '', `Configuration r${turn.settingsRevision}`));
    if (turn.finishedAt) elements.meta.append(node('span', '', `${Math.max(0, (new Date(turn.finishedAt) - new Date(turn.createdAt)) / 1000).toFixed(1)}s`));
    if (turn.usage) { const usage = node('span', '', usageLabel(turn.usage)); usage.title = json(turn.usage); elements.meta.append(usage); }
    elements.error.textContent = turn.errorMessage ? `${turn.errorCode ? `${turn.errorCode}: ` : ''}${turn.errorMessage}` : '';
    elements.continuity.textContent = turn.continuity ? `Continuity: ${label(turn.continuity)}. This is distinct from provider-native reasoning replay.` : '';
    elements.traceLabel.textContent = `${isTerminal(turn.status) ? 'Inspect turn' : 'Live trace'} · ${(turn.traceCount ?? 0).toLocaleString()} retained events${turn.droppedTraceCount ? ` · ${turn.droppedTraceCount} dropped` : ''}`;
    if (isTerminal(turn.status) && (turn.settings?.ambientWebSearch || turn.settings?.enabledTools?.includes('web.search'))) hydrateCitations(turn.id);
  }
  function usageLabel(usage) {
    const input = usage.inputTokens ?? usage.promptTokens; const output = usage.outputTokens ?? usage.completionTokens; const reasoning = usage.reasoningTokens ?? usage.outputTokensDetails?.reasoningTokens ?? usage.output_tokens_details?.reasoning_tokens;
    return [['input', input], ['output', output], ['reasoning', reasoning]].filter(([, value]) => value !== undefined && value !== null).map(([name, value]) => `${Number(value).toLocaleString()} ${name} tokens`).join(' · ') || 'Usage returned · inspect trace';
  }
  function appendEvents(id, events) {
    const trace = state.traces.get(id); if (!trace) return;
    const merged = mergeTrace(trace.events, events); trace.events = merged.events; trace.windowOmitted += merged.omitted;
    if (!trace.renderPending) { trace.renderPending = true; requestAnimationFrame(() => { if (!state.traces.has(id)) return; trace.renderPending = false; renderTrace(id); }); }
  }
  function eventElement(id, event, open, sourceRecord = false) {
    const li = node('li'); const detail = node('details', `typed-event ${classifyEvent(event)}`); detail.dataset.sequence = String(event.sequence); detail.open = open.has(String(event.sequence));
    const title = node('summary'); title.append(node('span', 'event-kind'), node('time', 'event-time', timestamp(event.time)), node('span', 'event-type', sourceRecord ? event.data?.providerType ?? 'Provider source event' : event.type), node('span', 'event-seq', `#${event.sequence}`)); detail.append(title);
    detail.append(node('p', 'event-correlation', [event.callId ? `Call ${event.callId}` : '', event.itemId ? `Item ${event.itemId}` : '', event.time].filter(Boolean).join(' · ')));
    const retained = state.traces.get(id); const cached = retained?.payloads.has(event.sequence);
    const payload = node('pre', 'json-view', json(cached ? retained.payloads.get(event.sequence) : event.data)); detail.append(payload);
    if (event.data?.payloadRef && !cached) {
      const full = node('button', 'button secondary', sourceRecord ? 'Load provider source record' : 'Load retained payload'); full.type = 'button'; full.addEventListener('click', async () => {
        full.disabled = true;
        const generation = state.generation;
        try {
          const data = await api(`${turnBase(id)}/events/${event.sequence}/payload`);
          if (generation !== state.generation) return;
          // Explicit inspection is bounded independently of the event window.
          // Evicted payloads can be read again from the durable service record.
          retained.payloads.set(event.sequence, data);
          if (retained.payloads.size > 8) retained.payloads.delete(retained.payloads.keys().next().value);
          payload.textContent = json(data); full.remove();
        } catch (error) { if (generation === state.generation) { notice(`Cannot load retained record: ${error.message}`); full.disabled = false; } }
      }); detail.append(full);
    }
    li.append(detail); return li;
  }
  function renderTrace(id) {
    const trace = state.traces.get(id); const elements = state.nodes.get(id); const turn = turnById(id); if (!trace || !elements || !turn) return;
    renderAnswer(turn, elements);
    const fullCoverage = trace.events.length === (turn.traceCount ?? 0) && (!trace.events.length || (trace.events[0].sequence === 1 && trace.events.at(-1).sequence === turn.traceCount));
    const moreRetained = trace.hasMore && !fullCoverage;
    const summary = summarizeTrace(trace.events); elements.summary.hidden = !summary; elements.summaryText.textContent = summary;
    elements.absence.hidden = Boolean(summary);
    elements.absence.textContent = !turn.settings?.reasoningSummary ? 'Provider reasoning summary was not requested. Private reasoning is not exposed.' : !isTerminal(turn.status) ? 'Summary requested; none emitted in the loaded trace yet.' : !fullCoverage && (!trace.loaded || trace.hasMore) ? 'Summary status is incomplete until the retained trace is loaded.' : 'Provider reasoning summary was not emitted or is unsupported. Private reasoning is not exposed.';
    const latest = trace.events.at(-1); elements.activity.replaceChildren(node('span', `status-dot${isTerminal(turn.status) ? '' : ' busy'}`), node('span', '', latest ? label(latest.type) : 'Waiting for retained trace'));
    const coverage = [trace.streamError, trace.gap, summary && moreRetained ? 'The summary shown covers loaded events only; load remaining trace pages for complete retained coverage.' : '', trace.windowOmitted ? `${trace.windowOmitted} events left the browser's 1,000-event display window; retained service trace is still independently paged.` : '', turn.droppedTraceCount ? `${turn.droppedTraceCount} service trace events were dropped. Coverage is incomplete.` : ''].filter(Boolean);
    elements.coverage.textContent = coverage.join(' '); elements.coverage.hidden = !coverage.length; elements.coverage.classList.toggle('warning', Boolean(coverage.length));
    if (elements.requestInspector.section.open) {
      const requests = trace.events.filter(event => event.type === 'provider.request');
      elements.requestInspector.body.textContent = requests.length ? json(requests.map(event => ({ sequence: event.sequence, callId: event.callId, data: event.data }))) : 'No provider request loaded. A pre-dispatch failure may have no request.';
    }
    if (elements.contextInspector.section.open) {
      const manifests = trace.events.filter(event => event.type === 'context.manifest'); elements.contextInspector.body.textContent = manifests.length ? json(manifests.map(event => event.data)) : 'No context manifest loaded.';
    }
    if (elements.nativeInspector.section.open) {
      const items = trace.events.filter(event => event.type === 'provider.item'); elements.nativeInspector.body.textContent = items.length ? json(items.map(event => ({ callId: event.callId, itemId: event.itemId, data: event.data }))) : 'No native items loaded. Opaque continuation material is not displayed.';
    }
    if (elements.searchInspector.section.open) {
      const searches = [...new Map([...trace.events, ...trace.citationEvents].filter(event => event.type === 'provider.citations' || /web.search|web_search/.test(event.type)).map(event => [event.sequence, event])).values()].sort((a, b) => a.sequence - b.sequence);
      elements.searchInspector.body.textContent = searches.length ? json(searches.map(event => ({ sequence: event.sequence, callId: event.callId, itemId: event.itemId, type: event.type, data: event.data }))) : 'No search/source events loaded. Search may be disabled or unused; inspect retained coverage before inferring absence.';
    }
    if (elements.sourceInspector.section.open) {
      const records = trace.events.filter(event => event.type === 'provider.record');
      const open = new Set([...elements.sourceInspector.records.querySelectorAll('details[open]')].map(detail => detail.dataset.sequence));
      elements.sourceInspector.records.replaceChildren(...(records.length ? records.map(event => eventElement(id, event, open, true)) : [node('li', 'trace-coverage', 'No provider source records loaded. Inspect trace coverage before inferring absence.')]));
    }
    const normalized = trace.events.filter(event => event.type !== 'provider.record');
    elements.timelineLabel.textContent = `Typed timeline · ${normalized.length.toLocaleString()} normalized events`;
    if (elements.timelineSection.open) {
      const open = new Set([...elements.timeline.querySelectorAll('details[open]')].map(detail => detail.dataset.sequence));
      elements.timeline.replaceChildren(...normalized.map(event => eventElement(id, event, open)));
    }
    elements.load.disabled = trace.loading || fullCoverage || (trace.loaded && !trace.hasMore);
    elements.load.textContent = trace.loading ? 'Loading…' : fullCoverage ? 'Retained trace loaded' : !trace.loaded ? 'Load retained trace' : trace.hasMore ? 'Load next 100 events' : 'Retained trace loaded';
    elements.pageInfo.textContent = fullCoverage ? `${trace.events.length.toLocaleString()} visible · full retained sequence loaded` : `${trace.events.length.toLocaleString()} visible · page cursor ${trace.after}`;
  }
  async function loadTrace(id) {
    const trace = state.traces.get(id); if (!trace || trace.loading || (trace.loaded && !trace.hasMore)) return;
    const generation = state.generation; trace.loading = true; renderTrace(id);
    try {
      const result = await api(`${turnBase(id)}/trace?after=${trace.after}&limit=100`);
      if (generation !== state.generation) return;
      trace.loaded = true; trace.after = result.next ?? result.events?.at(-1)?.sequence ?? trace.after; trace.hasMore = result.hasMore;
      appendEvents(id, result.events ?? []);
    } catch (error) { if (generation === state.generation) { trace.streamError = `Retained trace unavailable: ${error.message}`; notice(trace.streamError); } }
    finally { trace.loading = false; if (generation === state.generation) renderTrace(id); }
  }
  async function hydrateCitations(id) {
    const retained = state.traces.get(id); if (!retained || retained.citationsHydrated || retained.citationsLoading) return;
    retained.citationsLoading = true; const generation = state.generation; let after = 0; let hasMore = true; let payloadReads = 0; let payloadLimit = false;
    try {
      // Citation projection avoids restoring every native/delta event merely to
      // display saved answer links. Native source-record inspection stays lazy.
      for (let page = 0; page < 4 && hasMore; page++) {
        const result = await api(`${turnBase(id)}/citations?after=${after}&limit=100`);
        if (generation !== state.generation) return;
        for (const event of result.events ?? []) {
          if (event.data?.payloadRef && payloadReads >= 8) { payloadLimit = true; continue; }
          if (event.data?.payloadRef) payloadReads++;
          const data = event.data?.payloadRef ? await api(`${turnBase(id)}/events/${event.sequence}/payload`) : event.data;
          if (generation !== state.generation) return;
          retained.citationEvents.push({ ...event, data });
        }
        after = result.next ?? after; hasMore = Boolean(result.hasMore);
      }
      retained.citationCoverage = hasMore || payloadLimit ? 'Additional source metadata exceeds this display window. Inspect retained trace pages for the remaining records.' : '';
    } catch { if (generation === state.generation) retained.citationCoverage = 'Saved source links could not be restored. Inspect the retained trace or reopen this conversation to retry.'; }
    finally {
      retained.citationsLoading = false; retained.citationsHydrated = true;
      if (generation === state.generation) renderTrace(id);
    }
  }
  function connectTurn(turn) {
    if (state.streams.has(turn.id)) return;
    const generation = state.generation; const trace = state.traces.get(turn.id); const conversationId = state.conversation.id;
    const source = new EventSource(`/api/chats/${encodeURIComponent(conversationId)}/turns/${encodeURIComponent(turn.id)}/events?after=${trace.liveLast}`);
    state.streams.set(turn.id, { source });
    source.onopen = () => { trace.streamError = ''; renderTrace(turn.id); };
    source.onmessage = event => {
      if (generation !== state.generation) return;
      let value; try { value = JSON.parse(event.data); } catch { trace.streamError = 'An invalid stream event was received; refresh the retained trace.'; renderTrace(turn.id); return; }
      if (!Number.isSafeInteger(value.sequence) || value.sequence <= trace.liveLast) return;
      const stick = nearBottom();
      if (trace.liveLast && value.sequence > trace.liveLast + 1) trace.gap = `Stream sequence gap: expected ${trace.liveLast + 1}, received ${value.sequence}. Inspect the retained trace.`;
      trace.liveLast = value.sequence; turn.traceCount = Math.max(turn.traceCount ?? 0, value.sequence);
      appendEvents(turn.id, [value]);
      if (value.type === 'assistant.delta') { turn.assistantText = (turn.assistantText ?? '') + (value.data?.text ?? ''); updateTurn(turn); }
      if (/^turn\.(completed|failed|cancelled|canceled|interrupted|indeterminate)$/.test(value.type)) {
        source.close(); state.streams.delete(turn.id);
        if (value.data?.id === turn.id) Object.assign(turn, value.data);
        else turn.status = value.type.split('.')[1];
        updateTurn(turn); renderTrace(turn.id); updateControls();
        reconcileConversation(generation).catch(error => notice(`The turn ended, but refreshing its durable result failed: ${error.message}`));
      } else updateTurn(turn);
      if (stick) scrollLatest(true); else $('jump-container').hidden = false;
    };
    source.onerror = () => {
      if (generation !== state.generation || isTerminal(turn.status)) return;
      // EventSource reconnects with Last-Event-ID. The service also accepts the
      // explicit initial cursor; sequence deduplication prevents replayed output.
      trace.streamError = 'Live connection interrupted. Reconnecting; retained events are replayed without repeating assistant text.';
      renderTrace(turn.id);
    };
  }
  async function reconcileConversation(generation = state.generation) {
    const conversation = await api(base()); if (generation !== state.generation) return;
    for (const fresh of conversation.turns ?? []) {
      const turn = turnById(fresh.id);
      if (!turn) { state.conversation.turns.push(fresh); renderTurn(fresh); if (!isTerminal(fresh.status)) connectTurn(fresh); }
      else {
        // SSE may have delivered newer text than this request snapshot. Only
        // replace output when terminal or after a disconnected stream.
        if (isTerminal(fresh.status)) { Object.assign(turn, fresh); state.streams.get(turn.id)?.source.close(); state.streams.delete(turn.id); }
        else { turn.status = fresh.status; turn.traceCount = Math.max(turn.traceCount ?? 0, fresh.traceCount ?? 0); }
        const retained = state.traces.get(turn.id);
        if (retained && fresh.traceCount > retained.after) retained.hasMore = true;
        updateTurn(turn); renderTrace(turn.id);
      }
    }
    state.conversation.updatedAt = conversation.updatedAt; upsertConversation(state.conversation); updateControls();
  }
  async function sendMessage() {
    if (!state.conversation || activeTurn()) return;
    const content = $('message').value; if (!content.trim()) return;
    await saveSettings();
    const pending = state.pendingSend?.content === content ? state.pendingSend : { content, requestId: crypto.randomUUID() };
    state.pendingSend = pending;
    const turn = await api(`${base()}/turns`, 'POST', pending);
    state.pendingSend = null; $('message').value = ''; $('message').style.height = '';
    if (!state.conversation.turns.length) $('transcript').replaceChildren();
    const existing = turnById(turn.id);
    if (!existing) { state.conversation.turns.push(turn); renderTurn(turn); }
    else Object.assign(existing, turn);
    if (!isTerminal(turn.status)) connectTurn(existing ?? turn);
    updateControls(); scrollLatest(true); notice(null);
  }
  async function runAction(action) {
    if (state.busy) return;
    state.busy = true; updateControls();
    try { await action(); }
    catch (error) {
      notice(`${error.code ? `${error.code}: ` : ''}${error.message}${error.status === 409 ? ' Another tab or a running turn changed this conversation. Refresh before trying again; your unsaved fields are preserved.' : ''}`);
    } finally { state.busy = false; updateControls(); }
  }
  function renderCapabilities() {
    $('capability-list').replaceChildren(...(state.capabilities.length ? state.capabilities.map(capability => {
      const wrapper = node('div', 'capability'); const labelNode = node('label', 'check'); const input = node('input'); input.type = 'checkbox'; input.value = capability.id;
      const description = node('div'); const name = node('strong', '', capability.name); name.append(node('span', 'badge neutral', capability.effect)); description.append(name, node('p', '', capability.description), node('small', '', `${capability.id} · ${capability.source} · v${capability.version}`));
      labelNode.append(input, description); wrapper.append(labelNode); return wrapper;
    }) : [node('p', 'micro', 'No reusable capabilities are registered.')]));
  }
  function syncModelSelection() {
    const groups = state.models?.configured === true && !state.models.issue ? familyModelCatalog(state.models.models ?? []) : familyModelCatalog(knownFamilyModelIds);
    $('model-catalog').value = catalogSelection(groups, $('model').value);
  }
  function renderModelCatalog(metadata, error = null) {
    const accountCatalog = metadata?.configured === true && !metadata.issue && !error;
    const groups = familyModelCatalog(accountCatalog ? metadata.models ?? [] : knownFamilyModelIds);
    const selected = $('model').value;
    const options = [new Option('Custom / exact model ID below', '')];
    for (const group of groups) {
      const section = node('optgroup'); section.label = `${group.label}${accountCatalog ? '' : ' · access not checked'}`;
      for (const model of group.models) section.append(new Option(`${model.family[0].toUpperCase()}${model.family.slice(1)} · ${model.id}`, model.id));
      options.push(section);
    }
    $('model-catalog').replaceChildren(...options); $('model-catalog').value = catalogSelection(groups, selected);
    const count = groups.reduce((sum, group) => sum + group.models.length, 0);
    $('model-source').textContent = accountCatalog
      ? `${count} Terra, Luna, Sol and Astra model IDs from this account's catalog, grouped by version. Other models can be entered below. Effort support is model-specific; no hidden downgrade is applied.`
      : `Official model IDs verified 2 Nov 2026; account access has not been checked. ${error ? `Account catalog unavailable: ${error}. ` : metadata?.issue ? `${metadata.issue} ` : ''}Use an exact custom ID below when needed. Model and effort compatibility are provider-validated.`;
  }
  async function initialize() {
    // The account's model-list lookup may need the network. Saved experiments,
    // exact manual model IDs and local settings remain usable while it loads.
    const metadata = api('/api/chats/models').then(value => {
      state.models = value; $('model-options').replaceChildren(...(state.models.models ?? []).map(model => new Option(model, model)));
      const effort = $('reasoning-effort').value;
      if (state.models.reasoningEfforts?.length) {
        $('reasoning-effort').replaceChildren(new Option('Model default', ''), ...state.models.reasoningEfforts.map(value => new Option(label(value), value)));
        $('reasoning-effort').value = effort;
      }
      renderModelCatalog(value);
    }).catch(error => { renderModelCatalog(null, error.message); });
    const capabilities = api('/api/chats/capabilities').then(value => {
      state.capabilities = Array.isArray(value) ? value : value.capabilities ?? []; renderCapabilities();
      for (const input of $('capability-list').querySelectorAll('input')) input.checked = state.conversation?.settings.enabledTools?.includes(input.value) ?? false;
    }).catch(() => $('capability-list').replaceChildren(node('p', 'micro', 'Capability catalog unavailable. Refresh before enabling tools.')));
    const credentials = api('/api/providers/openai/settings').then(config => {
      const configured = config?.configured ?? config?.hasApiKey ?? config?.credentialConfigured;
      $('credential-status').replaceChildren(node('span', '', configured ? 'OpenAI credential configured on service. ' : 'OpenAI credential is unavailable. '));
      const keyLink = node('a', '', 'Existing provider settings ↗'); keyLink.href = '/#providers'; $('credential-status').append(keyLink); $('credential-status').classList.toggle('warning', !configured);
    }).catch(() => {
      $('credential-status').replaceChildren(node('span', '', 'Credential status could not be verified. '));
      const keyLink = node('a', '', 'Existing provider settings ↗'); keyLink.href = '/#providers'; $('credential-status').append(keyLink); $('credential-status').classList.add('warning');
    });
    try { await refreshList(); } catch (error) { notice(`Cannot load saved conversations: ${error.message}`); return; }
    const requested = new URLSearchParams(location.search).get('chat');
    if (requested) await runAction(() => selectConversation(requested, true));
    else if (state.conversations.length) await runAction(() => selectConversation([...state.conversations].sort((a, b) => new Date(b.updatedAt) - new Date(a.updatedAt))[0].id, true));
    else await runAction(newConversation);
    await Promise.allSettled([metadata, capabilities, credentials]);
  }
  $('new-chat').addEventListener('click', () => runAction(newConversation));
  $('refresh-chats').addEventListener('click', () => runAction(async () => { await refreshList(); if (state.conversation && !state.dirty) await reconcileConversation(); }));
  $('settings-form').addEventListener('input', event => { if (state.conversation) { state.dirty = true; if (event.target === $('instructions')) state.instructionsDirty = true; updateMode(); updateControls(); } });
  $('settings-form').addEventListener('submit', event => { event.preventDefault(); runAction(saveSettings); });
  $('reload-settings').addEventListener('click', () => runAction(async () => {
    const saved = await api(base());
    state.conversation.settings = saved.settings; state.conversation.settingsRevision = saved.settingsRevision;
    fillSettings(); notice(null);
  }));
  $('mode').addEventListener('change', updateMode);
  $('model-catalog').addEventListener('change', () => {
    if ($('model-catalog').value) { $('model').value = $('model-catalog').value; state.dirty = true; updateControls(); }
    else $('model').focus();
  });
  $('model').addEventListener('input', syncModelSelection);
  $('context-depth').addEventListener('change', () => { const preset = depthPresets[$('context-depth').value]; if (preset) for (const [key, value] of Object.entries(preset)) $(numericFields[key]).value = value; state.dirty = true; updateControls(); });
  $('composer').addEventListener('submit', event => { event.preventDefault(); runAction(sendMessage); });
  $('message').addEventListener('input', () => { $('message').style.height = 'auto'; $('message').style.height = `${Math.min(180, Math.max(58, $('message').scrollHeight))}px`; updateControls(); });
  $('message').addEventListener('keydown', event => { if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) { event.preventDefault(); if (!$('send-message').disabled) $('composer').requestSubmit(); } });
  $('cancel-turn').addEventListener('click', () => runAction(async () => { const turn = activeTurn(); if (turn) { await api(`${turnBase(turn.id)}/cancel`, 'POST', {}); $('composer-status').textContent = 'Cancellation requested; waiting for a durable terminal result.'; } }));
  $('rename-chat').addEventListener('click', () => { $('rename-title').value = state.conversation.title; $('rename-dialog').showModal(); $('rename-title').select(); });
  $('rename-cancel').addEventListener('click', () => $('rename-dialog').close());
  $('rename-form').addEventListener('submit', event => { event.preventDefault(); runAction(async () => { const title = $('rename-title').value.trim(); if (!title) throw new Error('Enter a conversation title.'); const renamed = await api(base(), 'PATCH', { title }); state.conversation.title = renamed.title ?? title; $('conversation-title').textContent = state.conversation.title; upsertConversation(state.conversation); $('rename-dialog').close(); }); });
  const setSettingsVisible = visible => { document.body.classList.toggle('settings-collapsed', !visible); $('toggle-settings').setAttribute('aria-expanded', String(visible)); if (visible && innerWidth <= 1000) $('close-settings').focus(); };
  $('toggle-settings').addEventListener('click', () => setSettingsVisible(document.body.classList.contains('settings-collapsed')));
  $('close-settings').addEventListener('click', () => { setSettingsVisible(false); $('toggle-settings').focus(); });
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && !document.body.classList.contains('settings-collapsed')) { setSettingsVisible(false); $('toggle-settings').focus(); } });
  $('jump-latest').addEventListener('click', () => { scrollLatest(true); $('jump-container').hidden = true; });
  $('transcript').addEventListener('scroll', () => { if (nearBottom()) $('jump-container').hidden = true; }, { passive: true });
  window.addEventListener('beforeunload', event => { if (state.dirty) { event.preventDefault(); event.returnValue = ''; } });
  window.addEventListener('pagehide', stopStreams);
  if (innerWidth <= 1000) setSettingsVisible(false);
  renderModelCatalog(null);
  // A disconnected terminal SSE may never deliver its last event. Read the
  // authoritative turn state periodically only while there is active work.
  setInterval(() => { if (activeTurn() && !state.busy) reconcileConversation().catch(() => {}); }, 5000);
  initialize().catch(error => notice(`Chat initialization failed: ${error.message}`));
}
