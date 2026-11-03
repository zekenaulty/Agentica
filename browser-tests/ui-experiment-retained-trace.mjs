// Fully intercepted local-source regression. No running service, user chat,
// provider, generation, or persisted data is contacted.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const origin = 'http://127.0.0.1:5988'; const assets = new URL('../Agentica.Lab.Web/wwwroot/', import.meta.url);
const id = 'retained-trace-fixture'; const time = '2026-11-03T00:00:00Z';
const settings = { mode: 'clean-room', model: 'gpt-6-luna', reasoningEffort: 'high', instructions: '', reasoningSummary: true, ambientWebSearch: false, maxWebSearchCalls: 3, maxOutputTokens: 32000, contextWindowTokens: 131072, maxInputCharacters: 256000, maxHistoryMessages: 128, historyPolicy: 'recent', contextDepth: 'exhaustive', maxToolResultCharacters: 4000, maxToolEvidenceCharacters: 8000, maxSteps: 12, maxContinuations: 4, maxProviderCalls: 12, timeoutSeconds: 180, enabledTools: [], memoryEnabled: false, threadRecallEnabled: false, threadRecallMode: 'auto', threadRecallQuery: '', threadRecallSelections: [], threadRecallMaxCharacters: 8000, threadRecallTopK: 5, threadRecallNeighborRadius: 1, threadRecallRecentTurns: 2, threadRecallMaxCalls: 8 };
const userText = 'Original **user** source\r\nkept exact.'; const assistantText = 'Original **assistant** source.';
const turn = { id: 'retained-turn', conversationId: id, sequence: 1, userText, assistantText, status: 'running', createdAt: time, finishedAt: null, settingsRevision: 4, settings, continuity: 'synthetic transcript-only fixture', traceCount: 1000, droppedTraceCount: 0 };
const conversation = { id, title: 'Retained trace fixture', createdAt: time, updatedAt: time, settingsRevision: 4, settings, turns: [turn] };
const event = (sequence, type = 'provider.item', data = { type: 'message', status: 'in_progress' }) => ({ sequence, type, data, time, callId: 'synthetic-call' });
const exactBody = { model: settings.model, input: [{ role: 'user', content: userText }], stream: true, store: false };
const records = Array.from({ length: 1000 }, (_, index) => event(index + 1));
records[0] = event(1, 'context.manifest', { selectedMessageIds: ['exact-user-source'], tokenCount: { tokens: 128, method: 'fixture only' } });
records[1] = event(2, 'provider.request', { body: exactBody, dispatchState: 'fixture only' });
records[2] = event(3, 'provider.record', { payloadRef: 'retained-source', providerType: 'response.created' });
records[3] = event(4, 'provider.summary.delta', { text: 'Early public summary.' });
const traceReads = []; const payloadReads = []; const unexpected = []; const errors = [];
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1040 }, reducedMotion: 'reduce' }); page.setDefaultTimeout(10000);
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    globalThis.__traceStreams = [];
    globalThis.EventSource = class { constructor(url) { this.url = url; this.closed = false; globalThis.__traceStreams.push(this); queueMicrotask(() => this.onopen?.()); } close() { this.closed = true; } };
  });
  await page.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url()); let body;
    if (url.origin !== origin) { unexpected.push(request.url()); return route.abort('blockedbyclient'); }
    if (url.pathname === '/api/chats/models') body = { configured: false, models: [settings.model], defaultModel: settings.model, reasoningEfforts: ['high'] };
    else if (url.pathname === '/api/chats/capabilities') body = [];
    else if (url.pathname === '/api/providers/openai/settings') body = { configured: false };
    else if (url.pathname === '/api/chats') body = [conversation];
    else if (url.pathname === `/api/chats/${id}` && request.method() === 'GET') body = { ...conversation, historyPage: { totalTurns: 1, latestSequence: 1, nextBeforeSequence: null, completedTurns: 0 } };
    else if (url.pathname === `/api/chats/${id}/turns/${turn.id}/trace`) {
      const after = Number(url.searchParams.get('after') ?? 0); const limit = Number(url.searchParams.get('limit') ?? 100);
      const events = records.filter(value => value.sequence > after).slice(0, limit); traceReads.push(after);
      const next = events.at(-1)?.sequence ?? after; body = { events, next, hasMore: next < records.at(-1).sequence };
    } else if (url.pathname === `/api/chats/${id}/turns/${turn.id}/events/3/payload`) {
      payloadReads.push(request.url()); body = { type: 'response.created', fixture: 'Explicit original source payload.' };
    } else if (url.pathname.startsWith('/api/')) { unexpected.push(`${request.method()} ${url.pathname}`); return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' }); }
    if (body !== undefined) return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
    const name = url.pathname.slice(1);
    if (!/^(?:[a-zA-Z0-9._-]+\.(?:html|css|mjs|js)|vendor\/[a-zA-Z0-9._/-]+\.(?:mjs|js))$/.test(name) || name.includes('..')) return route.fulfill({ status: 404, body: '' });
    try { return route.fulfill({ contentType: name.endsWith('.html') ? 'text/html' : name.endsWith('.css') ? 'text/css' : 'text/javascript', body: await readFile(new URL(name, assets)) }); }
    catch { unexpected.push(`Missing asset ${name}`); return route.fulfill({ status: 404, body: '' }); }
  });
  const emit = async values => {
    records.push(...values); turn.traceCount = values.at(-1).sequence;
    for (const value of values) if (value.type === 'assistant.delta') turn.assistantText += value.data.text;
    await page.evaluate(async values => {
      const stream = [...globalThis.__traceStreams].reverse().find(value => !value.closed);
      for (const value of values) stream.onmessage?.({ data: JSON.stringify(value), lastEventId: String(value.sequence) });
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    }, values);
  };
  await page.goto(origin + '/chat.html'); await page.waitForFunction(() => globalThis.__traceStreams.length === 1 && document.querySelector('.turn'));
  const trace = page.locator('.turn-trace'); const timeline = trace.locator('details.trace-inspector').filter({ has: page.locator('summary', { hasText: /^Typed timeline/ }) });
  const inspector = title => trace.locator('details.trace-inspector').filter({ has: page.locator('summary', { hasText: new RegExp(`^${title}`) }) });
  const request = inspector('Effective provider request'); const context = inspector('Context manifest'); const sources = inspector('Provider source records');
  await emit(Array.from({ length: 1000 }, (_, index) => event(1001 + index)));
  assert.equal(await trace.getAttribute('open'), null); assert.equal(traceReads.length, 0); assert.equal(payloadReads.length, 0);
  await trace.locator(':scope > summary').click();
  await page.waitForFunction(() => document.querySelector('.trace-page-controls span')?.textContent.includes('sequences 1–100'));
  for (const section of [timeline, request, context, sources]) await section.locator(':scope > summary').click();
  assert.equal(await timeline.locator('details.typed-event').count(), 99); assert.equal(await trace.locator('details.typed-event[open]').count(), 0);
  const shownBody = JSON.parse(await request.locator('pre').textContent())[0].data.body; assert.deepEqual(shownBody, exactBody);
  assert.match(await context.locator('pre').textContent(), /exact-user-source/); assert.match(await trace.locator('.trace-summary').textContent(), /Early public summary/);
  await timeline.locator('details[data-sequence="1"] > summary').click();
  await sources.locator('details[data-sequence="3"] > summary').click(); assert.equal(payloadReads.length, 0);
  await sources.getByRole('button', { name: 'Load provider source record', exact: true }).click();
  await sources.getByText('Explicit original source payload.', { exact: false }).waitFor(); assert.equal(payloadReads.length, 1);
  await emit([event(2001, 'assistant.delta', { text: '\n\n**Still streaming.**' }), ...Array.from({ length: 24 }, (_, index) => event(2002 + index))]);
  assert.match(await trace.locator('.trace-page-controls span').textContent(), /Retained inspection.*sequences 1–100/);
  assert.equal(await timeline.locator('details[data-sequence="1"]').getAttribute('open'), ''); assert.equal(await sources.locator('details[data-sequence="3"]').getAttribute('open'), '');
  assert.equal(await timeline.locator('details[data-sequence="2001"]').count(), 0); assert.match(await page.locator('.assistant-message .message-content').textContent(), /Still streaming/);
  await trace.getByRole('button', { name: 'Load next 100 events', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('.trace-page-controls span')?.textContent.includes('sequences 1–200'));
  assert.equal(await timeline.locator('details[data-sequence="1"]').getAttribute('open'), ''); assert.equal(await sources.locator('details[data-sequence="3"]').getAttribute('open'), '');
  assert.match(await sources.locator('pre').textContent(), /Explicit original source payload/); assert.equal(payloadReads.length, 1);
  await emit([event(2026)]); assert.match(await trace.locator('.trace-page-controls span').textContent(), /sequences 1–200/);
  await trace.getByRole('button', { name: 'Resume live', exact: true }).click();
  assert.match(await trace.locator('.trace-page-controls span').textContent(), /Live tail.*1,000 visible.*sequences 1027–2026/);
  assert.equal(await timeline.locator('details[data-sequence="2026"]').count(), 1); assert.equal(await timeline.locator('details[data-sequence="1"]').count(), 0);
  assert.match(await trace.locator('.trace-content > .trace-coverage').textContent(), /26 events left this 1,000-event live window/);
  await trace.getByRole('button', { name: 'Read from start', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('.trace-page-controls span')?.textContent.includes('Retained inspection · 100 visible · sequences 1–100'));
  assert.deepEqual(JSON.parse(await request.locator('pre').textContent())[0].data.body, exactBody);
  await trace.getByRole('button', { name: 'Resume live', exact: true }).click();
  assert.match(await trace.locator('.trace-page-controls span').textContent(), /Live tail.*sequences 1027–2026/);
  assert.equal(turn.userText, userText); assert.equal(turn.assistantText, assistantText + '\n\n**Still streaming.**'); assert.equal(conversation.settingsRevision, 4);
  assert.deepEqual(traceReads, [0, 100, 0]); assert.equal(payloadReads.length, 1); assert.deepEqual(unexpected, []); assert.deepEqual(errors, []);
  const proof = { status: 'PASS', scope: 'fully intercepted synthetic browser; no service, user data, provider or inference', fullLiveTail: 1000, initialRetainedRange: [1, 100], nextRetainedRange: [1, 200], retainedInspectionSurvivesSse: true, explicitPayloadReads: 1, explicitDisclosurePreserved: true, resumedLiveRange: [1027, 2026], liveEvictions: 26, exactRequestAndMessageSourcesPreserved: true, retainedRestartPreservesLiveTail: true, traceReads, pageErrors: errors };
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) { await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true }); await page.screenshot({ path: join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'experiment-retained-trace.png') }); await writeFile(join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'retained-trace-qualification.json'), JSON.stringify(proof, null, 2)); }
  console.log(JSON.stringify(proof, null, 2));
} catch (error) { console.error(JSON.stringify({ scope: 'synthetic retained trace regression', traceReads, payloadReads, unexpected, errors })); throw error; }
finally { await browser.close(); }
