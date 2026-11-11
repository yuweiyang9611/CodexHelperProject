import { createApp, nextTick, type App } from 'vue'
import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import DiagnosticsSettings from '../../src/views/DiagnosticsSettings.vue'
import { host } from '../../src/host'
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

function mountSettings(capabilities: string[], current = snapshot()) {
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
  const app = createApp(DiagnosticsSettings, { snapshot: current })
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
  it('only allows saving edited rates after the current draft was validated', async () => {
    let finish!: (response: unknown) => void
    const first = new Promise(resolve => { finish = resolve })
    const request = vi.spyOn(host, 'request')
      .mockReturnValueOnce(first)
      .mockResolvedValueOnce({ validationMessage: '日期冲突', canAdd: false })
    try {
      const { container, store } = mountSettings([])
      store.settingsDraft!.customModelRates = [{
        model: 'draft', inputCreditsPerMillion: 1, cachedInputCreditsPerMillion: 2, outputCreditsPerMillion: 3,
      }]
      await nextTick()
      expect(button(container, '保存设置').disabled).toBe(true)
      finish({ canAdd: true })
      await vi.waitFor(() => expect(container.querySelector('.setting-error')?.textContent).toBe('日期冲突'))
      expect(button(container, '保存设置').disabled).toBe(true)
      expect(request.mock.calls.at(-1)?.[0]).toBe('rates.previewDraft')
      expect(store.settings!.customModelRates).toHaveLength(0)
    } finally { request.mockRestore() }
  })

  it('can save unrelated edits when the rate preview host is unavailable', async () => {
    const request = vi.spyOn(host, 'request').mockRejectedValueOnce(new Error('offline'))
    try {
      const { container, store } = mountSettings([])
      store.settingsDraft!.theme = 'light'
      await vi.waitFor(() => expect(container.querySelector('.setting-error')?.textContent).toContain('预览失败'))
      expect(button(container, '保存设置').disabled).toBe(false)
    } finally { request.mockRestore() }
  })

  it('selects and checks a draft transcript before saving; cancel preserves the selection and result', async () => {
    const { container, store } = mountSettings([])
    const path = 'D:\\Fixture\\11111111-2222-4333-8444-555555555555.jsonl'
    const request = vi.spyOn(host, 'request')
      .mockResolvedValueOnce(path)
      .mockResolvedValueOnce({ status: 'tooLarge', message: '文件超过 32 MiB，请换用新的专用会话。' })
      .mockResolvedValueOnce(null)
    expect(button(container, '验证绑定').disabled).toBe(true)
    button(container, '选择文件').click()
    await vi.waitFor(() => expect(button(container, '验证绑定').disabled).toBe(false))
    expect(store.settingsDraft?.claudeDesktopUsageTranscript).toBe(path)
    button(container, '验证绑定').click()
    const feedback = container.querySelector('.binding-feedback')
    await vi.waitFor(() => expect(feedback?.textContent).toContain('新的专用会话'))
    expect(request.mock.calls[1]).toEqual(['settings.validateClaudeTranscript', { path }])
    button(container, '选择文件').click()
    await vi.waitFor(() => expect(button(container, '选择文件').disabled).toBe(false))
    expect(store.settingsDraft?.claudeDesktopUsageTranscript).toBe(path)
    expect(feedback?.textContent).toContain('新的专用会话')
    expect(store.settings?.claudeDesktopUsageTranscript).toBeFalsy()
    request.mockRestore()
  })

  it('drops a late validation result after editing the path', async () => {
    const { container, store } = mountSettings([])
    const firstPath = 'D:\\Fixture\\11111111-2222-4333-8444-555555555555.jsonl'
    store.settingsDraft!.claudeDesktopUsageTranscript = firstPath
    await nextTick()
    let finish!: (result: unknown) => void
    const request = vi.spyOn(host, 'request').mockReturnValueOnce(new Promise(resolve => { finish = resolve }))
    button(container, '验证绑定').click()
    store.settingsDraft!.claudeDesktopUsageTranscript = ''
    await nextTick()
    finish({ status: 'fresh', message: '已验证' })
    await vi.waitFor(() => expect(button(container, '选择文件').disabled).toBe(false))
    expect(container.querySelector('.binding-feedback')?.textContent).toBe('')
    expect(button(container, '验证绑定').disabled).toBe(true)
    request.mockRestore()
  })

  it('binds a Desktop usage transcript without changing a manual subscription or sending an update', async () => {
    const { container, store } = mountSettings([], snapshot({ runtime: 'claudeCode' }))
    store.settingsDraft!.claudeMonthlySubscriptionAmount = 100
    store.settingsDraft!.claudeAutoDetectSubscriptionAmount = false
    await nextTick()
    const bound = labelledControl(container, 'Claude Desktop 额度会话文件')
    bound.value = 'D:\\Fixture\\desktop-usage.jsonl'
    bound.dispatchEvent(new Event('input', { bubbles: true }))
    await nextTick()

    expect(store.settingsDraft?.claudeDesktopUsageTranscript).toBe('D:\\Fixture\\desktop-usage.jsonl')
    expect(store.settingsDraft?.claudeMonthlySubscriptionAmount).toBe(100)
    expect(store.settingsDraft?.claudeAutoDetectSubscriptionAmount).toBe(false)
    const instruction = container.querySelector<HTMLInputElement>('[aria-label="Desktop 额度更新指令"]')
    expect(instruction?.readOnly).toBe(true)
    expect(instruction?.value).toContain('mcp__ccd_session_mgmt__get_usage')
    expect(container.textContent).toContain('不会自动发送消息')
    expect(container.textContent).toContain('15 分钟')
  })

  it('explains that an unidentified Claude subscription needs an explicit manual monthly amount', async () => {
    const { container, store } = mountSettings([], snapshot({ runtime: 'claudeCode' }))
    const monthly = labelledControl(container, 'Claude Code 订阅月费')

    expect(monthly.closest('label')?.textContent).toContain('未识别订阅档位')
    expect(monthly.closest('label')?.textContent).toContain('填写月费会切换为手动金额')
    expect(monthly.closest('label')?.textContent).toContain('不代表实际账单')
    monthly.value = '100'
    monthly.dispatchEvent(new Event('input', { bubbles: true }))
    await nextTick()
    expect(store.settingsDraft?.claudeAutoDetectSubscriptionAmount).toBe(false)
    expect(store.settingsDraft?.claudeMonthlySubscriptionAmount).toBe(100)
  })

  it('restores desktop and rich status-strip controls while retaining history cleanup', async () => {
    const { container, store } = mountSettings([HOST_CAPABILITY.desktopMode, HOST_CAPABILITY.statusStripControl])
    expect(container.textContent).toContain('显示子代理任务')
    expect(labelledControl(container, '启动后置于桌面底层').disabled).toBe(false)
    expect(labelledControl(container, '状态条显示今日 Token').disabled).toBe(false)
    expect(labelledControl(container, '状态条额度口径').disabled).toBe(false)
    expect(container.textContent).toContain('不删除原始日志和设置')
    expect(container.textContent).toContain('下次刷新后会重新计入')
    const operation = vi.spyOn(store, 'runLocalOperation').mockResolvedValue(true)
    button(container, '清理已留存历史').click()
    expect(operation).toHaveBeenCalledWith('data.clearHistory')
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

    expect(container.querySelector('.capability-summary')?.textContent).toContain('系统额度通知、顶部状态条、桌面底层模式暂未接入')
    expect(labelledControl(container, '5h 提醒阈值').disabled).toBe(true)
    expect(labelledControl(container, '启用额度通知').disabled).toBe(true)
    expect(labelledControl(container, '状态条额度口径').disabled).toBe(true)
    expect(labelledControl(container, '状态条显示今日 Token').disabled).toBe(true)
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
    expect(labelledControl(container, '状态条显示今日 Token').disabled).toBe(false)
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
