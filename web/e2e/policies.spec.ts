import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { API, ensureTeam, loginAsAdmin } from './helpers';

async function noSeriousViolations(page: import('@playwright/test').Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious, JSON.stringify(serious.map((v) => ({ id: v.id, nodes: v.nodes.map((n) => n.target) })), null, 2)).toEqual([]);
}

/** 08 §3.6: create a lifecycle policy, save a second version, see the diff and the impact preview, activate, roll back. */
test('lifecycle policy: versions, diff, impact preview gates activate, rollback', async ({ page, request }) => {
  await ensureTeam(request);
  await loginAsAdmin(page);
  await page.goto('/policies/lifecycle');
  await expect(page.getByRole('heading', { name: 'Policies' })).toBeVisible();
  await noSeriousViolations(page);
  await page.getByTestId('policy-new').click();
  await page.waitForURL(/\/policies\/lifecycle\/new/);
  const name = `e2e-lifecycle-${Date.now()}`;
  await page.getByTestId('policy-name').fill(name);
  // The skeleton document is a valid repeating_while_active profile; create v1 as is.
  await page.getByTestId('policy-save').click();
  await page.waitForURL(/\/policies\/lifecycle\/(?!new)[0-9a-f-]+$/);
  await expect(page.getByTestId('policy-title')).toHaveText(name);
  await expect(page.getByTestId('version-state')).toContainText('v1 · inactive');

  // Impact preview loads for an inactive lifecycle version, then Activate enables.
  await expect(page.getByTestId('policy-impact')).toBeVisible();
  await expect(page.getByTestId('impact-count')).toBeVisible();
  const activate = page.getByTestId('policy-activate');
  await expect(activate).toBeEnabled();
  await noSeriousViolations(page);
  await activate.click();
  await page.getByRole('dialog').getByRole('button', { name: 'Activate' }).click();
  await expect(page.getByTestId('version-state')).toContainText('v1 · active');

  // Edit the document into v2: change the silence timeout (top of the file, so CodeMirror has it rendered).
  const editor = page.getByTestId('policy-editor').locator('.cm-content');
  await editor.click();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.insertText('lifecycle: { profile: repeating_while_active }\nauto_resolve: { after_silence: 2h }\n');
  await page.getByTestId('policy-save').click();
  await expect(page.getByTestId('version-state')).toContainText('v2 · inactive');
  await page.getByTestId('policy-compare').selectOption('1');
  await expect(page.getByTestId('policy-diff')).toContainText('after_silence: 45m');
  await expect(page.getByTestId('policy-diff')).toContainText('after_silence: 2h');
  await page.getByTestId('policy-compare').selectOption('');
  await expect(page.getByTestId('policy-impact')).toContainText('Impact of activating v2');
  await expect(activate).toBeEnabled();
  await activate.click();
  await page.getByRole('dialog').getByRole('button', { name: 'Activate' }).click();
  await expect(page.getByTestId('version-state')).toContainText('v2 · active');

  // Rollback = activating the earlier version.
  await page.getByTestId('policy-version-1').click();
  await expect(activate).toHaveText('Roll back to v1');
  await expect(activate).toBeEnabled();
  await activate.click();
  await page.getByRole('dialog').getByRole('button', { name: 'Roll back' }).click();
  await expect(page.getByTestId('version-state')).toContainText('v1 · active');

  // An invalid document is rejected inline.
  await page.getByTestId('policy-version-1').click();
  await editor.click();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.insertText('lifecycle: { profile: nope }\n');
  await page.getByTestId('policy-save').click();
  await expect(page.getByTestId('policy-errors')).toBeVisible();

  // The list shows the active version of the new policy.
  await page.goto('/policies/lifecycle');
  const row = page.locator('tr[data-policy-id]').filter({ hasText: name });
  await expect(row).toContainText('v1');
});

/** 06 §8 config-as-code: the export re-imported is a no-op; an edit dry-runs as a new version and applies. */
test('config export, dry run and apply', async ({ page, request }) => {
  await ensureTeam(request);
  await loginAsAdmin(page);
  await page.goto('/config');
  await expect(page.getByRole('heading', { name: 'Config as code' })).toBeVisible();
  await expect(page.getByTestId('config-export-editor')).toContainText('alerthub_config: 1');
  await noSeriousViolations(page);

  await page.getByTestId('config-use-export').click();
  await page.getByTestId('config-dry-run').click();
  const result = page.getByTestId('config-result');
  await expect(result).toHaveAttribute('data-dry-run', 'true');
  await expect(result).toHaveAttribute('data-has-changes', 'false');
  await expect(page.getByTestId('config-apply')).toBeDisabled();

  // Fetch the export over the API, edit one field and paste the edited bundle.
  const session = await page.context().cookies();
  const cookie = session.map((c) => `${c.name}=${c.value}`).join('; ');
  const exported = await request.get(`${API}/api/v1/config/export`, { headers: { Cookie: cookie } });
  expect(exported.status()).toBe(200);
  const yaml = await exported.text();
  expect(yaml).toContain('lifecycle:');
  const edited = yaml.includes('after_silence: 45m') ? yaml.replace('after_silence: 45m', 'after_silence: 3h') : yaml.replace('after_silence: 2h', 'after_silence: 3h');
  test.skip(edited === yaml, 'no lifecycle policy with a known timeout to edit');
  const editor = page.getByTestId('config-import-editor').locator('.cm-content');
  await editor.click();
  await page.keyboard.press('ControlOrMeta+A');
  await page.keyboard.insertText(edited);
  await page.getByTestId('config-dry-run').click();
  await expect(result).toHaveAttribute('data-has-changes', 'true');
  await expect(result.locator('tr[data-action="new_version"]').first()).toContainText('~ auto_resolve');
  await page.getByTestId('config-apply').click();
  await page.getByRole('dialog').getByRole('button', { name: 'Apply' }).click();
  await expect(result).toHaveAttribute('data-dry-run', 'false');
  await expect(result.locator('tr[data-action="new_version"]').first()).toContainText('new version v');
  await expect(page.getByTestId('config-export-editor')).toContainText('after_silence: 3h');
});

/** 06 §4 `/audit`: policy activity is listed newest first, the action filter lives in the URL, details show before/after. */
test('audit log lists policy activity with filters', async ({ page, request }) => {
  await ensureTeam(request);
  await loginAsAdmin(page);
  await page.goto('/audit?action=policy');
  await expect(page.getByRole('heading', { name: 'Audit log' })).toBeVisible();
  const rows = page.locator('tr[data-audit-id]');
  await expect(rows.first()).toBeVisible();
  const actions = await rows.evaluateAll((els) => els.map((e) => e.getAttribute('data-action') ?? ''));
  expect(actions.every((a) => a.startsWith('policy.'))).toBe(true);
  await noSeriousViolations(page);
  await rows.first().getByTestId('audit-details').click();
  await expect(page.getByText('After', { exact: true })).toBeVisible();
  await page.getByTestId('audit-target-type').selectOption('policy');
  await page.waitForURL(/targetType=policy/);
  await expect(rows.first()).toBeVisible();
  await page.getByTestId('audit-action').fill('policy.lifecycle.activate');
  await page.getByTestId('audit-apply').click();
  await page.waitForURL(/action=policy\.lifecycle\.activate/);
  await expect(rows.first()).toHaveAttribute('data-action', 'policy.lifecycle.activate');
});
