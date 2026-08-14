import { expect, test } from '@playwright/test'
import { openDemo, openTab } from './demo'

test('local JSONL periods drive token overview and usage charts', async ({ page }, testInfo) => {
  await openDemo(page, testInfo)

  const tokenCard = page.locator('.token-card')
  await expect(tokenCard).toContainText('本机原始统计')
  await expect(tokenCard).toContainText('今日')
  await expect(tokenCard).toContainText('3.84M')
  await expect(tokenCard).not.toContainText('官方账户统计')

  await openTab(page, 'usage')
  const usagePanel = page.locator('#panel-usage')
  await expect(usagePanel).toContainText('最近半年本机原始用量')
  await expect(usagePanel).toContainText('本机原始模型 Token 分布')
  const latestTooltip = await page.locator('.heat-day').last().getAttribute('title')
  expect(latestTooltip).toContain('2026-07-14')
  expect(latestTooltip).toContain('US$')
})
