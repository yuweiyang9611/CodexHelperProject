import { expect, test } from '@playwright/test'
import { openDemo } from './demo'

test('primary navigation is reachable and operable with the keyboard', async ({ page }, testInfo) => {
  test.skip(!['chromium-dark-100', 'chromium-light-125'].includes(testInfo.project.name),
    'One project per theme covers the keyboard flow.')
  await openDemo(page, testInfo)

  await page.keyboard.press('Tab')
  const skipLink = page.getByRole('link', { name: '跳转到主内容' })
  await expect(skipLink).toBeFocused()
  await expect(skipLink).toBeInViewport()
  await page.keyboard.press('Enter')
  await expect(page.locator('#dashboard-content')).toBeFocused()

  await page.keyboard.press('Tab')
  await expect(page.getByRole('button', { name: /Codex$/ })).toBeFocused()
  await expect(page.getByRole('button', { name: /Codex$/ })).toHaveCSS('outline-style', 'solid')
  if (testInfo.project.use.colorScheme === 'light') {
    await expect(page.getByRole('button', { name: /Codex$/ })).toHaveCSS('outline-color', 'rgb(36, 84, 159)')
  }

  await page.keyboard.press('Tab')
  await expect(page.getByRole('button', { name: /Claude Code/ })).toBeFocused()

  const compactToggle = page.getByRole('button', { name: '切换紧凑模式' })
  await compactToggle.focus()
  await expect(compactToggle).toHaveAttribute('aria-pressed', 'false')
  await page.keyboard.press('Space')
  await expect(compactToggle).toHaveAttribute('aria-pressed', 'true')
  await expect(compactToggle).toHaveAttribute('aria-disabled', 'false')
  await expect(compactToggle).toBeFocused()
  await page.keyboard.press('Space')
  await expect(compactToggle).toHaveAttribute('aria-pressed', 'false')
  await expect(compactToggle).toHaveAttribute('aria-disabled', 'false')
  await expect(compactToggle).toBeFocused()

  const todosTab = page.getByRole('tab').nth(1)
  await todosTab.focus()
  await page.keyboard.press('Enter')
  await expect(todosTab).toHaveAttribute('aria-selected', 'true')

  const openFilter = page.getByRole('button', { name: '未完成', exact: true })
  const todayFilter = page.getByRole('button', { name: '今天', exact: true })
  await expect(openFilter).toHaveAttribute('aria-pressed', 'true')
  await expect(todayFilter).toHaveAttribute('aria-pressed', 'false')
  await todayFilter.focus()
  await page.keyboard.press('Space')
  await expect(todayFilter).toHaveAttribute('aria-pressed', 'true')
  await expect(openFilter).toHaveAttribute('aria-pressed', 'false')

  // This block is about End and the ArrowLeft/ArrowRight wrap, so the locator is
  // "the last tab" rather than a fixed position. The .settings-card assertion below
  // is what pins down which panel that actually is.
  const settingsTab = page.getByRole('tab').last()
  await settingsTab.focus()
  await expect(settingsTab).toHaveCSS('outline-style', 'solid')
  await page.keyboard.press('ArrowRight')
  await expect(page.getByRole('tab').first()).toBeFocused()
  await expect(page.getByRole('tab').first()).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('End')
  await expect(settingsTab).toBeFocused()
  await expect(settingsTab).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('Home')
  await expect(page.getByRole('tab').first()).toBeFocused()
  await page.keyboard.press('ArrowLeft')
  await expect(settingsTab).toBeFocused()
  await expect(settingsTab).toHaveAttribute('aria-selected', 'true')
  await expect(page.locator('.settings-card')).toBeVisible()

  const firstSetting = page.locator('.settings-grid input').first()
  await firstSetting.focus()
  await expect(firstSetting).toHaveCSS('outline-style', 'solid')
  await page.keyboard.press('Tab')
  await expect(page.locator('.settings-grid input').nth(1)).toBeFocused()
})
