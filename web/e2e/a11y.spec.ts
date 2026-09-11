import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { loginAsAdmin } from './helpers';

/** 08 §6: zero serious/critical axe violations on every route shipped so far. */
async function expectNoSeriousViolations(page: Parameters<typeof test>[1] extends never ? never : import('@playwright/test').Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })), null, 2)).toEqual([]);
}

test('login page is accessible', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByRole('heading', { name: 'Alert Hub' })).toBeVisible();
  await expectNoSeriousViolations(page);
});

test('queue is accessible', async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto('/queue?view=needsAttention');
  await expect(page.getByRole('status')).toBeVisible();
  await expectNoSeriousViolations(page);
});
