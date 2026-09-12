import { expect, type APIRequestContext, type Page } from '@playwright/test';

/**
 * The stack under test is real: API (8080), Ingest (8081) and Workers on a fresh database, plus the Vite dev server.
 * `E2E_ADMIN_PASSWORD` is the bootstrap password (Auth:Local:BootstrapPassword) the API was started with.
 */
export const API = process.env.E2E_API_URL ?? 'http://localhost:8080';
export const INGEST = process.env.E2E_INGEST_URL ?? 'http://localhost:8081';
export const ADMIN_PASSWORD = process.env.E2E_ADMIN_PASSWORD ?? 'Bootstrap-Admin-Passw0rd!';
export const ADMIN_FINAL_PASSWORD = process.env.E2E_ADMIN_FINAL_PASSWORD ?? 'E2E-admin-passphrase-2026';

/** Logs in through the UI, completing the forced change on the very first run. */
export async function loginAsAdmin(page: Page): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Username').fill('admin');
  await page.getByLabel('Password', { exact: true }).fill(ADMIN_FINAL_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  const outcome = await Promise.race([
    page.waitForURL(/\/queue/).then(() => 'queue' as const),
    page.getByText('Incorrect username or password').waitFor().then(() => 'rejected' as const),
  ]);
  if (outcome === 'queue') return;

  // First run: the bootstrap password is still in force and must be changed.
  await page.getByLabel('Password', { exact: true }).fill(ADMIN_PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL(/\/login\/change-password/);
  await page.getByLabel('Current password').fill(ADMIN_PASSWORD);
  await page.getByLabel('New password', { exact: true }).fill(ADMIN_FINAL_PASSWORD);
  await page.getByLabel('Confirm new password').fill(ADMIN_FINAL_PASSWORD);
  await page.getByRole('button', { name: 'Set new password' }).click();
  await page.waitForURL(/\/queue/);
}

interface ApiSession {
  cookie: string;
  csrf: string;
}

async function sessionFor(request: APIRequestContext, response: import('@playwright/test').APIResponse): Promise<ApiSession> {
  const setCookie = response.headersArray().find((h) => h.name.toLowerCase() === 'set-cookie')?.value ?? '';
  const cookie = setCookie.split(';')[0] ?? '';
  const csrfResponse = await request.get(`${API}/auth/csrf`, { headers: { Cookie: cookie } });
  const { token } = (await csrfResponse.json()) as { token: string };
  return { cookie, csrf: token };
}

/** Logs the admin in over the API; on a fresh stack it completes the forced password change first (06 §6). */
async function apiLogin(request: APIRequestContext): Promise<ApiSession> {
  const attempt = async (password: string) => request.post(`${API}/auth/login`, { data: { username: 'admin', password, keepSignedIn: false } });
  let response = await attempt(ADMIN_FINAL_PASSWORD);
  if (response.status() === 204) return sessionFor(request, response);

  response = await attempt(ADMIN_PASSWORD);
  expect(response.status(), 'admin login with the bootstrap password').toBe(204);
  const bootstrap = await sessionFor(request, response);
  const changed = await request.post(`${API}/auth/change-password`, {
    headers: { Cookie: bootstrap.cookie, 'X-CSRF-Token': bootstrap.csrf },
    data: { current: ADMIN_PASSWORD, new: ADMIN_FINAL_PASSWORD },
  });
  expect(changed.status(), 'forced password change').toBe(204);
  return bootstrap;
}

/** Makes sure at least one team exists (a fresh stack has none); heartbeats and routing need one. Returns its id. */
export async function ensureTeam(request: APIRequestContext, name = 'e2e-team'): Promise<string> {
  const session = await apiLogin(request);
  const headers = { Cookie: session.cookie, 'X-CSRF-Token': session.csrf };
  const existing = await request.get(`${API}/api/v1/teams`, { headers });
  const teams = (await existing.json()) as { id: string; name: string }[];
  const found = teams.find((t) => t.name === name);
  if (found) return found.id;
  const created = await request.post(`${API}/api/v1/teams`, { headers, data: { name, accessScopes: ['e2e'], isTriage: teams.length === 0 } });
  expect(created.status(), 'create team').toBe(201);
  return ((await created.json()) as { id: string }).id;
}

/** Creates a generic-webhook integration through the API and fires one alert through the Ingest host; returns the alert id. */
export async function fireAlert(request: APIRequestContext, summary: string): Promise<string> {
  const session = await apiLogin(request);
  const headers = { Cookie: session.cookie, 'X-CSRF-Token': session.csrf };
  const name = `e2e-${Date.now()}`;
  const created = await request.post(`${API}/api/v1/integrations`, { headers, data: { name, type: 'generic_webhook', accessScope: 'e2e', ownerTeamId: null } });
  expect(created.status(), 'create integration').toBe(201);
  const { ingestPath, ingestToken } = (await created.json()) as { ingestPath: string; ingestToken: string };

  const alertId = `alert-${Date.now()}`;
  const ingest = await request.post(`${INGEST}${ingestPath}`, {
    headers: { Authorization: `Bearer ${ingestToken}`, 'Content-Type': 'application/json' },
    data: {
      eventType: 'firing',
      alertId,
      eventId: crypto.randomUUID(),
      occurredAt: new Date().toISOString(),
      severity: 'high',
      environment: 'production',
      service: 'orders',
      resource: { id: `res-${alertId}`, name: 'Orders API' },
      rule: { id: '5xx', name: '5xx rate' },
      summary,
    },
  });
  expect(ingest.status(), 'ingest accepted').toBe(202);
  return alertId;
}
