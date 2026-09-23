import { createApp, nextTick, type App } from 'vue'
import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import DiagnosticsSettings from '../../src/views/DiagnosticsSettings.vue'
import { HOST_CAPABILITY } from '../../src/hostCapabilities'
import { useDashboardStore } from '../../src/stores/dashboard'
import { appSettings, snapshot } from './fixtures'

let pinia: Pinia
let mountedApps: App[]

beforeEach(() => {
  pinia = createPinia()
  setActivePinia(pinia)
  mountedApps = []
  localStorage.clear()
})

afterEach(() => {
  mountedApps.forEach((app) => app.unmount())
  document.body.replaceChildren()
  useDashboardStore(pinia).$dispose()
})

function mountSettings(capabilities: string[]) {
  const store = useDashboardStore(pinia)
  const saved = appSettings({
    notificationsEnabled: true,
    quotaForecastAlertsEnabled: true,
    statusStripEnabled: true,
    statusStripPositionLocked: true,
    desktopMode: true,
    startAtLogin: true,
    closeToTray: true,
  })
  store.settings = saved
  store.settingsDraft = appSettings({ ...saved })
  store.hostCapabilities = [...capabilities]

  const container = document.createElement('div')
  document.body.append(container)
  const app = createApp(DiagnosticsSettings, { snapshot: snapshot() })
  app.use(pinia)
  app.mount(container)
  mountedApps.push(app)
  return { container, store }
}

function labelledControl(container: ParentNode, text: string): HTMLInputElement | HTMLSelectElement {
  const label = [...container.querySelectorAll('label')]
    .find((candidate) => candidate.textContent?.includes(text))
  const control = label?.querySelector<HTMLInputElement | HTMLSelectElement>('input, select')
  if (!control) throw new Error(`Missing control labelled ${text}`)
  return control
}

function button(container: ParentNode, text: string): HTMLButtonElement {
  const match = [...container.querySelectorAll<HTMLButtonElement>('button')]
    .find((candidate) => candidate.textContent?.includes(text))
  if (!match) throw new Error(`Missing button ${text}`)
  return match
}

