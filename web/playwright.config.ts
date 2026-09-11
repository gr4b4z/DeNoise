import { defineConfig, devices } from '@playwright/test';

// E2E runs against a real stack: API on 8080 (fresh database, bootstrap admin) and the Vite dev server proxying to it.
// `pnpm e2e` expects the API to be running; see docs/milestones/M4b-react-queue-ui.md for the two commands.
const baseURL = process.env.E2E_BASE_URL ?? 'http://localhost:5173';

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    ...devices['Desktop Chrome'],
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH } : {},
  },
  webServer: process.env.E2E_BASE_URL
    ? undefined
    : { command: 'pnpm dev', url: 'http://localhost:5173', reuseExistingServer: true, timeout: 60_000 },
});
