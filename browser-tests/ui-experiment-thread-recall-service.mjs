// Black-box qualification against the credential-free deterministic service.
// Creates only synthetic chats. No mocks, SQLite writes or live providers.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
assert.equal(process.env.AGENTICA_EXPERIMENT_FIXTURE, '1', 'Explicitly select a fixture service.');
assert.ok(process.env.AGENTICA_LAB_TEST_URL, 'Select an isolated service URL.');
const origin = new URL(process.env.AGENTICA_LAB_TEST_URL).origin;
assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(new URL(origin).hostname));
async function api(path, method = 'GET', body) {
  const response = await fetch(origin + path, { method, headers: body === undefined ? undefined : { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const value = await response.json(); assert.ok(response.ok, `${method} ${path}: ${response.status} ${JSON.stringify(value)}`); return value;
}
const models = await api('/api/chats/models'); assert.equal(models.configured, false); assert.match(models.defaultModel, /^fixture-/);
let conversation = await api('/api/chats', 'POST', { title: 'Isolated thread recall service qualification' });
const id = conversation.id; const path = `/api/chats/${id}`;
assert.equal(conversation.settings.threadRecallEnabled, false); assert.equal(conversation.settings.memoryEnabled, false);
conversation = await api(`${path}/settings`, 'PUT', { expectedRevision: conversation.settingsRevision, settings: { ...conversation.settings, model: models.defaultModel, historyPolicy: 'recent', maxHistoryMessages: 8 } });
const seeds = [];
for (let sequence = 1; sequence <= 25; sequence++) {
  const content = sequence === 1 ? 'Canonical origin 🧭 copper-lantern belongs to this synthetic conversation.' : `Unrelated synthetic fixture turn ${sequence}.`;
  let turn = await api(`${path}/turns`, 'POST', { content, requestId: `recall-seed-${sequence}` });
  for (let poll = 0; poll < 80 && turn.status === 'running'; poll++) { await new Promise(resolve => setTimeout(resolve, 100)); turn = await api(`${path}/turns/${turn.id}`); }
  assert.equal(turn.status, 'completed', JSON.stringify(turn)); seeds.push(turn);
}
const sourceId = `${seeds[0].id}:user`; const marker = 'copper-lantern'; const markerStart = seeds[0].userText.indexOf(marker);
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1040 }, reducedMotion: 'reduce' }); page.setDefaultTimeout(15000);
  const errors = []; const reads = []; const historyRequests = []; const screenshots = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { const url = new URL(request.url()); if (url.pathname === path && request.method() === 'GET') { assert.equal(url.searchParams.get('limit'), '20'); reads.push(url.search); } if (url.pathname.startsWith(`${path}/history/`)) historyRequests.push(url.pathname); });
  await page.goto(`${origin}/chat.html?chat=${id}`); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 20 && !document.querySelector('#message')?.disabled);
  assert.equal(historyRequests.length, 0, 'Recall Off performs no index or source API calls.');
  assert.match(await page.locator('#history-page-status').textContent(), /20 of 25/);
  assert.equal(await page.locator(`.turn[data-turn-id="${seeds[0].id}"]`).count(), 0);
  await page.locator('#load-earlier-turns').click(); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 25);
  await page.locator('#thread-recall-section').evaluate(element => { element.open = true; }); assert.equal(historyRequests.length, 0);
  await page.locator('#thread-recall-enabled').check(); await page.selectOption('#thread-recall-mode', 'manual');
  await page.locator('#thread-history-query').fill(marker); await page.locator('#search-thread-history').click(); await page.locator('#thread-history-results .source-reference').first().waitFor();
  // Read the canonical user source, which is outside the protected recent tail.
  const sourceResult = await api(`${path}/history/messages/${encodeURIComponent(sourceId)}?startOffset=${markerStart}&maxCharacters=${marker.length}&neighborRadius=1`);
  const exactHit = sourceResult.hits.find(hit => hit.source.sourceId === sourceId);
  assert.equal(exactHit.startOffset, markerStart); assert.equal(exactHit.excerpt, marker); assert.equal(exactHit.endOffset, markerStart + marker.length);
  const userResult = page.locator('#thread-history-results .recall-hit').filter({ has: page.locator('strong', { hasText: 'Turn 1 · user' }) });
  await userResult.locator('.source-reference').click(); await page.locator('#history-source-dialog[open]').waitFor();
  const selected = await page.locator('#history-source-text').evaluate((element, text) => { const start = element.value.indexOf(text); if (start < 0) throw new Error('Exact source marker missing.'); element.focus(); element.setSelectionRange(start, start + text.length); return { start, length: text.length }; }, marker);
  assert.ok(selected.start >= 0); await page.locator('#pin-history-source').click(); await page.locator('#close-history-source').click();
  const save = page.waitForResponse(response => response.url().endsWith(`${path}/settings`) && response.request().method() === 'PUT');
  await page.locator('#save-settings').click(); assert.ok((await save).ok()); await page.waitForFunction(() => document.querySelector('#save-status')?.textContent.startsWith('Saved · revision'));
  conversation = await api(`${path}?limit=20`); assert.deepEqual(conversation.settings.threadRecallSelections, [{ sourceId, startOffset: markerStart, endOffset: markerStart + marker.length }]);
  assert.equal(await page.locator('.turn').count(), 25, 'Saving header-only settings preserves loaded pages.');
  await page.locator('#message').fill('Use the pinned synthetic evidence if relevant.'); await page.locator('#message').press('Enter');
  await page.waitForFunction(() => document.querySelectorAll('.turn').length === 26 && [...document.querySelectorAll('.turn-meta .badge')].every(element => element.textContent === 'completed'));
  conversation = await api(`${path}?limit=20`); const recalled = conversation.turns.at(-1); assert.equal(recalled.sequence, 26);
  const events = []; let after = 0;
  for (let pageIndex = 0; pageIndex < 5; pageIndex++) { const tracePage = await api(`${path}/turns/${recalled.id}/trace?after=${after}&limit=100`); events.push(...tracePage.events); if (!tracePage.hasMore) break; after = tracePage.next; }
  const recallEvent = events.find(event => event.type === 'context.recall'); assert.ok(recallEvent, 'Actual service emits allocation manifest.');
  const manifest = recallEvent.data?.payloadRef ? await api(`${path}/turns/${recalled.id}/events/${recallEvent.sequence}/payload`) : recallEvent.data;
  assert.equal(manifest.mode, 'manual'); assert.equal(manifest.scopeRef, id); assert.equal(manifest.asOfSequence, 25);
  assert.ok(manifest.selected.some(evidence => evidence.sourceId === sourceId && evidence.startOffset === markerStart && evidence.endOffset === markerStart + marker.length && evidence.content === marker));
  assert.ok(manifest.presentedCharacters >= marker.length); assert.equal(recalled.settings.memoryEnabled, false);
  const article = page.locator(`.turn[data-turn-id="${recalled.id}"]`); await article.locator('.turn-trace').evaluate(element => { element.open = true; });
  const inspector = article.locator('.trace-inspector').filter({ has: page.locator('summary', { hasText: 'Thread recall evidence manifest' }) }).first(); await inspector.locator('summary').click();
  const loadPayload = inspector.getByRole('button', { name: 'Load retained recall manifest' }); if (await loadPayload.count()) await loadPayload.click();
  await inspector.locator('pre').filter({ hasText: sourceId }).waitFor(); assert.match(await inspector.locator('pre').textContent(), /sourceCalls/);
  await inspector.locator('.source-reference').first().click(); await page.locator('#history-source-dialog[open]').waitFor(); assert.equal(await page.locator('#history-source-text').inputValue(), marker);
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) { await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true }); const screenshot = join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'experiment-thread-recall-service.png'); await page.screenshot({ path: screenshot }); screenshots.push(screenshot); }
  await page.locator('#close-history-source').click(); await page.reload(); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 20 && !document.querySelector('#message')?.disabled);
  assert.equal(await page.locator('#thread-recall-enabled').isChecked(), true); assert.equal(await page.locator('#thread-recall-mode').inputValue(), 'manual');
  await page.locator('#thread-recall-section').evaluate(element => { element.open = true; }); assert.match(await page.locator('#thread-recall-pins').textContent(), new RegExp(sourceId));
  assert.deepEqual(errors, []);
  const proof = { status: 'PASS', scope: 'actual credential-free deterministic service; no mocks or live providers', conversationId: id, canonicalTurns: 26, seedFixtureTurns: 25, recalledFixtureTurns: 1, initialNewestTurns: 20, earlierPageTurns: 5, allBrowserConversationReadsBounded: reads.every(query => query.includes('limit=20')), offIndexCalls: 0, exactRange: { sourceId, startOffset: markerStart, endOffset: markerStart + marker.length }, actualRecallManifest: manifest, savedSettingsReloaded: true, retainedSourceLinksWork: true, memoryEnabled: false, realProviderCalls: 0, semanticModelUseProven: false, pageErrors: errors, screenshots };
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) await writeFile(join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'thread-recall-service-qualification.json'), JSON.stringify(proof, null, 2));
  console.log(JSON.stringify(proof, null, 2));
} finally { await browser.close(); }
