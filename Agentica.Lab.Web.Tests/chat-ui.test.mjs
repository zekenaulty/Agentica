import test from 'node:test';
import assert from 'node:assert/strict';
import './chat-markdown.test.mjs';
import { actionErrorMessage, appendTraceWindow, catalogSelection, citationPresentation, classifyEvent, contextCheckSummary, depthPresets, exactSourceSelection, familyModelCatalog, instructionValue, isTerminal, knownFamilyModelIds, mergeTrace, mergeTurnPages, resumeTraceTail, safeHttpUrl, summarizeTrace, usageLabel, validateSettings } from '../Agentica.Lab.Web/wwwroot/chat.mjs';

test('explicit context check displays provider tokens and evidence without using byte estimates', () => {
  const result = { inferenceDispatched: false, settingsRevision: 7, manifest: { inputAllowance: 62976, estimatedInputUnits: 999999, selectedMessageIds: ['u1', 'a1', 'u2', 'a2', 'current'], tokenCount: { tokens: 62600, method: 'openai.responses.input_tokens', providerCount: true } }, recall: { selected: [{ content: 'exact quote' }], presentedCharacters: 11 } };
  const text = contextCheckSummary(result, { threadRecallEnabled: true, threadRecallRecentTurns: 2 });
  assert.match(text, /62,600 \/ 62,976/); assert.match(text, /4 history messages/); assert.match(text, /2 complete turns configured/); assert.match(text, /1 quoted passages, 11 characters/); assert.match(text, /no generation or turn created/); assert.doesNotMatch(text, /999999|estimate/i);
  for (const tokenCount of [null, { tokens: 62600, providerCount: false }, { tokens: -1, providerCount: true }]) assert.throws(() => contextCheckSummary({ ...result, manifest: { ...result.manifest, tokenCount } }, {}), /unavailable.*No estimate was substituted/);
});

test('Harness context check reports projection-only validity without claiming measured tokens', () => {
  const result = { inferenceDispatched: false, settingsRevision: 9, manifest: { tokenCount: null, estimatedInputUnits: 999999, inputAllowance: 62976, selectedMessageIds: ['u1', 'a1', 'current'] }, recall: { selected: [{ content: 'quote' }], presentedCharacters: 5 } };
  const text = contextCheckSummary(result, { mode: 'harness', threadRecallEnabled: true, threadRecallRecentTurns: 1 });
  assert.match(text, /Harness projection valid/); assert.match(text, /Provider token count is deferred until an actual planner request is prepared/); assert.match(text, /2 history messages/); assert.match(text, /1 quoted passages, 5 characters/); assert.match(text, /no generation or turn created/); assert.doesNotMatch(text, /999999|62,976|Provider input:/);
  assert.throws(() => contextCheckSummary(result, { mode: 'clean-room' }), /unavailable/);
});

test('semantic context preview explicitly defers retrieval instead of claiming complete measured recall', () => {
  const result = { inferenceDispatched: false, settingsRevision: 1, manifest: { tokenCount: null, selectedMessageIds: ['current'] }, recall: { selected: [], presentedCharacters: 0 } };
  assert.match(contextCheckSummary(result, { mode: 'harness', threadRecallEnabled: true, threadSemanticRecallEnabled: true }), /Semantic lookup deferred until an admitted turn; no embedding request made/);
  assert.doesNotMatch(contextCheckSummary(result, { mode: 'harness', threadRecallEnabled: false, threadSemanticRecallEnabled: true }), /Semantic lookup deferred/);
});

test('history pages merge chronologically without duplicates and remain bounded', () => {
  const turns = Array.from({ length: 5000 }, (_, index) => ({ id: `turn-${index + 1}`, sequence: index + 1 }));
  const latest = turns.slice(-20); const earlier = turns.slice(-40, -20);
  const merged = mergeTurnPages(latest, earlier);
  assert.equal(merged.turns.length, 40); assert.equal(merged.turns[0].sequence, 4961); assert.equal(merged.turns.at(-1).sequence, 5000);
  assert.equal(mergeTurnPages(merged.turns, latest).turns.length, 40, 'Refreshing latest does not discard earlier loaded rows.');
  const bounded = mergeTurnPages(turns.slice(-200), [{ id: 'turn-5001', sequence: 5001 }]);
  assert.equal(bounded.turns.length, 200); assert.equal(bounded.omitted, 1); assert.equal(bounded.turns.at(-1).sequence, 5001);
});

