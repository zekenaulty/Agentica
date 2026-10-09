// Uses an explicitly unconfigured provider. The factory rejects configuration before
// any provider network request; this is an exceptional-terminal UI test, not a live call.
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const origin = process.env.AGENTICA_LAB_TEST_URL ?? 'http://127.0.0.1:5078';
const providers = await (await fetch(`${origin}/api/providers`)).json();
const unconfigured = providers.find(provider => !provider.configured && ['openai', 'anthropic', 'grok', 'gemini'].includes(provider.provider));
if (!unconfigured) throw new Error('This smoke needs an explicitly unconfigured provider; no run was started.');
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(origin);
  await page.locator('#provider-list .provider-card').first().waitFor();
  const outcome = await page.evaluate(async provider => {
    const { AgenticaHost } = await import('/sdk/agentica-host.mjs');
    const identity = crypto.randomUUID(); let actionCalls = 0; let finish; let timeout;
    const terminated = new Promise((resolve, reject) => { finish = resolve; timeout = setTimeout(() => reject(new Error('No reliable terminal message.')), 8000); });
    const client = new AgenticaHost({ url: `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/api/host`,
      onAction: () => { actionCalls++; throw new Error('No action may execute after provider setup failure.'); },
      onMessage: message => { if (message.type === 'run.terminated') finish(message); }
    });
    try {
      const started = await client.start({ hostId: 'browser-terminal-smoke', sessionId: identity, sessionEpoch: identity,
        scopeId: 'terminal-fixture', perspectiveId: 'operator', objectiveId: identity, objective: 'Verify provider setup termination',
        observation: { observationId: identity, revision: 0, observedAt: new Date().toISOString(), data: { fixture: true } },
        capabilities: [{ id: 'fixture.inspect', name: 'Inspect fixture', description: 'Read-only fixture observation.', kind: 'query', effect: 'readOnly', inputSchema: { fields: [] } }], provider: { provider } });
      const message = await terminated;
      return { runId: started.runId, type: message.type, payload: message.payload, actionCalls };
    } finally { clearTimeout(timeout); client.close(); }
  }, unconfigured.provider);
  assert.equal(outcome.type, 'run.terminated'); assert.equal(outcome.payload.status, 'failed'); assert.equal(outcome.actionCalls, 0);
  const snapshot = await (await fetch(`${origin}/api/runs/${outcome.runId}`)).json();
  assert.equal(snapshot.outcome, null); assert.equal(snapshot.termination.status, 'failed'); assert.equal(snapshot.pendingActions.length, 0);
  await page.getByRole('button', { name: 'Refresh' }).click();
  await page.locator('.run-item').filter({ hasText: 'Verify provider setup termination' }).first().click();
  await page.waitForFunction(() => document.querySelector('#activity')?.textContent.startsWith('failed'));
  assert.equal(await page.locator('#cancel-run').isDisabled(), true);
  assert.match(await page.locator('#outcome-json').textContent(), /run.failed/);
  assert.equal(await page.locator('#thinking-dot').evaluate(element => element.classList.contains('busy')), false);
  assert.deepEqual(errors, []);
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) {
    await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true });
    await page.screenshot({ path: join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'lab-terminated.png') });
  }
  console.log(JSON.stringify({ status: 'PASS', provider: unconfigured.provider, configured: false, reliableTermination: true, outcome: null, snapshotTermination: 'failed', hostActionCalls: 0, activityStopped: true, pageErrors: errors }));
} finally { await browser.close(); }
