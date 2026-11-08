// Conversation state belongs to Lab. This page never stores provider credentials or
// promotes diagnostic events into the conversation's input history.
import { renderMarkdown } from './chat-markdown.mjs';
import { readableWork, recentWorkExcerpt } from './chat-work.mjs';
export const isTerminal = status => !['queued', 'pending', 'running', 'active', 'starting', 'cancelling'].includes(String(status ?? '').toLowerCase());
export const depthPresets = Object.freeze({
  minimal: { maxHistoryMessages: 4, maxInputCharacters: 12000 },
  focused: { maxHistoryMessages: 12, maxInputCharacters: 32000 },
  normal: { maxHistoryMessages: 32, maxInputCharacters: 80000 },
  deep: { maxHistoryMessages: 64, maxInputCharacters: 160000 },
  exhaustive: { maxHistoryMessages: 128, maxInputCharacters: 256000 }
});
export function usageLabel(usage) {
  const attempts = usage?.attempts;
  if (Array.isArray(attempts) && attempts.length) {
    const totals = { input: 0, output: 0, reasoning: 0 }; const known = { input: false, output: false, reasoning: false };
    let unknown = 0; let notDispatched = 0;
    for (const attempt of attempts) {
      if ((attempt.DispatchCertainty ?? attempt.dispatchCertainty) === 0) { notDispatched++; continue; }
      const observed = attempt.Usage ?? attempt.usage;
      if (!observed || [observed.PromptTokens ?? observed.promptTokens, observed.CompletionTokens ?? observed.completionTokens,
        observed.ThinkingTokens ?? observed.thinkingTokens].every(value => value == null)) unknown++;
      for (const [name, value] of [['input', observed?.PromptTokens ?? observed?.promptTokens],
        ['output', observed?.CompletionTokens ?? observed?.completionTokens], ['reasoning', observed?.ThinkingTokens ?? observed?.thinkingTokens]]) {
        if (Number.isSafeInteger(value) && value >= 0) { totals[name] += value; known[name] = true; }
      }
    }
    const labels = Object.entries(totals).filter(([name]) => known[name]).map(([name, value]) => `${value.toLocaleString()} observed ${name} tokens`);
    if (notDispatched) labels.push(`${notDispatched} attempt${notDispatched === 1 ? '' : 's'} not dispatched`);
    if (unknown) labels.push(`${unknown} attempt${unknown === 1 ? '' : 's'} with usage unknown`);
    return labels.join(' · ') || 'Usage unknown · inspect attempts';
  }
  const value = usage?.provider ?? usage ?? {};
  const input = value.inputTokens ?? value.promptTokens ?? value.PromptTokens ?? value.input_tokens;
  const output = value.outputTokens ?? value.completionTokens ?? value.CompletionTokens ?? value.output_tokens;
  const reasoning = value.thinkingTokens ?? value.ThinkingTokens ?? value.reasoningTokens ?? value.output_tokens_details?.reasoning_tokens;
  return [['input', input], ['output', output], ['reasoning', reasoning]].filter(([, count]) => count != null)
    .map(([name, count]) => `${Number(count).toLocaleString()} ${name} tokens`).join(' · ') || 'Usage returned · inspect trace';
}

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
export function actionErrorMessage(error) {
  const message = `${error.code ? `${error.code}: ` : ''}${error.message ?? 'The request failed.'}`;
  return error.code === 'settings.conflict' ? `${message} Use “Reload saved configuration” to load the current revision. Your message draft is preserved.` : message;
}
export function contextCheckSummary(result, settings) {
  const count = result?.manifest?.tokenCount;
  const unavailable = () => { const error = new Error('Provider input token count is unavailable. No estimate was substituted and no generation was dispatched.'); error.code = 'context.token_count_unavailable'; return error; };
  if (result?.inferenceDispatched !== false || !result?.manifest) throw unavailable();
  const format = value => value.toLocaleString('en-US');
  const history = Math.max(0, (result.manifest.selectedMessageIds?.length ?? 1) - 1);
  const quotes = result.recall?.selected ?? [];
  const characters = result.recall?.presentedCharacters ?? quotes.reduce((sum, quote) => sum + (quote.content?.length ?? 0), 0);
  const floor = settings.threadRecallEnabled ? `${settings.threadRecallRecentTurns ?? 2} complete turns configured as the protected recent floor` : 'protected recent floor off';
  const semantic = settings.threadRecallEnabled && settings.threadSemanticRecallEnabled ? ' · Semantic lookup deferred until an admitted turn; no embedding request made' : '';
  const projection = `${history} history messages retained · ${floor} · ${quotes.length} quoted passages, ${format(characters)} characters · settings revision ${result.settingsRevision}${semantic}`;
  if (settings.mode === 'harness' && count == null)
    return `Harness projection valid · ${projection}. Provider token count is deferred until an actual planner request is prepared. Input projection only; no generation or turn created.`;
  if (count?.providerCount !== true || !Number.isSafeInteger(count.tokens) || count.tokens < 0 || !Number.isSafeInteger(result.manifest.inputAllowance)) {
    throw unavailable();
  }
  return `Provider input: ${format(count.tokens)} / ${format(result.manifest.inputAllowance)} tokens allowed · ${projection}. Count method: ${count.method}. Input counting only; no generation or turn created.`;
}
export function mergeTurnPages(current, incoming, limit = 200) {
  const turns = [...new Map([...current, ...incoming].map(turn => [turn.id, turn])).values()].sort((a, b) => a.sequence - b.sequence);
  return { turns: turns.slice(-limit), omitted: Math.max(0, turns.length - limit) };
}
export function exactSourceSelection(hit, start, end) {
  const length = hit.excerpt.length;
  if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start < 0 || end <= start || end > length) throw new Error('Select a nonempty passage within this source excerpt.');
  return { sourceId: hit.source.sourceId, startOffset: hit.startOffset + start, endOffset: hit.startOffset + end };
}
const recallNumericDefaults = Object.freeze({ threadRecallMaxCharacters: 8000, threadRecallTopK: 5, threadRecallNeighborRadius: 1, threadRecallRecentTurns: 2, threadRecallMaxCalls: 8 });
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
  if (settings.threadRecallEnabled && settings.threadRecallMode === 'agent' && settings.mode !== 'harness') return 'Agent-directed history recall requires Harness mode.';
  if (settings.threadRecallEnabled && (settings.threadRecallRecentTurns ?? 2) * 2 > settings.maxHistoryMessages) return 'The protected recent-turn floor cannot exceed the history-message limit.';
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
export function appendTraceWindow(trace, incoming, retained = false) {
  // Retained inspection has its own bounded window. Live arrivals can update
  // the tail without evicting the page an operator explicitly chose to read.
  const live = mergeTrace(trace.events, incoming); trace.events = live.events;
  if (!retained) trace.windowOmitted += live.omitted;
  if (retained) {
    const inspected = mergeTrace(trace.inspectionEvents, incoming);
    trace.inspectionEvents = inspected.events; trace.inspectionOmitted += inspected.omitted; trace.inspecting = true;
  } else if (incoming.some(event => Number.isSafeInteger(event.sequence) && event.sequence > trace.after)) trace.hasMore = true;
}
export async function resumeTraceTail(trace, readTail, isCurrent = () => true) {
  const result = await readTail();
  if (!isCurrent()) return null;
  const incoming = result.events ?? [];
  const last = incoming.at(-1)?.sequence ?? 0;
  // Retained head pages are not a live tail. Replace them with the latest
  // bounded service window, preserving SSE arrivals newer than this snapshot.
  trace.events = mergeTrace(incoming, trace.events.filter(event => event.sequence > last)).events;
  trace.windowOmitted = Math.max(0, (trace.events[0]?.sequence ?? 1) - 1);
  trace.inspecting = false;
  return result;
}

if (typeof document !== 'undefined') startChat();

