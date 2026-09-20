import { expect, test } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'
import { openDemo, openTab } from './demo'

test('usage distribution supports periods, models, daily details and keyboard highlighting', async ({ page }, testInfo) => {
  test.skip(!['chromium-dark-100', 'chromium-light-125'].includes(testInfo.project.name))
  await openDemo(page, testInfo)
  await openTab(page, 'usage')
  const card = page.getByRole('article', { name: '用量分布', exact: true })
  await card.scrollIntoViewIfNeeded()
  await expect(card.locator('.distribution-day')).toHaveCount(7)
  await page.mouse.move(0, 0)
  await expect(card).toHaveScreenshot('usage-distribution-features.png')
  await card.getByRole('button', { name: '30 天', exact: true }).click()
  await expect(card.locator('.distribution-day')).toHaveCount(30)
  await card.getByRole('combobox', { name: '用量分组' }).selectOption('model')
  await expect(card.locator('.distribution-legend')).toContainText('gpt-6-astra')
  const model = card.locator('.distribution-legend button').first()
  await model.focus()
  await page.keyboard.press('Enter')
  await expect(model).toHaveAttribute('aria-pressed', 'true')
  await card.locator('.distribution-day').last().focus()
  await expect(card.locator('.distribution-tooltip')).toContainText('7月14日')
  await page.keyboard.press('Tab')
  await page.mouse.move(0, 0)
  const results = await new AxeBuilder({ page }).include('.distribution-card').analyze()
  expect(results.violations).toEqual([])
  await expect(card).toHaveScreenshot('usage-distribution.png')
})
