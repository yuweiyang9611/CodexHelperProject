import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { openDemo, openTab, TAB_IDS } from './demo'

const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

test('all dashboard views satisfy WCAG A and AA automated checks', async ({ page }, testInfo) => {
  test.skip(!['chromium-dark-100', 'chromium-light-125'].includes(testInfo.project.name),
    'Axe is run once per theme; DPI-only projects are covered by visual tests.')

  await openDemo(page, testInfo)

  for (const id of TAB_IDS) {
    await openTab(page, id)
    if (id === 'diagnostics') {
      await page.locator('.rate-editor > summary').click()
      await page.getByRole('button', { name: '添加费率版本' }).click()
    }
    const results = await new AxeBuilder({ page })
      .include('.app-shell')
      .withTags(wcagTags)
      .analyze()

    expect(results.violations, `Axe violations in dashboard tab ${id}`).toEqual([])
  }
})

test('pinned rate snapshot satisfies WCAG A and AA automated checks', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers the imported snapshot state.')

  await openDemo(page, testInfo, { pinnedRates: true })
  await openTab(page, 'diagnostics')
  const results = await new AxeBuilder({ page })
    .include('.app-shell')
    .withTags(wcagTags)
    .analyze()

  expect(results.violations).toEqual([])
})
