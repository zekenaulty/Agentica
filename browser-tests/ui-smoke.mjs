// Optional real-browser smoke. Requires a locally installed Playwright package and
// running isolated service; it selects only the deterministic demo provider.
import { mkdir } from 'node:fs/promises';
import { join } from 'node:path';
import assert from 'node:assert/strict';
const { chromium } = await import(process.env.AGENTICA_PLAYWRIGHT_MODULE ?? '@playwright/test');
const origin = process.env.AGENTICA_LAB_TEST_URL ?? 'http://127.0.0.1:5078';
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(origin);
  await page.locator('#provider-list .provider-card').first().waitFor();
  await page.selectOption('#sample-provider', 'demo');
  await page.getByRole('button', { name: 'Start example' }).click();
  await page.waitForFunction(() => document.querySelector('#outcome-json')?.textContent.includes('"succeeded"'), { timeout: 15000 });
  assert.equal(await page.locator('.inventory-item.accepted').count(), 1);
  assert.equal(await page.locator('#actions-json').textContent(), '[]');
  assert.equal(await page.locator('#cancel-run').isDisabled(), true);
  assert.equal(await page.locator('#notice').isVisible(), false);
  assert.ok(Number(await page.locator('#output-count').textContent()) > 0);
  await page.getByRole('tab', { name: 'Context', exact: true }).click();
  assert.match(await page.locator('#context-json').textContent(), /sample-1/);
  await page.getByRole('tab', { name: 'Activity', exact: true }).click();
  await page.evaluate(() => window.scrollTo(0, 0));
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) {
    await mkdir(process.env.AGENTICA_BROWSER_ARTIFACTS, { recursive: true });
    await page.screenshot({ path: join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'lab-desktop.png') });
  }
  await page.setViewportSize({ width: 390, height: 844 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, 'Mobile page must not overflow horizontally.');
  if (process.env.AGENTICA_BROWSER_ARTIFACTS) await page.screenshot({ path: join(process.env.AGENTICA_BROWSER_ARTIFACTS, 'lab-mobile.png') });
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ status: 'PASS', provider: 'demo', acceptedRecords: 1, pendingActions: 0, contextVisible: true, outputProgress: true, mobileOverflow: false, pageErrors: errors }));
} finally { await browser.close(); }
