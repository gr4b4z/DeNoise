import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';

/**
 * M11b login page. The stack under test runs without an identity provider, so `/auth/providers` is answered here with
 * the two configurations the page must render: SSO next to the password form, and SSO with password sign-in disabled
 * (break-glass link). The SSO round trip itself is covered against a mock provider in OidcScenarios.cs.
 */
const SSO = { displayName: 'Microsoft', loginUrl: '/auth/oidc/start' };

async function providers(page: Page, body: { local: boolean; oidc: typeof SSO | null }) {
  await page.route('**/auth/providers', (route) => route.fulfill({ json: { ...body, selfServiceReset: false } }));
}

async function expectNoSeriousViolations(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })), null, 2)).toEqual([]);
}

test('without an identity provider the login page is unchanged: no SSO button', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByLabel('Username')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
  await expect(page.getByRole('link', { name: /Sign in with/ })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Break-glass sign-in' })).toHaveCount(0);
});

test('the SSO button appears when the provider is configured, next to the password form', async ({ page }) => {
  await providers(page, { local: true, oidc: SSO });
  await page.goto('/login?returnTo=%2Fhistory');
  const sso = page.getByRole('link', { name: 'Sign in with Microsoft' });
  await expect(sso).toBeVisible();
  await expect(sso).toHaveAttribute('href', '/auth/oidc/start?returnTo=%2Fhistory');
  await expect(page.getByLabel('Username')).toBeVisible();
  await expect(page.getByLabel('Password', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
  await expectNoSeriousViolations(page);
});

test('an off-host returnTo is not carried into the SSO link', async ({ page }) => {
  await providers(page, { local: true, oidc: SSO });
  await page.goto('/login?returnTo=%2F%2Fevil.example');
  await expect(page.getByRole('link', { name: 'Sign in with Microsoft' })).toHaveAttribute('href', '/auth/oidc/start');
});

test('with password sign-in disabled a break-glass link replaces the form, and opens it', async ({ page }) => {
  await providers(page, { local: false, oidc: SSO });
  await page.goto('/login');
  await expect(page.getByRole('link', { name: 'Sign in with Microsoft' })).toBeVisible();
  await expect(page.getByLabel('Username')).toHaveCount(0);
  const breakGlass = page.getByRole('link', { name: 'Break-glass sign-in' });
  await expect(breakGlass).toBeVisible();
  await expectNoSeriousViolations(page);

  await breakGlass.click();
  await page.waitForURL(/breakGlass=true/);
  await expect(page.getByText('Break-glass sign-in: emergency and service accounts only.')).toBeVisible();
  await expect(page.getByLabel('Username')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
});

test('a failed SSO round trip shows one message without a reason', async ({ page }) => {
  await providers(page, { local: true, oidc: SSO });
  await page.goto('/login?error=sso');
  await expect(page.getByRole('alert')).toHaveText(/Single sign-on did not complete/);
});