test('operator selection uses exact UTF-16 source offsets without normalizing text', () => {
  const hit = { source: { sourceId: 'source-id' }, startOffset: 120, excerpt: 'A🧭B\r\nC' };
  assert.deepEqual(exactSourceSelection(hit, 1, 3), { sourceId: 'source-id', startOffset: 121, endOffset: 123 });
  assert.equal(hit.excerpt.slice(1, 3), '🧭');
  assert.throws(() => exactSourceSelection(hit, 0, 0), /nonempty/);
  assert.throws(() => exactSourceSelection(hit, 0, 100), /nonempty/);
});

test('only a settings revision conflict receives reload guidance', () => {
  assert.equal(actionErrorMessage({ status: 409, code: 'context.limit', message: 'The draft exceeds its input allowance. No model call was made.' }), 'context.limit: The draft exceeds its input allowance. No model call was made.');
  assert.equal(actionErrorMessage({ status: 409, code: 'chat.busy', message: 'An active turn already exists.' }), 'chat.busy: An active turn already exists.');
  assert.equal(actionErrorMessage({ status: 409, code: 'turn.request_conflict', message: 'Different content.' }), 'turn.request_conflict: Different content.');
  assert.equal(actionErrorMessage({ status: 409, message: 'Unclassified conflict.' }), 'Unclassified conflict.');
  assert.match(actionErrorMessage({ status: 409, code: 'settings.conflict', message: 'Settings changed elsewhere.' }), /Reload saved configuration/);
});

test('assistant citation ranges produce inline markers and source links without changing source text', () => {
  const text = 'Read the source.';
  const event = { sequence: 1, type: 'provider.citations', callId: 'call-1', data: { target: 'assistant', citations: [{ url: 'https://example.com/page', title: 'Source', startIndex: 9, endIndex: 15 }], sources: [{ url: 'https://example.com/page', title: 'Source' }] } };
  const result = citationPresentation(text, [event, { ...event, sequence: 2 }]);
  assert.equal(text, 'Read the source.'); assert.equal(result.inline.length, 1); assert.equal(result.inline[0].offset, 15);
  assert.equal(result.sources.length, 1); assert.equal(result.sources[0].number, 1); assert.equal(result.sources[0].callId, 'call-1');
});

test('tool source ranges never become annotations on the final assistant answer', () => {
  const result = citationPresentation('Final answer.', [{ sequence: 1, type: 'provider.citations', data: { target: 'tool', citations: [{ url: 'https://example.com/tool', title: 'Tool source', startIndex: 0, endIndex: 5 }], sources: [] } }]);
  assert.deepEqual(result.inline, []); assert.equal(result.sources[0].target, 'tool');
});

test('citations accept only absolute HTTP(S), and unknown Unicode offsets remain source links', () => {
  for (const url of ['javascript:alert(1)', 'data:text/html,test', 'file:///secret', '/relative', '', null]) assert.equal(safeHttpUrl(url), null);
  // Build synthetic user-info at runtime so fixtures contain no credential URI.
  const userInfo = new URL('https://example.com/');
  userInfo.username = 'fixture-user'; assert.equal(safeHttpUrl(userInfo.href), null);
  userInfo.password = 'fixture-password'; assert.equal(safeHttpUrl(userInfo.href), null);
  assert.equal(safeHttpUrl('https://example.com/ok'), 'https://example.com/ok');
  const result = citationPresentation('A non-ASCII 🌍 answer.', [{ sequence: 1, type: 'provider.citations', data: { target: 'assistant', citations: [
    { url: 'javascript:alert(1)', title: 'unsafe', startIndex: 0, endIndex: 1 },
    { url: 'https://example.com/safe', title: 'Safe', startIndex: 0, endIndex: 5 }
  ], sources: [] } }]);
  assert.deepEqual(result.inline, []); assert.equal(result.sources.length, 1); assert.equal(result.unplaced, 1);
});

