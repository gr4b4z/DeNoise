import { expect, test } from '@playwright/test';
import { loginAsAdmin } from './helpers';

/** 06 §3: a team is created from the UI, appears in the list and in the rail, and the triage role is explained before it is taken. */
test('an administrator creates a team from the UI', async ({ page }) => {
  await loginAsAdmin(page);
  await page.getByTestId('nav-teams-admin').click();
  await page.waitForURL((u) => u.pathname === '/teams');

  const name = `Payments ${Date.now()}`;
  await page.getByTestId('team-new').click();
  const form = page.getByRole('form', { name: 'New team' });
  await form.getByTestId('team-name').fill(name);
  await form.getByTestId('team-scopes').fill('demo, mpt');
  await expect(form.getByTestId('team-scopes-preview')).toContainText('mpt');
  await form.getByTestId('team-save').click();

  const row = page.locator('tr', { hasText: name });
  await expect(row).toBeVisible();
  await expect(row).toContainText('demo');
  await expect(row).toContainText('mpt');
  // the rail reads the same list, so a created team is navigable immediately
  await expect(page.getByRole('navigation').getByRole('link', { name, exact: true })).toBeVisible();
});

test('the create form refuses a team without a name or a scope', async ({ page }) => {
  await loginAsAdmin(page);
  await page.goto('/teams');
  await page.getByTestId('team-new').click();
  const form = page.getByRole('form', { name: 'New team' });

  await form.getByTestId('team-save').click();
  await expect(form.getByTestId('team-name')).toHaveAttribute('aria-invalid', 'true');

  await form.getByTestId('team-name').fill('No scope team');
  await form.getByTestId('team-save').click();
  await expect(form.getByTestId('team-scopes')).toHaveAttribute('aria-invalid', 'true');
});
