// Fully intercepted browser fixture. Provider token counts below are synthetic
// test data; no service, real chat, provider count or inference is contacted.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const origin = 'http://127.0.0.1:5986'; const assets = new URL('../Agentica.Lab.Web/wwwroot/', import.meta.url);
const id = 'context-check-fixture'; const time = '2026-11-03T00:00:00Z';
const settings = { mode: 'clean-room', model: 'gpt-6-sol', reasoningEffort: 'xhigh', instructions: '', reasoningSummary: false, ambientWebSearch: false, maxWebSearchCalls: 3, maxOutputTokens: 32000, contextWindowTokens: 131072, maxInputCharacters: 256000, maxHistoryMessages: 128, historyPolicy: 'recent', contextDepth: 'exhaustive', maxToolResultCharacters: 4000, maxToolEvidenceCharacters: 8000, maxSteps: 12, maxContinuations: 4, maxProviderCalls: 12, timeoutSeconds: 180, enabledTools: [], memoryEnabled: false, threadRecallEnabled: false, threadRecallMode: 'auto', threadRecallQuery: '', threadRecallSelections: [], threadRecallMaxCharacters: 8000, threadRecallTopK: 5, threadRecallNeighborRadius: 1, threadRecallRecentTurns: 2, threadRecallMaxCalls: 8 };
let conversation = { id, title: 'Synthetic context count fixture', createdAt: time, updatedAt: time, settingsRevision: 7, settings, turns: [1, 2].map(sequence => ({ id: `history-${sequence}`, conversationId: id, sequence, userText: `Synthetic old question ${sequence}.`, assistantText: `Synthetic old answer ${sequence}.`, status: 'completed', createdAt: time, finishedAt: time, settingsRevision: 7, settings, continuity: 'fixture only', traceCount: 0, droppedTraceCount: 0 })) };
const prefix = '  Preserve exact whitespace 🧭\n'; const draft = prefix + 'x'.repeat(62600 - prefix.length - 3) + '\n  '; assert.equal(draft.length, 62600);
const checkRequests = []; const turnRequests = []; const saves = []; const errors = []; const screenshots = []; let failCount = false; let releaseCheck; let releaseSend;
const countResult = () => ({ manifest: { conversationId: id, turnId: 'preview', selectedMessageIds: ['history-1:user', 'history-1:assistant', 'history-2:user', 'history-2:assistant', 'preview:user'], omittedMessageIds: [], selectedCharacters: draft.length + 100, estimatedInputUnits: 999999, inputAllowance: 62976, estimator: 'synthetic provider measurement', historyPolicy: 'recent', continuity: 'fixture only', tokenCount: { tokens: 62600, method: 'fixture-provider.input_tokens', requestHash: 'synthetic-exact-request-hash', providerCount: true, countRequestHash: 'synthetic-count-request-hash' } }, measurements: [{ tokens: 62600, providerCount: true, method: 'fixture-provider.input_tokens' }], settingsRevision: conversation.settingsRevision, inferenceDispatched: false });
const unavailable = { code: 'context.token_count_unavailable', message: 'Provider input token counting is unavailable. No generation request was dispatched.' };
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1040 }, reducedMotion: 'reduce' }); page.setDefaultTimeout(10000); page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url()); assert.equal(url.origin, origin); let body;
    if (url.pathname === '/api/chats/models') body = { configured: false, models: ['gpt-6-sol'], defaultModel: 'gpt-6-sol', reasoningEfforts: ['high', 'xhigh'] };
    else if (url.pathname === '/api/chats/capabilities') body = [];
    else if (url.pathname === '/api/providers/openai/settings') body = { configured: false };
    else if (url.pathname === '/api/chats') body = [conversation];
    else if (url.pathname === `/api/chats/${id}` && request.method() === 'GET') body = { ...conversation, historyPage: { totalTurns: 2, latestSequence: 2, nextBeforeSequence: null, completedTurns: 2 } };
    else if (url.pathname === `/api/chats/${id}/settings` && request.method() === 'PUT') { const update = request.postDataJSON(); saves.push(update); assert.equal(update.expectedRevision, conversation.settingsRevision); conversation = { ...conversation, settings: update.settings, settingsRevision: conversation.settingsRevision + 1 }; body = { ...conversation, turns: [] }; }
    else if (url.pathname === `/api/chats/${id}/context-check` && request.method() === 'POST') {
      checkRequests.push(request.postDataJSON()); assert.deepEqual(checkRequests.at(-1), { content: draft });
      if (failCount) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify(unavailable) });
      await new Promise(resolve => { releaseCheck = resolve; }); body = countResult(); if (conversation.settings.mode === 'harness') { body.manifest.tokenCount = null; body.measurements = []; }
    } else if (url.pathname === `/api/chats/${id}/turns` && request.method() === 'POST') {
      turnRequests.push(request.postDataJSON()); assert.equal(turnRequests.at(-1).content, draft); await new Promise(resolve => { releaseSend = resolve; });
      return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify(unavailable) });
    } else if (url.pathname.startsWith('/api/')) throw new Error(`Unexpected request: ${request.method()} ${url.pathname}`);
    if (body !== undefined) return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
    const name = url.pathname.slice(1); if (!['chat.html', 'chat.css', 'chat.mjs', 'styles.css'].includes(name)) return route.fulfill({ status: 404, body: '' });
    return route.fulfill({ contentType: name.endsWith('.html') ? 'text/html' : name.endsWith('.css') ? 'text/css' : 'text/javascript', body: await readFile(new URL(name, assets)) });
  });
  const capture = async name => { if (!process.env.AGENTICA_BROWSER_ARTIFACTS) return; await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true }); const path = join(process.env.AGENTICA_BROWSER_ARTIFACTS, name); await page.screenshot({ path }); screenshots.push(path); };
  await page.goto(origin + '/chat.html'); await page.waitForFunction(() => !document.querySelector('#message')?.disabled); assert.equal(checkRequests.length, 0);
  await page.locator('#message').fill(draft); assert.equal(checkRequests.length, 0, 'Typing does not count provider tokens.');
  await page.locator('#max-output').fill('64000'); await page.locator('#check-context').click(); await page.waitForFunction(() => document.querySelector('#chat-notice')?.textContent.startsWith('Save configuration before checking context.'));
  assert.equal(checkRequests.length, 0); assert.equal(saves.length, 0); assert.equal(await page.locator('#message').inputValue(), draft);
  await page.locator('#save-settings').click(); await page.waitForFunction(() => document.querySelector('#save-status')?.textContent === 'Saved · revision 8');
  await page.locator('#check-context').click(); await page.waitForFunction(() => document.querySelector('#check-context')?.textContent === 'Counting input…');
  assert.match(await page.locator('#composer-status').textContent(), /No generation request or turn is being created/); assert.equal(turnRequests.length, 0); releaseCheck();
  await page.locator('#context-check-result').waitFor(); const resultText = await page.locator('#context-check-result').textContent();
  assert.match(resultText, /62,600 \/ 62,976/); assert.match(resultText, /4 history messages retained/); assert.match(resultText, /0 quoted passages, 0 characters/); assert.match(resultText, /no generation or turn created/); assert.doesNotMatch(resultText, /999999/);
  assert.equal(await page.locator('#message').inputValue(), draft); assert.equal(await page.locator('.turn').count(), 2); assert.equal(saves.length, 1); assert.equal(turnRequests.length, 0); await capture('experiment-context-check.png');
  failCount = true; await page.locator('#check-context').click(); await page.waitForFunction(() => document.querySelector('#chat-notice')?.textContent.startsWith('context.token_count_unavailable:'));
  assert.equal(await page.locator('#context-check-result').isVisible(), false); assert.doesNotMatch(await page.locator('#chat-notice').textContent(), /Increase|adjust|Reload saved|Refresh before|Another tab/i); assert.equal(await page.locator('#message').inputValue(), draft);
  await page.selectOption('#mode', 'harness'); await page.locator('#save-settings').click(); await page.waitForFunction(() => document.querySelector('#save-status')?.textContent === 'Saved · revision 9'); failCount = false;
  await page.locator('#check-context').click(); await page.waitForFunction(() => document.querySelector('#check-context')?.textContent === 'Checking projection…');
  assert.match(await page.locator('#composer-status').textContent(), /Provider token counting waits for the actual planner request/); releaseCheck(); await page.locator('#context-check-result').waitFor();
  const harnessText = await page.locator('#context-check-result').textContent(); assert.match(harnessText, /Harness projection valid/); assert.match(harnessText, /Provider token count is deferred until an actual planner request is prepared/); assert.doesNotMatch(harnessText, /62,600|62,976|999999|Provider input:/); assert.equal(await page.locator('#message').inputValue(), draft); assert.equal(turnRequests.length, 0);
  await page.locator('#send-message').click(); await page.waitForFunction(() => document.querySelector('#send-message')?.textContent === 'Checking context…');
  assert.match(await page.locator('#composer-status').textContent(), /Checking context; waiting for turn admission/); assert.equal(await page.locator('.turn').count(), 2); releaseSend();
  await page.waitForFunction(() => !document.querySelector('#send-message')?.disabled && document.querySelector('#chat-notice')?.textContent.startsWith('context.token_count_unavailable:'));
  assert.equal(await page.locator('#message').inputValue(), draft); assert.equal(await page.locator('.turn').count(), 2); assert.equal(turnRequests.length, 1); assert.equal(conversation.turns.length, 2);
  for (const width of [320, 390]) { await page.setViewportSize({ width, height: 844 }); if (await page.locator('#settings-panel').isVisible()) await page.locator('#close-settings').click(); assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true); assert.ok((await page.locator('#composer').boundingBox()).y >= 0); }
  assert.deepEqual(errors, []);
  const proof = { status: 'PASS', scope: 'fully intercepted synthetic browser fixture; no service, user data, provider counting or inference', draftCharacters: draft.length, preservedExactPayload: checkRequests.every(value => value.content === draft), savedSettingsRequiredBeforeCount: true, typingAndLoadCountRequests: 0, providerTokens: 62600, inputAllowance: 62976, retainedHistoryMessages: 4, contextCheckCreatesNoTurn: true, draftPreserved: true, unavailableTypedNoEstimateOrSettingsAdvice: true, harnessProjectionDefersProviderCount: true, sendChecksBeforeTurnAdmission: true, narrowViewportOverflow: false, pageErrors: errors, screenshots };
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) await writeFile(join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'context-check-qualification.json'), JSON.stringify(proof, null, 2)); console.log(JSON.stringify(proof, null, 2));
} finally { await browser.close(); }