function startChat() {
  const $ = id => document.getElementById(id);
  const state = { conversations: [], conversation: null, capabilities: [], models: null, dirty: false, instructionsDirty: false, busy: false, contextAction: null, generation: 0, streams: new Map(), traces: new Map(), nodes: new Map(), pendingSend: null, recallSelections: [], recallStatus: null, recallStatusLoading: false, recallRebuilding: false, source: null, historyPage: null, historyLoading: false, historyWindowOmitted: 0 };
  const json = value => JSON.stringify(value ?? null, null, 2);
  const node = (tag, className, text) => { const value = document.createElement(tag); if (className) value.className = className; if (text !== undefined) value.textContent = text; return value; };
  const label = value => String(value ?? 'unknown').replaceAll('_', ' ').replaceAll('.', ' ');
  const timestamp = value => { const date = new Date(value); return Number.isNaN(date.valueOf()) ? String(value ?? '') : date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }); };
  const statusClass = value => /complete|succeed/i.test(value) ? 'success' : /fail|error|cancel|interrupt|indeterminate/i.test(value) ? 'error' : /running|queued|active/i.test(value) ? 'running' : 'neutral';
  const badge = value => node('span', `badge ${statusClass(value)}`, label(value));
  const notice = message => { $('chat-notice').hidden = !message; $('chat-notice').textContent = message ?? ''; };
  const clearContextCheck = () => { $('context-check-result').hidden = true; $('context-check-result').textContent = ''; };
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
    maxProviderCalls: 'max-provider-calls', timeoutSeconds: 'timeout-seconds', maxWebSearchCalls: 'max-web-search-calls',
    threadRecallMaxCharacters: 'thread-recall-max-characters', threadRecallTopK: 'thread-recall-top-k', threadRecallNeighborRadius: 'thread-recall-neighbor-radius', threadRecallRecentTurns: 'thread-recall-recent-turns', threadRecallMaxCalls: 'thread-recall-max-calls'
  };
  function settingsFromForm() {
    return {
      ...state.conversation.settings,
      mode: $('mode').value, model: $('model').value.trim(), reasoningEffort: $('reasoning-effort').value || null,
      instructions: instructionValue(state.conversation.settings.instructions, $('instructions').value, state.instructionsDirty), reasoningSummary: $('reasoning-summary').checked,
      ambientWebSearch: $('mode').value === 'clean-room' && $('ambient-web-search').checked,
      threadRecallEnabled: $('thread-recall-enabled').checked, threadSemanticRecallEnabled: $('thread-semantic-recall-enabled').checked, threadRecallMode: $('thread-recall-mode').value, threadRecallQuery: $('thread-recall-query').value,
      threadRecallSelections: state.recallSelections.map(({ sourceId, startOffset, endOffset }) => ({ sourceId, startOffset, endOffset })),
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
    $('thread-recall-enabled').checked = Boolean(settings.threadRecallEnabled); $('thread-recall-mode').value = settings.threadRecallMode ?? 'auto'; $('thread-recall-query').value = settings.threadRecallQuery ?? '';
    $('thread-semantic-recall-enabled').checked = Boolean(settings.threadSemanticRecallEnabled);
    state.recallSelections = (settings.threadRecallSelections ?? []).map(selection => ({ ...selection }));
    $('context-depth').value = settings.contextDepth;
    for (const [key, id] of Object.entries(numericFields)) $(id).value = settings[key] ?? recallNumericDefaults[key] ?? (key === 'maxWebSearchCalls' ? 3 : '');
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
    $('thread-recall-agent-option').disabled = !harness;
    if (!harness && $('thread-recall-mode').value === 'agent') $('thread-recall-mode').value = 'auto';
    updateRecallUI();
  }
  function updateControls() {
    const selected = Boolean(state.conversation); const running = Boolean(activeTurn());
    if (state.dirty) clearContextCheck();
    $('settings-fields').disabled = !selected || state.busy || running;
    $('save-settings').disabled = !selected || !state.dirty || state.busy || running;
    $('reload-settings').disabled = !selected || state.busy || running;
    $('rename-chat').disabled = !selected || state.busy;
    $('message').disabled = !selected || state.busy || running;
    $('send-message').disabled = !selected || state.busy || running || !$('message').value.trim();
    $('check-context').disabled = !selected || state.busy || running || !$('message').value.trim();
    $('send-message').textContent = state.contextAction === 'send' ? 'Checking context…' : 'Send ↑';
    $('check-context').textContent = state.contextAction === 'check' ? state.conversation?.settings.mode === 'harness' ? 'Checking projection…' : 'Counting input…' : 'Check context';
    $('check-context').title = state.conversation?.settings.mode === 'harness' ? 'Validate the saved Harness input projection. Provider token counting waits for the actual planner request. Does not create a turn or generate a response.' : 'Count provider input tokens for the saved configuration. Does not create a turn or generate a response.';
    $('new-chat').disabled = state.busy;
    $('cancel-turn').hidden = !running; $('cancel-turn').disabled = state.busy;
    $('transcript').setAttribute('aria-busy', String(running));
    $('save-status').textContent = selected ? state.dirty ? 'Unsaved changes · saved before sending or switching.' : `Saved · revision ${state.conversation.settingsRevision}` : 'No conversation selected.';
    $('composer-status').textContent = state.contextAction === 'send' ? 'Checking context; waiting for turn admission.' : state.contextAction === 'check' ? state.conversation?.settings.mode === 'harness' ? 'Checking the Harness input projection. Provider token counting waits for the actual planner request; no generation or turn is being created.' : 'Counting provider input tokens only. No generation request or turn is being created.' : running ? 'Turn running · settings are frozen for this invocation.' : selected ? state.dirty ? 'The next turn will use your edited configuration.' : 'Saved conversation · only its own context is eligible.' : 'Select or create a conversation.';
    if (selected) $('conversation-subtitle').textContent = `${state.conversation.settings.mode === 'harness' ? 'Agentica Harness' : 'Clean Room'} · ${state.conversation.settings.model} · ${state.conversation.settings.reasoningEffort ?? 'model default'} effort · ${state.conversation.settings.threadRecallEnabled ? `thread recall ${state.conversation.settings.threadRecallMode} · conditioned` : 'thread recall off'} · personal memory off`;
    $('pin-history-source').disabled = !selected || running || state.busy || !$('thread-recall-enabled').checked;
    renderHistoryPaging();
  }
  async function saveSettings() {
    if (!state.dirty || !state.conversation) return;
    const settings = settingsFromForm(); const problem = validateSettings(settings); if (problem) throw new Error(problem);
    if (!$('settings-form').reportValidity()) throw new Error('Check the highlighted configuration fields.');
    const updated = await api(`${base()}/settings`, 'PUT', { expectedRevision: state.conversation.settingsRevision, settings });
    // Keep the durable transcript, which may have advanced while the settings saved.
    state.conversation = { ...updated, turns: state.conversation.turns }; fillSettings(); upsertConversation(updated);
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
    const conversation = await api(`/api/chats/${encodeURIComponent(id)}?limit=20`);
    if (generation !== state.generation) return;
    stopStreams(); state.conversation = conversation; state.traces.clear(); state.nodes.clear(); state.pendingSend = null; clearContextCheck();
    state.historyPage = conversation.historyPage ?? null; state.historyWindowOmitted = 0; state.recallStatus = null; state.recallStatusLoading = false; state.source = null;
    state.recallRebuilding = false; $('rebuild-recall-index').disabled = false; $('cancel-recall-rebuild').hidden = true; capabilityPreviewSignature = ''; capabilityPreviewGeneration++;
    if ($('history-source-dialog').open) $('history-source-dialog').close();
    $('thread-history-results').replaceChildren(); $('thread-history-search-status').textContent = '';
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
    return { events: [], inspectionEvents: [], inspecting: false, inspectionOmitted: 0, citationEvents: [], citationsLoading: false, citationsHydrated: false, citationCoverage: '', after: 0, hasMore: true, loaded: false, loading: false, liveLast: turn.traceCount ?? 0, windowOmitted: 0, streamError: '', gap: '', renderPending: false, payloads: new Map() };
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
    const user = node('div', 'user-message'); const userContent = node('div', 'message-content markdown-body');
    renderMarkdown(userContent, turn.userText ?? '');
    user.append(node('div', 'message-label', 'You'), userContent); article.append(user);
    const assistant = node('div', 'assistant-message'); const assistantLabel = node('div', 'message-label', 'Agentica Lab');
    const content = node('div', 'message-content markdown-body'); const placeholder = node('div', 'assistant-placeholder');
    const answerPending = node('button', 'work-inspect', 'New answer text · show latest'); answerPending.type = 'button'; answerPending.hidden = true;
    const workStatus = node('p', 'work-status'); workStatus.setAttribute('role', 'status');
    const workExcerpt = node('p', 'work-excerpt');
    const work = node('details', 'turn-work'); const workLabel = node('summary', '', 'Work');
    const workBody = node('div', 'work-body'); const workBlocks = node('div', 'work-blocks'); const workOperations = node('div', 'work-operations');
    const workCoverage = node('p', 'trace-coverage'); const workRead = node('button', 'button secondary', 'Read retained work'); workRead.type = 'button';
    workBody.append(workOperations, workBlocks, workCoverage, workRead); work.append(workLabel, workBody);
    const meta = node('div', 'turn-meta'); const error = node('div', 'turn-error'); const continuity = node('div', 'continuity-label'); const answerSources = node('div', 'answer-sources');
    assistant.append(assistantLabel, workStatus, workExcerpt, work, content, answerPending, placeholder, answerSources, meta, error, continuity); article.append(assistant);
    const trace = node('details', 'turn-trace'); const traceLabel = node('summary'); trace.append(traceLabel);
    const traceContent = node('div', 'trace-content'); const activity = node('div', 'trace-activity');
    const summary = node('section', 'trace-summary'); summary.append(node('h4', '', 'Provider reasoning summary')); const summaryText = node('pre'); summary.append(summaryText);
    const absence = node('p', 'trace-absence'); const coverage = node('p', 'trace-coverage');
    const attemptInspector = makeInspector('Provider attempts & recovery'); const requestInspector = makeInspector('Effective provider request'); const contextInspector = makeInspector('Context manifest'); const nativeInspector = makeInspector('Native output items · sanitized'); const settingsInspector = makeInspector('Invocation settings snapshot');
    const searchInspector = makeInspector('Web search & sources'); const recallInspector = makeInspector('Thread recall evidence manifest'); recallInspector.links = node('div', 'recall-results'); recallInspector.section.append(recallInspector.links);
    const sourceInspector = makeInspector('Provider source records'); sourceInspector.body.remove();
    sourceInspector.section.append(node('p', 'trace-coverage', 'Allowed provider source events, with sensitive fields redacted. Private reasoning and opaque continuation carriers are not exposed. These records are separate from the normalized timeline and are not a replay carrier.'));
    sourceInspector.records = node('ol', 'typed-timeline'); sourceInspector.section.append(sourceInspector.records);
    settingsInspector.body.textContent = json({ settingsRevision: turn.settingsRevision, settings: turn.settings });
    const timelineSection = node('details', 'trace-inspector'); const timelineLabel = node('summary', '', 'Typed timeline'); timelineSection.append(timelineLabel); const timeline = node('ol', 'typed-timeline'); timelineSection.append(timeline);
    const pageControls = node('div', 'trace-page-controls'); const load = node('button', 'button secondary', 'Load retained trace'); load.type = 'button';
    const restart = node('button', 'button secondary', 'Read from start'); restart.type = 'button'; restart.title = 'Reset only the browser trace window and reread retained pages. Makes no provider call.';
    const resumeLive = node('button', 'button secondary', 'Resume live'); resumeLive.type = 'button'; resumeLive.title = 'Show the latest bounded trace tail. Retained inspection remains available. Makes no provider call.';
    const pageInfo = node('span'); pageControls.append(load, restart, resumeLive, pageInfo);
    traceContent.append(activity, summary, absence, coverage, attemptInspector.section, requestInspector.section, contextInspector.section, recallInspector.section, searchInspector.section, nativeInspector.section, sourceInspector.section, settingsInspector.section, timelineSection, pageControls); trace.append(traceContent); article.append(trace);
    const entry = { article, content, placeholder, answerSources, meta, error, continuity, trace, traceLabel, activity, summary, summaryText, absence, coverage, timeline, timelineSection, timelineLabel, attemptInspector, requestInspector, contextInspector, nativeInspector, sourceInspector, searchInspector, recallInspector, load, resumeLive, pageInfo,
      work, workLabel, workStatus, workExcerpt, workBlocks, workOperations, workCoverage, workRead, workNodes: new Map(), answerPending };
    state.nodes.set(turn.id, entry); state.traces.set(turn.id, newTrace(turn));
    trace.addEventListener('toggle', () => { if (trace.open && !state.traces.get(turn.id)?.loaded) loadTrace(turn.id); });
    work.addEventListener('toggle', () => {
      if (work.open && !state.traces.get(turn.id)?.loaded) loadTraceTail(turn.id);
      renderTrace(turn.id);
    });
    workRead.addEventListener('click', () => { if (state.traces.get(turn.id)?.after) restart.click(); else loadTrace(turn.id); });
    answerPending.addEventListener('click', () => { document.getSelection()?.removeAllRanges(); renderAnswer(turn, entry); });
    for (const inspector of [timelineSection, attemptInspector.section, requestInspector.section, contextInspector.section, nativeInspector.section, sourceInspector.section, searchInspector.section, recallInspector.section]) inspector.addEventListener('toggle', () => { if (inspector.open) renderTrace(turn.id); });
    load.addEventListener('click', () => loadTrace(turn.id));
    restart.addEventListener('click', () => {
      const retained = state.traces.get(turn.id); if (!retained || retained.loading) return;
      retained.inspectionEvents = []; retained.after = 0; retained.loaded = false; retained.hasMore = true; retained.inspectionOmitted = 0;
      loadTrace(turn.id);
    });
    resumeLive.addEventListener('click', () => loadTraceTail(turn.id));
    $('transcript').append(article); updateTurn(turn); renderTrace(turn.id);
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
    const events = [...new Map([...(retained?.events ?? []), ...(retained?.inspectionEvents ?? []), ...(retained?.citationEvents ?? [])].map(event => [event.sequence, event])).values()];
    const text = turn.assistantText ?? ''; const presentation = citationPresentation(text, events);
    const signature = JSON.stringify(presentation.inline);
    if (elements.renderedText !== text || elements.renderedCitations !== signature) {
      if (selectionIn(elements.content)) elements.answerPending.hidden = false;
      else {
      elements.markdown = renderMarkdown(elements.content, text, { citations: presentation.inline, createCitation: citation => sourceLink(citation, true) });
      elements.renderedText = text; elements.renderedCitations = signature;
      elements.answerPending.hidden = true;
      }
    }
    elements.content.hidden = !text; elements.answerSources.replaceChildren(); elements.answerSources.hidden = !presentation.sources.length && !retained?.citationCoverage;
    if (retained?.citationCoverage) elements.answerSources.append(node('p', 'micro', retained.citationCoverage));
    for (const target of ['assistant', 'tool']) {
      const sources = presentation.sources.filter(source => source.target === target); if (!sources.length) continue;
      const section = node('section', `citation-sources ${target}-sources`); section.append(node('h4', '', target === 'tool' ? 'Tool research sources' : 'Answer sources'));
      if (target === 'tool') section.append(node('p', 'micro', 'Search-tool evidence. These source ranges do not annotate the final answer.'));
      if (target === 'assistant' && (presentation.unplaced || elements.markdown?.unplacedCitations)) section.append(node('p', 'micro', 'Source links are preserved below; inline placement is unavailable for some provider offsets.'));
      const list = node('ul'); for (const source of sources) { const item = node('li'); item.append(sourceLink(source), node('span', 'source-domain', new URL(source.url).hostname)); list.append(item); }
      section.append(list); elements.answerSources.append(section);
    }
  }
  function updateTurn(turn) {
    const elements = state.nodes.get(turn.id); if (!elements) return;
    renderAnswer(turn, elements);
    elements.placeholder.hidden = Boolean(turn.assistantText); elements.placeholder.textContent = isTerminal(turn.status) ? 'No assistant text emitted.' : 'Waiting for provider output…';
    elements.meta.replaceChildren(badge(turn.status), node('span', '', `${turn.settings?.model ?? 'OpenAI'} · ${turn.settings?.reasoningEffort ?? 'default'} effort`), node('span', '', `Configuration r${turn.settingsRevision}`));
    if (turn.settings?.threadRecallEnabled) elements.meta.append(node('span', 'recall-treatment', `Conditioned · thread recall ${turn.settings.threadRecallMode}`));
    if (turn.finishedAt) elements.meta.append(node('span', '', `${Math.max(0, (new Date(turn.finishedAt) - new Date(turn.createdAt)) / 1000).toFixed(1)}s`));
    if (turn.usage) { const usage = node('span', '', usageLabel(turn.usage)); usage.title = json(turn.usage); elements.meta.append(usage); }
    elements.error.textContent = turn.errorMessage ? `${turn.errorCode ? `${turn.errorCode}: ` : ''}${turn.errorMessage}` : '';
    elements.continuity.textContent = turn.continuity ? `Continuity: ${label(turn.continuity)}. This is distinct from provider-native reasoning replay.` : '';
    elements.traceLabel.textContent = `${isTerminal(turn.status) ? 'Inspect turn' : 'Live trace'} · ${(turn.traceCount ?? 0).toLocaleString()} retained events${turn.droppedTraceCount ? ` · ${turn.droppedTraceCount} dropped` : ''}`;
    if (isTerminal(turn.status) && (turn.settings?.ambientWebSearch || turn.settings?.enabledTools?.includes('web.search'))) hydrateCitations(turn.id);
  }
  function appendEvents(id, events, retained = false) {
    const trace = state.traces.get(id); if (!trace) return;
    appendTraceWindow(trace, events, retained);
    if (!trace.renderPending) {
      trace.renderPending = true;
      requestAnimationFrame(() => {
        if (state.traces.get(id) !== trace) return;
        trace.renderPending = false; const turn = turnById(id); if (turn) updateTurn(turn); renderTrace(id);
        if (trace.followOutput) { trace.followOutput = false; if (!selectionIn($('transcript'))) scrollLatest(true); }
      });
    }
  }
  async function retainedPayload(id, event) {
    const retained = state.traces.get(id); if (!retained) return undefined;
    if (retained.payloads.has(event.sequence)) return retained.payloads.get(event.sequence);
    const generation = state.generation; const data = await api(`${turnBase(id)}/events/${event.sequence}/payload`);
    if (generation !== state.generation) return undefined;
    retained.payloads.set(event.sequence, data); if (retained.payloads.size > 8) retained.payloads.delete(retained.payloads.keys().next().value);
    return data;
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
          const data = await retainedPayload(id, event);
          if (generation !== state.generation) return;
          // Explicit inspection is bounded independently of the event window.
          // Evicted payloads can be read again from the durable service record.
          payload.textContent = json(data); full.remove();
        } catch (error) { if (generation === state.generation) { notice(`Cannot load retained record: ${error.message}`); full.disabled = false; } }
      }); detail.append(full);
    }
    li.append(detail); return li;
  }
  function renderTrace(id) {
    const trace = state.traces.get(id); const elements = state.nodes.get(id); const turn = turnById(id); if (!trace || !elements || !turn) return;
    renderAnswer(turn, elements);
    const events = trace.inspecting ? trace.inspectionEvents : trace.events;
    const omitted = trace.inspecting ? trace.inspectionOmitted : trace.windowOmitted;
    const fullCoverage = events.length === (turn.traceCount ?? 0) && (!events.length || (events[0].sequence === 1 && events.at(-1).sequence === turn.traceCount));
    renderWork(turn, elements, events, fullCoverage, trace);
    const summary = summarizeTrace(events); elements.summary.hidden = !summary; elements.summaryText.textContent = summary;
    elements.absence.hidden = Boolean(summary);
    elements.absence.textContent = !turn.settings?.reasoningSummary ? 'Provider reasoning summary was not requested. Private reasoning is not exposed.' : !isTerminal(turn.status) ? 'Summary requested; none emitted in the loaded trace yet.' : !fullCoverage ? 'Summary status is incomplete in this trace window; inspect retained pages before inferring absence.' : 'Provider reasoning summary was not emitted or is unsupported. Private reasoning is not exposed.';
    const latest = trace.events.at(-1); elements.activity.replaceChildren(node('span', `status-dot${isTerminal(turn.status) ? '' : ' busy'}`), node('span', '', latest ? `Latest activity: ${label(latest.type)}` : 'Waiting for retained trace'));
    const coverage = [trace.streamError, trace.gap, trace.inspecting ? 'Inspecting retained pages. Live activity continues separately; Resume live shows the latest tail.' : '', summary && !fullCoverage ? 'The summary shown covers this window only; inspect retained pages for other summary events.' : '', omitted ? `${omitted} earlier events are outside this bounded ${trace.inspecting ? 'inspection' : 'live'} window; the service trace remains available through Read from start.` : '', turn.droppedTraceCount ? `${turn.droppedTraceCount} service trace events were dropped. Coverage is incomplete.` : ''].filter(Boolean);
    elements.coverage.textContent = coverage.join(' '); elements.coverage.hidden = !coverage.length; elements.coverage.classList.toggle('warning', Boolean(coverage.length));
    if (elements.attemptInspector.section.open) {
      const attempts = events.filter(event => event.type === 'provider.attempt');
      elements.attemptInspector.body.textContent = json({
        savedUsage: turn.usage ?? null,
        attempts: attempts.map(event => ({ callId: event.callId, data: event.data })),
        note: 'Provider status and observed usage are distinct from delivery and host acceptance. Unknown usage is not zero. Unresolved dispatch is never automatically retried.'
      });
    }
    if (elements.requestInspector.section.open) {
      const requests = events.filter(event => event.type === 'provider.request');
      elements.requestInspector.body.textContent = requests.length ? json(requests.map(event => ({ sequence: event.sequence, callId: event.callId, data: event.data }))) : 'No provider request loaded. A pre-dispatch failure may have no request.';
    }
    if (elements.contextInspector.section.open) {
      const manifests = events.filter(event => event.type === 'context.manifest'); elements.contextInspector.body.textContent = manifests.length ? json(manifests.map(event => event.data)) : 'No context manifest loaded.';
    }
    if (elements.nativeInspector.section.open) {
      const items = events.filter(event => event.type === 'provider.item'); elements.nativeInspector.body.textContent = items.length ? json(items.map(event => ({ callId: event.callId, itemId: event.itemId, data: event.data }))) : 'No native items loaded. Opaque continuation material is not displayed.';
    }
    if (elements.searchInspector.section.open) {
      const searches = [...new Map([...events, ...trace.citationEvents].filter(event => event.type === 'provider.citations' || /web.search|web_search/.test(event.type)).map(event => [event.sequence, event])).values()].sort((a, b) => a.sequence - b.sequence);
      elements.searchInspector.body.textContent = searches.length ? json(searches.map(event => ({ sequence: event.sequence, callId: event.callId, itemId: event.itemId, type: event.type, data: event.data }))) : 'No search/source events loaded. Search may be disabled or unused; inspect retained coverage before inferring absence.';
    }
    if (elements.recallInspector.section.open) {
      const recalls = events.filter(event => event.type === 'context.recall' || /^recall\./.test(event.type)).map(event => ({ ...event, data: trace.payloads.get(event.sequence) ?? event.data }));
      elements.recallInspector.body.textContent = recalls.length ? json(recalls.map(event => ({ sequence: event.sequence, type: event.type, callId: event.callId, data: event.data }))) : 'No recall manifest loaded. Thread recall may be off; inspect trace coverage before inferring absence.';
      elements.recallInspector.links.replaceChildren(...recalls.flatMap(event => {
        if (event.data?.payloadRef) { const load = node('button', 'button secondary', 'Load retained recall manifest'); load.type = 'button'; load.addEventListener('click', async () => { load.disabled = true; try { await retainedPayload(id, event); renderTrace(id); } catch (error) { load.disabled = false; notice(`Cannot load recall manifest: ${error.message}`); } }); return [load]; }
        return (event.data?.selected ?? event.data?.hits ?? []).filter(evidence => evidence.sourceId).map(evidence => {
          const attribution = evidence.semanticCitation ? ` · ${evidence.semanticCitation.perspective}: ${evidence.semanticCitation.authorId}` : '';
          return sourceButton(evidence.sourceId, evidence.startOffset, evidence.endOffset, `Open original · turn ${evidence.turnSequence} ${evidence.role}${attribution} · UTF-16 [${evidence.startOffset}, ${evidence.endOffset})`);
        });
      }));
    }
    if (elements.sourceInspector.section.open) {
      const records = events.filter(event => event.type === 'provider.record');
      const open = new Set([...elements.sourceInspector.records.querySelectorAll('details[open]')].map(detail => detail.dataset.sequence));
      elements.sourceInspector.records.replaceChildren(...(records.length ? records.map(event => eventElement(id, event, open, true)) : [node('li', 'trace-coverage', 'No provider source records loaded. Inspect trace coverage before inferring absence.')]));
    }
    const normalized = events.filter(event => event.type !== 'provider.record');
    elements.timelineLabel.textContent = `Typed timeline · ${normalized.length.toLocaleString()} normalized events`;
    if (elements.timelineSection.open) {
      const open = new Set([...elements.timeline.querySelectorAll('details[open]')].map(detail => detail.dataset.sequence));
      elements.timeline.replaceChildren(...normalized.map(event => eventElement(id, event, open)));
    }
    elements.load.disabled = trace.loading || fullCoverage || (trace.loaded && !trace.hasMore);
    elements.resumeLive.hidden = !trace.inspecting;
    elements.resumeLive.disabled = trace.loading;
    elements.load.textContent = trace.loading ? 'Loading…' : fullCoverage ? 'Retained trace loaded' : !trace.loaded ? 'Load retained trace' : trace.hasMore ? 'Load next 100 events' : 'Retained trace loaded';
    const range = events.length ? ` · sequences ${events[0].sequence}–${events.at(-1).sequence}` : '';
    elements.pageInfo.textContent = `${trace.inspecting ? 'Retained inspection' : 'Live tail'} · ${events.length.toLocaleString()} visible${range} · ${fullCoverage ? 'full retained sequence loaded' : `page cursor ${trace.after}`}`;
  }
  function renderWork(turn, elements, events, fullCoverage, trace) {
    const work = readableWork(events, turn.status); const finished = isTerminal(turn.status);
    const current = work.operations.at(-1);
    const status = turn.assistantText ? 'Writing the answer' : current?.status === 'running' ? `Running ${current.title}` : work.blocks.length ? 'Working through the request' : 'Waiting for provider response';
    if (elements.workStatus.textContent !== status) elements.workStatus.textContent = status;
    elements.workStatus.hidden = finished;
    const excerpt = recentWorkExcerpt(work.blocks.at(-1)?.text);
    if (elements.workExcerpt.textContent !== excerpt && !selectionIn(elements.workExcerpt)) elements.workExcerpt.textContent = excerpt;
    elements.workExcerpt.hidden = finished || !excerpt || elements.work.open;
    elements.work.hidden = !events.length && !turn.traceCount;
    elements.workLabel.textContent = `Work${work.blocks.length ? ` · ${work.blocks.length} summary section${work.blocks.length === 1 ? '' : 's'}` : ''}${work.operations.length ? ` · ${work.operations.length} observed operation${work.operations.length === 1 ? '' : 's'}` : ''}`;
    if (!elements.work.open) return;
    const keys = new Set(); let blockPosition = 0;
    for (const block of work.blocks) {
      keys.add(block.key); let view = elements.workNodes.get(block.key);
      if (!view) {
        const article = node('section', 'work-block'); const heading = node('h4', '', 'Reasoning summary'); const state = node('p', 'work-block-state'); const body = node('div', 'markdown-body');
        const inspect = node('button', 'work-inspect', 'Inspect fragments'); inspect.type = 'button';
        const latest = node('button', 'work-inspect', 'New summary text · show latest'); latest.type = 'button'; latest.hidden = true;
        latest.addEventListener('click', () => { document.getSelection()?.removeAllRanges(); renderTrace(turn.id); });
        inspect.addEventListener('click', () => { elements.trace.open = true; elements.timelineSection.open = true; renderTrace(turn.id); });
        article.append(heading, state, body, latest, inspect); view = { article, state, body, latest }; elements.workNodes.set(block.key, view);
      }
      const notes = [block.reconstructed ? 'Reconstructed grouping' : '', block.gap ? 'Provider snapshot · fragment coverage differs' : '',
        block.status === 'incomplete' ? 'Incomplete' : block.status === 'end-not-observed' ? 'End boundary not observed' : ''].filter(Boolean).join(' · ');
      if (view.state.textContent !== notes) view.state.textContent = notes;
      view.state.hidden = !notes;
      if (view.text !== block.text) {
        if (selectionIn(view.body)) view.latest.hidden = false;
        else { renderMarkdown(view.body, block.text); view.text = block.text; view.latest.hidden = true; }
      }
      if (elements.workBlocks.children[blockPosition] !== view.article)
        elements.workBlocks.insertBefore(view.article, elements.workBlocks.children[blockPosition] ?? null);
      blockPosition++;
    }
    for (const [key, view] of elements.workNodes) if (!keys.has(key)) { view.article.remove(); elements.workNodes.delete(key); }
    const operationSignature = JSON.stringify(work.operations);
    if (elements.operationSignature !== operationSignature) {
      elements.workOperations.replaceChildren(...work.operations.map(operation => {
        const row = node('section', 'work-operation'); row.append(node('strong', '', operation.title), node('span', 'work-operation-status', operation.status));
        if (operation.preview) row.append(node('p', '', Array.from(operation.preview).slice(0, 700).join('')));
        return row;
      }));
      elements.operationSignature = operationSignature;
    }
    const coverage = [!fullCoverage ? 'Showing work from the loaded trace window. Read retained work for earlier evidence.' : '', trace.gap, trace.streamError,
      turn.droppedTraceCount ? `${turn.droppedTraceCount} events exceeded service retention; this view has a capture gap.` : ''].filter(Boolean).join(' ');
    elements.workCoverage.textContent = coverage; elements.workCoverage.hidden = !coverage;
    elements.workRead.hidden = fullCoverage; elements.workRead.disabled = trace.loading;
  }
  function selectionIn(element) {
    const selection = document.getSelection();
    return selection && !selection.isCollapsed && (element.contains(selection.anchorNode) || element.contains(selection.focusNode));
  }
  async function loadTrace(id) {
    const trace = state.traces.get(id); if (!trace || trace.loading || (trace.loaded && !trace.hasMore)) return;
    const generation = state.generation; trace.loading = true; renderTrace(id);
    try {
      const result = await api(`${turnBase(id)}/trace?after=${trace.after}&limit=100`);
      if (generation !== state.generation) return;
      trace.loaded = true; trace.after = result.next ?? result.events?.at(-1)?.sequence ?? trace.after;
      trace.hasMore = Boolean(result.hasMore) || trace.after < (turnById(id)?.traceCount ?? 0);
      appendEvents(id, result.events ?? [], true);
    } catch (error) { if (generation === state.generation) { trace.streamError = `Retained trace unavailable: ${error.message}`; notice(trace.streamError); } }
    finally { trace.loading = false; if (generation === state.generation) renderTrace(id); }
  }
  async function loadTraceTail(id) {
    const trace = state.traces.get(id); if (!trace || trace.loading) return;
    const generation = state.generation; trace.loading = true; renderTrace(id);
    try {
      const result = await resumeTraceTail(trace,
        () => api(`${turnBase(id)}/trace?tail=true&limit=100`),
        () => generation === state.generation && state.traces.get(id) === trace);
      if (!result) return;
      const turn = turnById(id); if (turn) { turn.traceCount = Math.max(turn.traceCount ?? 0, result.next ?? 0); updateTurn(turn); }
      trace.streamError = '';
    } catch (error) { if (generation === state.generation) { trace.streamError = `Latest trace unavailable: ${error.message}`; notice(trace.streamError); } }
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
      const stick = nearBottom() && !selectionIn($('transcript'));
      if (trace.liveLast && value.sequence > trace.liveLast + 1) trace.gap = `Stream sequence gap: expected ${trace.liveLast + 1}, received ${value.sequence}. Inspect the retained trace.`;
      trace.liveLast = value.sequence; turn.traceCount = Math.max(turn.traceCount ?? 0, value.sequence);
      appendEvents(turn.id, [value]);
      if (value.type === 'assistant.delta') turn.assistantText = (turn.assistantText ?? '') + (value.data?.text ?? '');
      if (/^turn\.(completed|failed|cancelled|canceled|interrupted|indeterminate)$/.test(value.type)) {
        source.close(); state.streams.delete(turn.id);
        if (value.data?.id === turn.id) Object.assign(turn, value.data);
        else turn.status = value.type.split('.')[1];
        updateTurn(turn); renderTrace(turn.id); updateControls();
        reconcileConversation(generation).catch(error => notice(`The turn ended, but refreshing its durable result failed: ${error.message}`));
      }
      if (stick) trace.followOutput = true; else $('jump-container').hidden = false;
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
    const conversation = await api(`${base()}?limit=20`); if (generation !== state.generation) return;
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
    state.conversation.updatedAt = conversation.updatedAt;
    if (conversation.historyPage) state.historyPage = { ...conversation.historyPage, nextBeforeSequence: state.historyPage ? state.historyPage.nextBeforeSequence : conversation.historyPage.nextBeforeSequence };
    reorderLoadedTurns(); upsertConversation(state.conversation); updateControls();
  }
  async function sendMessage() {
    if (!state.conversation || activeTurn()) return;
    const content = $('message').value; if (!content.trim()) return;
    await saveSettings();
    const pending = state.pendingSend?.content === content ? state.pendingSend : { content, requestId: crypto.randomUUID() };
    state.pendingSend = pending;
    clearContextCheck(); state.contextAction = 'send'; updateControls();
    let turn;
    try { turn = await api(`${base()}/turns`, 'POST', pending); }
    finally { state.contextAction = null; updateControls(); }
    state.pendingSend = null; $('message').value = ''; $('message').style.height = '';
    if (!state.conversation.turns.length) $('transcript').replaceChildren();
    const existing = turnById(turn.id);
    if (state.historyPage && turn.sequence > state.historyPage.latestSequence) state.historyPage = { ...state.historyPage, totalTurns: state.historyPage.totalTurns + 1, latestSequence: turn.sequence };
    if (!existing) { state.conversation.turns.push(turn); renderTurn(turn); }
    else Object.assign(existing, turn);
    if (!isTerminal(turn.status)) connectTurn(existing ?? turn);
    reorderLoadedTurns(); updateControls(); scrollLatest(true); notice(null);
  }
  async function checkContext() {
    if (!state.conversation || activeTurn()) return;
    clearContextCheck();
    if (state.dirty) throw new Error('Save configuration before checking context. Your message draft is preserved.');
    const content = $('message').value; if (!content.trim()) return;
    state.contextAction = 'check'; updateControls(); notice(null);
    try {
      const result = await api(`${base()}/context-check`, 'POST', { content });
      $('context-check-result').textContent = contextCheckSummary(result, state.conversation.settings); $('context-check-result').hidden = false;
    } finally { state.contextAction = null; updateControls(); }
  }
  async function runAction(action) {
    if (state.busy) return;
    state.busy = true; updateControls();
    try { await action(); }
    catch (error) {
      notice(actionErrorMessage(error));
    } finally { state.busy = false; updateControls(); }
  }
  function renderCapabilities() {
    $('capability-list').replaceChildren(...(state.capabilities.length ? state.capabilities.map(capability => {
      const wrapper = node('div', 'capability'); const labelNode = node('label', 'check'); const input = node('input'); input.type = 'checkbox'; input.value = capability.id;
      const description = node('div'); const name = node('strong', '', capability.name); name.append(node('span', 'badge neutral', capability.effect)); description.append(name, node('p', '', capability.description), node('small', '', `${capability.id} · ${capability.source} · v${capability.version}`));
      labelNode.append(input, description); wrapper.append(labelNode); return wrapper;
    }) : [node('p', 'micro', 'No reusable capabilities are registered.')]));
  }
  function updateRecallUI() {
    const enabled = $('thread-recall-enabled').checked;
    $('thread-recall-controls').hidden = !enabled; $('thread-recall-summary').textContent = enabled ? `${$('thread-recall-mode').value} · ${state.recallSelections.length} pins · this conversation only` : 'off';
    $('thread-recall-condition').textContent = !enabled ? 'Off is the baseline. Enabling recall is a conditioned experiment using only this saved conversation. Personal/governed memory stays disabled.' : $('thread-recall-mode').value === 'agent' ? 'Conditioned Harness experiment. Bind conversation.read/search explicitly below for model-directed recall; this switch does not select tools. Personal/governed memory stays disabled.' : $('thread-recall-mode').value === 'manual' ? 'Conditioned experiment using operator-pinned exact passages. Search/read actions below are local diagnostics and make no model call.' : 'Conditioned experiment using deterministic local lookup. A blank override uses the current user message; selection and omissions are receipted.';
    if (enabled && $('thread-semantic-recall-enabled').checked && $('thread-recall-mode').value !== 'manual') $('thread-recall-condition').textContent = 'Semantic conversation retrieval uses the existing OpenAI key after turn admission. User and agent perspectives retain exact source citations. Encoding is bounded; incomplete coverage is reported. Context checks defer semantic lookup. Personal/governed memory stays disabled.';
    $('memory-recall-status').textContent = enabled ? 'Thread recall is on for this conversation only. Personal/governed memory remains disabled.' : 'Thread recall is off. Saved history remains within this conversation.';
    $('thread-recall-pins').replaceChildren(...(state.recallSelections.length ? state.recallSelections.map((selection, index) => {
      const card = node('div', 'recall-hit'); card.append(sourceButton(selection.sourceId, selection.startOffset, selection.endOffset, `${selection.sourceId} · UTF-16 [${selection.startOffset}, ${selection.endOffset})`));
      const remove = node('button', 'button secondary', 'Remove pin'); remove.type = 'button'; remove.addEventListener('click', () => { state.recallSelections.splice(index, 1); state.dirty = true; updateRecallUI(); updateControls(); }); card.append(remove); return card;
    }) : [node('p', 'field-help', 'No exact passages selected. Search and open an original source to pin a range.')]));
    if (enabled && $('thread-recall-section').open && state.conversation && !state.recallStatus && !state.recallStatusLoading) checkRecallCoverage();
    refreshRecallCapabilityPreview();
  }
  let capabilityPreviewSignature = ''; let capabilityPreviewGeneration = 0;
  async function refreshRecallCapabilityPreview() {
    if (!state.conversation) return;
    const enabled = $('thread-recall-enabled').checked;
    const query = new URLSearchParams({ threadRecallEnabled: String(enabled), threadSemanticRecallEnabled: String($('thread-semantic-recall-enabled').checked), threadRecallMode: $('thread-recall-mode').value, mode: $('mode').value }); const signature = query.toString();
    if (signature === capabilityPreviewSignature) return;
    capabilityPreviewSignature = signature; const revision = ++capabilityPreviewGeneration; const generation = state.generation;
    try {
      const capabilities = await api(`/api/chats/capabilities?${query}`); if (generation !== state.generation || revision !== capabilityPreviewGeneration) return;
      const selected = new Set([...$('capability-list').querySelectorAll('input:checked')].map(input => input.value));
      state.capabilities = capabilities; renderCapabilities(); for (const input of $('capability-list').querySelectorAll('input')) input.checked = selected.has(input.value);
    } catch { if (generation === state.generation) $('thread-recall-readiness').textContent = 'Effective capability metadata is unavailable; retry coverage before running this condition.'; }
  }
  function coverageText(value) {
    if (!value) return 'Coverage unavailable; absence of a result is not proof of no match.';
    return `${value.status ?? 'unknown'} · ${value.indexedSourceCount ?? 0}/${value.eligibleSourceCount ?? 0} eligible sources indexed · as of turn ${value.asOfSequence ?? '?'} · last indexed ${value.lastIndexedSequence ?? '?'}${value.reason ? ` · ${value.reason}` : ''}`;
  }
  async function checkRecallCoverage() {
    if (!state.conversation || !$('thread-recall-enabled').checked || state.recallStatusLoading) return;
    const generation = state.generation; state.recallStatusLoading = true; $('thread-recall-readiness').textContent = 'Checking local index coverage…';
    try { const result = await api(`${base()}/history/status`); if (generation !== state.generation) return; state.recallStatus = result; $('thread-recall-readiness').textContent = `${coverageText(result.coverage)}${result.maintenance ? ` · maintenance ${result.maintenance.status}${result.maintenance.error ? `: ${result.maintenance.error}` : ''}` : ''}`; $('cancel-recall-rebuild').hidden = result.maintenance?.status?.toUpperCase() !== 'INDEXING'; }
    catch (error) { if (generation === state.generation) { state.recallStatus = { unavailable: true }; $('thread-recall-readiness').textContent = `Coverage unavailable: ${error.message}`; } }
    finally { if (generation === state.generation) state.recallStatusLoading = false; }
  }
  async function rebuildRecallIndex() {
    if (!state.conversation || !$('thread-recall-enabled').checked || state.recallRebuilding) return;
    const generation = state.generation; state.recallRebuilding = true; $('rebuild-recall-index').disabled = true; $('cancel-recall-rebuild').hidden = false; $('thread-recall-readiness').textContent = 'Rebuilding this conversation’s local lexical index. No model or embedding call.';
    try {
      const result = await api(`${base()}/history/rebuild`, 'POST', {}); if (generation !== state.generation) return;
      $('thread-recall-readiness').textContent = `${result.status}: ${result.indexedSources}/${result.eligibleSources} sources · last indexed ${result.lastIndexedSequence}`;
      state.recallStatus = null; await checkRecallCoverage();
      for (let poll = 0; poll < 120 && generation === state.generation && state.recallStatus?.maintenance?.status?.toUpperCase() === 'INDEXING'; poll++) { await new Promise(resolve => setTimeout(resolve, 500)); await checkRecallCoverage(); }
    }
    catch (error) { if (generation === state.generation) $('thread-recall-readiness').textContent = `Index rebuild did not complete: ${error.message}. Source history remains available.`; }
    finally { if (generation === state.generation) { state.recallRebuilding = false; $('rebuild-recall-index').disabled = false; $('cancel-recall-rebuild').hidden = true; } }
  }
  function sourceButton(sourceId, startOffset = 0, endOffset = null, title = null) {
    const button = node('button', 'button secondary source-reference', title ?? `Open original ${sourceId}`); button.type = 'button';
    button.addEventListener('click', () => runAction(() => openHistorySource(sourceId, startOffset, endOffset))); return button;
  }
  async function searchThreadHistory(offset = 0) {
    if (!state.conversation || !$('thread-recall-enabled').checked) return;
    const query = $('thread-history-query').value; if (!query.trim()) throw new Error('Enter a local history query.');
    const generation = state.generation; $('thread-history-results').replaceChildren(); $('thread-history-search-status').textContent = 'Searching this conversation’s canonical history…';
    const result = await api(`${base()}/history/search`, 'POST', { query, topK: Number($('thread-recall-top-k').value), maxCharacters: Number($('thread-recall-max-characters').value), neighborRadius: Number($('thread-recall-neighbor-radius').value), offset });
    if (generation !== state.generation) return;
    if (result.scopeRef !== state.conversation.id) throw new Error('The historical result does not belong to this conversation.');
    $('thread-history-search-status').textContent = `${result.status} · ${result.hits.length} returned · ${result.candidateCount} candidates · ${result.inspectedCount} inspected · ${coverageText(result.coverage)}${result.truncated ? ' · results truncated' : ''}${result.reasons?.length ? ` · ${result.reasons.join('; ')}` : ''}`;
    $('thread-history-results').replaceChildren(...result.hits.map(hit => {
      const card = node('div', 'recall-hit'); card.append(node('strong', '', `Turn ${hit.source.turnSequence} · ${hit.source.role}`), node('pre', 'recall-excerpt', hit.excerpt), sourceButton(hit.source.sourceId, hit.startOffset, hit.endOffset, `Read original · UTF-16 [${hit.startOffset}, ${hit.endOffset})`)); return card;
    }));
    if (result.nextOffset !== null && result.nextOffset !== undefined) { const more = node('button', 'button secondary', 'Next result page'); more.type = 'button'; more.addEventListener('click', () => runAction(() => searchThreadHistory(result.nextOffset))); $('thread-history-results').append(more); }
  }
  async function openHistorySource(sourceId, startOffset = 0, endOffset = null) {
    if (!state.conversation || typeof sourceId !== 'string' || !/^[a-f0-9]{32}:(user|assistant)$/i.test(sourceId)) throw new Error('Invalid canonical source reference.');
    const generation = state.generation; const conversationId = state.conversation.id;
    const length = endOffset === null ? 4000 : Math.max(1, Math.min(32000, endOffset - startOffset));
    const query = new URLSearchParams({ startOffset: String(startOffset), maxCharacters: String(length), neighborRadius: String(Number($('thread-recall-neighbor-radius').value) || 0) });
    const result = await api(`${base()}/history/messages/${encodeURIComponent(sourceId)}?${query}`);
    if (generation !== state.generation) return;
    if (result.scopeRef !== conversationId) throw new Error('The original source does not belong to this conversation.');
    const hit = result.hits.find(value => value.source.sourceId === sourceId);
    if (!hit) throw new Error(`Original source is ${result.status}. ${result.reasons?.join('; ') ?? ''}`);
    if (hit.startOffset !== startOffset) throw new Error('The original read did not preserve the requested source offset. No passage was pinned.');
    state.source = { conversationId, hit }; $('history-source-title').textContent = `Original turn ${hit.source.turnSequence} · ${hit.source.role}`;
    $('history-source-metadata').textContent = `${hit.source.createdAt} · ${hit.source.sourceId} · ${hit.source.contentHash} · ${hit.source.characterCount} UTF-16 code units`;
    $('history-source-start').value = hit.startOffset; $('history-source-length').value = Math.max(1, hit.endOffset - hit.startOffset); $('history-source-text').value = hit.excerpt;
    $('history-source-range').textContent = `Exact range [${hit.startOffset}, ${hit.endOffset}) · ${coverageText(result.coverage)}${result.truncated ? ' · read truncated' : ''}`;
    $('history-source-neighbors').replaceChildren(...result.hits.filter(value => value.source.sourceId !== sourceId).map(value => sourceButton(value.source.sourceId, value.startOffset, value.endOffset, `Turn ${value.source.turnSequence} · ${value.source.role}`)), ...(hit.neighborRefs ?? []).filter(id => !result.hits.some(value => value.source.sourceId === id)).map(id => sourceButton(id)));
    if (!$('history-source-dialog').open) $('history-source-dialog').showModal(); updateControls();
  }
  function pinHistorySource() {
    if (!state.source || state.source.conversationId !== state.conversation?.id || !$('thread-recall-enabled').checked || activeTurn()) throw new Error('Enable recall in this conversation and finish its active turn before changing source selections.');
    const selection = exactSourceSelection(state.source.hit, $('history-source-text').selectionStart, $('history-source-text').selectionEnd);
    if (selection.endOffset <= selection.startOffset) throw new Error('Select a nonempty source passage.');
    if (!state.recallSelections.some(value => value.sourceId === selection.sourceId && value.startOffset === selection.startOffset && value.endOffset === selection.endOffset)) { if (state.recallSelections.length >= 20) throw new Error('At most 20 exact passages can be pinned. Remove a pin first.'); state.recallSelections.push(selection); }
    state.dirty = true; updateRecallUI(); updateControls(); $('history-source-range').textContent = `Pinned exact UTF-16 range [${selection.startOffset}, ${selection.endOffset}). Save configuration to use it.`;
  }
  function renderHistoryPaging() {
    const page = state.historyPage; const turns = state.conversation?.turns ?? []; $('history-paging').hidden = !page;
    if (!page) return;
    $('load-earlier-turns').disabled = state.busy || state.historyLoading || page.nextBeforeSequence === null || page.nextBeforeSequence === undefined || turns.length >= 200;
    $('history-page-status').textContent = `${turns.length} of ${page.totalTurns} stored turns loaded${turns.length ? ` · turns ${turns[0].sequence}–${turns.at(-1).sequence}` : ''}${turns.length >= 200 ? ' · 200-turn display ceiling; search or jump to another source window' : ''}${state.historyWindowOmitted ? ' · some turns lie outside this display window' : ''}`;
    $('return-latest-turns').hidden = !turns.length || (turns.at(-1).sequence >= page.latestSequence && !state.historyWindowOmitted);
  }
  function reorderLoadedTurns() {
    const merged = mergeTurnPages([], state.conversation.turns); state.conversation.turns = merged.turns; state.historyWindowOmitted += merged.omitted;
    const keep = new Set(merged.turns.map(turn => turn.id));
    for (const [id] of state.nodes) if (!keep.has(id)) { state.nodes.delete(id); state.traces.delete(id); state.streams.get(id)?.source.close(); state.streams.delete(id); }
    for (const turn of merged.turns) if (!state.nodes.has(turn.id)) renderTurn(turn);
    if (merged.turns.length) $('transcript').replaceChildren(...merged.turns.map(turn => state.nodes.get(turn.id).article));
    renderHistoryPaging();
  }
  async function loadEarlierTurns() {
    if (!state.conversation || state.historyLoading || state.historyPage?.nextBeforeSequence == null || state.conversation.turns.length >= 200) return;
    const generation = state.generation; const beforeHeight = $('transcript').scrollHeight; const beforeTop = $('transcript').scrollTop; state.historyLoading = true;
    try {
      const page = await api(`${base()}?limit=20&beforeSequence=${state.historyPage.nextBeforeSequence}`); if (generation !== state.generation) return;
      state.conversation.turns = mergeTurnPages(page.turns, state.conversation.turns).turns; state.historyPage = page.historyPage ?? state.historyPage;
      reorderLoadedTurns(); $('transcript').scrollTop = beforeTop + ($('transcript').scrollHeight - beforeHeight);
    } finally { state.historyLoading = false; renderHistoryPaging(); }
  }
  async function openHistoryWindow(turnSequence = null, turnId = null) {
    const page = await api(`${base()}?limit=20${turnSequence === null ? '' : `&beforeSequence=${turnSequence + 1}`}`); const active = activeTurn();
    ++state.generation; stopStreams(); state.traces.clear(); state.nodes.clear(); state.recallStatusLoading = false; state.recallRebuilding = false; $('rebuild-recall-index').disabled = false; $('cancel-recall-rebuild').hidden = true;
    state.conversation.turns = mergeTurnPages(page.turns, active ? [active] : []).turns; state.historyPage = page.historyPage ?? null; state.historyWindowOmitted = turnSequence === null ? 0 : 1;
    renderTranscript(); renderHistoryPaging(); if (active && !isTerminal(active.status)) connectTurn(active);
    if (turnId && state.nodes.has(turnId)) state.nodes.get(turnId).article.scrollIntoView({ block: 'start' }); else scrollLatest(true);
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
      capabilityPreviewSignature = ''; refreshRecallCapabilityPreview();
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
  $('settings-form').addEventListener('input', event => { if (state.conversation && event.target !== $('thread-history-query')) { state.dirty = true; clearContextCheck(); if (event.target === $('instructions')) state.instructionsDirty = true; updateMode(); updateControls(); } });
  $('settings-form').addEventListener('submit', event => { event.preventDefault(); runAction(saveSettings); });
  $('reload-settings').addEventListener('click', () => runAction(async () => {
    const saved = await api(`${base()}?limit=20`);
    state.conversation.settings = saved.settings; state.conversation.settingsRevision = saved.settingsRevision;
    fillSettings(); clearContextCheck(); notice(null);
  }));
  $('mode').addEventListener('change', updateMode);
  $('thread-recall-section').addEventListener('toggle', () => { if ($('thread-recall-section').open) updateRecallUI(); });
  $('refresh-recall-index').addEventListener('click', checkRecallCoverage);
  $('rebuild-recall-index').addEventListener('click', rebuildRecallIndex);
  $('cancel-recall-rebuild').addEventListener('click', () => { api(`${base()}/history/rebuild/cancel`, 'POST', {}).then(() => { $('thread-recall-readiness').textContent = 'Cancellation requested. Completed index work and canonical history are retained.'; return checkRecallCoverage(); }).catch(error => notice(error.message)); });
  $('search-thread-history').addEventListener('click', () => runAction(() => searchThreadHistory()));
  $('thread-history-query').addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); runAction(() => searchThreadHistory()); } });
  $('close-history-source').addEventListener('click', () => $('history-source-dialog').close());
  $('read-history-source-range').addEventListener('click', () => runAction(() => { if (!state.source || state.source.conversationId !== state.conversation.id) throw new Error('Open a source in this conversation first.'); const start = Number($('history-source-start').value); const length = Number($('history-source-length').value); if (!Number.isSafeInteger(start) || start < 0 || !Number.isSafeInteger(length) || length < 1 || length > 32000) throw new Error('Use a valid UTF-16 start and a read length of 1–32000.'); return openHistorySource(state.source.hit.source.sourceId, start, start + length); }));
  $('pin-history-source').addEventListener('click', () => runAction(pinHistorySource));
  $('jump-history-source').addEventListener('click', () => runAction(async () => { if (!state.source || state.source.conversationId !== state.conversation.id) throw new Error('Open a source in this conversation first.'); const source = state.source.hit.source; $('history-source-dialog').close(); await openHistoryWindow(source.turnSequence, source.turnId); }));
  $('load-earlier-turns').addEventListener('click', () => runAction(loadEarlierTurns));
  $('return-latest-turns').addEventListener('click', () => runAction(() => openHistoryWindow()));
  $('model-catalog').addEventListener('change', () => {
    if ($('model-catalog').value) { $('model').value = $('model-catalog').value; state.dirty = true; updateControls(); }
    else $('model').focus();
  });
  $('model').addEventListener('input', syncModelSelection);
  $('context-depth').addEventListener('change', () => { const preset = depthPresets[$('context-depth').value]; if (preset) for (const [key, value] of Object.entries(preset)) $(numericFields[key]).value = value; state.dirty = true; updateControls(); });
  $('composer').addEventListener('submit', event => { event.preventDefault(); runAction(sendMessage); });
  $('check-context').addEventListener('click', () => runAction(checkContext));
  $('message').addEventListener('input', () => { clearContextCheck(); $('message').style.height = 'auto'; $('message').style.height = `${Math.min(180, Math.max(58, $('message').scrollHeight))}px`; updateControls(); });
  $('message').addEventListener('keydown', event => { if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) { event.preventDefault(); if (!$('send-message').disabled) $('composer').requestSubmit(); } });
  $('cancel-turn').addEventListener('click', () => runAction(async () => { const turn = activeTurn(); if (turn) { await api(`${turnBase(turn.id)}/cancel`, 'POST', {}); $('composer-status').textContent = 'Cancellation requested; waiting for a durable terminal result.'; } }));
  $('rename-chat').addEventListener('click', () => { $('rename-title').value = state.conversation.title; $('rename-dialog').showModal(); $('rename-title').select(); });
  $('rename-cancel').addEventListener('click', () => $('rename-dialog').close());
  $('rename-form').addEventListener('submit', event => { event.preventDefault(); runAction(async () => { const title = $('rename-title').value.trim(); if (!title) throw new Error('Enter a conversation title.'); const renamed = await api(base(), 'PATCH', { title }); state.conversation.title = renamed.title ?? title; $('conversation-title').textContent = state.conversation.title; upsertConversation(state.conversation); $('rename-dialog').close(); }); });
  const setSettingsVisible = visible => { document.body.classList.toggle('settings-collapsed', !visible); $('toggle-settings').setAttribute('aria-expanded', String(visible)); if (visible && innerWidth <= 1000) $('close-settings').focus(); };
  $('toggle-settings').addEventListener('click', () => setSettingsVisible(document.body.classList.contains('settings-collapsed')));
  $('close-settings').addEventListener('click', () => { setSettingsVisible(false); $('toggle-settings').focus(); });
  document.addEventListener('keydown', event => { if ($('history-source-dialog').open || $('rename-dialog').open) return; if (event.key === 'Escape' && !document.body.classList.contains('settings-collapsed')) { setSettingsVisible(false); $('toggle-settings').focus(); } });
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
