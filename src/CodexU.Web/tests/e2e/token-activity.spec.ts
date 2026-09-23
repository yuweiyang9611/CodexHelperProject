import { expect, test } from '@playwright/test'
import { openDemo, openTab } from './demo'

test('overview and analysis share local totals while account quota stays independent', async ({ page }, testInfo) => {
  await openDemo(page, testInfo)
  const initialQuota = await page.locator('.quota-rings').innerText()
  const initialTotal = await page.locator('.metric-0 strong').innerText()
  await expect(page.locator('.token-card')).toContainText('本机原始 Token')
  await openTab(page, 'usage')
  await expect(page.locator('.analysis-summary > div').first().locator('strong')).toHaveText(initialTotal)
  await page.getByRole('button', { name: '今日', exact: true }).click()
  await expect(page.locator('.analysis-summary')).toBeVisible()
  const dayTotal = await page.locator('.analysis-summary > div').first().locator('strong').innerText()
  await expect(page.locator('.daily-bars button')).toHaveCount(1)
  await openTab(page, 'overview')
  await expect(page.locator('.metric-0 strong')).toHaveText(dayTotal)
  await expect(page.locator('.quota-rings')).toHaveText(initialQuota, { useInnerText: true })
})

test('last selected runtime survives reopening without restoring task pages', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  await openDemo(page, testInfo)
  await page.getByRole('button', { name: 'Claude Code', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Claude Code', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.token-card')).toContainText('本机原始 Token')
  await page.reload()
  await expect(page.getByRole('button', { name: 'Claude Code', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByRole('tab')).toHaveText(['总览', '用量分析', '设置'])
  await expect(page.getByRole('button', { name: '近 30 天', exact: true })).toHaveAttribute('aria-pressed', 'true')
})
