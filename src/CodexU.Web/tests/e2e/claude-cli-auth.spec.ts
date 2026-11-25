import { expect, test } from '@playwright/test'
import { openDemo, openTab } from './demo'

test('Claude CLI login check is available from either runtime without changing settings', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100')
  await openDemo(page, testInfo)
  await openTab(page, 'diagnostics')
  const status = page.getByRole('region', { name: 'Claude CLI 登录状态', exact: true })
  await expect(status.getByRole('status')).toHaveText('未找到 CLI')
  await expect(status.getByText('此处显示 CLI 自身的登录状态。Desktop 登录与 CLI 登录分别检查。')).toBeVisible()
  const before = await page.getByLabel(/Claude Code 订阅月费/).inputValue()
  await status.getByRole('button', { name: '重新检查登录状态' }).click()
  await expect(status.getByRole('status')).toHaveText('未找到 CLI')
  await expect(page.getByLabel(/Claude Code 订阅月费/)).toHaveValue(before)
  await expect(page.getByRole('button', { name: '保存设置', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: 'Claude Code', exact: true }).click()
  await expect(status.getByRole('status')).toHaveText('未找到 CLI')
  await expect(page.getByRole('button', { name: '保存设置', exact: true })).toBeDisabled()
})
