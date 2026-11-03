// Fully intercepted browser fixture: real chat assets, synthetic HTTP storage,
// no running service, real conversation, provider or model call.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const origin = 'http://127.0.0.1:5988';
const source = new URL('../Agentica.Lab.Web/wwwroot/', import.meta.url);
const artifactDirectory = process.env.AGENTICA_BROWSER_ARTIFACTS;
const time = () => new Date().toISOString();
let conversation = { id: 'send-recovery-fixture', title: 'Isolated send recovery fixture', createdAt: time(), updatedAt: time(), settingsRevision: 7,
  settings: { mode: 'clean-room', model: 'gpt-6-sol', reasoningEffort: 'xhigh', instructions: '', reasoningSummary: false, ambientWebSearch: false, maxWebSearchCalls: 3,
    maxOutputTokens: 32000, contextWindowTokens: 131072, maxInputCharacters: 256000, maxHistoryMessages: 128, historyPolicy: 'recent', contextDepth: 'exhaustive',
    maxToolResultCharacters: 4000, maxToolEvidenceCharacters: 8000, maxSteps: 12, maxContinuations: 4, maxProviderCalls: 12, timeoutSeconds: 180, enabledTools: [], memoryEnabled: false }, turns: [] };
const turnRequests = []; const saves = []; const order = []; const pageErrors = []; let rejectSettings = false;
const contextMessage = 'Fixture mandatory input exceeds its allowance. No model call was made.';
const draft = 'Synthetic draft retained after a context limit.\n  Preserve whitespace.';
const conflictDraft = 'A second synthetic draft must survive a settings revision conflict.';
const screenshots = [];
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1040 } }); page.setDefaultTimeout(10000);
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url()); assert.equal(url.origin, origin);
    let result;
    if (url.pathname === '/api/chats/models') result = { configured: false, models: ['gpt-6-sol'], defaultModel: 'gpt-6-sol', reasoningEfforts: ['none', 'low', 'medium', 'high', 'xhigh', 'max'] };
    else if (url.pathname === '/api/chats/capabilities') result = [];
    else if (url.pathname === '/api/providers/openai/settings') result = { configured: false };
    else if (url.pathname === '/api/chats' && request.method() === 'GET') result = [conversation];
    else if (url.pathname === `/api/chats/${conversation.id}` && request.method() === 'GET') result = conversation;
    else if (url.pathname === `/api/chats/${conversation.id}/settings` && request.method() === 'PUT') {
      const body = request.postDataJSON(); saves.push(body); order.push('settings');
      if (rejectSettings) return route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ code: 'settings.conflict', message: 'Settings changed elsewhere. Reload before saving.' }) });
      assert.equal(body.expectedRevision, conversation.settingsRevision);
      conversation = { ...conversation, settingsRevision: conversation.settingsRevision + 1, settings: body.settings }; result = conversation;
    } else if (url.pathname === `/api/chats/${conversation.id}/turns` && request.method() === 'POST') {
      const body = request.postDataJSON(); turnRequests.push(body); order.push('turn');
      if (turnRequests.length === 1) return route.fulfill({ status: 409, contentType: 'application/json', body: JSON.stringify({ code: 'context.limit', message: contextMessage }) });
      assert.equal(turnRequests.length, 2, 'Only the deliberate retry may reach the turn endpoint.');
      const turn = { id: 'accepted-retry-fixture', conversationId: conversation.id, sequence: 1, userText: body.content, assistantText: 'Offline fixture accepted the retried draft.', status: 'completed', createdAt: time(), finishedAt: time(), settingsRevision: conversation.settingsRevision, settings: conversation.settings, continuity: 'fixture transcript only', traceCount: 0, droppedTraceCount: 0 };
      conversation.turns = [turn]; result = turn;
    } else if (url.pathname.startsWith('/api/')) throw new Error(`Unexpected API operation: ${request.method()} ${url.pathname}`);
    if (result !== undefined) return route.fulfill({ contentType: 'application/json', body: JSON.stringify(result) });
    const file = url.pathname === '/' ? 'index.html' : url.pathname.slice(1);
    if (!['chat.html', 'chat.css', 'chat.mjs', 'styles.css'].includes(file)) return route.fulfill({ status: 404, body: '' });
    return route.fulfill({ contentType: file.endsWith('.html') ? 'text/html' : file.endsWith('.css') ? 'text/css' : 'text/javascript', body: await readFile(new URL(file, source)) });
  });
  const capture = async name => { if (!artifactDirectory) return; await mkdir(artifactDirectory, { recursive: true }); const path = join(artifactDirectory, name); await page.screenshot({ path }); screenshots.push(path); };
  await page.goto(origin + '/chat.html'); await page.waitForFunction(() => !document.querySelector('#message')?.disabled);
  await page.locator('#max-output').fill('64000'); await page.locator('#message').fill(draft); await page.locator('#send-message').click();
  await page.waitForFunction(() => document.querySelector('#chat-notice')?.textContent.startsWith('context.limit:'));
  assert.equal(await page.locator('#chat-notice').textContent(), `context.limit: ${contextMessage}`);
  assert.doesNotMatch(await page.locator('#chat-notice').textContent(), /Another tab|Refresh before|Reload saved configuration/);
  assert.equal(await page.locator('#message').inputValue(), draft); assert.equal(await page.locator('.turn').count(), 0);
  assert.equal(await page.locator('#save-status').textContent(), 'Saved · revision 8');
  assert.deepEqual(order, ['settings', 'turn']); assert.equal(conversation.turns.length, 0); await capture('experiment-context-limit-recovery.png');

  await page.locator('#max-output').fill('32000'); await page.locator('#send-message').click();
  await page.waitForFunction(() => document.querySelector('.turn-meta .badge')?.textContent === 'completed');
  assert.equal(turnRequests.length, 2); assert.equal(turnRequests[0].requestId, turnRequests[1].requestId);
  assert.equal(turnRequests[0].content, draft); assert.equal(turnRequests[1].content, draft);
  assert.equal(await page.locator('#message').inputValue(), ''); assert.equal(await page.locator('.turn').count(), 1);
  assert.equal(conversation.turns[0].settings.maxOutputTokens, 32000); assert.equal(conversation.turns[0].settingsRevision, 9);
  assert.deepEqual(order, ['settings', 'turn', 'settings', 'turn']); assert.equal(await page.locator('#chat-notice').isVisible(), false);

  rejectSettings = true;
  await page.locator('#message').fill(conflictDraft); await page.locator('#max-history').fill('12'); await page.locator('#send-message').click();
  await page.waitForFunction(() => document.querySelector('#chat-notice')?.textContent.startsWith('settings.conflict:'));
  assert.match(await page.locator('#chat-notice').textContent(), /Reload saved configuration/);
  assert.equal(await page.locator('#message').inputValue(), conflictDraft); assert.equal(await page.locator('#max-history').inputValue(), '12');
  assert.match(await page.locator('#save-status').textContent(), /Unsaved changes/); assert.equal(turnRequests.length, 2, 'Failed autosave cannot submit another turn.');
  assert.deepEqual(pageErrors, []); await capture('experiment-settings-conflict.png');
  const proof = { status: 'PASS', scope: 'fully intercepted isolated browser fixture; no service, user chat or provider calls', contextLimitNotMisclassified: true, draftPreservedAfterRejection: true, settingsSavedBeforeSend: true, sameRequestIdOnRetry: true, revisedSettingsUsedOnSuccess: true, singleAcceptedTurn: true, composerClearedOnlyAfterSuccess: true, genuineSettingsConflictGuidance: true, conflictDraftAndFieldsPreserved: true, noTurnSubmittedAfterSaveConflict: true, turnPostAttempts: turnRequests.length, pageErrors, screenshots };
  if (artifactDirectory) await writeFile(join(artifactDirectory, 'send-recovery-qualification.json'), JSON.stringify(proof, null, 2));
  console.log(JSON.stringify(proof, null, 2));
} finally { await browser.close(); }
