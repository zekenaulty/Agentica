// Search UI qualification against the credential-free deterministic service
// fixture only. No live provider or public-web request is made.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
assert.equal(process.env.AGENTICA_EXPERIMENT_FIXTURE, '1', 'Explicitly select a fixture service.');
assert.ok(process.env.AGENTICA_LAB_TEST_URL, 'Select an isolated service URL.');
const origin = new URL(process.env.AGENTICA_LAB_TEST_URL).origin;
assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(new URL(origin).hostname));
async function api(path) { const response = await fetch(origin + path); assert.ok(response.ok); return response.json(); }
const models = await api('/api/chats/models'); assert.equal(models.configured, false); assert.match(models.defaultModel, /^fixture-/);
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1040 } }); page.setDefaultTimeout(10000);
  const errors = []; const posts = []; const citationReads = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === 'POST' && /\/turns$/.test(path)) posts.push(path);
    if (request.method() === 'GET' && /\/citations$/.test(path)) citationReads.push(path);
  });
  await page.goto(origin + '/chat.html'); await page.waitForFunction(() => !document.querySelector('#new-chat')?.disabled);
  await page.locator('#new-chat').click(); await page.waitForFunction(() => !document.querySelector('#message')?.disabled && document.querySelector('#save-status')?.textContent === 'Saved · revision 1');
  const id = new URL(page.url()).searchParams.get('chat'); let conversation = await api(`/api/chats/${id}`);
  assert.equal(conversation.settings.ambientWebSearch, false); assert.equal(conversation.settings.maxWebSearchCalls, 3); assert.equal(await page.locator('#ambient-web-search').isChecked(), false);
  const save = async () => {
    const response = page.waitForResponse(response => response.url().endsWith(`/api/chats/${id}/settings`) && response.request().method() === 'PUT');
    await page.locator('#save-settings').click(); const value = await response; assert.ok(value.ok());
    await page.waitForFunction(() => document.querySelector('#save-status')?.textContent.startsWith('Saved · revision')); return value.json();
  };
  await page.locator('#ambient-web-search').check(); await page.locator('#max-web-search-calls').fill('5'); conversation = await save();
  assert.equal(conversation.settings.ambientWebSearch, true); assert.equal(conversation.settings.maxWebSearchCalls, 5);
  assert.match(await page.locator('#mode-description').textContent(), /Provider-native web search is enabled/);
  await page.locator('#message').fill('Fixture ambient search citation test.'); await page.locator('#message').press('Enter');
  await page.waitForFunction(() => document.querySelector('.turn-meta .badge')?.textContent === 'completed');
  conversation = await api(`/api/chats/${id}`); const ambient = conversation.turns[0]; const ambientArticle = page.locator(`.turn[data-turn-id="${ambient.id}"]`);
  await ambientArticle.locator('.answer-citation').waitFor(); assert.equal(await ambientArticle.locator('.answer-citation').getAttribute('href'), 'https://example.com/source');
  assert.equal(await ambientArticle.locator('.answer-citation').getAttribute('rel'), 'noopener noreferrer');
  const rawRenderedText = await ambientArticle.locator('.assistant-message .message-content').evaluate(element => { const copy = element.cloneNode(true); for (const citation of copy.querySelectorAll('a')) citation.remove(); return copy.textContent; });
  assert.equal(rawRenderedText, ambient.assistantText);
  await page.reload(); await page.locator(`.turn[data-turn-id="${ambient.id}"] .answer-citation`).waitFor();
  assert.equal(await page.locator(`.turn[data-turn-id="${ambient.id}"] .turn-trace`).getAttribute('open'), null, 'Saved links restore while trace remains collapsed.');
  assert.equal(await page.locator('#ambient-web-search').isChecked(), true); assert.equal(await page.locator('#max-web-search-calls').inputValue(), '5');
  await page.selectOption('#mode', 'harness'); assert.equal(await page.locator('#ambient-web-search').isDisabled(), true); assert.equal(await page.locator('#ambient-web-search').isChecked(), false);
  await page.locator('#capability-list input[value="web.search"]').check(); conversation = await save();
  assert.equal(conversation.settings.ambientWebSearch, false); assert.ok(conversation.settings.enabledTools.includes('web.search'));
  await page.locator('#message').fill('Fixture explicit web.search tool-source test.'); await page.locator('#message').press('Enter');
  await page.waitForFunction(() => document.querySelectorAll('.turn-meta .badge').length === 2 && [...document.querySelectorAll('.turn-meta .badge')].every(element => element.textContent === 'completed'));
  conversation = await api(`/api/chats/${id}`); const harness = conversation.turns[1]; const toolArticle = page.locator(`.turn[data-turn-id="${harness.id}"]`);
  await toolArticle.locator('.tool-sources a').waitFor(); assert.equal(await toolArticle.locator('.answer-citation').count(), 0, 'Tool offsets do not annotate the final Harness answer.');
  assert.match(await toolArticle.locator('.tool-sources').textContent(), /do not annotate the final answer/);
  assert.equal(await toolArticle.locator('.assistant-message .message-content').textContent(), harness.assistantText);
  await toolArticle.locator('.turn-trace').evaluate(element => { element.open = true; });
  const inspector = toolArticle.locator('.trace-inspector').filter({ has: page.locator('summary', { hasText: 'Web search & sources' }) }).first();
  await inspector.locator('summary').click(); await inspector.locator('pre').filter({ hasText: 'provider.citations' }).waitFor();
  assert.match(await inspector.locator('pre').textContent(), /"target": "tool"/);
  const artifactDirectory = process.env.AGENTICA_BROWSER_ARTIFACTS; const screenshots = [];
  if (artifactDirectory) {
    await mkdir(artifactDirectory, { recursive: true });
    await page.locator('#settings-panel').evaluate(element => { element.scrollTop = 0; }); await toolArticle.scrollIntoViewIfNeeded();
    const path = join(artifactDirectory, 'experiment-chat-web-search.png'); await page.screenshot({ path }); screenshots.push(path);
  }
  await page.reload(); await page.locator(`.turn[data-turn-id="${ambient.id}"] .answer-citation`).waitFor(); await page.locator(`.turn[data-turn-id="${harness.id}"] .tool-sources a`).waitFor();
  assert.equal(await page.locator(`.turn[data-turn-id="${harness.id}"] .answer-citation`).count(), 0);
  await page.locator('#new-chat').click(); await page.waitForFunction(previous => new URL(location.href).searchParams.get('chat') !== previous && !document.querySelector('#message')?.disabled, id);
  assert.equal(await page.locator('#ambient-web-search').isChecked(), false); assert.equal(await page.locator('#max-web-search-calls').inputValue(), '3');
  assert.equal(posts.length, 2); assert.ok(citationReads.length >= 2); assert.deepEqual(errors, []);
  const proof = { status: 'PASS', scope: 'credential-free deterministic service fixture; no live provider or public web', conversationId: id, ambientTurnId: ambient.id, toolTurnId: harness.id, searchOffByDefault: true, savedSettingsReloaded: true, freshDefaultsIsolated: true, assistantInlineCitationVisible: true, rawTranscriptUnchanged: true, safeLinkAttributes: true, toolSourcesSeparateFromAnswer: true, restoredLinksWithoutTraceExpansion: true, sourceTraceVisible: true, citationProjectionReads: citationReads.length, fixtureTurns: posts.length, realProviderCalls: 0, pageErrors: errors, screenshots };
  if (artifactDirectory) await writeFile(join(artifactDirectory, 'web-search-qualification.json'), JSON.stringify(proof, null, 2));
  console.log(JSON.stringify(proof, null, 2));
} finally { await browser.close(); }