test('out-of-range citations preserve safe source links rather than guessing a span', () => {
  const result = citationPresentation('Short.', [{ sequence: 1, type: 'provider.citations', data: { target: 'assistant', citations: [{ url: 'https://example.com/', startIndex: -1, endIndex: 500 }], sources: [] } }]);
  assert.deepEqual(result.inline, []); assert.equal(result.sources.length, 1); assert.equal(result.unplaced, 1);
});

test('model catalog exposes the eight verified family IDs without inventing version/family combinations', () => {
  const catalog = familyModelCatalog(knownFamilyModelIds);
  assert.deepEqual(catalog.map(group => group.version), ['6.1', '6', '5.6']);
  assert.deepEqual(catalog.map(group => group.models.length), [2, 3, 3]);
  assert.deepEqual(new Set(catalog.flatMap(group => group.models.map(model => model.id))), new Set([
    'gpt-6.1-sol', 'gpt-6.1-terra', 'gpt-6-astra', 'gpt-6-sol', 'gpt-6-luna',
    'gpt-5.6-sol', 'gpt-5.6-terra', 'gpt-5.6-luna'
  ]));
  for (const invented of ['gpt-6-terra', 'gpt-6.1-luna', 'gpt-6.1-astra', 'gpt-5.6-astra']) assert.equal(catalogSelection(catalog, invented), '');
});

test('account models determine future catalog entries, numeric version order and exact dated IDs', () => {
  const ids = ['gpt-6.2-luna', 'gpt-6.10-terra-2027-01-02', 'gpt-6.10-astra', 'gpt-6.10-terra', 'gpt-6.10-terra-2027-02-03', 'gpt-5.6-sol', 'gpt-6.2-luna', 'gpt-5.5-sol', 'gpt-4.1', 'unrelated-model', null];
  const catalog = familyModelCatalog(ids);
  assert.deepEqual(catalog.map(group => group.version), ['6.10', '6.2', '5.6']);
  assert.deepEqual(catalog[0].models.map(model => model.id), ['gpt-6.10-astra', 'gpt-6.10-terra', 'gpt-6.10-terra-2027-02-03', 'gpt-6.10-terra-2027-01-02']);
  assert.equal(catalogSelection(catalog, 'gpt-6.10-sol'), '', 'Catalog expansion cannot manufacture an absent family.');
  assert.equal(catalog[1].models.length, 1, 'Duplicate exact IDs are removed.');
});

test('model catalog selection preserves a saved custom model for the exact-ID field', () => {
  const saved = { model: 'private-custom-model', instructions: 'Exact\r\nsource', reasoningEffort: 'high' };
  const catalog = familyModelCatalog(knownFamilyModelIds);
  assert.equal(catalogSelection(catalog, saved.model), '');
  assert.deepEqual(saved, { model: 'private-custom-model', instructions: 'Exact\r\nsource', reasoningEffort: 'high' });
  assert.equal(catalogSelection(catalog, 'gpt-6.1-sol'), 'gpt-6.1-sol');
});

test('stream replay merges out-of-order pages without duplicating summary text or losing correlations', () => {
  const first = { sequence: 11, type: 'provider.summary.delta', data: { text: 'Inspect ' }, callId: 'call-a', itemId: 'reasoning-a' };
  const second = { sequence: 12, type: 'provider.summary.delta', data: { text: 'the evidence.' }, callId: 'call-a', itemId: 'reasoning-a' };
  const tool = { sequence: 13, type: 'harness.event', data: { executionEvent: { kind: 'ToolCallCompleted' } }, callId: 'call-b' };
  const replay = mergeTrace([second, tool], [first, second]);
  assert.deepEqual(replay.events.map(event => event.sequence), [11, 12, 13]);
  assert.equal(summarizeTrace(replay.events), 'Inspect the evidence.');
  assert.equal(replay.events[0].itemId, 'reasoning-a');
  assert.equal(replay.events[2].callId, 'call-b');
  assert.equal(classifyEvent(tool), 'tool');
});

