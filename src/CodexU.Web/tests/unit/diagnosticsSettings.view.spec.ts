import { createApp, nextTick, type App } from 'vue'
import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
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
  it('disables unsupported Electron settings without rewriting saved values', async () => {
    const { container, store } = mountSettings([
      HOST_CAPABILITY.nativeDialogs,
      HOST_CAPABILITY.tray,
      HOST_CAPABILITY.globalHotKey,
      HOST_CAPABILITY.startupRegistration,
    ])
    await nextTick()

    expect(container.querySelector('.capability-summary')?.textContent).toContain('系统额度通知、顶部状态条、桌面底层模式暂未接入')
    expect(labelledControl(container, '5h 提醒阈值').disabled).toBe(true)
    expect(labelledControl(container, '启用额度通知').disabled).toBe(true)
    expect(labelledControl(container, '状态条额度口径').disabled).toBe(true)
    expect(labelledControl(container, '启用顶部状态条').disabled).toBe(true)
    expect(labelledControl(container, '锁定状态条位置').disabled).toBe(true)
    expect(labelledControl(container, '启动后置于桌面底层').disabled).toBe(true)
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
    expect(labelledControl(container, '状态条额度口径').disabled).toBe(false)
    expect(labelledControl(container, '启用顶部状态条').disabled).toBe(false)
    expect(labelledControl(container, '启动后置于桌面底层').disabled).toBe(false)
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
