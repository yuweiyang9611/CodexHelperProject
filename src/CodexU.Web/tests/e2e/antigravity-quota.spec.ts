import { expect, test } from '@playwright/test'
import { openDemo, openTab } from './demo'

test('Antigravity quota keeps each group and distinguishes exhausted, missing and disabled windows', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  await openDemo(page, testInfo)
  await page.getByRole('button', { name: 'Claude Code', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Claude Code', exact: true })).toHaveAttribute('aria-pressed', 'true')

  await openTab(page, 'antigravity')
  const panel = page.getByRole('tabpanel', { name: 'Antigravity', exact: true })
  await expect(panel.getByRole('heading', { level: 3 })).toHaveText(['Gemini Pro', 'Gemini Flash', 'Claude 和 GPT', 'AI Credits'])
  await expect(panel.getByText('剩余 72.5%')).toBeVisible()
  await expect(panel.getByText('剩余 0%', { exact: true })).toBeVisible()
  await expect(panel.getByText('未启用', { exact: true })).toBeVisible()
  await expect(panel.getByText('剩余额度未知', { exact: true })).toBeVisible()
  await expect(panel.getByText('剩余 1,000', { exact: true })).toBeVisible()
  await expect(panel.locator('time[datetime="2026-07-14T05:30:00.000Z"]')).toBeVisible()
  await expect(panel.getByText(/2 小时后重置/)).toBeVisible()

  const refresh = panel.getByRole('button', { name: '刷新额度', exact: true })
  await refresh.focus()
  await refresh.press('Enter')
  await expect(refresh).toHaveAttribute('aria-disabled', 'true')
  await expect(refresh).toBeEnabled()
  await expect(refresh).toBeFocused()
  await expect(page.getByRole('button', { name: 'Claude Code', exact: true })).toHaveAttribute('aria-pressed', 'true')

  await page.setViewportSize({ width: 620, height: 740 })
  const size = await panel.evaluate(element => ({ width: element.clientWidth, content: element.scrollWidth }))
  expect(size.content).toBeLessThanOrEqual(size.width)
  const screenshot = testInfo.outputPath('antigravity-quota-narrow.png')
  await panel.screenshot({ path: screenshot, animations: 'disabled' })
  await testInfo.attach('antigravity-quota-narrow', { path: screenshot, contentType: 'image/png' })
})
