import { expect, test } from '@playwright/test'
import { openAntigravity, openDemo, openTab } from './demo'

test('Antigravity quota keeps each group and distinguishes exhausted, missing and disabled windows', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  await openDemo(page, testInfo)
  await page.getByRole('button', { name: 'Claude Code', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Claude Code', exact: true })).toHaveAttribute('aria-pressed', 'true')

  const panel = await openAntigravity(page)
  await expect(page.getByRole('button', { name: 'Claude Code', exact: true })).toHaveAttribute('aria-pressed', 'false')
  await expect(page.getByRole('tab')).toHaveCount(3)
  await expect(page.locator('#tab-antigravity')).toHaveAttribute('aria-selected', 'true')
  await expect(panel.getByRole('heading', { level: 3 })).toHaveText(['Gemini Pro', 'Gemini Flash', 'Claude 和 GPT', 'AI Credits'])
  await expect(panel.getByRole('img', { name: '5 小时 剩余 72.5%', exact: true })).toBeVisible()
  await expect(panel.getByRole('img', { name: '每周 剩余 0%', exact: true })).toBeVisible()
  await expect(panel.getByRole('img', { name: '每周 未启用', exact: true })).toBeVisible()
  await expect(panel.getByRole('img', { name: '5 小时 剩余额度未知', exact: true })).toBeVisible()
  await expect(panel.getByRole('img', { name: '余额 剩余 1,000', exact: true })).toBeVisible()
  await expect(panel.locator('time[datetime="2026-07-14T05:30:00.000Z"]')).toBeVisible()
  await expect(panel.getByText(/2 小时后重置/)).toBeVisible()

  const refresh = page.getByRole('button', { name: '刷新本机数据', exact: true })
  await refresh.focus()
  await expect(refresh).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(refresh).toBeDisabled()
  await expect(refresh).toBeEnabled()
  await expect(panel.getByRole('heading', { name: 'Gemini Pro', exact: true })).toBeVisible()

  await page.setViewportSize({ width: 620, height: 740 })
  const size = await panel.evaluate(element => ({ width: element.clientWidth, content: element.scrollWidth }))
  expect(size.content).toBeLessThanOrEqual(size.width)
  const screenshot = testInfo.outputPath('antigravity-quota-narrow.png')
  await panel.screenshot({ path: screenshot, animations: 'disabled' })
  await testInfo.attach('antigravity-quota-narrow', { path: screenshot, contentType: 'image/png' })

  await page.reload()
  await expect(page.getByRole('button', { name: 'Antigravity', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(panel.getByRole('heading', { name: 'Gemini Pro', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Claude Code', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Claude Code', exact: true })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.locator('.token-card')).toBeVisible()
})

test('三运行时同时显示三个来源并标明费用统计范围', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  await openDemo(page, testInfo)
  await openTab(page, 'combined')

  const panel = page.getByRole('tabpanel', { name: '三运行时', exact: true })
  const cards = panel.locator('.combined-runtime-column')
  await expect(cards.nth(0)).toContainText('Codex')
  await expect(cards.nth(1)).toContainText('Claude Code')
  await expect(cards.nth(2)).toContainText('Antigravity')
  await expect(cards.nth(2).getByRole('heading', { name: 'Gemini Pro', exact: true })).toBeVisible()
  await expect(panel.getByText('用量、费用、项目和耗尽预测仅统计 Codex 与 Claude Code。')).toBeVisible()
})
