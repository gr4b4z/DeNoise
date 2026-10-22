import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { ensureTeam, fireAlert, loginAsAdmin } from './helpers';

async function noSeriousViolations(page: import('@playwright/test').Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })), null, 2)).toEqual([]);
}

/** 08 §3.7: a maintenance window created in wall-clock time shows its scope as a sentence and can be ended early. */
test('create a maintenance window, see it listed, end it now', async ({ page, request }) => {
  await ensureTeam(request);
  await loginAsAdmin(page);
  await page.goto('/suppressions');
  await expect(page.getByRole('heading', { name: 'Suppressions' })).toBeVisible();
  await page.getByTestId('suppression-new').click();
  // A unique reason and a service no other spec fires for: a window left behind by a failed run must not mute other tests.
  const reason = `e2e patching ${Date.now()}`;
  await page.getByLabel('Reason (required)').fill(reason);
  await page.getByLabel('Field').first().selectOption('service');
  await page.getByLabel('Value').first().fill('e2e-maintenance');
  await expect(page.getByTestId('scope-preview')).toContainText("service is 'e2e-maintenance'");
  // Ends tomorrow at the same wall-clock time: an already-started 24 h window in Warsaw time.
  const starts = await page.getByLabel('Starts (local time)').inputValue();
  const tomorrow = new Date(`${starts}:00Z`);
  tomorrow.setUTCDate(tomorrow.getUTCDate() + 1);
  await page.getByLabel('Ends (local time)').fill(tomorrow.toISOString().slice(0, 16));
  await page.getByLabel('Time zone (IANA)').fill('Europe/Warsaw');
  await noSeriousViolations(page);
  await page.getByTestId('suppression-create').click();

  const row = page.locator('tr[data-suppression-id]').filter({ hasText: reason }).first();
  await expect(row).toBeVisible();
  await expect(row.getByTestId('suppression-scope')).toHaveText("service is 'e2e-maintenance'");
  await expect(row.getByTestId('suppression-window')).toContainText('Europe/Warsaw');
  await expect(row).toHaveAttribute('data-active', 'true');
  await noSeriousViolations(page);

  await row.getByRole('button', { name: 'End now' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'End now' }).click();
  await expect(page.locator('tr[data-suppression-id]').filter({ hasText: reason })).toHaveCount(0);
  await page.getByLabel('Include ended').check();
  await expect(page.locator('tr[data-suppression-id]').filter({ hasText: reason }).first()).toContainText('ended early');
});

/** 08 §3.3 team overview cards link to the queue; 08 §3.1 saved filters; the history screen lists closed episodes only. */
test('team overview, saved filter and history', async ({ page, request }) => {
  const teamId = await ensureTeam(request);
  await fireAlert(request, 'e2e overview alert');
  await loginAsAdmin(page);
  await page.goto(`/teams/${teamId}`);
  await expect(page.getByTestId('team-title')).toHaveText('e2e-team');
  await expect(page.getByTestId('team-cards').locator('[data-card]')).toHaveCount(5);
  await noSeriousViolations(page);
  await page.getByTestId('team-cards').locator('[data-card="open"]').click();
  await page.waitForURL(/\/queue\?/);

  page.once('dialog', (d) => void d.accept(`e2e saved ${Date.now()}`));
  await page.getByTestId('save-filter').click();
  await expect(page.getByTestId('saved-filters').getByText(/e2e saved/)).toBeVisible();

  await page.goto('/history');
  await expect(page.getByRole('heading', { name: 'History' })).toBeVisible();
  await page.getByTestId('history-reason').selectOption('manual_close');
  await expect(page).toHaveURL(/closureReason=manual_close/);
  await noSeriousViolations(page);
});
