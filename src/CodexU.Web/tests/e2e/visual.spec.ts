import { expect, test, type Page } from '@playwright/test'
import { openDemo, openTab, settleUi } from './demo'

async function expectOverviewToFit(page: Page) {
  const shellBounds = await page.locator('.app-shell').evaluate((element) => {
    const bounds = element.getBoundingClientRect()
    return { left: bounds.left, right: bounds.right, viewportWidth: window.innerWidth }
  })
  expect(shellBounds.left).toBeGreaterThanOrEqual(-1)
  expect(shellBounds.right).toBeLessThanOrEqual(shellBounds.viewportWidth + 1)

  const clippedValues = await page.locator(
    '.metric > strong, .metric > small, .value-summary-grid strong, .value-breakdown strong',
  ).evaluateAll((elements) => elements
    .filter((element) => element.scrollWidth - element.clientWidth > 1)
    .map((element) => element.textContent?.trim()))
  expect(clippedValues, 'Overview values must not be hidden by card overflow or ellipsis.').toEqual([])

  const rightEdges = await page.evaluate(() => {
    const tokenCard = document.querySelector('.token-card')?.getBoundingClientRect()
    const lastMetric = document.querySelector('.metric:last-child')?.getBoundingClientRect()
    const valueCard = document.querySelector('.value-card')?.getBoundingClientRect()
    const lastBreakdown = document.querySelector('.value-breakdown > span:last-child')?.getBoundingClientRect()
    return {
      metricInside: Boolean(tokenCard && lastMetric && lastMetric.right <= tokenCard.right + 1),
      // Value details are collapsed by default. Absence is expected; when disclosed,
      // their final card must still stay inside the value container.
      breakdownInside: Boolean(valueCard && (!lastBreakdown || lastBreakdown.right <= valueCard.right + 1)),
    }
  })
  expect(rightEdges).toEqual({ metricInside: true, breakdownInside: true })
}

async function scrollTargetIntoView(page: Page, selector: string) {
  const target = page.locator(selector)
  await target.evaluate((element) => element.scrollIntoView({ block: 'start', inline: 'nearest' }))
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur())
  await settleUi(page)
  await expect(target).toBeVisible()

  const bounds = await target.evaluate((element) => {
    const rect = element.getBoundingClientRect()
    const visibleHeight = Math.max(0, Math.min(rect.bottom, window.innerHeight) - Math.max(rect.top, 0))
    return { top: rect.top, bottom: rect.bottom, height: rect.height, visibleHeight, viewportHeight: window.innerHeight }
  })
  expect(bounds.top, `${selector} should intersect the visible scroll frame.`).toBeLessThan(bounds.viewportHeight)
  expect(bounds.bottom, `${selector} should intersect the visible scroll frame.`).toBeGreaterThan(0)
  expect(bounds.visibleHeight, `${selector} should have a meaningful visible region.`)
    .toBeGreaterThanOrEqual(Math.min(180, bounds.height * .45))
}

async function scrollTargetToViewportOffset(page: Page, selector: string, offset: number) {
  const target = page.locator(selector)
  await page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur())
  for (let attempt = 0; attempt < 2; attempt++) {
    await target.evaluate((element, targetOffset) => {
      const scrollFrame = element.closest('.viewport-frame') as HTMLElement | null
      if (!scrollFrame) throw new Error('Missing .viewport-frame scroll container.')
      const currentOffset = element.getBoundingClientRect().top - scrollFrame.getBoundingClientRect().top
      scrollFrame.scrollTop += Math.round(currentOffset - targetOffset)
    }, offset)
    await settleUi(page)
  }
  await expect(target).toBeVisible()
  await expect.poll(() => target.evaluate((element) => {
    const scrollFrame = element.closest('.viewport-frame')
    if (!scrollFrame) return Number.NaN
    return Math.round(element.getBoundingClientRect().top - scrollFrame.getBoundingClientRect().top)
  })).toBe(offset)
}

test.beforeEach(async ({ page }, testInfo) => {
  await openDemo(page, testInfo)
})

test('dashboard overview matches its visual baseline', async ({ page }) => {
  await expect(page).toHaveScreenshot('dashboard-overview.png', { fullPage: true })

  const overflow = await page.evaluate(() => ({
    horizontal: document.documentElement.scrollWidth - document.documentElement.clientWidth,
    vertical: document.documentElement.scrollHeight - document.documentElement.clientHeight,
  }))
  expect(overflow.horizontal).toBeLessThanOrEqual(1)
  expect(overflow.vertical).toBeGreaterThanOrEqual(0)
  await expectOverviewToFit(page)
})

