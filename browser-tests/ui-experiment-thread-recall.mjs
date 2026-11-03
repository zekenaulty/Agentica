// Fully intercepted browser qualification. Five thousand canonical turns exist
// only in this synthetic fixture; no user chat, service, index or provider runs.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const origin = 'http://127.0.0.1:5987';
const assets = new URL('../Agentica.Lab.Web/wwwroot/', import.meta.url);
const fixtureId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; const freshId = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
const turnId = sequence => sequence.toString(16).padStart(32, '0');
const sourceId = `${turnId(7)}:user`; const sourceText = 'Origin 🧭 statement: alpha beta was corrected later.';
const settings = { mode: 'clean-room', model: 'fixture-model', reasoningEffort: 'high', instructions: '', reasoningSummary: false, ambientWebSearch: false, maxWebSearchCalls: 3,
  maxOutputTokens: 4096, contextWindowTokens: 131072, maxInputCharacters: 80000, maxHistoryMessages: 32, historyPolicy: 'recent', contextDepth: 'normal', maxToolResultCharacters: 4000, maxToolEvidenceCharacters: 8000, maxSteps: 12, maxContinuations: 4, maxProviderCalls: 12, timeoutSeconds: 180, enabledTools: [], memoryEnabled: false,
  threadRecallEnabled: false, threadRecallMode: 'auto', threadRecallQuery: '', threadRecallSelections: [], threadRecallMaxCharacters: 8000, threadRecallTopK: 5, threadRecallNeighborRadius: 1, threadRecallRecentTurns: 2, threadRecallMaxCalls: 8 };
