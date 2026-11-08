// Fully intercepted browser fixture: local source assets, synthetic REST/SSE,
// no running Lab service, saved operator conversation, provider or inference.
import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const origin = 'http://127.0.0.1:5987';
const assets = new URL('../Agentica.Lab.Web/wwwroot/', import.meta.url);
const id = 'markdown-fixture';
const time = '2026-11-03T00:00:00Z';
const settings = {
  mode: 'clean-room', model: 'gpt-6-luna', reasoningEffort: 'high', instructions: '', reasoningSummary: true,
  ambientWebSearch: false, maxWebSearchCalls: 3, maxOutputTokens: 32000, contextWindowTokens: 131072,
  maxInputCharacters: 256000, maxHistoryMessages: 128, historyPolicy: 'recent', contextDepth: 'exhaustive',
  maxToolResultCharacters: 4000, maxToolEvidenceCharacters: 8000, maxSteps: 12, maxContinuations: 4,
  maxProviderCalls: 12, timeoutSeconds: 180, enabledTools: [], memoryEnabled: false, threadRecallEnabled: false,
  threadRecallMode: 'auto', threadRecallQuery: '', threadRecallSelections: [], threadRecallMaxCharacters: 8000,
  threadRecallTopK: 5, threadRecallNeighborRadius: 1, threadRecallRecentTurns: 2, threadRecallMaxCalls: 8
};
const markdown = role => `  # ${role} heading

**${role} bold** and *emphasis* with \`inline code\`.

- first bullet
- second bullet

1. first ordered
2. second ordered

- [x] completed task
- [ ] pending task

> Quoted evidence stays attributed.

\`\`\`js
const example = "<script>not executable</script>";
\`\`\`

| Column | Value |
| --- | --- |
| ${role} | table value |

[Safe documentation](https://example.test/reference)
[Unsafe script](javascript:globalThis.__markdownInjected=1)
[Unsafe data](data:text/html,malicious)
![External image](https://outside.invalid/markdown-pixel.png)

<script>globalThis.__markdownInjected = 2</script>
<img src="https://outside.invalid/raw-pixel.png" onerror="globalThis.__markdownInjected=3">
<a href="javascript:globalThis.__markdownInjected=4" onclick="globalThis.__markdownInjected=5">Raw HTML link</a>
  `;
