import { defineConfig, devices } from '@playwright/test'

const mockMode = process.env.PLAYWRIGHT_REAL !== '1'
const storageState = process.env.PLAYWRIGHT_STORAGE_STATE ?? (mockMode ? undefined : 'playwright/.auth/user.json')

export default defineConfig({
  testDir: './e2e',
  testMatch: mockMode ? undefined : '**/real-*.spec.ts',
  testIgnore: mockMode ? '**/real-*.spec.ts' : undefined,
  fullyParallel: false,
  forbidOnly: process.env.CI === 'true',
  retries: process.env.CI === 'true' ? 2 : 0,
  workers: 1,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? 'http://127.0.0.1:4173',
    storageState,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: mockMode
    ? {
        command: 'npm run dev -- --host 127.0.0.1 --port 4173 --strictPort',
        url: 'http://127.0.0.1:4173',
        reuseExistingServer: process.env.CI !== 'true',
        timeout: 120_000,
        env: { VITE_USE_MOCKS: 'true' },
      }
    : undefined,
})
