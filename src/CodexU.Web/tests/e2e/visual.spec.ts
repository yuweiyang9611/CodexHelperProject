import { expect, test, type Page } from '@playwright/test'
import { openDemo, openTab, settleUi } from './demo'

async function expectOverviewToFit(page: Page) {
  const bounds = await page.locator('.app-shell').evaluate(element => {
    const rect = element.getBoundingClientRect()
    return { left: rect.left, right: rect.right, width: window.innerWidth, overflow: element.scrollWidth - element.clientWidth }
  })
  expect(bounds.left).toBeGreaterThanOrEqual(-1)
  expect(bounds.right).toBeLessThanOrEqual(bounds.width + 1)
  expect(bounds.overflow).toBeLessThanOrEqual(1)
  const clipped = await page.locator('.metric > strong, .value-main > strong').evaluateAll(elements => elements
    .filter(element => element.scrollWidth > element.clientWidth + 1).map(element => element.textContent))
  expect(clipped).toEqual([])
}

async function reveal(page: Page, selector: string) {
  await page.locator(selector).first().scrollIntoViewIfNeeded()
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur())
  await settleUi(page)
}

async function alignInScrollFrame(page: Page, selector: string, offset: number) {
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur())
  for (let attempt = 0; attempt < 2; attempt++) {
    await page.locator(selector).evaluate((element, targetOffset) => {
      const frame = element.closest('.viewport-frame') as HTMLElement
      frame.scrollTop += Math.round(element.getBoundingClientRect().top - frame.getBoundingClientRect().top - targetOffset)
    }, offset)
    await settleUi(page)
  }
  await expect.poll(() => page.locator(selector).evaluate(element => {
    const frame = element.closest('.viewport-frame')!
    return Math.round(element.getBoundingClientRect().top - frame.getBoundingClientRect().top)
  })).toBe(offset)
}

test.beforeEach(async ({ page }, testInfo) => { await openDemo(page, testInfo) })

test('dashboard overview matches its visual baseline', async ({ page }) => {
  await expect(page.getByRole('tab')).toHaveText(['总览', '用量分析', '设置'])
  await expect(page.getByRole('tab', { name: '总览', exact: true })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('button', { name: '近 30 天', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.app-shell')).not.toContainText('回本倍数')
  await expectOverviewToFit(page)
  await expect(page).toHaveScreenshot('dashboard-overview.png', { fullPage: true })
})

test('value details explain selected amounts without claiming profits', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  const toggle = page.getByRole('button', { name: '展开明细', exact: true })
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  await toggle.click()
  await expect(page.locator('.value-details')).toContainText('金额未知')
  await expect(page.locator('.value-details')).toContainText('费率')
  await expect(page.locator('.value-details')).not.toContainText('净收益')
  await expect(page.getByRole('button', { name: '收起明细', exact: true })).toHaveAttribute('aria-expanded', 'true')
})

test('analysis and grouped session details match their visual baselines', async ({ page }, testInfo) => {
  test.skip(!['chromium-dark-100', 'chromium-light-125'].includes(testInfo.project.name))
  await openTab(page, 'usage')
  await expect(page.locator('.analysis-summary')).toBeVisible()
  await expect(page.locator('.lifecycle-card')).toHaveCount(0)
  await reveal(page, '.analysis-trend')
  await expect(page).toHaveScreenshot('usage-analysis.png')
  await page.locator('.session-group > summary').first().click()
  await reveal(page, '.session-member')
  await expect(page).toHaveScreenshot('session-details.png')
})

test('Claude analysis states runtime field limits', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  await page.getByRole('button', { name: 'Claude Code', exact: true }).click()
  await openTab(page, 'usage')
  await page.getByText('Token 分项与包含关系', { exact: true }).click()
  await expect(page.locator('.lifecycle-card')).toHaveCount(0)
  await reveal(page, '.analysis-summary')
  await expect(page).toHaveScreenshot('usage-analysis-claude.png')
})

test('comparison is optional inside overview with the shared range', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  await page.getByRole('button', { name: '近 7 天', exact: true }).click()
  await page.getByRole('button', { name: '并排比较两种工具', exact: true }).click()
  await expect(page.locator('.combined-columns')).toBeVisible()
  await expect(page.getByRole('button', { name: '近 7 天', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('tab', { name: '双运行时', exact: true })).toHaveCount(0)
  await reveal(page, '.combined-columns')
  await expect(page).toHaveScreenshot('overview-comparison.png')
})

test('settings and diagnostics match their visual baseline', async ({ page }) => {
  await openTab(page, 'diagnostics')
  await reveal(page, '#panel-diagnostics')
  await expect(page).toHaveScreenshot('settings-diagnostics.png', { maxDiffPixelRatio: 0.0005 })
})

test('settings and versioned rate editor match their visual baseline', async ({ page }, testInfo) => {
  test.skip(!['chromium-dark-100', 'chromium-light-125'].includes(testInfo.project.name))
  await openTab(page, 'diagnostics')
  await reveal(page, '.settings-card')
  await expect(page).toHaveScreenshot('application-settings.png')
  await page.locator('.rate-editor > summary').click()
  await page.getByRole('button', { name: '添加费率版本', exact: true }).click()
  await reveal(page, '.rate-editor')
  await expect(page).toHaveScreenshot('versioned-rate-editor.png')
})

test('pinned rate snapshot remains visible and immutable', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  await openDemo(page, testInfo, { pinnedRates: true })
  await openTab(page, 'diagnostics')
  await expect(page.getByText('锁定快照', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '添加费率版本' })).toBeDisabled()
  await expect(page.locator('.rate-row input, .rate-row select').first()).toBeDisabled()
  await expect(page.locator('.rate-editor')).toHaveScreenshot('pinned-rate-snapshot.png', {
    mask: [page.locator('.rate-row input[type="date"]')], maskColor: '#151a28',
  })
})

test('compact and scaled layouts fit without clipping metrics', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  await openDemo(page, testInfo, { compact: true })
  await expectOverviewToFit(page)
  await expect(page).toHaveScreenshot('dashboard-compact.png', { fullPage: true })
  await page.setViewportSize({ width: 1080, height: 768 })
  await openDemo(page, testInfo, { scale: 140 })
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; scroll-behavior: auto !important; transition: none !important; }' })
  await expectOverviewToFit(page)
  await expect(page).toHaveScreenshot('dashboard-scale-140.png')
  await openTab(page, 'usage')
  await page.locator('.session-group > summary').first().click()
  await reveal(page, '.session-member')
  await expectOverviewToFit(page)
  await expect(page).toHaveScreenshot('session-scale-140.png')
  await openTab(page, 'diagnostics')
  await page.locator('.rate-editor > summary').click()
  await page.getByRole('button', { name: '添加费率版本' }).click()
  await reveal(page, '.rate-editor')
  await expectOverviewToFit(page)
  await alignInScrollFrame(page, '.rate-editor', 24)
  await expect(page).toHaveScreenshot('settings-rate-editor-scale-140.png', {
    mask: [page.locator('.rate-row input[type="date"]')], maskColor: '#151a28',
  })
  await page.setViewportSize({ width: 920, height: 540 })
  await openDemo(page, testInfo, { compact: true, scale: 140 })
  await expectOverviewToFit(page)
  await expect(page).toHaveScreenshot('dashboard-compact-scale-140.png')
})
