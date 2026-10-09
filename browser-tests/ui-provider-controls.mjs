// Real browser UI; all HTTP and WebSocket transports are local test doubles.
// No service process, credentials or live provider requests are used.
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const origin = 'http://localhost:5078';
const sourceRoot = new URL('../Agentica.Lab.Web/wwwroot/', import.meta.url);
const providers = [
  { provider: 'demo', defaultModel: 'scripted-inventory', configured: true, api: 'scripted-fixture', streams: true },
  { provider: 'gemini', defaultModel: 'gemini-fixture', configured: true, api: 'interactions', streams: true },
  { provider: 'openai', defaultModel: 'openai-fixture', configured: true, api: 'responses', streams: true }
];
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage(); const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    globalThis.__capturedStarts = [];
    globalThis.WebSocket = class extends EventTarget {
      constructor() { super(); this.readyState = 0; queueMicrotask(() => { this.readyState = 1; this.dispatchEvent(new Event('open')); }); }
      send(text) {
        const message = JSON.parse(text);
        if (message.type !== 'start') throw new Error('Unexpected fixture host command.');
        globalThis.__capturedStarts.push(message.payload);
        const runId = `fixture-run-${globalThis.__capturedStarts.length}`;
        queueMicrotask(() => {
          this.dispatchEvent(new MessageEvent('message', { data: JSON.stringify({ protocolVersion: 1, type: 'started', requestId: message.requestId, runId, payload: { runId } }) }));
          this.dispatchEvent(new MessageEvent('message', { data: JSON.stringify({ protocolVersion: 1, type: 'outcome', runId, payload: { outcome: { status: 'succeeded' } } }) }));
        });
      }
      close() { this.readyState = 3; this.dispatchEvent(new Event('close')); }
    };
    globalThis.EventSource = class {
      constructor() { queueMicrotask(() => this.onopen?.()); }
      close() {}
    };
  });
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    assert.equal(url.origin, origin, 'The provider-free UI fixture must never contact another origin.');
    let payload;
    if (url.pathname === '/api/providers') payload = providers;
    else if (url.pathname === '/api/runs' || url.pathname === '/api/recovery') payload = [];
    else if (url.pathname.startsWith('/api/runs/')) payload = {
      runId: url.pathname.split('/').at(-1), hostId: 'fixture', sessionId: 'fixture-session', sessionEpoch: 'fixture-epoch',
      objectiveId: 'fixture-objective', objective: 'Provider control fixture', status: 'succeeded',
      createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), connected: false,
      pendingActions: [], context: {}, outcome: { outcome: { status: 'succeeded' } }
    };
    if (payload !== undefined) return route.fulfill({ contentType: 'application/json', body: JSON.stringify(payload) });
    const file = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
    if (!['index.html', 'styles.css', 'app.mjs', 'sdk/agentica-host.mjs'].includes(file)) return route.fulfill({ status: 404, body: '' });
    const contentType = file.endsWith('.html') ? 'text/html' : file.endsWith('.css') ? 'text/css' : 'text/javascript';
    return route.fulfill({ contentType, body: await readFile(new URL(file, sourceRoot)) });
  });
  await page.goto(origin);
  await page.locator('#provider-list .provider-card').first().waitFor();
  assert.equal(await page.locator('#sample-provider').inputValue(), 'demo');
  assert.equal(await page.locator('#gemini-transport-field').isVisible(), false);
  const submit = async count => {
    await page.getByRole('button', { name: 'Start example' }).click();
    await page.waitForFunction(expected => globalThis.__capturedStarts.length === expected, count);
    await page.waitForFunction(() => !document.querySelector('#start-sample').disabled);
    assert.equal(await page.locator('#notice').isVisible(), false);
    return page.evaluate(() => globalThis.__capturedStarts.at(-1));
  };
  await page.selectOption('#sample-provider', 'gemini');
  assert.equal(await page.locator('#gemini-transport-field').isVisible(), true);
  assert.equal(await page.locator('#sample-gemini-api').isDisabled(), false);
  assert.equal(await page.locator('#sample-gemini-api').inputValue(), 'interactions');
  assert.equal((await submit(1)).provider.geminiApi, 'interactions');
  await page.selectOption('#sample-gemini-api', 'generatecontent');
  await page.getByRole('button', { name: 'Refresh', exact: false }).click();
  await page.waitForFunction(() => document.querySelector('#sample-gemini-api').value === 'generatecontent');
  assert.equal((await submit(2)).provider.geminiApi, 'generatecontent');
  await page.selectOption('#sample-provider', 'openai');
  assert.equal(await page.locator('#gemini-transport-field').isVisible(), false);
  assert.equal(await page.locator('#sample-gemini-api').isDisabled(), true);
  assert.equal(Object.hasOwn((await submit(3)).provider, 'geminiApi'), false);
  await page.selectOption('#sample-provider', 'demo');
  assert.equal(Object.hasOwn((await submit(4)).provider, 'geminiApi'), false);
  await page.selectOption('#sample-provider', 'gemini');
  assert.equal(await page.locator('#sample-gemini-api').inputValue(), 'interactions');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ status: 'PASS', liveCalls: 0, initialProvider: 'demo', geminiDefault: 'interactions', optionalRoute: 'generatecontent', nonGeminiFieldOmitted: true, pageErrors: errors }));
} finally { await browser.close(); }
