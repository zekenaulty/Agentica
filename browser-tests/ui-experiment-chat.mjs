// Real Lab HTTP/storage + real browser. No turn is sent unless the caller
// explicitly selects a credential-free fixture service. This is offline UI
// qualification, not live model or full Agentica harness acceptance.
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import assert from 'node:assert/strict';

const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
if (!process.env.AGENTICA_LAB_TEST_URL) throw new Error('Set AGENTICA_LAB_TEST_URL to an isolated Lab service. The active Lab is not selected implicitly.');
const origin = new URL(process.env.AGENTICA_LAB_TEST_URL).origin;
assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(new URL(origin).hostname), 'Use an isolated loopback service.');
const fixture = process.env.AGENTICA_EXPERIMENT_FIXTURE === '1';
const artifactDirectory = process.env.AGENTICA_BROWSER_ARTIFACTS;
const screenshots = [];
const idFromPage = page => new URL(page.url()).searchParams.get('chat');
async function api(path, method = 'GET', data) {
  const response = await fetch(origin + path, { method, headers: data === undefined ? {} : { 'Content-Type': 'application/json' }, body: data === undefined ? undefined : JSON.stringify(data) });
  const result = await response.json();
  assert.ok(response.ok, `${method} ${path}: HTTP ${response.status} ${result.message ?? ''}`);
  return result;
}
const serviceModels = await api('/api/chats/models');
if (fixture) {
  assert.equal(serviceModels.configured, false, 'Streaming qualification requires a credential-free service.');
  assert.match(serviceModels.defaultModel, /^fixture-/, 'Streaming qualification requires an explicitly named fixture model.');
}
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1040 } });
  page.setDefaultTimeout(10000);
  const errors = []; const turnPosts = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (request.method() === 'POST' && /\/api\/chats\/[^/]+\/turns$/.test(new URL(request.url()).pathname)) turnPosts.push(request.postDataJSON()); });
  // API traffic and assets must stay with the local service. Its fixture
  // executor is responsible for never contacting a live provider.
  await page.route('**/*', route => {
    const url = new URL(route.request().url());
    assert.equal(url.origin, origin, 'The browser must never receive or contact an external provider.');
    return route.continue();
  });
  const save = async () => {
    const response = page.waitForResponse(response => /\/api\/chats\/[^/]+\/settings$/.test(new URL(response.url()).pathname) && response.request().method() === 'PUT');
    await page.locator('#save-settings').click();
    const result = await response;
    assert.ok(result.ok(), `Configuration save failed: ${await result.text()}`);
    await page.waitForFunction(() => document.querySelector('#save-status')?.textContent.startsWith('Saved · revision'));
    return result.json();
  };
  const showSettings = async () => { if (!(await page.locator('#settings-panel').isVisible())) await page.locator('#toggle-settings').click(); };
  const screenshot = async filename => {
    if (!artifactDirectory) return;
    await mkdir(artifactDirectory, { recursive: true });
    const path = join(artifactDirectory, filename); await page.screenshot({ path }); screenshots.push(path);
  };
  await page.goto(`${origin}/chat.html`);
  await page.waitForFunction(() => !document.querySelector('#new-chat')?.disabled);
  await page.locator('#new-chat').click();
  await page.waitForFunction(() => document.querySelector('#save-status')?.textContent === 'Saved · revision 1' && !document.querySelector('#message')?.disabled);
  const firstId = idFromPage(page); assert.ok(firstId);
  const first = await api(`/api/chats/${firstId}`);
  assert.equal(first.settings.instructions, ''); assert.equal(first.settings.mode, 'clean-room');
  assert.equal(first.settings.memoryEnabled, false); assert.deepEqual(first.settings.enabledTools, []); assert.deepEqual(first.turns, []);
  assert.equal(await page.locator('#instructions').inputValue(), '');
  assert.equal(await page.locator('#harness-settings').isVisible(), false);

  await page.locator('#model').fill('gpt-6-luna-browser-config-only');
  await page.selectOption('#reasoning-effort', 'high');
  await page.locator('#reasoning-summary').check();
  await page.locator('#instructions').fill('Synthetic experiment only.\nPreserve exactly:  two spaces.');
  await page.selectOption('#context-depth', 'focused');
  await page.selectOption('#history-policy', 'recent');
  const saved = await save();
  assert.equal(saved.settings.model, 'gpt-6-luna-browser-config-only'); assert.equal(saved.settings.reasoningEffort, 'high');
  assert.equal(saved.settings.instructions, 'Synthetic experiment only.\nPreserve exactly:  two spaces.');
  assert.equal(saved.settings.maxHistoryMessages, 12); assert.equal(saved.settings.maxInputCharacters, 32000);

  // API import deliberately contains line endings a textarea cannot represent.
  // Editing an unrelated field must not normalize the exact persisted source.
  const importedInstructions = 'First line\r\nSecond line\rThird line\n';
  await api(`/api/chats/${firstId}/settings`, 'PUT', { expectedRevision: saved.settingsRevision, settings: { ...saved.settings, instructions: importedInstructions } });
  await page.locator('#reload-settings').click();
  await page.waitForFunction(() => document.querySelector('#instructions')?.value === 'First line\nSecond line\nThird line\n');
  await page.locator('#model').fill('gpt-6-luna-browser-import-preservation');
  const exactSaved = await save();
  assert.equal(exactSaved.settings.instructions, importedInstructions, 'A model edit must preserve untouched imported CRLF/CR instructions.');
  const title = `Browser qualification ${firstId.slice(0, 8)}`;
  await page.locator('#rename-chat').click(); await page.locator('#rename-title').fill(title); await page.locator('#rename-title').press('Enter');
  await page.waitForFunction(expected => document.querySelector('#conversation-title')?.textContent === expected, title);
  assert.equal((await api(`/api/chats/${firstId}`)).title, title);
  await page.reload(); await page.waitForFunction(expected => document.querySelector('#conversation-title')?.textContent === expected, title);
  assert.equal(await page.locator('#model').inputValue(), 'gpt-6-luna-browser-import-preservation');
  assert.equal((await api(`/api/chats/${firstId}`)).settings.instructions, importedInstructions);

  await page.selectOption('#mode', 'harness');
  await page.locator('#capability-list input').first().waitFor();
  const tool = page.locator('#capability-list input').first(); const toolId = await tool.inputValue(); await tool.check();
  assert.equal(await page.locator('#harness-settings').isVisible(), true);
  assert.match(await page.locator('#mode-description').textContent(), /real Agentica planner/);
  const harnessSaved = await save(); assert.equal(harnessSaved.settings.mode, 'harness'); assert.deepEqual(harnessSaved.settings.enabledTools, [toolId]);
  assert.equal(harnessSaved.settings.memoryEnabled, false);
  await page.locator('#settings-panel').evaluate(element => { element.scrollTop = 0; });
  await screenshot('experiment-chat-desktop.png');
  await page.selectOption('#mode', 'clean-room'); const cleanSaved = await save(); assert.deepEqual(cleanSaved.settings.enabledTools, []);

  await page.locator('#new-chat').click();
  await page.waitForFunction(previous => new URL(location.href).searchParams.get('chat') !== previous && document.querySelector('#save-status')?.textContent === 'Saved · revision 1', firstId);
  const secondId = idFromPage(page); const second = await api(`/api/chats/${secondId}`);
  assert.equal(second.settings.instructions, ''); assert.equal(second.settings.mode, 'clean-room');
  assert.equal(second.settings.memoryEnabled, false); assert.deepEqual(second.settings.enabledTools, []); assert.deepEqual(second.turns, []);
  await page.locator('.conversation-button').filter({ hasText: title }).click();
  await page.waitForFunction(expected => document.querySelector('#conversation-title')?.textContent === expected, title);
  assert.equal(idFromPage(page), firstId);
  assert.equal((await api(`/api/chats/${firstId}`)).settings.instructions, importedInstructions);

  const viewportResults = [];
  for (const width of [390, 320]) {
    await page.setViewportSize({ width, height: 844 });
    if (await page.locator('#settings-panel').isVisible()) await page.locator('#close-settings').click();
    const measurement = await page.evaluate(() => {
      const composer = document.querySelector('#composer').getBoundingClientRect();
      return { width: innerWidth, scrollWidth: document.documentElement.scrollWidth, composerTop: composer.top, composerBottom: composer.bottom, height: innerHeight };
    });
    assert.ok(measurement.scrollWidth <= width, `${width}px page overflows: ${JSON.stringify(measurement)}`);
    assert.ok(measurement.composerTop >= 0 && measurement.composerBottom <= measurement.height + 1, 'Composer stays in the viewport.');
    await showSettings();
    const panel = await page.locator('#settings-panel').boundingBox(); assert.ok(panel.x >= -1 && panel.x + panel.width <= width + 1, 'Configuration drawer stays inside narrow viewport.');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    await screenshot(`experiment-chat-${width}px.png`);
    await page.locator('#close-settings').click(); viewportResults.push({ width, horizontalOverflow: false, composerVisible: true });
  }
  await page.setViewportSize({ width: 1500, height: 1040 }); await showSettings();
  let streamQualification = 'not run; storage/configuration checks only';
  if (fixture) {
    await page.locator('#model').fill(serviceModels.defaultModel);
    await page.locator('#instructions').fill(''); await page.locator('#reasoning-summary').check(); await save();
    await page.locator('#message').fill('Browser fixture: emit a provider summary, assistant output and typed native trace.');
    await page.locator('#message').press('Enter');
    await page.waitForFunction(() => document.querySelector('.assistant-message .message-content')?.textContent.length > 0 && document.querySelector('#transcript')?.getAttribute('aria-busy') === 'true');
    await page.waitForFunction(() => document.querySelector('.turn-meta .badge')?.textContent === 'completed');
    const durable = await api(`/api/chats/${firstId}`); const turn = durable.turns.at(-1);
    assert.equal(turn.status, 'completed'); assert.ok(turn.assistantText.length > 0); assert.ok(turn.usage);
    const article = page.locator(`.turn[data-turn-id="${turn.id}"]`);
    await article.locator('.turn-trace').evaluate(element => { element.open = true; });
    await page.waitForFunction(() => document.querySelector('.trace-summary pre')?.textContent.length > 0);
    const summary = await article.locator('.trace-summary pre').textContent(); assert.ok(summary.length);
    const expand = async name => { const detail = article.locator('.trace-inspector').filter({ has: page.locator('summary', { hasText: name }) }).first(); await detail.evaluate(element => { element.open = true; }); return detail; };
    const request = await expand('Effective provider request'); await page.waitForFunction(() => document.querySelector('.turn-trace .trace-inspector .json-view')?.textContent.includes('"store": false'));
    assert.match(await request.locator('pre').textContent(), /"store": false/);
    const context = await expand('Context manifest'); await context.locator('pre').filter({ hasText: firstId }).waitFor();
    const native = await expand('Native output items'); await native.locator('pre').filter({ hasText: /reasoning|response\./ }).waitFor();
    const source = await expand('Provider source records');
    const sourceRecord = source.locator('.typed-event').first(); await sourceRecord.waitFor();
    await sourceRecord.locator('summary').click();
    const payloadResponse = page.waitForResponse(response => /\/api\/chats\/[^/]+\/turns\/[^/]+\/events\/\d+\/payload$/.test(new URL(response.url()).pathname));
    await sourceRecord.getByRole('button', { name: 'Load provider source record', exact: true }).click();
    assert.ok((await payloadResponse).ok());
    await sourceRecord.locator('pre').filter({ hasText: /"type":\s*"response\./ }).waitFor();
    assert.match(JSON.parse(await sourceRecord.locator('pre').textContent()).type, /^response\./);
    const sourceSequence = await sourceRecord.getAttribute('data-sequence');
    const timeline = await expand('Typed timeline'); await timeline.locator('.typed-event').first().waitFor();
    assert.ok(await timeline.locator('.typed-event').count() > 0);
    assert.equal(await timeline.locator(`.typed-event[data-sequence="${sourceSequence}"]`).count(), 0, 'Provider source records stay separate from normalized timeline entries.');
    assert.equal(await article.locator('.assistant-message .message-content').textContent(), turn.assistantText);
    assert.equal(await article.locator('.trace-coverage').isVisible(), false, 'Complete live/replayed trace must not claim that pages are missing.');
    await page.locator('#settings-panel').evaluate(element => { element.scrollTop = 0; });
    await screenshot('experiment-chat-fixture-trace.png');
    await page.reload();
    await page.locator(`.turn[data-turn-id="${turn.id}"]`).waitFor();
    assert.equal(await page.locator(`.turn[data-turn-id="${turn.id}"] .assistant-message .message-content`).textContent(), turn.assistantText);
    streamQualification = { source: 'credential-free service fixture executor; no browser HTTP mocks', partialOutputObserved: true, durableTranscript: true, providerSummaryVisible: true, requestAndContextInspected: true, nativeItemsVisible: true, sourceRecordsLazyLoaded: true, sourceRecordsSeparateFromTimeline: true, typedTimelineVisible: true, usageReturned: true };
  }
  assert.equal(turnPosts.length, fixture ? 1 : 0, 'No extra model turn is started by storage or settings interactions.');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ status: 'PASS', scope: 'offline Lab browser/storage/configuration qualification', firstConversationId: firstId, freshConversationId: secondId, persistentRenameAndReload: true, blankDefaultsAndIsolation: true, exactInstructionsPreserved: true, modelAndEffortSaved: true, contextControlsSaved: true, harnessPaletteAndRevocation: true, memoryDisabled: true, viewports: viewportResults, streamQualification, turnPosts: turnPosts.length, screenshots, pageErrors: errors }, null, 2));
} finally { await browser.close(); }