test('only observable provider summary artifacts become a summary', () => {
  const events = [
    { sequence: 1, type: 'provider.item', data: { type: 'reasoning', encrypted_content: 'opaque-not-a-summary', text: 'private-not-a-summary' } },
    { sequence: 2, type: 'provider.text.delta', data: { text: 'assistant output' } },
    { sequence: 3, type: 'harness.event', data: { executionEvent: { privateReasoning: 'not-a-provider-summary' } } },
    { sequence: 4, type: 'provider.record', data: { type: 'response.output_item.done', item: { type: 'reasoning', summary: [{ type: 'summary_text', text: 'Source record, not a normalized summary artifact.' }] } } }
  ];
  assert.equal(summarizeTrace(events), '');
  const nativeSummary = { sequence: 5, type: 'provider.item', data: { type: 'reasoning', summary: [{ type: 'summary_text', text: 'Available provider summary.' }] } };
  assert.equal(summarizeTrace([...events, nativeSummary]), 'Available provider summary.');
  const delta = { sequence: 6, type: 'provider.summary.delta', data: { text: 'Available provider summary.' } };
  assert.equal(summarizeTrace([...events, nativeSummary, delta]), 'Available provider summary.');
});

test('browser trace retention is bounded and reports the exact omitted window', () => {
  const events = Array.from({ length: 1010 }, (_, index) => ({ sequence: index + 1, type: 'provider.item', data: { index } }));
  const result = mergeTrace([], events);
  assert.equal(result.events.length, 1000);
  assert.equal(result.omitted, 10);
  assert.equal(result.events[0].sequence, 11);
  assert.equal(result.events.at(-1).sequence, 1010);
  assert.equal(mergeTrace(result.events, [events.at(-1)]).omitted, 0);
});

test('retained pages stay independently inspectable while a full live tail advances', () => {
  const events = (start, count) => Array.from({ length: count }, (_, index) => ({ sequence: start + index, type: 'provider.item' }));
  const trace = { events: events(1001, 1000), inspectionEvents: [], inspecting: false, inspectionOmitted: 0, windowOmitted: 0, after: 0, hasMore: true };
  appendTraceWindow(trace, events(1, 100), true);
  assert.equal(trace.inspecting, true);
  assert.deepEqual(trace.inspectionEvents.map(event => event.sequence), events(1, 100).map(event => event.sequence));
  assert.equal(trace.events[0].sequence, 1001); assert.equal(trace.events.at(-1).sequence, 2000);
  assert.equal(trace.windowOmitted, 0, 'Fetching older pages does not inflate live eviction accounting.');
  appendTraceWindow(trace, events(2001, 25));
  assert.equal(trace.inspectionEvents[0].sequence, 1); assert.equal(trace.inspectionEvents.at(-1).sequence, 100);
  assert.equal(trace.events[0].sequence, 1026); assert.equal(trace.events.at(-1).sequence, 2025); assert.equal(trace.windowOmitted, 25);
  appendTraceWindow(trace, events(101, 100), true);
  assert.equal(trace.inspectionEvents[0].sequence, 1); assert.equal(trace.inspectionEvents.at(-1).sequence, 200);
  assert.equal(trace.events.at(-1).sequence, 2025); assert.equal(trace.inspectionOmitted, 0);
  trace.inspecting = false;
  assert.equal((trace.inspecting ? trace.inspectionEvents : trace.events).at(-1).sequence, 2025);
  assert.equal(trace.inspectionEvents[0].sequence, 1, 'Resuming the live tail does not destroy the retained inspection window.');
});

