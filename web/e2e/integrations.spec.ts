import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { ensureTeam, loginAsAdmin } from './helpers';

async function noSeriousViolations(page: import('@playwright/test').Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })), null, 2)).toEqual([]);
}

/** 08 §3.5: the ten-step wizard onboards an Azure Monitor source from the seeded reference mapping; the detail shows mappings and an empty failure queue. */
test('onboard an Azure Monitor integration through the wizard', async ({ page, request }) => {
  await ensureTeam(request);
  await loginAsAdmin(page);
  await page.goto('/integrations');
  await expect(page.getByRole('heading', { name: 'Integrations' })).toBeVisible();
  await page.getByRole('link', { name: 'New integration' }).click();

  const name = `e2e-azure-${Date.now()}`;
  await page.getByTestId('wizard-type').selectOption('azure_monitor');
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Owner team').selectOption({ index: 1 });
  await expect(page.getByLabel('Access scope')).toHaveValue('e2e');
  await noSeriousViolations(page);
  await page.getByTestId('wizard-next').click();

  await page.getByTestId('wizard-create').click();
  const secret = page.getByTestId('one-time-secret');
  await expect(secret).toBeVisible();
  expect(((await secret.textContent()) ?? '').length).toBeGreaterThan(20);
  await page.getByRole('button', { name: 'I have stored it' }).click();
  await expect(page.getByText('Ingest path')).toBeVisible();
  await page.getByTestId('wizard-next').click();

  // Mapping step: the seeded common-alert-schema mapping and its sample are pre-filled; preview maps.
  await expect(page.getByTestId('wizard-yaml')).toContainText('identity_version'); // CodeMirror renders visible lines only
  await expect(page.getByTestId('wizard-sample')).toHaveValue(/azureMonitorCommonAlertSchema/);
  await page.getByTestId('wizard-preview').click();
  const card = page.getByTestId('preview-card');
  await expect(card).toHaveAttribute('data-ok', 'true');
  await expect(card.locator('[data-field="event_type"]')).toHaveText('firing');
  await expect(card.locator('[data-field="severity"]')).toHaveText('high');
  await noSeriousViolations(page);
  await page.getByTestId('wizard-next').click();

  await expect(page.getByTestId('wizard-fingerprint')).not.toBeEmpty();
  await page.getByTestId('wizard-next').click();
  await expect(page.getByRole('radio', { name: /Explicit recovery/ })).toBeChecked();
  await page.getByTestId('wizard-next').click();
  await expect(page.getByText('cpu-high: CPU above 90%')).toBeVisible();
  await page.getByTestId('wizard-next').click();
  await expect(page.getByTestId('routing-team')).toHaveText('e2e-team');
  await page.getByTestId('wizard-next').click();
  await expect(page.getByTestId('wizard-inactivity')).toContainText('60 minutes');
  await expect(page.getByTestId('wizard-inactivity')).toContainText('unverified');
  await page.getByTestId('wizard-next').click();
  await page.getByRole('radio', { name: /Managed canary rule/ }).check();
  await page.getByTestId('wizard-next').click();
  await noSeriousViolations(page);
  await page.getByTestId('wizard-activate').click();

  await expect(page.getByTestId('integration-title')).toHaveText(name);
  await expect(page.getByTestId('integration-health')).toBeVisible();
  await page.getByRole('tab', { name: 'Mappings' }).click();
  await expect(page.getByTestId('mapping-version-picker').locator('option')).toHaveCount(4);
  await expect(page.getByTestId('mapping-editor')).toContainText('mapping:');
  await page.getByRole('tab', { name: 'Failures' }).click();
  await expect(page.getByTestId('failures-empty')).toBeVisible();
  await noSeriousViolations(page);

  // 09 M12 shadow gate: Azure Monitor has no state-query API, so the divergence check reports "not supported" with the reason.
  await page.getByRole('tab', { name: 'Health' }).click();
  await expect(page.getByTestId('divergence-none')).toBeVisible();
  await page.getByTestId('divergence-run').click();
  const report = page.getByTestId('divergence-report');
  await expect(report).toHaveAttribute('data-supported', 'false');
  await expect(report).toContainText('not supported');
  await noSeriousViolations(page);
});
