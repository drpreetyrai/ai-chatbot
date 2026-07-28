import { resolve } from 'node:path'
import { defineConfig, devices } from '@playwright/test'

/**
 * Absolute, because a RELATIVE ARIA_SQLITE_PATH is anchored to the directory
 * holding .env (so the API and workers share one database). Passing "./aria-e2e.db"
 * here would land it at the repo root while global setup deleted the one in web/ —
 * and every run would silently inherit the previous run's clinic.
 */
export const E2E_DB = resolve(import.meta.dirname, 'aria-e2e.db')


/**
 * E2E runs against the real stack: the real API, the real guardrails, the real
 * database. Nothing is mocked, because the value of an end-to-end test is
 * precisely that it exercises the seams the unit tests cannot see.
 *
 * The transcript replays instantly here (ARIA_DEMO_PLAYBACK_SPEED) so the hero
 * journey takes seconds rather than the realistic pace a clinician sees.
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,        // the seeded clinic is shared state
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? 'list' : [['list']],
  timeout: 60_000,

  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },

  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],

  webServer: [
    {
      command: 'dotnet run --project ../src/Aria.Api',
      url: 'http://localhost:5199/health',
      // Normally Playwright owns the API, because the suite must control its
      // configuration completely. E2E_REUSE=1 attaches to a server you started
      // yourself — useful when debugging a failure interactively.
      reuseExistingServer: process.env.E2E_REUSE === '1',
      timeout: 120_000,
      env: {
        // Fully isolated from the developer's .env. If a real Foundry endpoint, a
        // real FHIR server or a real Entra tenant leaked in here, the suite would
        // sign in against a live directory and write to live systems.
        ARIA_IGNORE_DOTENV: 'true',
        ARIA_ENVIRONMENT: 'Development',
        ARIA_SQLITE_PATH: E2E_DB,
        // The API drops and recreates its own schema at startup. Deleting the file
        // from here would race the server that has it open.
        ARIA_RESET_DATABASE: 'true',
        ARIA_DEMO_PLAYBACK_SPEED: '500',
        ARIA_DEV_JWT_SIGNING_KEY: 'e2e-only-signing-key-0123456789abcdef0123',
        ASPNETCORE_URLS: 'http://localhost:5199',
      },
    },
    {
      command: 'npm run dev',
      url: 'http://localhost:5173',
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
    },
  ],
})