const makeTurn = (sequence, userText, assistantText, status = 'completed') => ({
  id: `markdown-turn-${sequence}`, conversationId: id, sequence, userText, assistantText, status,
  createdAt: time, finishedAt: status === 'completed' ? time : null,
  settingsRevision: 1, settings, continuity: 'synthetic transcript-only fixture', traceCount: 0, droppedTraceCount: 0
});
const completed = makeTurn(1, markdown('User'), markdown('Assistant'));
const running = makeTurn(2, 'Show a streamed Markdown example.', '## Streamed answer\n', 'running');
running.settings = { ...settings, ambientWebSearch: true };
const conversation = { id, title: 'Synthetic Markdown experiment', createdAt: time, updatedAt: time, settingsRevision: 1, settings, turns: [completed, running] };
const event = (sequence, type, data) => ({ sequence, type, data, time, callId: 'synthetic-call', itemId: 'synthetic-item' });
const records = [
  event(1, 'context.manifest', { selectedMessageIds: ['synthetic:user'], estimator: 'fixture only' }),
  event(2, 'provider.record', { providerType: 'response.output_text.delta', payloadRef: 'synthetic-retained-record', bytes: 512 }),
  event(3, 'provider.summary.delta', { text: 'Synthetic public summary.' })
];
running.traceCount = records.length;
const posted = []; const traceReads = []; const payloadReads = []; const externalRequests = []; const unexpectedRequests = [];
const pageErrors = []; const screenshots = [];
const browser = await chromium.launch({ headless: true, channel: process.env.AGENTICA_BROWSER_CHANNEL || undefined });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1040 }, reducedMotion: 'reduce' });
  page.setDefaultTimeout(10000);
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.addInitScript(() => {
    globalThis.__markdownStreams = [];
    globalThis.EventSource = class {
      constructor(url) { this.url = url; this.closed = false; globalThis.__markdownStreams.push(this); queueMicrotask(() => this.onopen?.()); }
      close() { this.closed = true; }
    };
  });
  await page.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url()); let body;
    if (url.origin !== origin) { externalRequests.push(request.url()); return route.abort('blockedbyclient'); }
    if (url.pathname === '/api/chats/models') body = { configured: false, models: ['gpt-6-luna'], defaultModel: 'gpt-6-luna', reasoningEfforts: ['high'] };
    else if (url.pathname === '/api/chats/capabilities') body = [];
    else if (url.pathname === '/api/providers/openai/settings') body = { configured: false };
    else if (url.pathname === '/api/chats') body = [conversation];
    else if (url.pathname === `/api/chats/${id}` && request.method() === 'GET') body = { ...conversation, historyPage: { totalTurns: conversation.turns.length, latestSequence: conversation.turns.length, nextBeforeSequence: null, completedTurns: conversation.turns.filter(turn => turn.status === 'completed').length } };
    else if (url.pathname === `/api/chats/${id}/turns` && request.method() === 'POST') {
      posted.push(request.postDataJSON());
      const turn = makeTurn(conversation.turns.length + 1, posted.at(-1).content, markdown('Posted assistant'));
      conversation.turns.push(turn); body = turn;
    } else if (url.pathname === `/api/chats/${id}/turns/${running.id}/trace`) {
      traceReads.push(request.url()); const after = Number(url.searchParams.get('after') ?? 0);
      body = { events: records.filter(value => value.sequence > after), next: records.at(-1)?.sequence ?? 0, hasMore: false };
    } else if (url.pathname === `/api/chats/${id}/turns/${running.id}/citations`) {
      const after = Number(url.searchParams.get('after') ?? 0);
      body = { events: records.filter(value => value.type === 'provider.citations' && value.sequence > after), next: records.at(-1)?.sequence ?? 0, hasMore: false };
    } else if (url.pathname === `/api/chats/${id}/turns/${running.id}/events/2/payload`) {
      payloadReads.push(request.url()); body = { type: 'response.output_text.delta', output_index: 0, item_id: 'synthetic-item', delta: 'Explicitly loaded source payload.' };
    } else if (url.pathname.startsWith('/api/')) {
      unexpectedRequests.push(`${request.method()} ${url.pathname}`); return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
    }
    if (body !== undefined) return route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
    const name = url.pathname.slice(1);
    if (!/^(?:[a-zA-Z0-9._-]+\.(?:html|css|mjs|js)|vendor\/[a-zA-Z0-9._/-]+\.(?:mjs|js))$/.test(name) || name.includes('..'))
      return route.fulfill({ status: 404, body: '' });
    try { return route.fulfill({ contentType: name.endsWith('.html') ? 'text/html' : name.endsWith('.css') ? 'text/css' : 'text/javascript', body: await readFile(new URL(name, assets)) }); }
    catch { unexpectedRequests.push(`Missing local asset ${name}`); return route.fulfill({ status: 404, body: '' }); }
  });
  const turnNode = turn => page.locator(`.turn[data-turn-id="${turn.id}"]`);
  const assertClosed = async () => assert.equal(await page.locator('.turn-trace details[open], details.turn-trace[open]').count(), 0, 'Trace disclosures stay closed until explicitly opened.');
  const inspector = title => turnNode(running).locator('details.trace-inspector').filter({ has: page.locator('summary', { hasText: new RegExp(`^${title}`) }) });
  const capture = async name => {
    if (!process.env.AGENTICA_BROWSER_ARTIFACTS) return;
    await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true });
    const path = join(process.env.AGENTICA_BROWSER_ARTIFACTS, name); await page.screenshot({ path }); screenshots.push(path);
  };
  const emit = async value => {
    records.push(value); running.traceCount = value.sequence;
    if (value.type === 'assistant.delta') running.assistantText += value.data.text;
    await page.evaluate(({ turnId, value }) => {
      const stream = [...globalThis.__markdownStreams].reverse().find(item => !item.closed && item.url.includes(`/turns/${turnId}/events`));
      if (!stream) throw new Error('Synthetic SSE stream is unavailable.');
      stream.onmessage?.({ data: JSON.stringify(value), lastEventId: String(value.sequence) });
    }, { turnId: running.id, value });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  };
  const emitBatch = async values => {
    for (const value of values) {
      records.push(value); running.traceCount = value.sequence;
      if (value.type === 'assistant.delta') running.assistantText += value.data.text;
    }
    return page.evaluate(async ({ turnId, values }) => {
      const stream = [...globalThis.__markdownStreams].reverse().find(item => !item.closed && item.url.includes(`/turns/${turnId}/events`));
      const content = document.querySelector(`.turn[data-turn-id="${turnId}"] .assistant-message .message-content`);
      if (!stream || !content) throw new Error('Synthetic streaming target is unavailable.');
      const before = content.textContent; let renders = 0;
      const observer = new MutationObserver(records => { renders += records.filter(record => record.type === 'childList' && record.target === content).length; });
      observer.observe(content, { childList: true });
      for (const value of values) stream.onmessage?.({ data: JSON.stringify(value), lastEventId: String(value.sequence) });
      const unchangedBeforePaint = content.textContent === before;
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      observer.disconnect();
      return { unchangedBeforePaint, renders, afterPaint: content.textContent };
    }, { turnId: running.id, values });
  };
  const reconnect = async () => {
    await page.evaluate(() => {
      const stream = [...globalThis.__markdownStreams].reverse().find(item => !item.closed);
      stream.onerror?.(); stream.onopen?.();
    });
  };
  const assertMarkdown = async (wrapper, role) => {
    assert.equal(await wrapper.locator('h1').textContent(), `${role} heading`);
    assert.equal(await wrapper.locator('strong').textContent(), `${role} bold`);
    assert.equal(await wrapper.locator('em').textContent(), 'emphasis');
    assert.equal(await wrapper.locator('ul').count() >= 2, true);
    assert.equal(await wrapper.locator('ol > li').count(), 2);
    assert.equal(await wrapper.locator('input[type="checkbox"][disabled]').count(), 2);
    assert.equal(await wrapper.locator('input[type="checkbox"]:checked').count(), 1);
    assert.match(await wrapper.locator('pre code').textContent(), /const example = "<script>not executable<\/script>";/);
    assert.equal(await wrapper.locator('table th').count(), 2); assert.equal(await wrapper.locator('table td').count(), 2);
    assert.match(await wrapper.locator('blockquote').textContent(), /Quoted evidence/);
    const safe = wrapper.locator('a[href="https://example.test/reference"]'); assert.equal(await safe.count(), 1);
    assert.equal(await safe.getAttribute('target'), '_blank'); assert.match(await safe.getAttribute('rel'), /noopener/);
    assert.equal(await wrapper.locator('script,img,iframe,object,embed,form,[onclick],[onerror]').count(), 0);
    const hrefs = await wrapper.locator('a').evaluateAll(nodes => nodes.map(node => node.getAttribute('href')).filter(Boolean));
    assert.equal(hrefs.every(href => /^https?:\/\//i.test(href)), true, 'Markdown links only activate allowed web URLs.');
  };

  await page.goto(origin + '/chat.html');
  await page.waitForFunction(() => document.querySelectorAll('.turn').length === 2 && globalThis.__markdownStreams.length === 1);
  await assertMarkdown(turnNode(completed).locator('.user-message .message-content'), 'User');
  await assertMarkdown(turnNode(completed).locator('.assistant-message .message-content'), 'Assistant');
  await assertClosed(); assert.equal(traceReads.length, 0); assert.equal(payloadReads.length, 0);
  assert.equal(await page.evaluate(() => globalThis.__markdownInjected), undefined);

  await emit(event(4, 'assistant.delta', { text: '\n```js\nconst partial = 1;' }));
  assert.equal(await turnNode(running).locator('.assistant-message h2').textContent(), 'Streamed answer');
  assert.match(await turnNode(running).locator('.assistant-message pre code').textContent(), /^const partial = 1;\n?$/);
  await emit(event(5, 'provider.item', { type: 'reasoning', status: 'in_progress' }));
  await assertClosed(); await reconnect(); await assertClosed(); assert.equal(traceReads.length, 0);
  await page.reload();
  await page.waitForFunction(() => globalThis.__markdownStreams.length === 1 && document.querySelector('.turn[data-turn-id="markdown-turn-2"]'));
  assert.match(await turnNode(running).locator('.assistant-message pre code').textContent(), /^const partial = 1;\n?$/);
  await assertClosed(); assert.equal(traceReads.length, 0); assert.equal(payloadReads.length, 0);

  await turnNode(running).locator('details.turn-trace > summary').click();
  await page.waitForFunction(() => document.querySelector('.turn[data-turn-id="markdown-turn-2"] .trace-page-controls span')?.textContent.includes('full retained sequence'));
  assert.equal(traceReads.length, 1); assert.equal(await turnNode(running).locator('details.trace-inspector[open]').count(), 0);
  await turnNode(running).getByRole('button', { name: 'Resume live', exact: true }).click();
  const timeline = inspector('Typed timeline'); const sources = inspector('Provider source records'); const request = inspector('Effective provider request');
  for (const section of [timeline, sources, request]) await section.locator(':scope > summary').click();
  assert.equal(await turnNode(running).locator('details.typed-event[open]').count(), 0); assert.equal(payloadReads.length, 0);
  await sources.locator('details[data-sequence="2"] > summary').click(); assert.equal(payloadReads.length, 0, 'Opening a record alone does not read the retained payload.');
  await sources.getByRole('button', { name: 'Load provider source record', exact: true }).click();
  await sources.getByText('Explicitly loaded source payload.', { exact: false }).waitFor(); assert.equal(payloadReads.length, 1);
  await timeline.locator('details[data-sequence="1"] > summary').click();
  const batch = await emitBatch([
    event(6, 'assistant.delta', { text: '\n```\n\n| Stream | State |\n| --- | --- |\n| Markdown | complete |\n' }),
    event(7, 'assistant.delta', { text: '\n**Live bold** and [safe Markdown link](https://example.test/markdown-link).\n' })
  ]);
  assert.equal(batch.unchangedBeforePaint, true); assert.equal(batch.renders, 1, 'Two synchronous deltas render once on the next animation frame.');
  assert.match(batch.afterPaint, /Live bold/);
  await emit(event(8, 'provider.record', { providerType: 'response.output_item.done', payloadRef: 'synthetic-new-record', bytes: 256 }));
  assert.equal(await turnNode(running).locator('details.turn-trace').getAttribute('open'), '');
  for (const section of [timeline, sources, request]) assert.equal(await section.getAttribute('open'), '');
  assert.equal(await sources.locator('details[data-sequence="2"]').getAttribute('open'), '');
  assert.equal(await timeline.locator('details[data-sequence="1"]').getAttribute('open'), '');
  assert.equal(await sources.locator('details[data-sequence="8"]').getAttribute('open'), null);
  assert.equal(await sources.locator('details.typed-event[open]').count(), 1);
  assert.equal(await timeline.locator('details.typed-event[open]').count(), 1);
  assert.equal(payloadReads.length, 1, 'Live rerender reuses the explicitly loaded retained payload.');
  assert.equal(await turnNode(running).locator('.assistant-message table td').count(), 2);
  assert.equal(await turnNode(running).locator('.assistant-message strong').textContent(), 'Live bold');
  await reconnect();
  for (const section of [timeline, sources, request]) assert.equal(await section.getAttribute('open'), '');
  assert.equal(await sources.locator('details[data-sequence="2"]').getAttribute('open'), '');
  assert.equal(await timeline.locator('details[data-sequence="1"]').getAttribute('open'), '');
  await capture('experiment-markdown-live.png');

  await sources.locator('details[data-sequence="2"] > summary').click();
  await timeline.locator('details[data-sequence="1"] > summary').click();
  for (const section of [timeline, sources, request]) await section.locator(':scope > summary').click();
  await turnNode(running).locator('details.turn-trace > summary').click();
  await reconnect(); await assertClosed();
  const citationSource = running.assistantText;
  const boldOffset = citationSource.indexOf('Live bold'); const linkOffset = citationSource.indexOf('safe Markdown link');
  const codeOffset = citationSource.indexOf('const partial');
  await emit(event(9, 'provider.citations', { target: 'assistant', citations: [
    { url: 'https://example.test/bold-source', title: 'Bold source', startIndex: boldOffset, endIndex: boldOffset + 4 },
    { url: 'https://example.test/link-source', title: 'Link source', startIndex: linkOffset, endIndex: linkOffset + 4 },
    { url: 'https://example.test/code-source', title: 'Code source', startIndex: codeOffset, endIndex: codeOffset + 5 }
  ], sources: [] }));
  const answer = turnNode(running).locator('.assistant-message .message-content');
  assert.equal(await answer.locator('.answer-citation').count(), 2);
  assert.equal(await answer.locator('pre .answer-citation, code .answer-citation, strong .answer-citation, a a.answer-citation').count(), 0);
  assert.equal(await answer.locator('a[href="https://example.test/markdown-link"]').textContent(), 'safe Markdown link');
  assert.equal(await answer.locator('strong').textContent(), 'Live bold');
  assert.equal(await answer.evaluate(element => {
    const block = element.querySelector('strong').closest('.markdown-block');
    return [...element.querySelectorAll('.answer-citation')].every(marker => marker.parentElement.classList.contains('markdown-block-citations') && marker.parentElement.parentElement === block && !marker.parentElement.closest('a,pre,code,strong'));
  }), true, 'Citation markers belong to the containing block, outside formatted/link/code content.');
  assert.equal(await answer.locator('.answer-citation[href="https://example.test/code-source"]').count(), 0);
  const savedSources = turnNode(running).locator('.answer-sources .assistant-sources');
  assert.equal(await savedSources.locator('a.source-link').count(), 3);
  assert.equal(await savedSources.locator('a[href="https://example.test/code-source"]').count(), 1);
  assert.match(await savedSources.textContent(), /inline placement is unavailable/);
  assert.equal(running.assistantText, citationSource, 'Citation presentation preserves the canonical raw Markdown source.');
  await assertClosed();
  running.status = 'completed'; running.finishedAt = time;
  await emit(event(10, 'turn.completed', { ...running, traceCount: 10 }));
  await page.waitForFunction(() => !document.querySelector('#message')?.disabled);
  await assertClosed();

  const exactDraft = markdown('Posted user');
  await page.locator('#message').fill(exactDraft); await page.locator('#send-message').click();
  await page.waitForFunction(() => document.querySelectorAll('.turn').length === 3);
  assert.equal(posted.length, 1); assert.equal(posted[0].content, exactDraft, 'Rendering never rewrites the submitted Markdown source.');
  assert.equal(typeof posted[0].requestId, 'string');
  await assertMarkdown(turnNode(conversation.turns[2]).locator('.user-message .message-content'), 'Posted user');
  await assertMarkdown(turnNode(conversation.turns[2]).locator('.assistant-message .message-content'), 'Posted assistant');
  await assertClosed();
  await page.reload(); await page.waitForFunction(() => document.querySelectorAll('.turn').length === 3);
  await turnNode(running).locator('.answer-sources a[href="https://example.test/code-source"]').waitFor();
  assert.equal(await turnNode(running).locator('.assistant-message .message-content .answer-citation').count(), 2);
  assert.equal(running.assistantText, citationSource);
  await assertClosed(); assert.equal(traceReads.length, 2, 'One retained read and one explicitly requested Resume live read. Reload adds neither.'); assert.equal(payloadReads.length, 1);
  assert.equal(traceReads.filter(url => new URL(url).searchParams.get('tail') === 'true').length, 1);
  assert.equal(await page.evaluate(() => globalThis.__markdownInjected), undefined);
  const fallback = await page.evaluate(async () => {
    const { renderMarkdown } = await import('/chat-markdown.mjs');
    const source = '| Left | Right |\n| --- | --- |\n' + '| value | value |\n'.repeat(12000);
    const probe = document.createElement('div'); probe.className = 'message-content markdown-body';
    document.querySelector('#transcript').append(probe);
    try {
      const large = renderMarkdown(probe, source);
      const measured = {
        characters: source.length, fallback: large.fallback, exactSource: probe.textContent === source,
        singleTextNode: probe.childNodes.length === 1 && probe.firstChild?.nodeType === Node.TEXT_NODE,
        preservesLineBreaks: probe.textContent.split('\n').length === source.split('\n').length,
        whiteSpace: getComputedStyle(probe).whiteSpace, markedFallback: probe.classList.contains('markdown-fallback')
      };
      const normal = renderMarkdown(probe, '# Recovered\n\n**Readable** again.');
      return { ...measured, recovered: normal.fallback === false && !probe.classList.contains('markdown-fallback') && probe.querySelector('h1')?.textContent === 'Recovered' && probe.querySelector('strong')?.textContent === 'Readable' };
    } finally { probe.remove(); }
  });
  assert.ok(fallback.characters > 200000); assert.equal(fallback.fallback, true); assert.equal(fallback.exactSource, true);
  assert.equal(fallback.singleTextNode, true); assert.equal(fallback.preservesLineBreaks, true);
  assert.equal(fallback.whiteSpace, 'pre-wrap'); assert.equal(fallback.markedFallback, true); assert.equal(fallback.recovered, true);
  assert.deepEqual(externalRequests, []); assert.deepEqual(unexpectedRequests, []); assert.deepEqual(pageErrors, []);
  await capture('experiment-markdown.png');
  for (const width of [390, 320]) {
    await page.setViewportSize({ width, height: 844 });
    if (await page.locator('#settings-panel').isVisible()) await page.locator('#close-settings').click();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
  }
  const proof = {
    status: 'PASS', scope: 'fully intercepted synthetic browser; no Lab service, user data, provider or inference',
    userAndAssistantMarkdown: true, taskListsAndCodeAndTables: true, unsafeHtmlImagesAndLinksInert: true,
    exactPostedSource: true, incompleteFenceRenderedDuringSse: true, liveMarkdownRendered: true,
    synchronousDeltasCoalescedToOneRender: true, largeMarkdownFallback: fallback,
    citationMarkersOutsideFormattedLinksAndCode: true, codeCitationUnplacedSourcePreservedAfterReload: true,
    traceClosedByDefaultOnLoadRunningLiveReconnectReload: true, explicitDisclosureChoicesPreserved: true,
    traceRequests: traceReads.length, explicitPayloadRequests: payloadReads.length, externalRequests, pageErrors, screenshots
  };
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) await writeFile(join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'markdown-qualification.json'), JSON.stringify(proof, null, 2));
  console.log(JSON.stringify(proof, null, 2));
} catch (error) {
  console.error(JSON.stringify({ scope: 'synthetic fixture failure diagnostics', pageErrors, unexpectedRequests, externalRequests, traceReads, payloadReads }));
  throw error;
} finally { await browser.close(); }