test('retained inspection remains bounded with honest eviction and pending page coverage', () => {
  const trace = { events: [], inspectionEvents: [], inspecting: false, inspectionOmitted: 0, windowOmitted: 0, after: 1100, hasMore: false };
  appendTraceWindow(trace, Array.from({ length: 1100 }, (_, index) => ({ sequence: index + 1 })), true);
  assert.equal(trace.inspectionEvents.length, 1000); assert.equal(trace.inspectionEvents[0].sequence, 101);
  assert.equal(trace.inspectionOmitted, 100); assert.equal(trace.windowOmitted, 0);
  appendTraceWindow(trace, [{ sequence: 1101 }]);
  assert.equal(trace.hasMore, true); assert.equal(trace.inspectionEvents.at(-1).sequence, 1100);
  assert.equal(trace.events.at(-1).sequence, 1101); assert.equal(trace.windowOmitted, 1);
});

test('resuming a reopened completed trace fetches the newest bounded tail and preserves inspection paging', async () => {
  const events = (start, count) => Array.from({ length: count }, (_, index) => ({ sequence: start + index }));
  const head = events(1, 13);
  const trace = { events: head, inspectionEvents: head, inspecting: true, inspectionOmitted: 0, windowOmitted: 0, after: 13, hasMore: true };
  let reads = 0;
  await resumeTraceTail(trace, async () => { reads++; return { events: events(2641, 13), next: 2653 }; });
  assert.equal(reads, 1, 'A retained head cannot become a live tail by changing its label.');
  assert.equal(trace.inspecting, false);
  assert.deepEqual(trace.events.map(event => event.sequence), events(2641, 13).map(event => event.sequence));
  assert.deepEqual(trace.inspectionEvents, head);
  assert.equal(trace.after, 13); assert.equal(trace.hasMore, true);
  assert.equal(trace.windowOmitted, 2640);
});

test('tail restoration preserves newer live arrivals and leaves inspection intact when unavailable or stale', async () => {
  const trace = { events: [{ sequence: 1 }], inspectionEvents: [{ sequence: 1 }], inspecting: true, windowOmitted: 0, after: 1 };
  await assert.rejects(resumeTraceTail(trace, async () => { throw new Error('offline'); }), /offline/);
  assert.equal(trace.inspecting, true); assert.equal(trace.events[0].sequence, 1);
  await resumeTraceTail(trace, async () => {
    trace.events.push({ sequence: 103 });
    return { events: [{ sequence: 101 }, { sequence: 102 }], next: 102 };
  });
  assert.deepEqual(trace.events.map(event => event.sequence), [101, 102, 103]);
  assert.deepEqual(trace.inspectionEvents, [{ sequence: 1 }]);
  assert.equal(await resumeTraceTail(trace, async () => ({ events: [{ sequence: 999 }] }), () => false), null);
  assert.deepEqual(trace.events.map(event => event.sequence), [101, 102, 103]);
});

test('wrapped native completion summaries are visible and do not repeat streamed artifacts', () => {
  const complete = { sequence: 3, type: 'provider.item', callId: 'call-a', itemId: 'rs-a', data: { type: 'response.output_item.done', item: { type: 'reasoning', id: 'rs-a', summary: [{ type: 'summary_text', text: 'Visible summary.' }] } } };
  assert.equal(summarizeTrace([complete]), 'Visible summary.');
  assert.equal(summarizeTrace([
    { sequence: 1, type: 'provider.summary.delta', callId: 'call-a', itemId: 'rs-a', data: { text: 'Visible ' } },
    { sequence: 2, type: 'provider.summary.delta', callId: 'call-a', itemId: 'rs-a', data: { text: 'summary.' } }, complete,
    { ...complete, sequence: 4 },
    { sequence: 5, type: 'provider.item', callId: 'call-b', itemId: 'rs-b', data: { type: 'response.output_item.done', item: { type: 'reasoning', id: 'rs-b', summary: [{ type: 'summary_text', text: 'A second call.' }] } } }
  ]), 'Visible summary.\n\nA second call.');
});

test('unrelated settings edits preserve exact imported instruction line endings', () => {
  const original = 'First line\r\nSecond line\rThird line\n';
  const textareaValue = 'First line\nSecond line\nThird line\n';
  assert.equal(instructionValue(original, textareaValue, false), original);
  assert.equal(instructionValue(original, textareaValue, true), textareaValue);
  assert.equal(instructionValue(original, '', true), '');
});

