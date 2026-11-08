// Real browser against intercepted Lab assets and disposable records. No provider or saved chat.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const browser = await chromium.launch({ headless: true, channel: process.env.AGENTICA_BROWSER_CHANNEL || undefined });
const origin = 'http://127.0.0.1:5992'; const assets = new URL('../Agentica.Lab.Web/wwwroot/', import.meta.url);
const settings = { mode: 'clean-room', model: 'fixture', reasoningEffort: 'high', instructions: '', reasoningSummary: true,
  enabledTools: [], memoryEnabled: false, threadRecallEnabled: false, maxOutputTokens: 4096, contextWindowTokens: 131072,
  maxInputCharacters: 80000, maxHistoryMessages: 32, historyPolicy: 'stop', contextDepth: 'normal' };
const turn = { id: 'turn', conversationId: 'room', sequence: 1, userText: 'Synthetic reader test.', assistantText: 'Saved answer.',
  status: 'completed', createdAt: '2026-11-08T12:00:00Z', settingsRevision: 1, settings, traceCount: 0, droppedTraceCount: 0 };
const room = { id: 'room', title: 'Reader regressions', createdAt: turn.createdAt, updatedAt: turn.createdAt, settingsRevision: 1, settings, turns: [turn] };
const records = []; const payloads = new Map(); const requests = []; const errors = []; const results = [];
const record = (type, data) => ({ sequence: records.length + 1, type, data, callId: 'call', itemId: 'reason', time: turn.createdAt });
const source = summaryIndex => ({ version: 1, itemId: 'reason', outputIndex: 0, summaryIndex });
let page; let failPayloadOnce = false;
try {
  page = await browser.newPage({ viewport: { width: 1400, height: 1000 } }); page.setDefaultTimeout(5000);
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    globalThis.streams = [];
    globalThis.EventSource = class {
      constructor() { globalThis.streams.push(this); queueMicrotask(() => this.onopen?.()); }
      close() { this.closed = true; }
    };
  });
  await page.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url());
    if (url.origin !== origin || request.method() !== 'GET') { errors.push(`Unexpected request ${request.method()} ${url.href}`); return route.abort(); }
    requests.push(url.pathname + url.search); let body;
    if (url.pathname === '/api/chats') body = [room];
    else if (url.pathname === '/api/chats/models') body = { configured: false, models: ['fixture'], reasoningEfforts: ['high'] };
    else if (url.pathname === '/api/chats/capabilities') body = [];
    else if (url.pathname === '/api/providers/openai/settings') body = { configured: false };
    else if (url.pathname === '/api/chats/room') body = room;
    else if (url.pathname.endsWith('/citations')) body = { events: [], next: records.length, hasMore: false };
    else if (url.pathname.endsWith('/trace')) {
      const after = Number(url.searchParams.get('after') || 0); const tail = url.searchParams.get('tail') === 'true';
      const candidates = tail ? [...records].reverse() : records.filter(event => event.sequence > after);
      const events = []; let characters = 0;
      for (const event of candidates) {
        const size = JSON.stringify(event).length;
        if (events.length >= 100 || (events.length && characters + size > 32000)) break;
        events.push(event); characters += size;
      }
      if (tail) events.reverse();
      const next = events.at(-1)?.sequence ?? after; body = { events, next, hasMore: next < records.length };
    } else if (/\/events\/\d+\/payload$/.test(url.pathname)) {
      if (failPayloadOnce) { failPayloadOnce = false; return route.fulfill({ status: 503, json: { message: 'Synthetic temporary read failure.' } }); }
      body = payloads.get(Number(url.pathname.split('/').at(-2)));
      assert.notEqual(body, undefined, 'Only this turn’s retained payloads may be read.');
    } else if (url.pathname.startsWith('/api/')) throw new Error(`Unexpected API ${url.pathname}`);
    if (body !== undefined) return route.fulfill({ json: body });
    const name = url.pathname.slice(1);
    try { return route.fulfill({ contentType: name.endsWith('.css') ? 'text/css' : /\.(mjs|js)$/.test(name) ? 'text/javascript' : 'text/html', body: await readFile(new URL(name, assets)) }); }
    catch { return route.fulfill({ status: 404, body: '' }); }
  });
  const open = async () => { turn.traceCount = records.length; await page.goto(origin + '/chat.html?chat=room'); await page.waitForSelector('.turn'); };
  const work = page.locator('.turn-work'); const pageInfo = page.locator('.trace-page-controls span');
  const check = async (name, action) => {
    records.length = 0; payloads.clear(); requests.length = 0; failPayloadOnce = false;
    turn.status = 'completed'; turn.assistantText = 'Saved answer.';
    try { await action(); results.push({ name, result: 'PASS' }); }
    catch (error) { results.push({ name, result: 'FAIL', message: error.message, notice: await page.locator('#chat-notice').textContent().catch(() => ''), requests: [...requests] }); }
  };
  await check('preview follows the latest updated interleaved block', async () => {
    turn.status = 'running'; turn.assistantText = ''; await open();
    await page.waitForFunction(() => globalThis.streams.length === 1);
    for (const [index, text] of [[0, 'First block.'], [1, 'Second block.'], [0, ' Latest update to first block.']]) {
      const event = record('provider.summary.delta', { text, source: source(index) }); records.push(event);
      await page.evaluate(event => globalThis.streams.find(stream => !stream.closed).onmessage({ data: JSON.stringify(event) }), event);
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    }
    assert.match(await page.locator('.work-excerpt').textContent(), /Latest update to first block\.$/);
    await work.locator(':scope > summary').click();
    await page.waitForFunction(() => document.querySelectorAll('.work-block').length === 2);
    assert.equal(await page.locator('.work-block').count(), 2, 'Reading order remains independent of recency.');
    assert.match(await page.locator('.work-block').first().textContent(), /First block/);
  });
  await check('retained work advances through all three pages without a restart', async () => {
    for (let index = 0; index < 205; index++) records.push(record('provider.summary.delta', { text: 'part ', source: source(0) }));
    const original = JSON.stringify(records); await open(); await work.locator(':scope > summary').click();
    await page.waitForFunction(() => document.querySelector('.trace-page-controls span').textContent.includes('106–205'));
    await work.getByRole('button', { name: /retained work/i }).click();
    await page.waitForFunction(() => document.querySelector('.trace-page-controls span').textContent.includes('1–100'));
    await work.getByRole('button', { name: /retained work/i }).click();
    await page.waitForFunction(() => document.querySelector('.trace-page-controls span').textContent.includes('Retained inspection'));
    // Wait for the request handler and its render without depending on the expected next page.
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    assert.match(await pageInfo.textContent(), /1–200/);
    await work.getByRole('button', { name: /retained work/i }).click();
    await page.waitForFunction(() => document.querySelector('.trace-page-controls span').textContent.includes('full retained sequence loaded'));
    assert.match(await pageInfo.textContent(), /1–205/);
    assert.deepEqual(requests.filter(url => url.includes('/trace?after=')), [
      '/api/chats/room/turns/turn/trace?after=0&limit=100', '/api/chats/room/turns/turn/trace?after=100&limit=100', '/api/chats/room/turns/turn/trace?after=200&limit=100']);
    assert.equal(JSON.stringify(records), original);
  });
  await check('separate payloads are declared, explicitly loaded, and retryable', async () => {
    const text = 'Exact retained summary. '.repeat(2500);
    payloads.set(1, { text, source: source(0) });
    payloads.set(2, { type: 'response.reasoning_summary_part.done', item_id: 'reason', summary_index: 0, part: { type: 'summary_text', text } });
    for (const [sequence, data] of payloads) records.push(record(sequence === 1 ? 'provider.summary.delta' : 'provider.item', {
      payloadRef: `/api/chats/room/turns/turn/events/${sequence}/payload`, originalCharacters: JSON.stringify(data).length,
      preview: JSON.stringify(data).slice(0, 12000), truncated: false }));
    const original = JSON.stringify(records); await open(); await work.locator(':scope > summary').click();
    await page.waitForFunction(() => document.querySelector('.trace-page-controls span').textContent.includes('full retained sequence loaded'));
    assert.equal(await work.locator('.trace-coverage').isVisible(), true, 'Sequence coverage does not imply payload coverage.');
    assert.equal(requests.some(url => url.endsWith('/payload')), false, 'Opening work does not eagerly read large payloads.');
    if (process.env.AGENTICA_BROWSER_ARTIFACTS) {
      await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true });
      await page.screenshot({ path: join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'separate-work-payloads.png'), fullPage: true });
    }
    failPayloadOnce = true;
    const first = work.getByRole('button', { name: 'Load retained work record #1', exact: true });
    await first.click();
    await page.waitForFunction(() => document.querySelector('.work-payloads').textContent.includes('Synthetic temporary read failure'));
    await first.click();
    await page.waitForFunction(() => document.querySelector('.work-block .markdown-body')?.textContent.startsWith('Exact retained summary.'));
    await work.getByRole('button', { name: 'Load retained work record #2', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.work-payloads').children.length === 0);
    assert.equal((await page.locator('.work-block .markdown-body').textContent()).trimEnd(), text.trimEnd());
    assert.doesNotMatch(await page.locator('.work-block-state').textContent(), /coverage differs|End boundary/);
    assert.equal(await work.locator('.trace-coverage').isVisible(), false);
    assert.equal(requests.filter(url => url.endsWith('/payload')).length, 3, 'Two successful explicit reads and one failed read.');
    assert.equal(JSON.stringify(records), original);
    records.length = 0; records.push(record('provider.item', { truncated: true, reason: 'Per-event retention ceiling', originalCharacters: 2000000 }));
    await open(); await work.locator(':scope > summary').click();
    await page.waitForFunction(() => document.querySelector('.work-payloads')?.textContent.includes('not retained'));
    assert.equal(await work.getByRole('button', { name: /Load retained work record/ }).count(), 0);
  });
  assert.deepEqual(errors, []);
  const proof = { scope: 'intercepted Edge browser; no provider, live service, or saved chat', results, errors };
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) {
    await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true });
    await writeFile(join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'work-reader-regressions.json'), JSON.stringify(proof, null, 2));
  }
  console.log(JSON.stringify(proof, null, 2));
  assert.deepEqual(results.filter(result => result.result !== 'PASS'), []);
} finally { await browser.close(); }
