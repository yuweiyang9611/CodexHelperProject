import { expect, test } from '@playwright/test'
import { openDemo, openTab } from './demo'

test('todo editing, reversible deletion, and counted cleanup work from the keyboard', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'chromium-dark-100',
    'One stable browser project covers the stateful todo interaction flow.')

  await openDemo(page, testInfo)
  await openTab(page, 'todos')

  await page.getByLabel('待办内容').fill('整理交互测试')
  await page.getByRole('button', { name: '添加', exact: true }).click()
  await expect(page.getByRole('status')).toContainText('已添加“整理交互测试”')
  // The browser-only demo bridge keeps its empty array by reference on add. Remounting
  // reads that array again; native host responses are fresh and render immediately.
  await openTab(page, 'usage')
  await openTab(page, 'todos')
  let row = page.locator('.todo-row').filter({ hasText: '整理交互测试' })
  await expect(row).toBeVisible()

  const editButton = row.getByRole('button', { name: '编辑' })
  await editButton.click()
  let editInput = page.getByRole('textbox', { name: '编辑待办：整理交互测试' })
  await expect(editInput).toBeFocused()
  await editInput.press('Escape')
  await expect(editInput).toHaveCount(0)
  await expect(row.getByRole('button', { name: '编辑' })).toBeFocused()

  await row.getByRole('button', { name: '编辑' }).click()
  editInput = page.getByRole('textbox', { name: '编辑待办：整理交互测试' })
  await editInput.fill('整理并验证交互')
  await editInput.press('Enter')
  row = page.locator('.todo-row').filter({ hasText: '整理并验证交互' })
  await expect(row).toBeVisible()
  await expect(row.getByRole('button', { name: '编辑' })).toBeFocused()

  await row.getByRole('button', { name: '删除' }).click()
  await expect(row).toHaveCount(0)
  await expect(page.getByText('已暂时移除“整理并验证交互”')).toBeVisible()

  // The panel uses v-if and is destroyed on a tab switch. The undo entry must live
  // in Pinia long enough to reappear when the panel mounts again.
  await openTab(page, 'usage')
  await openTab(page, 'todos')
  await page.getByRole('button', { name: '撤销（5 秒）' }).click()
  row = page.locator('.todo-row').filter({ hasText: '整理并验证交互' })
  await expect(row).toBeVisible()

  await page.getByRole('button', { name: '全部', exact: true }).click()
  await row.getByRole('button', { name: '标记 整理并验证交互 为已完成' }).click()
  await expect(row).toHaveClass(/done/)

  const clearButton = page.getByRole('button', { name: '清理已完成（1）' })
  await clearButton.click()
  const confirmation = page.getByRole('alertdialog')
  await expect(confirmation).toContainText('确认清理 1 项待办？')
  await expect(confirmation.getByRole('button', { name: '确认清理' })).toBeFocused()
  await confirmation.press('Escape')
  await expect(confirmation).toHaveCount(0)
  await expect(clearButton).toBeFocused()

  await clearButton.click()
  await page.getByRole('alertdialog').getByRole('button', { name: '确认清理' }).click()
  await expect(row).toHaveCount(0)
  await expect(page.getByRole('status')).toContainText('已清理 1 项已完成待办')
})
