import { expect, type Page, type TestInfo } from '@playwright/test'

const fixedNow = new Date('2026-07-14T12:00:00+09:00')

export async function openDemo(
  page: Page,
  testInfo: TestInfo,
  options: { compact?: boolean, scale?: number, pinnedRates?: boolean } = {},
) {
  // Keep dates, relative-time labels and the heatmap range deterministic without
  // replacing timers used by the browser demo bridge.
  await page.clock.setFixedTime(fixedNow)
  const theme = testInfo.project.use.colorScheme === 'light' ? 'light' : 'dark'
  const parameters = new URLSearchParams({ visualTest: '1', theme })
  if (options.compact) parameters.set('compact', '1')
  if (options.scale) parameters.set('scale', String(options.scale))
  if (options.pinnedRates) parameters.set('pinnedRates', '1')
  await page.goto(`/?${parameters}`)
  await expect(page.locator('.overview-grid')).toBeVisible()
  await expect(page.locator('.app-shell')).toHaveClass(theme === 'light' ? /light/ : /app-shell(?!.*light)/)
  await expect(page.locator('.app-shell')).toHaveClass(options.compact ? /compact/ : /app-shell(?!.*compact)/)
  await settleUi(page)
}

/**
 * Codex and Claude dashboard tabs, in the order they appear.
 *
 * Tabs are opened by name rather than by position: an index silently points at a
 * different panel the moment a tab is inserted, and a test that opens the wrong panel
 * usually still passes — it just stops checking what it was written to check.
 */
export const TAB_IDS = [
  'usage',
  'projects',
  'skills',
  'combined',
  'diagnostics',
] as const

export type TabId = typeof TAB_IDS[number]

export async function openAntigravity(page: Page) {
  const runtime = page.getByRole('button', { name: 'Antigravity', exact: true })
  await runtime.click()
  await expect(runtime).toHaveAttribute('aria-pressed', 'true')
  const panel = page.locator('#panel-antigravity')
  await expect(panel.getByRole('heading', { name: 'Gemini Pro', exact: true })).toBeVisible()
  await settleUi(page)
  return panel
}

export async function openTab(page: Page, id: TabId) {
  const tab = page.locator(`#tab-${id}`)
  await tab.click()
  await expect(tab).toHaveAttribute('aria-selected', 'true')
  // The combined panel loads independently. Wait for its final height before
  // scrolling or capturing it; a short loading panel clamps the scroll position.
  if (id === 'combined') {
    const combined = page.locator('#panel-combined')
    await expect(combined.locator('.combined-runtime-column')).toHaveCount(3)
    await expect(combined.getByRole('heading', { name: 'Gemini Pro', exact: true })).toBeVisible()
  }
  if (id === 'diagnostics') {
    await expect(page.getByRole('region', { name: 'Claude CLI 登录状态', exact: true })).toHaveAttribute('aria-busy', 'false')
  }
  await settleUi(page)
}

export async function settleUi(page: Page) {
  await page.evaluate(async () => {
    await document.fonts.ready
    await new Promise<void>((resolve) => requestAnimationFrame(() => requestAnimationFrame(() => resolve())))
  })
}