describe('desktop host capabilities', () => {
  it('removes retired task and desktop controls, and explains history cleanup', async () => {
    const { container, store } = mountSettings([HOST_CAPABILITY.desktopMode, HOST_CAPABILITY.statusStripControl])
    expect(container.textContent).not.toContain('显示子代理任务')
    expect(container.textContent).not.toContain('桌面仪表盘')
    expect(container.textContent).not.toContain('状态条显示今日 Token')
    expect(container.textContent).toContain('不删除原始日志和设置')
    expect(container.textContent).toContain('下次刷新后会重新计入')
    const operation = vi.spyOn(store, 'runLocalOperation').mockResolvedValue(true)
    button(container, '清理已留存历史').click()
    expect(operation).toHaveBeenCalledWith('data.clearHistory')
  })

  it('keeps subscription comparison optional and remembers the choice', async () => {
    const { container } = mountSettings([])
    const control = labelledControl(container, '显示订阅月费对比') as HTMLInputElement
    expect(control.checked).toBe(false)
    control.click()
    await nextTick()
    expect(localStorage.getItem('codexu.subscriptionComparison')).toBe('true')
  })

  it('shows download progress, retry and restart actions as update state changes', async () => {
    const { container, store } = mountSettings([HOST_CAPABILITY.automaticUpdates])
    store.updateStatus = { currentVersion: '0.6.0', latestVersion: '0.7.0', isUpdateAvailable: true, isPrerelease: false, checkedAt: new Date().toISOString(), status: '发现新版本' }
    store.updateState = { supported: true, phase: 'downloading', message: '正在下载', progress: 42 }
    await nextTick()
    expect(container.querySelector('progress')?.value).toBe(42)
    expect(button(container, '下载更新').disabled).toBe(true)
    expect(labelledControl(container, '自动下载并在退出时安装更新')).toBeTruthy()
    store.updateState = { supported: true, phase: 'error', message: '校验失败' }
    await nextTick()
    expect(button(container, '重试下载').disabled).toBe(false)
    store.updateState = { supported: true, phase: 'ready', message: '已就绪' }
    store.isRunningLocalOperation = true
    await nextTick()
    expect(button(container, '重启并更新').disabled).toBe(true)
    store.isRunningLocalOperation = false
    await nextTick()
    expect(button(container, '重启并更新').disabled).toBe(false)
  })

  it('keeps portable hosts on the manual release page path', async () => {
    const { container, store } = mountSettings([HOST_CAPABILITY.automaticUpdates])
    store.updateState = { supported: false, phase: 'idle', message: '便携版请手动更新' }
    await nextTick()
    expect(container.textContent).toContain('便携版请手动更新')
    expect(container.textContent).not.toContain('自动下载并在退出时安装更新')
    expect(button(container, '打开发布页').disabled).toBe(false)
  })
  it('disables unsupported Electron settings without rewriting saved values', async () => {
    const { container, store } = mountSettings([
      HOST_CAPABILITY.nativeDialogs,
      HOST_CAPABILITY.tray,
      HOST_CAPABILITY.globalHotKey,
      HOST_CAPABILITY.startupRegistration,
    ])
    await nextTick()

    expect(container.querySelector('.capability-summary')?.textContent).toContain('系统额度通知、顶部状态条暂未接入')
    expect(labelledControl(container, '5h 提醒阈值').disabled).toBe(true)
    expect(labelledControl(container, '启用额度通知').disabled).toBe(true)
    expect(labelledControl(container, '启用顶部状态条').disabled).toBe(true)
    expect(labelledControl(container, '锁定状态条位置').disabled).toBe(true)
    expect(button(container, '立即预览').disabled).toBe(true)
    expect(button(container, '找回状态条').disabled).toBe(true)

    expect(labelledControl(container, '全局快捷键').disabled).toBe(false)
    expect(labelledControl(container, '开机自动启动').disabled).toBe(false)
    expect(labelledControl(container, '关闭主窗口时隐藏到托盘').disabled).toBe(false)
    expect(labelledControl(container, '启用额度通知').getAttribute('aria-describedby')).toBe('native-notifications-capability-note')
    expect(container.querySelector('#native-notifications-capability-note')).not.toBeNull()
    expect(labelledControl(container, '启用顶部状态条').getAttribute('aria-describedby')).toBe('desktop-capability-note')
    expect(container.querySelector('#desktop-capability-note')).not.toBeNull()
    expect(store.settingsDraft?.notificationsEnabled).toBe(true)
    expect(store.settingsDraft?.statusStripEnabled).toBe(true)
    expect(store.settingsDraft?.desktopMode).toBe(true)
    expect(store.settingsDirty).toBe(false)
  })

  it('enables each setting only when the host advertises its capability', async () => {
    const { container } = mountSettings([
      HOST_CAPABILITY.nativeNotifications,
      HOST_CAPABILITY.statusStripControl,
      HOST_CAPABILITY.desktopMode,
      HOST_CAPABILITY.tray,
      HOST_CAPABILITY.globalHotKey,
      HOST_CAPABILITY.startupRegistration,
    ])
    await nextTick()

    expect(container.querySelector('.capability-summary')).toBeNull()
    expect(labelledControl(container, '启用额度通知').disabled).toBe(false)
    expect(labelledControl(container, '启用顶部状态条').disabled).toBe(false)
    expect(button(container, '立即预览').disabled).toBe(false)
    expect(button(container, '找回状态条').disabled).toBe(false)
  })

  it('also gates platform-specific startup, tray and hot-key controls', async () => {
    const { container } = mountSettings([])
    await nextTick()

    expect(labelledControl(container, '全局快捷键').disabled).toBe(true)
    expect(labelledControl(container, '开机自动启动').disabled).toBe(true)
    expect(labelledControl(container, '关闭主窗口时隐藏到托盘').disabled).toBe(true)
    expect(container.querySelector('#desktop-capability-note')?.textContent).toContain('全局快捷键')
  })
})
