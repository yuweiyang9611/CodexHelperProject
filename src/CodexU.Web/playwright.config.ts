import { defineConfig } from '@playwright/test'

const port = 4173

export default defineConfig({
  testDir: './tests/e2e',
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  timeout: 45_000,
  expect: {
    timeout: 10_000,
    toHaveScreenshot: {
      animations: 'disabled',
      caret: 'hide',
      maxDiffPixelRatio: 0.005,
      scale: 'css',
    },
  },
  outputDir: 'test-results',
  reporter: process.env.CI
    ? [['github'], ['html', { outputFolder: 'playwright-report', open: 'never' }]]
    : [['list'], ['html', { outputFolder: 'playwright-report', open: 'never' }]],
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    browserName: 'chromium',
    locale: 'zh-CN',
    timezoneId: 'Asia/Tokyo',
    contextOptions: { reducedMotion: 'reduce' },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: `npm run preview -- --host 127.0.0.1 --port ${port}`,
    url: `http://127.0.0.1:${port}`,
    reuseExistingServer: false,
    timeout: 120_000,
  },
  projects: [
    {
      name: 'chromium-dark-100',
      use: { viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1, colorScheme: 'dark' },
    },
    {
      name: 'chromium-dark-125',
      use: { viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1.25, colorScheme: 'dark' },
    },
    {
      name: 'chromium-light-125',
      use: { viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1.25, colorScheme: 'light' },
    },
    {
      name: 'chromium-dark-150',
      use: { viewport: { width: 1280, height: 900 }, deviceScaleFactor: 1.5, colorScheme: 'dark' },
    },
    {
      name: 'chromium-dark-200',
      use: { viewport: { width: 1280, height: 900 }, deviceScaleFactor: 2, colorScheme: 'dark' },
    },
    {
      name: 'chromium-light-200',
      use: { viewport: { width: 1280, height: 900 }, deviceScaleFactor: 2, colorScheme: 'light' },
    },
  ],
})
