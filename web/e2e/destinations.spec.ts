import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { API, ensureTeam, loginAsAdmin } from './helpers';

/** A URL the API host answers on; `Notifications:AllowInsecureDestinations` must be on for the e2e stack (it is in CI). */
const RECEIVER = `${API}/healthz/live`;

async function noSeriousViolations(page: import('@playwright/test').Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })), null, 2)).toEqual([]);
}

/** 08 §3.7b: built-ins are cloneable, not editable; a new version is inactive until activated; the preview renders live. */
test('clone a built-in template, save it and activate the version', async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto('/templates');
  await expect(page.getByRole('heading', { name: 'Webhook templates' })).toBeVisible();
  await expect(page.locator('tr[data-builtin="true"]')).toHaveCount(4);
  await noSeriousViolations(page);

  await page.getByRole('link', { name: 'teams-adaptive-card' }).click();
  await expect(page.getByTestId('template-title')).toHaveText('teams-adaptive-card');
  await expect(page.getByText('read-only')).toBeVisible();
  await expect(page.getByTestId('template-preview').first().locator('pre')).toContainText('AdaptiveCard');
  await page.getByRole('link', { name: 'Clone' }).click();

  const name = `e2e-card-${Date.now()}`;
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Description').fill('cloned in e2e');
  await expect(page.getByTestId('template-preview').first().locator('pre')).toContainText('AdaptiveCard', { timeout: 15_000 });
  await page.getByRole('button', { name: 'Create template' }).click();

  await expect(page.getByTestId('template-title')).toHaveText(name);
  await expect(page.getByTestId('version-state')).toContainText('inactive');
  await page.getByRole('button', { name: 'Activate' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Activate' }).click();
  await expect(page.getByTestId('version-state')).toContainText('v1 · active');
  await noSeriousViolations(page);
});

/** 08 §3.7b: first destination pair through the form, template picker with preview, signing secret shown once, test send, reveal, deliveries. */
test('create a destination pair, send a test, reveal the URL, see the delivery', async ({ page, request }) => {
  await ensureTeam(request);
  await loginAsAdmin(page);
  await page.goto('/destinations');
  await expect(page.getByRole('heading', { name: 'Destinations' })).toBeVisible();
  await page.getByRole('link', { name: 'New destination' }).click();

  const name = `e2e-dest-${Date.now()}`;
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel(/^URL/).fill(RECEIVER);
  await page.getByTestId('template-picker').selectOption({ value: '00000000-0000-0000-0000-00000000f002' });
  await expect(page.getByTestId('template-preview').locator('pre')).toContainText('AdaptiveCard', { timeout: 15_000 });
  const firstPair = page.getByLabel('Fallback name');
  if (await firstPair.isVisible()) {
    await firstPair.fill(`${name}-fallback`);
    await page.getByLabel('Fallback URL').fill(RECEIVER);
  } else {
    await page.getByRole('combobox', { name: 'Fallback destination' }).selectOption({ index: 1 });
  }
  await noSeriousViolations(page);
  await page.getByRole('button', { name: 'Create destination' }).click();

  const secret = page.getByTestId('one-time-secret');
  await expect(secret).toBeVisible();
  expect(((await secret.textContent()) ?? '').length).toBeGreaterThan(20);
  await page.getByRole('button', { name: 'I have stored it' }).click();
  await expect(page.getByTestId('destination-title')).toHaveText(name);

  // Test send goes through the real channel; the receiver answers (whatever it says) and the result is shown inline.
  await page.getByRole('button', { name: 'Send test' }).click();
  const result = page.getByTestId('test-result');
  await expect(result.getByText(/HTTP \d{3}/)).toBeVisible({ timeout: 20_000 });
  await expect(result.getByText(/\d+ ms/)).toBeVisible();

  // Secrets are masked; revealing needs the password again.
  await expect(page.getByLabel(/^URL/)).toHaveAttribute('placeholder', /^http:\/\/localhost/);
  await page.getByRole('button', { name: 'Reveal' }).click();
  await page.getByLabel('Your password').fill(process.env.E2E_ADMIN_FINAL_PASSWORD ?? 'E2E-admin-passphrase-2026');
  await page.getByRole('dialog').getByRole('button', { name: 'Reveal' }).click();
  await expect(page.getByTestId('revealed-url')).toHaveText(RECEIVER);
  await page.getByRole('dialog').getByRole('button', { name: 'Close' }).click();

  await page.getByRole('tab', { name: 'Deliveries' }).click();
  await expect(page.locator('tr[data-outcome]').first()).toBeVisible();
  await noSeriousViolations(page);
});
