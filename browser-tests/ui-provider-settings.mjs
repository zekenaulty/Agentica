// Real browser UI with intercepted HTTP and WebSocket transports. No service,
// real credentials, provider requests or charged model runs are used.
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const origin = 'http://localhost:5078';
const sourceRoot = new URL('../Agentica.Lab.Web/wwwroot/', import.meta.url);
const fixtureCredential = ['fixture', 'credential', 'not-a-real-key'].join('-');
let settings = { provider: 'openai', model: 'gpt-6-luna', thinkingEffort: 'high', configured: false, credentialSource: 'none', credentialLifetime: 'serviceProcess', supportedReasoningEfforts: ['none', 'low', 'medium', 'high', 'xhigh', 'max'] };
const requests = []; const puts = [];
let rejectNextSave = false; let holdNextRead; let releaseRead; let observedHeldRead;
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => {
    globalThis.__capturedStarts = [];
    globalThis.__capturedSocketMessages = [];
    globalThis.WebSocket = class extends EventTarget {
      constructor() { super(); this.readyState = 0; queueMicrotask(() => { this.readyState = 1; this.dispatchEvent(new Event('open')); }); }
      send(text) {
        const message = JSON.parse(text); globalThis.__capturedSocketMessages.push(message);
        if (message.type !== 'start') throw new Error('Unexpected fixture host command.');
        globalThis.__capturedStarts.push(message.payload);
        const runId = `settings-fixture-${globalThis.__capturedStarts.length}`;
        queueMicrotask(() => {
          this.dispatchEvent(new MessageEvent('message', { data: JSON.stringify({ protocolVersion: 1, type: 'started', requestId: message.requestId, runId, payload: { runId } }) }));
          this.dispatchEvent(new MessageEvent('message', { data: JSON.stringify({ protocolVersion: 1, type: 'outcome', runId, payload: { outcome: { status: 'succeeded' } } }) }));
        });
      }
      close() { this.readyState = 3; this.dispatchEvent(new Event('close')); }
    };
    globalThis.EventSource = class { constructor() { queueMicrotask(() => this.onopen?.()); } close() {} };
  });
  await page.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url());
    assert.equal(url.origin, origin, 'The fixture must never contact another origin.');
    requests.push({ path: url.pathname, method: request.method(), body: request.postData() });
    let payload;
    if (url.pathname === '/api/providers/openai/settings') {
      if (request.method() === 'PUT') {
        const body = request.postDataJSON(); puts.push(body);
        if (rejectNextSave) { rejectNextSave = false; return route.fulfill({ status: 400, contentType: 'application/json', body: JSON.stringify({ message: `Untrusted error: ${fixtureCredential}` }) }); }
        const credentialSource = body.credentialAction === 'set' ? 'serviceMemory' : body.credentialAction === 'environment' ? 'environment' : settings.credentialSource;
        settings = { ...settings, model: body.model, thinkingEffort: body.thinkingEffort, credentialSource, configured: credentialSource !== 'none' };
        payload = settings;
      } else {
        payload = { ...settings };
        if (holdNextRead) { const hold = holdNextRead; holdNextRead = null; observedHeldRead(); await hold; }
      }
    } else if (url.pathname === '/api/providers') payload = [
      { provider: 'demo', defaultModel: 'scripted-inventory', configured: true, api: 'scripted-fixture', streams: true },
      { provider: 'openai', defaultModel: settings.model, defaultThinkingEffort: settings.thinkingEffort, configured: settings.configured, api: 'responses', streams: true },
      { provider: 'gemini', defaultModel: 'gemini-fixture', configured: true, api: 'interactions', streams: true }
    ];
    else if (url.pathname === '/api/runs' || url.pathname === '/api/recovery') payload = [];
    else if (url.pathname.startsWith('/api/runs/')) payload = {
      runId: url.pathname.split('/').at(-1), hostId: 'fixture', sessionId: 'fixture-session', sessionEpoch: 'fixture-epoch', objectiveId: 'fixture-objective',
      objective: 'Provider settings fixture', status: 'succeeded', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(),
      connected: false, pendingActions: [], context: {}, outcome: { outcome: { status: 'succeeded' } }
    };
    if (payload !== undefined) return route.fulfill({ contentType: 'application/json', body: JSON.stringify(payload) });
    const file = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
    if (!['index.html', 'styles.css', 'app.mjs', 'sdk/agentica-host.mjs'].includes(file)) return route.fulfill({ status: 404, body: '' });
    return route.fulfill({ contentType: file.endsWith('.html') ? 'text/html' : file.endsWith('.css') ? 'text/css' : 'text/javascript', body: await readFile(new URL(file, sourceRoot)) });
  });
  await page.goto(origin);
  await page.getByText('Credential: No credential configured.', { exact: false }).waitFor();
  assert.equal(await page.locator('#sample-provider').inputValue(), 'demo');
  assert.equal(await page.locator('#sample-reasoning-field').isVisible(), false);
  assert.equal(await page.locator('#openai-model').inputValue(), 'gpt-6-luna');
  assert.equal(await page.locator('#openai-model').getAttribute('maxlength'), '200');
  assert.equal(await page.locator('#sample-model').getAttribute('maxlength'), '200');
  assert.equal(await page.locator('#openai-thinking-effort').inputValue(), 'high');
  assert.equal(await page.locator('#openai-api-key').getAttribute('type'), 'password');
  assert.equal(await page.locator('#openai-thinking-effort option[value="minimal"]').count(), 0);
  const refresh = async () => {
    const response = page.waitForResponse(response => response.url().endsWith('/api/providers/openai/settings') && response.request().method() === 'GET');
    await page.locator('#refresh').click(); await response;
  };
  await page.locator('#openai-model').fill('gpt-6-luna-test-override');
  await page.selectOption('#openai-thinking-effort', 'max');
  await refresh();
  assert.equal(await page.locator('#openai-model').inputValue(), 'gpt-6-luna-test-override', 'Background refresh must preserve model edits.');
  assert.equal(await page.locator('#openai-thinking-effort').inputValue(), 'max', 'Background refresh must preserve effort edits.');
  await page.locator('#openai-model').fill('gpt-6-luna');
  await page.selectOption('#openai-thinking-effort', 'high');
  const save = async (selector = '#save-openai-settings') => {
    const response = page.waitForResponse(response => response.url().endsWith('/api/providers/openai/settings') && response.request().method() === 'PUT');
    await page.locator(selector).click(); await response;
    await page.waitForFunction(() => !document.querySelector('#save-openai-settings').disabled);
    assert.equal(await page.locator('#openai-api-key').inputValue(), '');
  };
  await page.locator('#openai-api-key').fill(fixtureCredential);
  await save();
  assert.deepEqual(puts.at(-1), { model: 'gpt-6-luna', thinkingEffort: 'high', credentialAction: 'set', apiKey: fixtureCredential });
  assert.match(await page.locator('#openai-credential-state').textContent(), /Service memory/);
  assert.equal(await page.evaluate(() => globalThis.__capturedStarts.length), 0, 'Saving credentials must never start a run.');
  await save();
  assert.deepEqual(puts.at(-1), { model: 'gpt-6-luna', thinkingEffort: 'high', credentialAction: 'keep' });
  await page.selectOption('#sample-provider', 'openai');
  assert.equal(await page.locator('#sample-thinking-effort').inputValue(), 'high');
  assert.equal(await page.locator('#sample-model').getAttribute('placeholder'), 'gpt-6-luna');
  await page.selectOption('#openai-thinking-effort', 'low');
  await save();
  assert.equal(await page.locator('#sample-thinking-effort').inputValue(), 'low', 'Untouched sample effort must follow a saved default.');
  assert.equal(await page.locator('#sample-thinking-source').textContent(), 'saved default');
  await page.selectOption('#openai-thinking-effort', 'high');
  await save();
  assert.equal(await page.locator('#sample-thinking-effort').inputValue(), 'high');
  await page.selectOption('#sample-thinking-effort', 'max');
  await refresh();
  assert.equal(await page.locator('#sample-thinking-effort').inputValue(), 'max');
  assert.equal(await page.locator('#sample-thinking-source').textContent(), 'per-run override');
  await page.selectOption('#sample-thinking-effort', 'high');
  await page.getByRole('button', { name: 'Start example' }).click();
  await page.waitForFunction(() => globalThis.__capturedStarts.length === 1 && !document.querySelector('#start-sample').disabled);
  const started = await page.evaluate(() => globalThis.__capturedStarts[0]);
  assert.equal(started.provider.provider, 'openai'); assert.equal(started.provider.thinkingEffort, 'high');
  assert.equal(Object.hasOwn(started.provider, 'apiKey'), false);
  assert.equal(Object.hasOwn(started.provider, 'geminiApi'), false);
  // An already-running poll cannot overwrite a later successful save.
  holdNextRead = new Promise(resolve => { releaseRead = resolve; });
  const readHeld = new Promise(resolve => { observedHeldRead = resolve; });
  await page.locator('#refresh').click();
  await readHeld;
  await page.selectOption('#openai-thinking-effort', 'medium');
  await save();
  releaseRead();
  await refresh();
  assert.equal(await page.locator('#openai-thinking-effort').inputValue(), 'medium');
  assert.equal(await page.locator('#sample-thinking-effort').inputValue(), 'high', 'An intentional sample override must survive saving a different default.');
  // A service error is allowed to contain untrusted text; never echo it into UI.
  rejectNextSave = true;
  await page.locator('#openai-api-key').fill(fixtureCredential);
  await save();
  assert.match(await page.locator('#openai-settings-status').textContent(), /Could not save settings/);
  assert.equal((await page.locator('body').innerText()).includes(fixtureCredential), false);
  await page.locator('#openai-api-key').fill(fixtureCredential);
  await save('#use-openai-environment');
  assert.equal(puts.at(-1).credentialAction, 'environment');
  assert.equal(Object.hasOwn(puts.at(-1), 'apiKey'), false, 'Environment reset must not submit an unused typed key.');
  assert.match(await page.locator('#openai-credential-state').textContent(), /Service environment/);
  await page.locator('#openai-model').fill('');
  await page.locator('#openai-api-key').fill(fixtureCredential);
  const priorPutCount = puts.length;
  await page.locator('#save-openai-settings').click();
  assert.equal(puts.length, priorPutCount); assert.equal(await page.locator('#openai-api-key').inputValue(), '');
  await page.locator('#openai-model').fill('gpt-6-luna');
  assert.equal(await page.evaluate(() => globalThis.__capturedStarts.length), 1);
  assert.equal(await page.evaluate(secret => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage }, socket: globalThis.__capturedSocketMessages }).includes(secret), fixtureCredential), false);
  assert.ok(requests.filter(request => request.body?.includes(fixtureCredential)).every(request => request.path === '/api/providers/openai/settings' && request.method === 'PUT'));
  await page.selectOption('#sample-provider', 'gemini');
  assert.equal(await page.locator('#gemini-transport-field').isVisible(), true);
  assert.equal(await page.locator('#sample-gemini-api').inputValue(), 'interactions');
  assert.equal(await page.locator('#sample-reasoning-field').isVisible(), false);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.locator('#openai-settings-form').scrollIntoViewIfNeeded();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, 'Mobile settings must not overflow horizontally.');
  await page.reload();
  await page.getByText('Credential: Service environment.', { exact: false }).waitFor();
  assert.equal(await page.locator('#openai-api-key').inputValue(), '');
  // A configured custom model may not support reasoning parameters at all.
  settings = { ...settings, model: 'custom-non-reasoning-model', thinkingEffort: null };
  await page.reload();
  await page.getByText('Credential: Service environment.', { exact: false }).waitFor();
  assert.equal(await page.locator('#openai-model').inputValue(), 'custom-non-reasoning-model');
  assert.equal(await page.locator('#openai-thinking-effort').inputValue(), '');
  await save();
  assert.equal(puts.at(-1).thinkingEffort, null, 'Provider default must remain an explicit nullable setting.');
  await page.selectOption('#sample-provider', 'openai');
  assert.equal(await page.locator('#sample-thinking-effort').inputValue(), '', 'A known null default must not fall back to high.');
  await page.getByRole('button', { name: 'Start example' }).click();
  await page.waitForFunction(() => globalThis.__capturedStarts.length === 1 && !document.querySelector('#start-sample').disabled);
  assert.equal(await page.evaluate(() => Object.hasOwn(globalThis.__capturedStarts[0].provider, 'thinkingEffort')), false, 'Default effort must be omitted from the run payload.');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ status: 'PASS', liveCalls: 0, initialProvider: 'demo', configuredModel: 'gpt-6-luna', startEffort: 'high', nullableDefaultOmitted: true, credentialModes: ['set', 'keep', 'environment'], secretOnlyInSettingsPut: true, secretClearedOnFailure: true, unsavedEditsPreserved: true, stalePollIgnored: true, mobileOverflow: false }));
} finally { releaseRead?.(); await browser.close(); }