test('quota risk leads the overview and value details are disclosed on demand', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers the overview interaction semantics.')

  const risk = page.getByRole('alert').filter({ hasText: '额度可能在重置前耗尽' })
  await expect(risk).toContainText('5 小时额度')
  await expect(risk).toContainText('预计')

  const toggle = page.getByRole('button', { name: '展开明细' })
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  await expect(page.locator('.value-details')).toHaveCount(0)
  await toggle.click()
  await expect(page.getByRole('button', { name: '收起明细' })).toHaveAttribute('aria-expanded', 'true')
  await expect(page.locator('.value-details')).toBeVisible()
})

test('primary detail tabs match their dark theme visual baselines', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers each primary detail tab.')

  const tabs = [
    { id: 'todos' as const, panel: '#panel-todos', snapshot: 'todos.png' },
    { id: 'usage' as const, panel: '#panel-usage', snapshot: 'usage-trend.png' },
    { id: 'projects' as const, panel: '#panel-projects', snapshot: 'projects.png' },
    { id: 'skills' as const, panel: '#panel-skills', snapshot: 'skills.png' },
    { id: 'combined' as const, panel: '#panel-combined', snapshot: 'combined-runtime.png' },
  ]

  for (const tab of tabs) {
    await openTab(page, tab.id)
    await scrollTargetIntoView(page, tab.panel)
    await expect(page).toHaveScreenshot(tab.snapshot, { maxDiffPixelRatio: 0.0005 })
  }
})

test('claude usage tab states what that runtime does not record', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers the Claude-side usage pane.')

  // Claude records no task lifecycle and keeps no index. The card used to render
  // those as "0 启动 / 0 完成 / 0 中止" and "0 复用 · 0 续读" beside real token charts,
  // which reads as a measurement of activity nobody measured.
  await page.getByRole('button', { name: 'Claude Code' }).click()
  await openTab(page, 'usage')
  await scrollTargetIntoView(page, '.lifecycle-card')

  await expect(page.locator('.lifecycle-unavailable')).toBeVisible()
  await expect(page.locator('.lifecycle-grid')).toHaveCount(0)
  await expect(page).toHaveScreenshot('usage-trend-claude.png', { maxDiffPixelRatio: 0.0005 })
})

test('combined runtime totals and merged project ranking match their baseline', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers the combined view below the fold.')

  // The per-tab capture above only reaches the two runtime columns. The derived
  // cards — combined totals, combined cost, earliest-exhausting window and the merged
  // project ranking — sit below it, and the merge rules they render are the riskiest
  // part of this view.
  await openTab(page, 'combined')
  await scrollTargetIntoView(page, '.combined-table')
  await expect(page).toHaveScreenshot('combined-runtime-totals.png', { maxDiffPixelRatio: 0.0005 })
})

test('credit details popover remains visible in the three-column layout', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers popover clipping in the wide three-column layout.')

  await page.setViewportSize({ width: 1600, height: 900 })
  await openDemo(page, testInfo)
  await page.locator('.credit-info').focus()
  const popover = page.locator('.credit-popover')
  await expect(popover).toHaveCSS('visibility', 'visible')
  await expect(popover).toHaveCSS('opacity', '1')

  const visibility = await popover.evaluate((element) => {
    const rect = element.getBoundingClientRect()
    const samplePoints = [
      [rect.left + 24, rect.top + 24],
      [rect.right - 24, rect.top + 24],
      [rect.left + 24, rect.bottom - 24],
      [rect.right - 24, rect.bottom - 24],
    ]
    return {
      left: rect.left,
      right: rect.right,
      top: rect.top,
      bottom: rect.bottom,
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight,
      visibleAtEdges: samplePoints.every(([x, y]) =>
        document.elementFromPoint(x, y)?.closest('.credit-popover') === element),
    }
  })
  expect(visibility.left).toBeGreaterThanOrEqual(0)
  expect(visibility.right).toBeLessThanOrEqual(visibility.viewportWidth)
  expect(visibility.top).toBeGreaterThanOrEqual(0)
  expect(visibility.bottom).toBeLessThanOrEqual(visibility.viewportHeight)
  expect(visibility.visibleAtEdges, 'Popover edges must not be clipped or covered by sibling cards.').toBe(true)
})

test('settings and diagnostics match their visual baseline', async ({ page }) => {
  await openTab(page, 'diagnostics')
  await scrollTargetIntoView(page, '#panel-diagnostics')
  await expect(page).toHaveScreenshot('settings-diagnostics.png', { maxDiffPixelRatio: 0.0005 })
})

test('application settings and versioned rate editor match their visual baseline', async ({ page }, testInfo) => {
  test.skip(!['chromium-dark-100', 'chromium-light-125'].includes(testInfo.project.name),
    'One project per theme covers the long settings surface.')
  await openTab(page, 'diagnostics')

  await scrollTargetIntoView(page, '.settings-card')
  await expect(page).toHaveScreenshot('application-settings.png', { maxDiffPixelRatio: 0.0005 })

  await page.locator('.rate-editor > summary').click()
  await page.getByRole('button', { name: '添加费率版本' }).click()
  await scrollTargetIntoView(page, '.rate-editor')
  await expect(page).toHaveScreenshot('versioned-rate-editor.png', { maxDiffPixelRatio: 0.0005 })
})

