import { expect, test } from '@playwright/test'
import { openDemo, openTab } from './demo'

async function openRateEditor(page: import('@playwright/test').Page) {
  const editor = page.locator('.rate-editor')
  await expect(editor).not.toHaveAttribute('open', '')
  await editor.locator('summary').click()
  await expect(editor).toHaveAttribute('open', '')
}

test('settings announce local operations and reject ambiguous or invalid rates', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers dynamic settings behavior.')

  await openDemo(page, testInfo)
  await openTab(page, 'diagnostics')

  const operationStatus = page.locator('.maintenance-card [role="status"]')
  await expect(operationStatus).toHaveAttribute('aria-live', 'polite')
  await page.getByRole('button', { name: '导出 JSON 统计' }).click()
  await expect(operationStatus).toContainText('操作已模拟')

  await openRateEditor(page)
  const addRate = page.getByRole('button', { name: '添加费率版本' })
  await addRate.click()
  await addRate.click()
  const rows = page.locator('.rate-row')
  await rows.nth(0).locator('input').first().fill('gpt-5.2')
  await rows.nth(1).locator('input').first().fill('gpt-5.2-codex-latest')

  const validation = page.locator('.setting-error')
  const save = page.getByRole('button', { name: '保存设置', exact: true })
  await expect(validation).toContainText('同一生效日期存在重复费率')
  await expect(save).toBeDisabled()

  await rows.nth(1).locator('input').first().fill('gpt-5.3-codex')
  await rows.nth(0).locator('input[type="number"]').first().fill('')
  await expect(validation).toContainText('有效数值')
  await expect(save).toBeDisabled()
})

test('settings save sends and merges a field patch', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers the settings patch protocol.')

  await openDemo(page, testInfo)
  await openTab(page, 'diagnostics')

  const refresh = page.getByLabel('自动刷新（分钟）')
  const scale = page.getByLabel('界面缩放 %')
  await expect(refresh).toHaveValue('5')
  const save = page.getByRole('button', { name: '保存设置', exact: true })
  await scale.fill('145')
  await save.click()
  await refresh.fill('7')

  await expect(scale).toHaveValue('140')
  await expect(refresh).toHaveValue('7')
  await expect(save).toBeEnabled()

  await save.click()
  await expect(refresh).toHaveValue('7')
  await expect(save).toBeDisabled()
})

test('blank rate effective date is normalized without leaving a dirty draft', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers host normalization of an optional rate date.')

  await openDemo(page, testInfo)
  await openTab(page, 'diagnostics')

  await openRateEditor(page)
  await page.getByRole('button', { name: '添加费率版本' }).click()
  const rateRow = page.locator('.rate-row').last()
  // combobox, not textbox: the model field carries a `list` of the built-in
  // catalog's models, which changes the input's implicit ARIA role.
  await rateRow.getByRole('combobox', { name: '模型', exact: true }).fill('custom-model')
  const effectiveDate = rateRow.getByLabel('生效日期', { exact: true })
  await effectiveDate.fill('')

  const save = page.getByRole('button', { name: '保存设置', exact: true })
  await save.click()

  await expect(effectiveDate).toHaveValue('')
  await expect(save).toBeDisabled()
})

test('settings are grouped, advanced rates start collapsed, and the action bar reports dirty state', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers the grouped settings interaction.')

  await openDemo(page, testInfo)
  await openTab(page, 'diagnostics')

  await expect(page.locator('.settings-group > legend')).toHaveText([
    '常规', '数据源', '通知与额度', '桌面行为', '价格与费率',
  ])
  await expect(page.locator('.rate-editor')).not.toHaveAttribute('open', '')
  await expect(page.locator('.settings-action-bar')).toHaveCSS('position', 'sticky')
  await expect(page.locator('.settings-action-bar [role="status"]')).toContainText('所有更改已保存')

  await page.getByLabel('自动刷新（分钟）').fill('8')
  await expect(page.locator('.settings-action-bar [role="status"]')).toContainText('有未保存更改')
  await expect(page.getByRole('button', { name: '保存设置', exact: true })).toBeEnabled()
})

test('status strip preview and recovery do not silently save the settings draft', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers status strip controls.')

  await openDemo(page, testInfo)
  await openTab(page, 'diagnostics')

  const enable = page.getByLabel('启用顶部状态条')
  const lock = page.getByLabel('锁定状态条位置')
  const save = page.getByRole('button', { name: '保存设置', exact: true })
  await expect(enable).not.toBeChecked()
  await lock.check()
  await expect(save).toBeEnabled()

  await page.getByRole('button', { name: '立即预览' }).click()
  const stripStatus = page.locator('.status-strip-control [role="status"]')
  await expect(stripStatus).toContainText('不会保存当前草稿')
  await expect(enable).not.toBeChecked()
  await expect(lock).toBeChecked()
  await expect(save).toBeEnabled()

  await page.getByRole('button', { name: '找回状态条' }).click()
  await expect(stripStatus).toContainText('启用后才会常驻')
  await expect(enable).not.toBeChecked()
  await expect(save).toBeEnabled()
})

test('narrow windows reflow without overriding the selected UI scale', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable project covers narrow responsive layout.')

  await page.setViewportSize({ width: 480, height: 800 })
  await openDemo(page, testInfo, { scale: 140 })

  const layout = await page.evaluate(() => {
    const shell = document.querySelector<HTMLElement>('.app-shell')!
    const overview = document.querySelector<HTMLElement>('.overview-grid')!
    return {
      zoom: getComputedStyle(shell).zoom,
      overviewColumns: getComputedStyle(overview).gridTemplateColumns.split(' ').length,
      frameOverflow: document.querySelector<HTMLElement>('.viewport-frame')!.scrollWidth
        - document.querySelector<HTMLElement>('.viewport-frame')!.clientWidth,
    }
  })
  expect(layout.zoom).toBe('1.4')
  expect(layout.overviewColumns).toBe(1)
  expect(layout.frameOverflow).toBeLessThanOrEqual(1)

  await openTab(page, 'diagnostics')
  await expect(page.locator('.settings-groups')).toHaveCSS('grid-template-columns', /\d+(?:\.\d+)?px/)
  await expect(page.getByLabel('关闭主窗口时隐藏到托盘')).toBeVisible()
})