const turns = Array.from({ length: 5000 }, (_, index) => { const sequence = index + 1; return { id: turnId(sequence), conversationId: fixtureId, sequence, userText: sequence === 7 ? sourceText : `User record ${sequence}.`, assistantText: `Assistant record ${sequence}.`, status: 'completed', createdAt: '2026-11-01T00:00:00Z', finishedAt: '2026-11-01T00:00:01Z', settingsRevision: 1, settings, continuity: 'synthetic visible transcript', traceCount: 0, droppedTraceCount: 0 }; });
turns.at(-1).settings = { ...settings, threadRecallEnabled: true, threadRecallMode: 'manual' }; turns.at(-1).traceCount = 1;
turns[4998].settings = { ...settings, threadRecallEnabled: true, threadRecallMode: 'manual' }; turns[4998].traceCount = 1;
let conversation = { id: fixtureId, title: '5000-turn recall fixture', createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-01T00:00:01Z', settingsRevision: 1, settings, turns: [] };
let fresh; let maintenance = 'IDLE'; const historyRequests = []; const conversationReads = []; const saves = []; const pageErrors = []; const screenshots = [];
const coverage = status => ({ status, asOfSequence: 5000, lastIndexedSequence: status === 'PARTIAL' ? 2500 : 5000, eligibleSourceCount: 10000, indexedSourceCount: status === 'PARTIAL' ? 5000 : 10000, ftsAvailable: true, indexVersion: 'synthetic-fts-v1', reason: status === 'PARTIAL' ? 'Fixture projection is incomplete.' : null });
function sourceHit(id, start = 0, maximum = 4000) {
  const [idPart, role] = id.split(':'); const sequence = parseInt(idPart, 16); const turn = turns[sequence - 1]; if (!turn) return null;
  const content = role === 'user' ? turn.userText : turn.assistantText; const end = Math.min(content.length, start + maximum);
  return { source: { sourceId: id, turnId: turn.id, turnSequence: sequence, role, createdAt: turn.createdAt, contentHash: `fixture-hash-${sequence}-${role}`, characterCount: content.length, order: sequence * 2 }, startOffset: start, endOffset: end, excerpt: content.slice(start, end), matchReason: 'exact fixture source', neighborRefs: [`${turnId(6)}:assistant`, `${turnId(7)}:assistant`] };
}
const result = (hits, status = 'COMPLETE') => ({ retrievalId: 'synthetic-retrieval', scopeRef: fixtureId, asOfSequence: 5000, status, queryMethod: 'local lexical fixture', coverage: coverage(status), candidateCount: hits.length, inspectedCount: hits.length, hits, nextOffset: null, truncated: false, reasons: status === 'PARTIAL' ? ['Not all canonical sources were searched.'] : [], elapsedMilliseconds: 1 });
const manifestEvent = { sequence: 1, type: 'context.recall', time: '2026-11-01T00:00:01Z', data: { enabled: true, mode: 'manual', scopeRef: fixtureId, asOfSequence: 4999, queryOrigin: 'operator-selected', queryMethod: 'canonical exact', coverage: coverage('COMPLETE'), candidateCount: 1, inspectedCount: 1, selected: [{ sourceId, turnId: turnId(7), turnSequence: 7, role: 'user', createdAt: '2026-11-01T00:00:00Z', contentHash: 'fixture-hash-7-user', startOffset: 7, endOffset: 9, content: '🧭', matchReason: 'operator-selected' }], omissions: [], presentedCharacters: 2, truncated: false, consumer: 'direct quoted evidence', attention: 'not measured' } };
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1040 }, reducedMotion: 'reduce' }); page.setDefaultTimeout(10000); page.on('pageerror', error => pageErrors.push(error.message));
  await page.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url()); assert.equal(url.origin, origin); let body;
    const current = url.pathname.includes(freshId) ? fresh : conversation;
    if (url.pathname === '/api/chats/models') body = { configured: false, models: ['fixture-model'], defaultModel: 'fixture-model', reasoningEfforts: ['high'] };
    else if (url.pathname === '/api/providers/openai/settings') body = { configured: false };
    else if (url.pathname === '/api/chats/capabilities') body = ['conversation.read', 'conversation.search'].map(id => ({ id, name: id, description: url.searchParams.get('threadRecallEnabled') === 'true' ? 'v2 bound historical source' : 'v1 supplied context only', effect: 'readOnly', source: 'fixture', version: url.searchParams.get('threadRecallEnabled') === 'true' ? '2' : '1' }));
    else if (url.pathname === '/api/chats' && request.method() === 'GET') body = [conversation, ...(fresh ? [fresh] : [])];
    else if (url.pathname === '/api/chats' && request.method() === 'POST') { fresh = { ...conversation, id: freshId, title: 'Fresh Off fixture', settingsRevision: 1, settings: { ...settings }, turns: [] }; body = fresh; }
    else if (url.pathname === `/api/chats/${current?.id}` && request.method() === 'GET') {
      assert.equal(url.searchParams.get('limit'), '20', 'The browser must always request bounded canonical history.');
      const before = Number(url.searchParams.get('beforeSequence') ?? 5001); const eligible = current.id === fixtureId ? turns.filter(turn => turn.sequence < before) : []; const selected = eligible.slice(-20);
      conversationReads.push({ before, returned: selected.length }); body = { ...current, turns: selected, historyPage: { nextBeforeSequence: selected.length && selected[0].sequence > 1 ? selected[0].sequence : null, totalTurns: current.id === fixtureId ? 5000 : 0, latestSequence: current.id === fixtureId ? 5000 : 0, completedTurns: current.id === fixtureId ? 5000 : 0, earlierCompletedTurns: Math.max(0, eligible.length - selected.length) } };
    } else if (url.pathname === `/api/chats/${current?.id}/settings` && request.method() === 'PUT') { const update = request.postDataJSON(); saves.push(update); assert.equal(update.expectedRevision, current.settingsRevision); current.settings = update.settings; current.settingsRevision++; body = { ...current, turns: [] }; }
    else if (url.pathname.startsWith(`/api/chats/${fixtureId}/history/`)) {
      historyRequests.push({ path: url.pathname, method: request.method(), query: url.search });
      if (url.pathname.endsWith('/status')) body = { head: { scopeRef: fixtureId, asOfSequence: 5000, lastCommittedSequence: 5000, eligibleSourceCount: 10000, revision: 'fixture-head' }, coverage: coverage('COMPLETE'), maintenance: { status: maintenance } };
      else if (url.pathname.endsWith('/search')) { const query = request.postDataJSON(); body = query.query === 'missing' ? result([], 'PARTIAL') : result([sourceHit(sourceId)]); }
      else if (url.pathname.endsWith('/rebuild/cancel')) { maintenance = 'CANCELLED'; body = { status: maintenance, indexedSources: 5000, eligibleSources: 10000, lastIndexedSequence: 2500 }; }
      else if (url.pathname.endsWith('/rebuild')) { maintenance = 'INDEXING'; body = { status: maintenance, indexedSources: 5000, eligibleSources: 10000, lastIndexedSequence: 2500 }; }
      else if (url.pathname.includes('/messages/')) { const id = decodeURIComponent(url.pathname.split('/messages/')[1]); const hit = sourceHit(id, Number(url.searchParams.get('startOffset') ?? 0), Number(url.searchParams.get('maxCharacters') ?? 4000)); body = hit ? result([hit]) : result([], 'UNAVAILABLE'); }
    } else if (url.pathname.endsWith(`/turns/${turnId(5000)}/trace`) || url.pathname.endsWith(`/turns/${turnId(4999)}/trace`)) body = turns.at(-1).status === 'running' && url.pathname.includes(turnId(5000)) ? { events: [], next: 0, hasMore: false } : { events: [{ ...manifestEvent, data: { payloadRef: 'retained-fixture-manifest', bytes: 28000 } }], next: 1, hasMore: false };
    else if (url.pathname.endsWith('/events/1/payload')) body = manifestEvent.data;
    else if (url.pathname.startsWith('/api/')) throw new Error(`Unexpected API request; no inference or unbounded read is permitted: ${request.method()} ${url.pathname}`);
    if (body !== undefined) return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
    const file = url.pathname.slice(1); if (!['chat.html', 'chat.css', 'chat.mjs', 'styles.css'].includes(file)) return route.fulfill({ status: 404, body: '' });
    return route.fulfill({ contentType: file.endsWith('.html') ? 'text/html' : file.endsWith('.css') ? 'text/css' : 'text/javascript', body: await readFile(new URL(file, assets)) });
  });
  const capture = async name => { if (!process.env.AGENTICA_BROWSER_ARTIFACTS) return; await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true }); const path = join(process.env.AGENTICA_BROWSER_ARTIFACTS, name); await page.screenshot({ path }); screenshots.push(path); };
  await page.goto(origin + '/chat.html'); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 20 && !document.querySelector('#message')?.disabled);
  assert.equal(historyRequests.length, 0); assert.equal(await page.locator('#thread-recall-enabled').isChecked(), false);
  await page.locator('#transcript').evaluate(element => { element.scrollTop = 100; }); const anchor = page.locator(`.turn[data-turn-id="${turnId(4981)}"]`); const beforeY = (await anchor.boundingBox()).y;
  await page.locator('#load-earlier-turns').click(); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 40);
  assert.ok(Math.abs((await anchor.boundingBox()).y - beforeY) <= 2, 'Earlier-page insertion preserves the scrolled anchor.');
  await page.locator('#refresh-chats').click(); await page.waitForFunction(() => !document.querySelector('#message')?.disabled); assert.equal(await page.locator('.turn').count(), 40);
  await page.locator('#thread-recall-section').evaluate(element => { element.open = true; }); assert.equal(historyRequests.length, 0);
  await page.locator('#thread-recall-enabled').check(); await page.waitForFunction(() => document.querySelector('#thread-recall-readiness')?.textContent.includes('10000/10000'));
  await page.selectOption('#thread-recall-mode', 'manual'); await page.locator('#thread-history-query').fill('alpha'); await page.locator('#search-thread-history').click();
  await page.locator('#thread-history-results .source-reference').first().click(); await page.locator('#history-source-dialog[open]').waitFor();
  assert.equal(await page.locator('#history-source-text').inputValue(), sourceText);
  await page.locator('#history-source-text').evaluate(element => { element.focus(); element.setSelectionRange(7, 9); }); await page.locator('#pin-history-source').click();
  assert.match(await page.locator('#history-source-range').textContent(), /\[7, 9\)/); await capture('experiment-thread-recall-source.png'); await page.locator('#close-history-source').click();
  await page.locator('#save-settings').click(); await page.waitForFunction(() => document.querySelector('#save-status')?.textContent === 'Saved · revision 2');
  assert.deepEqual(saves.at(-1).settings.threadRecallSelections, [{ sourceId, startOffset: 7, endOffset: 9 }]); assert.equal(await page.locator('.turn').count(), 40, 'A header-only settings response cannot discard loaded pages.');
  await page.reload(); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 20 && !document.querySelector('#message')?.disabled);
  await page.locator('#thread-recall-section').evaluate(element => { element.open = true; }); assert.match(await page.locator('#thread-recall-pins').textContent(), /\[7, 9\)/);
  await page.locator('#message').fill('Draft survives the bounded source jump.'); await page.locator('#thread-recall-pins .source-reference').click(); await page.locator('#history-source-dialog[open]').waitFor(); assert.equal(await page.locator('#history-source-text').inputValue(), '🧭');
  await page.locator('#jump-history-source').click(); await page.waitForFunction(id => Boolean(document.querySelector(`.turn[data-turn-id="${id}"]`)), turnId(7));
  assert.ok(await page.locator('.turn').count() <= 20); assert.equal(await page.locator('#message').inputValue(), 'Draft survives the bounded source jump.');
  await page.locator('#return-latest-turns').click(); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 20);
  const latest = page.locator(`.turn[data-turn-id="${turnId(5000)}"]`); await latest.locator('.turn-trace').evaluate(element => { element.open = true; });
  const manifest = latest.locator('.trace-inspector').filter({ has: page.locator('summary', { hasText: 'Thread recall evidence manifest' }) }).first(); await manifest.locator('summary').click(); await manifest.getByRole('button', { name: 'Load retained recall manifest' }).click(); await manifest.locator('pre').filter({ hasText: 'not measured' }).waitFor(); assert.equal(await manifest.locator('.source-reference').count(), 1);
  await page.locator('#thread-history-query').fill('missing'); await page.locator('#search-thread-history').click(); await page.waitForFunction(() => document.querySelector('#thread-history-search-status')?.textContent.startsWith('PARTIAL')); assert.doesNotMatch(await page.locator('#thread-history-search-status').textContent(), /NO_MATCH/);
  await page.locator('#rebuild-recall-index').click(); await page.locator('#cancel-recall-rebuild').waitFor(); await page.locator('#cancel-recall-rebuild').click(); await page.waitForFunction(() => document.querySelector('#thread-recall-readiness')?.textContent.includes('CANCELLED'));
  assert.equal(await page.locator('#thread-recall-agent-option').isDisabled(), true); await page.selectOption('#mode', 'harness'); assert.equal(await page.locator('#thread-recall-agent-option').isDisabled(), false); await page.selectOption('#thread-recall-mode', 'agent'); assert.equal(await page.locator('#capability-list input:checked').count(), 0, 'Agent mode does not silently bind tools.');
  await page.locator('#thread-recall-enabled').uncheck(); const beforeNewRequests = historyRequests.length; await page.locator('#new-chat').click(); await page.waitForFunction(id => new URL(location.href).searchParams.get('chat') === id, freshId);
  assert.equal(await page.locator('#thread-recall-enabled').isChecked(), false); assert.equal(await page.locator('#instructions').inputValue(), ''); assert.equal(historyRequests.length, beforeNewRequests, 'Fresh Off state performs no index query.');
  await page.setViewportSize({ width: 320, height: 844 }); if (await page.locator('#settings-panel').isVisible()) await page.locator('#close-settings').click(); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); assert.ok((await page.locator('#composer').boundingBox()).y >= 0);
  // A live tail keeps its identity/text while older pages and a source window
  // are opened. The synthetic EventSource never contacts a service.
  await page.addInitScript(() => { globalThis.__recallFixtureStreams = []; globalThis.EventSource = class { constructor(url) { this.url = url; this.closed = false; globalThis.__recallFixtureStreams.push(this); queueMicrotask(() => this.onopen?.()); } close() { this.closed = true; } }; });
  Object.assign(turns.at(-1), { status: 'running', assistantText: 'Live fixture', traceCount: 0, finishedAt: null, settings: { ...settings } });
  await page.setViewportSize({ width: 1500, height: 1040 }); await page.goto(`${origin}/chat.html?chat=${fixtureId}`); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 20 && document.querySelector('#transcript')?.getAttribute('aria-busy') === 'true');
  await page.locator('#load-earlier-turns').click(); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 40);
  const emit = event => page.evaluate(value => { const stream = [...globalThis.__recallFixtureStreams].reverse().find(item => !item.closed); if (!stream) throw new Error('No open fixture stream.'); stream.onmessage({ data: JSON.stringify(value) }); }, event);
  turns.at(-1).assistantText = 'Live fixture delta'; turns.at(-1).traceCount = 1;
  await emit({ sequence: 1, type: 'assistant.delta', time: '2026-11-01T00:00:02Z', data: { text: ' delta' } });
  await page.locator('#refresh-chats').click(); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 40);
  const previous = page.locator(`.turn[data-turn-id="${turnId(4999)}"]`); await previous.locator('.turn-trace').evaluate(element => { element.open = true; });
  const previousManifest = previous.locator('.trace-inspector').filter({ has: page.locator('summary', { hasText: 'Thread recall evidence manifest' }) }).first(); await previousManifest.locator('summary').click(); await previousManifest.getByRole('button', { name: 'Load retained recall manifest' }).click(); await previousManifest.locator('.source-reference').click(); await page.locator('#history-source-dialog[open]').waitFor(); await page.locator('#jump-history-source').click();
  await page.waitForFunction(id => Boolean(document.querySelector(`.turn[data-turn-id="${id}"]`)), turnId(7));
  assert.equal(await page.locator(`.turn[data-turn-id="${turnId(5000)}"] .message-content`).last().textContent(), 'Live fixture delta');
  turns.at(-1).assistantText = 'Live fixture delta second'; turns.at(-1).traceCount = 2;
  await emit({ sequence: 2, type: 'assistant.delta', time: '2026-11-01T00:00:03Z', data: { text: ' second' } });
  Object.assign(turns.at(-1), { status: 'completed', traceCount: 3, finishedAt: '2026-11-01T00:00:04Z' });
  await emit({ sequence: 3, type: 'turn.completed', time: '2026-11-01T00:00:04Z', data: turns.at(-1) });
  await page.waitForFunction(() => document.querySelector('#transcript')?.getAttribute('aria-busy') === 'false');
  assert.equal(await page.locator(`.turn[data-turn-id="${turnId(5000)}"] .assistant-message .message-content`).textContent(), 'Live fixture delta second');
  assert.equal(await page.locator(`.turn[data-turn-id="${turnId(7)}"]`).count(), 1); assert.ok(await page.locator('.turn').count() <= 40);
  assert.ok(conversationReads.every(read => read.returned <= 20)); assert.deepEqual(pageErrors, []);
  const proof = { status: 'PASS', scope: 'fully intercepted 5000-turn browser fixture; no service, user chat, index or model calls', firstPageTurns: 20, maximumPageTurns: Math.max(...conversationReads.map(read => read.returned)), earlierPagesRetainedOnRefreshAndSave: true, scrollAnchorPreserved: true, activeSseSurvivesPagingRefreshAndSourceJump: true, terminalReconciliationPreservesEarlierWindow: true, initialOffIndexRequests: 0, freshOffIndexRequests: 0, localSearchAndCoverageVisible: true, exactUnicodePin: { sourceId, startOffset: 7, endOffset: 9, text: '🧭' }, pinSavedReloaded: true, originalSourceAndNeighborReferences: true, boundedJumpPreservesDraft: true, recallManifestInspectable: true, partialNegativeNotClaimedNoMatch: true, explicitRebuildCancellation: true, agentHarnessOnlyNoAutoToolBinding: true, narrowViewportOverflow: false, inferenceCalls: 0, pageErrors, screenshots };
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) await writeFile(join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'thread-recall-qualification.json'), JSON.stringify(proof, null, 2)); console.log(JSON.stringify(proof, null, 2));
} finally { await browser.close(); }