test('malformed stream sequence values do not enter the retained browser timeline', () => {
  const result = mergeTrace([], [
    { sequence: '1', type: 'provider.summary.delta', data: { text: 'bad' } },
    { sequence: -1 }, { sequence: NaN }, { sequence: 0 }, { sequence: 1, type: 'turn.started' }
  ]);
  assert.deepEqual(result.events, [{ sequence: 1, type: 'turn.started' }]);
});

test('clean-room safety invariants cannot be changed by normal form settings', () => {
  const settings = { mode: 'clean-room', model: 'exact-model-id', enabledTools: [], memoryEnabled: false,
    maxOutputTokens: 4096, contextWindowTokens: 131072, maxInputCharacters: 80000, maxHistoryMessages: 32,
    maxToolResultCharacters: 4000, maxToolEvidenceCharacters: 8000, maxSteps: 12, maxContinuations: 4,
    maxProviderCalls: 12, timeoutSeconds: 180 };
  assert.equal(validateSettings(settings), null);
  assert.match(validateSettings({ ...settings, enabledTools: ['tool'] }), /cannot offer tools/);
  assert.match(validateSettings({ ...settings, memoryEnabled: true }), /Memory is disabled/);
  assert.match(validateSettings({ ...settings, maxProviderCalls: Infinity }), /positive whole/);
  assert.match(validateSettings({ ...settings, maxContinuations: -1 }), /nonnegative/);
  assert.equal(validateSettings({ ...settings, mode: 'harness', enabledTools: ['tool'] }), null);
  assert.match(validateSettings({ ...settings, mode: 'harness', ambientWebSearch: true }), /explicit web.search/);
  assert.equal(validateSettings({ ...settings, ambientWebSearch: true, maxWebSearchCalls: 3 }), null);
  assert.match(validateSettings({ ...settings, maxWebSearchCalls: 21 }), /1–20/);
  assert.match(validateSettings({ ...settings, threadRecallEnabled: true, threadRecallMode: 'agent' }), /Harness/);
  assert.match(validateSettings({ ...settings, threadRecallEnabled: true, threadRecallRecentTurns: 20 }), /protected recent-turn/);
});

test('depth presets are independent of reasoning effort and stay within the service history ceiling', () => {
  for (const preset of Object.values(depthPresets)) {
    assert.equal(Object.hasOwn(preset, 'reasoningEffort'), false);
    assert.equal(Object.hasOwn(preset, 'enabledTools'), false);
    assert.ok(preset.maxInputCharacters <= 262144);
    assert.ok(preset.maxHistoryMessages <= 500);
  }
});

test('interrupted, failed and incomplete statuses end execution without implying success', () => {
  for (const status of ['running', 'queued', 'pending', 'cancelling']) assert.equal(isTerminal(status), false);
  for (const status of ['completed', 'failed', 'interrupted', 'cancelled', 'incomplete', 'indeterminate']) assert.equal(isTerminal(status), true);
  assert.equal(classifyEvent({ type: 'turn.failed' }), 'error');
});


test('retained attempts expose known failed-call usage without inventing unknown usage or adding reasoning twice', () => {
  const label = usageLabel({ attempts: [
    { Usage: { PromptTokens: 12, CompletionTokens: 8, ThinkingTokens: 3, OutputTokenSemantics: 1 }, ProviderStatus: 'completed', Outcome: 4 },
    { Usage: null, Outcome: 4 }
  ] });
  assert.match(label, /12 observed input/); assert.match(label, /8 observed output/); assert.match(label, /3 observed reasoning/);
  assert.match(label, /1 attempt with usage unknown/); assert.doesNotMatch(label, /11 observed output/);
  assert.match(usageLabel({ attempts: [{ usage: null }] }), /usage unknown/);
  const rejected = usageLabel({ attempts: [{ DispatchCertainty: 0, Usage: null }] });
  assert.match(rejected, /not dispatched/); assert.doesNotMatch(rejected, /usage unknown/);
  assert.match(usageLabel({ provider: { input_tokens: 12, output_tokens: 8 } }), /12 input tokens/);
});