test('pinned rate snapshot is visible and immutable', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers the imported snapshot state.')
  await openDemo(page, testInfo, { pinnedRates: true })
  await openTab(page, 'diagnostics')
  await scrollTargetIntoView(page, '.rate-editor')

  await expect(page.getByText('锁定快照', { exact: true })).toBeVisible()
  await expect(page.getByText('archive-2026.01', { exact: true }).first()).toBeVisible()
  await expect(page.getByRole('button', { name: '添加费率版本' })).toBeDisabled()
  await expect(page.locator('.rate-row input, .rate-row select')).toHaveCount(16)
  await expect(page.locator('.rate-row input, .rate-row select').first()).toBeDisabled()
  await expect(page.getByRole('button', { name: '删除自定义费率' }).first()).toBeDisabled()
  // Scoped to the editor rather than the page: a pinned catalog is short, so the
  // scroll that brings it into view clamps against the bottom of the content, and any
  // row added to the settings grid above it reframes the whole capture. The element
  // shot pins what this test is actually about and cannot be moved by unrelated
  // settings work.
  await expect(page.locator('.rate-editor')).toHaveScreenshot('pinned-rate-snapshot.png', {
    mask: [page.locator('.rate-row input[type="date"]')],
    maskColor: '#151a28',
    maxDiffPixelRatio: 0.0005,
  })
})

test('compact dashboard matches its visual baseline', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100', 'One stable project covers compact layout.')
  await openDemo(page, testInfo, { compact: true })
  await expect(page).toHaveScreenshot('dashboard-compact.png', { fullPage: true })
})

test('140 percent UI scale fits minimum normal and compact windows', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100', 'One stable project covers application scaling.')

  await page.setViewportSize({ width: 1080, height: 768 })
  await openDemo(page, testInfo, { scale: 140 })
  await page.addStyleTag({ content: '*, *::before, *::after { animation: none !important; scroll-behavior: auto !important; transition: none !important; }' })
  await settleUi(page)
  await expectOverviewToFit(page)
  await expect(page).toHaveScreenshot('dashboard-scale-140.png')
  await page.getByRole('button', { name: '展开明细' }).click()
  await scrollTargetToViewportOffset(page, '.dashboard-section', 96)
  await expect(page).toHaveScreenshot('dashboard-scale-140-lower.png')

  await openTab(page, 'diagnostics')
  await page.locator('.rate-editor > summary').click()
  await page.getByRole('button', { name: '添加费率版本' }).click()
  await scrollTargetIntoView(page, '.rate-editor')
  const settingsOverflow = await page.evaluate(() => ({
    shell: (() => {
      const element = document.querySelector('.app-shell')
      return element ? element.scrollWidth - element.clientWidth : Number.POSITIVE_INFINITY
    })(),
    rateRows: [...document.querySelectorAll('.rate-row')]
      .map((element) => element.scrollWidth - element.clientWidth),
    overflowingDescendants: (() => {
      const shell = document.querySelector('.app-shell')
      if (!shell) return ['missing .app-shell']
      const shellBounds = shell.getBoundingClientRect()
      return [...shell.querySelectorAll('*')]
        .filter((element) => element.getBoundingClientRect().right > shellBounds.right + 1)
        .slice(0, 10)
        .map((element) => `${element.tagName.toLowerCase()}.${element.className}`)
    })(),
  }))
  expect(settingsOverflow.shell,
    `The scaled application shell must not overflow horizontally: ${settingsOverflow.overflowingDescendants.join(', ')}`)
    .toBeLessThanOrEqual(1)
  expect(settingsOverflow.rateRows.length, 'The versioned rate editor must contain a test row.')
    .toBeGreaterThan(0)
  expect(settingsOverflow.rateRows.every((overflow) => overflow <= 1),
    'Scaled rate rows must not overflow horizontally.').toBe(true)
  await scrollTargetToViewportOffset(page, '.rate-editor', 24)
  await expect(page).toHaveScreenshot('settings-rate-editor-scale-140.png', {
    mask: [page.locator('.rate-row input[type="date"]')],
    maskColor: '#151a28',
    maxDiffPixelRatio: 0.0005,
  })

  await page.setViewportSize({ width: 920, height: 540 })
  await openDemo(page, testInfo, { compact: true, scale: 140 })
  await expectOverviewToFit(page)
  await expect(page).toHaveScreenshot('dashboard-compact-scale-140.png')
  await scrollTargetIntoView(page, '.value-card')
  await expect(page).toHaveScreenshot('dashboard-compact-scale-140-lower.png')
})
