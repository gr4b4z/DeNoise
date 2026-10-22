import { expect, test } from '@playwright/test';
import { fireAlert, loginAsAdmin } from './helpers';

/**
 * Spec §22 *UI loses its live connection* (10 §4): block the SSE stream → FreshnessBar goes red, data stays visible,
 * an action still works; unblock → the bar is live again and changes that happened meanwhile arrive by replay.
 */
test('UI loses its live connection', async ({ page, request, context }) => {
  const summary = `e2e live ${Date.now()}`;
  await fireAlert(request, summary);

  await loginAsAdmin(page);
  await page.goto('/queue?view=needsAttention');
  const bar = page.getByRole('status');
  await expect(bar).toHaveAttribute('data-freshness', 'live', { timeout: 20_000 });
  const row = page.locator('tr[data-episode-id]', { hasText: summary }).first();
  await expect(row).toBeVisible({ timeout: 30_000 });

  // The connection drops (laptop lid, proxy hiccup): every reconnect attempt fails.
  await context.route('**/api/v1/events/stream**', (route) => route.abort('connectionfailed'));
  // The open stream is not interrupted by a route rule, so reload: the fresh EventSource is what the rule blocks.
  await page.reload();
  await expect(bar).toHaveAttribute('data-freshness', /disconnected|polling/, { timeout: 30_000 });
  await expect(bar).toContainText(/Data may be out of date|Live connection lost/);

  // Data is still there and actions still work.
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Acknowledge' }).click();
  await expect(page.locator('tr[data-episode-id]', { hasText: summary }).first().getByText('Acknowledged')).toBeVisible({ timeout: 15_000 });

  // Something happens while we are cut off.
  const second = `e2e missed ${Date.now()}`;
  await fireAlert(request, second);

  // The connection comes back: live again and the missed episode appears without a manual refresh.
  await context.unroute('**/api/v1/events/stream**');
  await expect(bar).toHaveAttribute('data-freshness', 'live', { timeout: 45_000 });
  await expect(page.locator('tr[data-episode-id]', { hasText: second }).first()).toBeVisible({ timeout: 45_000 });
});

test('opening an episode from the queue shows the drawer with explanation and timeline', async ({ page, request }) => {
  const summary = `e2e drawer ${Date.now()}`;
  await fireAlert(request, summary);
  await loginAsAdmin(page);
  await page.goto('/queue?view=needsAttention');
  const row = page.locator('tr[data-episode-id]', { hasText: summary }).first();
  await expect(row).toBeVisible({ timeout: 30_000 });
  await row.click();
  const drawer = page.getByRole('complementary', { name: 'Episode' });
  await expect(drawer).toBeVisible();
  await expect(drawer.getByRole('heading', { level: 2 })).toHaveText(summary);
  await expect(drawer.getByText('Why you see this')).toBeVisible();
  await drawer.getByRole('tab', { name: 'Timeline' }).click();
  await expect(drawer.locator('li[data-kind="source_event"]').first()).toBeVisible();
  await expect(page).toHaveURL(/episode=/);
});
