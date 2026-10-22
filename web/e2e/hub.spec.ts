import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { loginAsAdmin } from './helpers';

async function noSeriousViolations(page: import('@playwright/test').Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })), null, 2)).toEqual([]);
}

/** 08 §3.9: hub health cards, component heartbeats, queues, retention status with an on-demand run, failure queue for platform_admin. */
test('hub health screen shows components, queues, retention and runs retention on demand', async ({ page }) => {
  await loginAsAdmin(page);
  await page.getByTestId('nav-hub').click();
  await page.waitForURL(/\/hub$/);
  await expect(page.getByRole('heading', { name: 'Hub health' })).toBeVisible();
  const cards = page.getByTestId('hub-cards').locator('[data-card]');
  await expect(cards).toHaveCount(5);
  await expect(page.getByTestId('hub-cards').locator('[data-card="database"]')).toHaveAttribute('data-ok', 'true');
  // The three hosts of the local stack report heartbeats (api, ingest, workers roles).
  await expect(page.getByTestId('hub-components').locator('tr').first()).toBeVisible();
  await expect(page.getByTestId('hub-retention')).toContainText('Raw partitions');
  await expect(page.getByTestId('hub-failures')).toBeVisible();
  await noSeriousViolations(page);

  await page.getByTestId('retention-run').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Run retention now' }).click();
  await expect(page.getByTestId('retention-result')).toContainText('partitions dropped');
  await expect(page.getByTestId('retention-last-run')).not.toContainText('never ran yet');
});
