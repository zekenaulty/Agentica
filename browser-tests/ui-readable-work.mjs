// Intercepted browser-only reading journey. No provider, saved user chat or live service.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const browser = await chromium.launch({ headless: true, channel: process.env.AGENTICA_BROWSER_CHANNEL || undefined });
const origin = 'http://127.0.0.1:5991'; const assets = new URL('../Agentica.Lab.Web/wwwroot/', import.meta.url);
const settings = { mode: 'clean-room', model: 'gpt-6-luna', reasoningEffort: 'high', instructions: '', reasoningSummary: true,
  enabledTools: [], memoryEnabled: false, threadRecallEnabled: false, maxOutputTokens: 4096, contextWindowTokens: 131072,
  maxInputCharacters: 80000, maxHistoryMessages: 32, historyPolicy: 'stop', contextDepth: 'normal' };
const turn = { id: 'reading-turn', conversationId: 'reading-room', sequence: 1, userText: 'Synthetic reading experiment.', assistantText: '',
  status: 'running', createdAt: '2026-11-08T12:00:00Z', settingsRevision: 1, settings, continuity: 'synthetic transcript', traceCount: 0, droppedTraceCount: 0 };
const conversation = { id: turn.conversationId, title: 'Readable work fixture', createdAt: turn.createdAt, updatedAt: turn.createdAt, settingsRevision: 1, settings, turns: [turn] };
const records = []; const errors = []; const external = []; let reads = 0; let page;
const event = (type, data, itemId = 'reason-a') => ({ sequence: records.length + 1, type, data, callId: 'call-a', itemId, time: turn.createdAt });
try {
  page = await browser.newPage({ viewport: { width: 1400, height: 1000 } });
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    globalThis.streams = [];
    globalThis.EventSource = class {
      constructor(url) { this.url = url; this.closed = false; globalThis.streams.push(this); queueMicrotask(() => this.onopen?.()); }
      close() { this.closed = true; }
    };
  });
  await page.route('**/*', async route => {
    const url = new URL(route.request().url()); let body;
    if (url.origin !== origin) { external.push(url.href); return route.abort(); }
    if (url.pathname === '/api/chats') body = [conversation];
    else if (url.pathname === '/api/chats/models') body = { configured: false, models: ['gpt-6-luna'], reasoningEfforts: ['high'] };
    else if (url.pathname === '/api/chats/capabilities') body = [];
    else if (url.pathname === '/api/providers/openai/settings') body = { configured: false };
    else if (url.pathname === '/api/chats/reading-room') body = conversation;
    else if (url.pathname.endsWith('/trace')) { reads++; body = { events: records, next: records.length, hasMore: false }; }
    else if (url.pathname.endsWith('/citations')) body = { events: [], next: records.length, hasMore: false };
    else if (url.pathname.startsWith('/api/')) throw new Error(`Unexpected fixture API ${url.pathname}`);
    if (body !== undefined) return route.fulfill({ json: body });
    const name = url.pathname === '/' ? 'chat.html' : url.pathname.slice(1);
    try { return route.fulfill({ contentType: name.endsWith('.css') ? 'text/css' : /\.(mjs|js)$/.test(name) ? 'text/javascript' : 'text/html', body: await readFile(new URL(name, assets)) }); }
    catch { return route.fulfill({ status: 404, body: '' }); }
  });
  const emit = async value => {
    records.push(value); turn.traceCount = records.length;
    if (value.type === 'assistant.delta') turn.assistantText += value.data.text;
    await page.evaluate(value => [...globalThis.streams].reverse().find(stream => !stream.closed)?.onmessage?.({ data: JSON.stringify(value), lastEventId: String(value.sequence) }), value);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  };
  await page.goto(origin + '/chat.html?chat=reading-room');
  await page.waitForFunction(() => globalThis.streams.length === 1);
  const work = page.locator('.turn-work'); const raw = page.locator('.turn-trace'); const answer = page.locator('.assistant-message .message-content');
  assert.equal(await raw.getAttribute('open'), null); assert.equal(await work.getAttribute('open'), null);
  await emit(event('provider.item', { type: 'response.reasoning_summary_part.added', item_id: 'reason-a', output_index: 0, summary_index: 0, part: { type: 'summary_text', text: '' } }));
  const summary = '**Approach**\n\nI will inspect the fixture evidence. The source records and their citations should identify what supports the answer.\n\nI am checking the original passage before treating the search result as sufficient evidence. A successful lookup still needs a useful result.';
  const characters = Array.from(summary);
  const fragments = Array.from({ length: 40 }, (_, index) => characters.slice(Math.floor(index * characters.length / 40), Math.floor((index + 1) * characters.length / 40)).join(''));
  for (const text of fragments) await emit(event('provider.summary.delta', { text, source: { version: 1, itemId: 'reason-a', outputIndex: 0, summaryIndex: 0 } }));
  assert.match(await page.locator('.work-excerpt').textContent(), /^… /, 'A bounded recent excerpt marks omitted earlier text.');
  assert.match(await page.locator('.work-excerpt').textContent(), /A successful lookup still needs a useful result\.$/);
  await work.locator(':scope > summary').click();
  await page.waitForFunction(() => document.querySelectorAll('.work-block').length === 1);
  assert.equal(await page.locator('.work-block h4').count(), 1);
  assert.equal(await page.locator('.work-block strong').textContent(), 'Approach');
  assert.match(await page.locator('.work-block .markdown-body').textContent(), /I will inspect the fixture evidence/);
  await page.locator('.work-block .markdown-body p').nth(1).evaluate(node => {
    const range = document.createRange(); range.selectNodeContents(node); const selection = document.getSelection(); selection.removeAllRanges(); selection.addRange(range);
  });
  const selected = await page.evaluate(() => document.getSelection().toString());
  const more = '\n\nThe source check is complete.';
  await emit(event('provider.summary.delta', { text: more, source: { version: 1, itemId: 'reason-a', outputIndex: 0, summaryIndex: 0 } }));
  assert.match(await page.locator('.work-excerpt').textContent(), /The source check is complete\.$/, 'Recent text keeps advancing beyond the original preview limit.');
  assert.equal(await page.evaluate(() => document.getSelection().toString()), selected);
  await page.getByRole('button', { name: 'New summary text · show latest' }).click();
  assert.match(await page.locator('.work-block .markdown-body').textContent(), /source check is complete/);
  await emit(event('provider.item', { type: 'response.reasoning_summary_part.done', item_id: 'reason-a', output_index: 0, summary_index: 0, part: { type: 'summary_text', text: fragments.join('') + more } }));
  await emit(event('harness.event', { executionEvent: { type: 'step.started', context: { runId: 'r', stepId: 's', toolId: 'fixture.inspect' } } }));
  await emit(event('harness.event', { executionEvent: { type: 'receipt.emitted', context: { runId: 'r', stepId: 's', toolId: 'fixture.inspect', receiptId: 'receipt-1' }, data: { status: 'succeeded' }, payload: { message: 'Found two fixture sources.' } } }));
  assert.match(await page.locator('.work-operation').textContent(), /Found two fixture sources/);
  await emit(event('assistant.delta', { text: '# Result\n\nThe fixture evidence supports this answer.' }, 'answer-a'));
  assert.equal(await answer.locator('h1').textContent(), 'Result');
  assert.equal(await work.getAttribute('open'), ''); assert.equal(await raw.getAttribute('open'), null);
  turn.status = 'completed'; turn.finishedAt = '2026-11-08T12:00:20Z';
  await emit(event('turn.completed', { ...turn, traceCount: records.length + 1 }));
  assert.equal(await work.getAttribute('open'), '', 'Completion preserves chosen inspection.');
  assert.equal(await answer.isVisible(), true, 'The answer remains available beside work.');
  await page.getByRole('button', { name: 'Inspect fragments' }).click();
  assert.equal(await raw.getAttribute('open'), ''); assert.equal(await answer.isVisible(), true);
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) {
    await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true });
    await raw.locator(':scope > summary').click();
    await page.screenshot({ path: join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'readable-work-desktop.png'), fullPage: true });
  }
  await page.reload(); await work.locator(':scope > summary').click();
  await page.waitForFunction(() => document.querySelectorAll('.work-block').length === 1);
  assert.equal(await answer.locator('h1').textContent(), 'Result'); assert.equal(await raw.getAttribute('open'), null);
  assert.match(await page.locator('.work-block .markdown-body').textContent(), /source check is complete/);
  await page.setViewportSize({ width: 390, height: 844 });
  if (await page.locator('#settings-panel').isVisible()) await page.locator('#close-settings').click();
  assert.equal(await page.locator('.work-block .markdown-body').isVisible(), true);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) await page.screenshot({ path: join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'readable-work-mobile.png'), fullPage: true });
  assert.deepEqual(errors, []); assert.deepEqual(external, []);
  const proof = { result: 'PASS', scope: 'intercepted browser fixture; no live provider or service', fragments: 41, summaryBlocks: 1,
    exactRawRecordsRetained: records.length, selectionPreserved: true, observedToolResult: true, progressiveHostAnswer: true,
    completionPreservesInspection: true, diagnosticsKeepAnswerVisible: true, retainedReopen: true, narrowViewport: true, reads, errors, external };
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) await writeFile(join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'readable-work-qualification.json'), JSON.stringify(proof, null, 2));
  console.log(JSON.stringify(proof, null, 2));
} finally { await browser.close(); }
