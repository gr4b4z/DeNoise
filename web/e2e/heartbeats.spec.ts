import { expect, test } from '@playwright/test';
import { ensureTeam, INGEST, loginAsAdmin } from './helpers';

/** 08 §3.4: create a heartbeat, get the one-time URL, ping it through the ingest host, see the run and the healthy state. */
test('register a heartbeat, ping it, see the run', async ({ page, request }) => {
  await ensureTeam(request);
  await loginAsAdmin(page);
  await page.goto('/heartbeats');
  await expect(page.getByRole('heading', { name: 'Heartbeats' })).toBeVisible();
  await page.getByRole('link', { name: 'New heartbeat' }).click();

  const name = `e2e-hb-${Date.now()}`;
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Every').fill('5m');
  await expect(page.getByText('next 5 runs')).toBeVisible();
  await page.getByRole('button', { name: 'Create heartbeat' }).click();

  const secret = page.getByTestId('one-time-secret');
  await expect(secret).toBeVisible();
  const pingUrl = (await secret.textContent())?.trim() ?? '';
  expect(pingUrl).toMatch(/\/hb\/[a-z2-9]{12}\./);
  await page.getByRole('button', { name: 'I have stored it' }).click();
  await expect(page.getByTestId('heartbeat-name')).toHaveText(name);

  // The job pings from the outside (ingest host); the UI reflects the run without a manual refresh.
  const path = pingUrl.slice(pingUrl.indexOf('/hb/'));
  const ping = await request.get(`${INGEST}${path}`);
  expect(ping.status()).toBe(200);
  await expect(page.locator('tr[data-run-kind="success"]').first()).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('[data-heartbeat-state="healthy"]').first()).toBeVisible();

  // Wrong token is refused like an unknown key.
  const wrong = await request.get(`${INGEST}${path.slice(0, path.lastIndexOf('.'))}.not-the-token-not-the-token`);
  expect(wrong.status()).toBe(404);
});
