import test from 'node:test';
import assert from 'node:assert/strict';
import { classifyEvent, depthPresets, instructionValue, isTerminal, mergeTrace, summarizeTrace, validateSettings } from '../Agentica.Lab.Web/wwwroot/chat.mjs';

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
