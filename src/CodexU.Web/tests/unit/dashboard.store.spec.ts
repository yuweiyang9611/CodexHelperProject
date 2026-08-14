import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AppSettings, DashboardSnapshot } from '../../src/types'
import { appSettings, runtimeRead, snapshot } from './fixtures'

const mocks = vi.hoisted(() => ({
  request: vi.fn<(method: string, payload?: object) => Promise<unknown>>(),
  listeners: new Map<string, (payload: unknown) => void>(),
}))

vi.mock('../../src/host', () => ({
  host: {
    request: (method: string, payload?: object) => mocks.request(method, payload),
    on: (event: string, handler: (payload: unknown) => void) => {
      mocks.listeners.set(event, handler)
    },
  },
}))

const { useDashboardStore } = await import('../../src/stores/dashboard')

/** A promise whose settlement this test controls. */
function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

/** Route each IPC method to a canned result, defaulting to a benign value. */
function routeRequests(routes: Record<string, unknown | (() => unknown)>) {
  mocks.request.mockImplementation(async (method) => {
    if (method in routes) {
      const route = routes[method]
      return typeof route === 'function' ? (route as () => unknown)() : route
    }
    if (method === 'app.initialize') return { appVersion: '9.9.9', platform: 'test', theme: 'dark', mockData: false, capabilities: [] }
    if (method === 'todos.list') return []
    if (method === 'settings.get') return appSettings()
    if (method === 'rates.getCatalog') return { builtIn: {}, builtInRates: [] }
    if (method === 'statusStrip.getState') return {
      configuredEnabled: false,
      visible: false,
      positionLocked: false,
      hasManualPosition: false,
      positionMode: '跟随 Codex',
      displayName: '主显示器 · DISPLAY1',
      message: '状态条当前已关闭。',
    }
    return snapshot()
  })
}

function emit(event: string, payload: unknown) {
  const handler = mocks.listeners.get(event)
  if (!handler) throw new Error(`no listener bound for ${event}`)
  handler(payload)
}

beforeEach(() => {
  setActivePinia(createPinia())
  mocks.request.mockReset()
  mocks.listeners.clear()
})

describe('initialize', () => {
  it('loads every source and reports no error on success', async () => {
    const loaded = snapshot({ refreshedAt: '2026-07-14T00:00:00Z' })
    routeRequests({
      'usage.getSnapshot': loaded,
      'settings.get': appSettings({ checkForUpdates: false }),
      'todos.list': [{ id: 't1', text: '写测试', done: false, priority: 'normal', createdAt: '2026-07-14T00:00:00Z' }],
    })

    const store = useDashboardStore()
    await store.initialize()

    expect(store.error).toBeNull()
    expect(store.isLoading).toBe(false)
    expect(store.snapshot).toEqual(loaded)
    expect(store.todos).toHaveLength(1)
    expect(store.appVersion).toBe('9.9.9')
  })

  it('keeps the sources that succeeded and aggregates the ones that failed', async () => {
    routeRequests({
      'usage.getSnapshot': () => Promise.reject(new Error('读取用量失败')),
      'settings.get': appSettings({ checkForUpdates: false }),
      'todos.list': () => Promise.reject(new Error('待办损坏')),
    })

    const store = useDashboardStore()
    await store.initialize()

    // Settings still landed even though two siblings failed.
    expect(store.settings).not.toBeNull()
    expect(store.isLoading).toBe(false)
    expect(store.error).toContain('用量读取失败：读取用量失败')
    expect(store.error).toContain('待办读取失败：待办损坏')
  })

  it('does not let a failed host handshake abort the rest of the load', async () => {
    routeRequests({
      'app.initialize': () => Promise.reject(new Error('宿主未就绪')),
      'settings.get': appSettings({ checkForUpdates: false }),
    })

    const store = useDashboardStore()
    await store.initialize()

    expect(store.error).toContain('宿主初始化失败：宿主未就绪')
    expect(store.settings).not.toBeNull()
    expect(store.appVersion).toBe('development')
  })

  it('checks for updates only when the setting is on', async () => {
    routeRequests({ 'settings.get': appSettings({ checkForUpdates: false }) })
    const store = useDashboardStore()
    await store.initialize()
    expect(mocks.request).not.toHaveBeenCalledWith('update.check', expect.anything())

    setActivePinia(createPinia())
    mocks.request.mockClear()
    routeRequests({
      'settings.get': appSettings({ checkForUpdates: true }),
      'update.check': { currentVersion: '1', isUpdateAvailable: false, isPrerelease: false, checkedAt: '', status: 'ok' },
    })
    const enabled = useDashboardStore()
    await enabled.initialize()
    expect(mocks.request).toHaveBeenCalledWith('update.check', { force: false })
  })
})

describe('stale response guarding', () => {
  it('discards an in-flight refresh once a runtime switch supersedes it', async () => {
    const slowRefresh = deferred<DashboardSnapshot>()
    const runtimeSwitch = deferred<DashboardSnapshot>()
    mocks.request.mockImplementation(async (method) => {
      if (method === 'usage.refresh') return slowRefresh.promise
      if (method === 'runtime.select') return runtimeSwitch.promise
      return snapshot()
    })

    const store = useDashboardStore()
    const refreshing = store.refresh()
    const switching = store.selectRuntime('claudeCode')

    // The newer selection resolves first and wins.
    const selected = snapshot({ runtime: 'claudeCode', refreshedAt: '2026-07-14T02:00:00Z' })
    runtimeSwitch.resolve(selected)
    await switching
    expect(store.snapshot).toEqual(selected)

    // The superseded refresh must not clobber it.
    slowRefresh.resolve(snapshot({ runtime: 'codex', refreshedAt: '2026-07-14T01:00:00Z' }))
    await refreshing
    expect(store.snapshot).toEqual(selected)
    expect(store.runtime).toBe('claudeCode')
  })

  it('does not surface an error from a superseded refresh', async () => {
    const slowRefresh = deferred<DashboardSnapshot>()
    const runtimeSwitch = deferred<DashboardSnapshot>()
    mocks.request.mockImplementation(async (method) => {
      if (method === 'usage.refresh') return slowRefresh.promise
      if (method === 'runtime.select') return runtimeSwitch.promise
      return snapshot()
    })

    const store = useDashboardStore()
    const refreshing = store.refresh()
    const switching = store.selectRuntime('claudeCode')

    runtimeSwitch.resolve(snapshot({ runtime: 'claudeCode' }))
    await switching

    slowRefresh.reject(new Error('过期刷新失败'))
    await refreshing

    expect(store.error).toBeNull()
    expect(store.isRefreshing).toBe(false)
  })

  it('ignores a pushed snapshot for a runtime the user is switching away from', async () => {
    const runtimeSwitch = deferred<DashboardSnapshot>()
    mocks.request.mockImplementation(async (method) => {
      if (method === 'runtime.select') return runtimeSwitch.promise
      if (method === 'settings.get') return appSettings({ checkForUpdates: false })
      if (method === 'todos.list') return []
      if (method === 'app.initialize') return { appVersion: '1', platform: 'test', theme: 'dark', mockData: false, capabilities: [] }
      return snapshot()
    })

    const store = useDashboardStore()
    await store.initialize()
    const switching = store.selectRuntime('claudeCode')

    // A late push for the old runtime arrives mid-switch and must be dropped.
    emit('usage.snapshotChanged', snapshot({ runtime: 'codex', refreshedAt: '2026-07-14T03:00:00Z' }))
    expect(store.snapshot?.runtime).toBe('codex')
    expect(store.snapshot?.refreshedAt).not.toBe('2026-07-14T03:00:00Z')

    // A push for the runtime being switched to is accepted.
    emit('usage.snapshotChanged', snapshot({ runtime: 'claudeCode', refreshedAt: '2026-07-14T04:00:00Z' }))
    expect(store.snapshot?.runtime).toBe('claudeCode')
    expect(store.isRefreshing).toBe(false)

    runtimeSwitch.resolve(snapshot({ runtime: 'claudeCode' }))
    await switching
  })

  it('refuses to start a second refresh while one is running', async () => {
    const slowRefresh = deferred<DashboardSnapshot>()
    mocks.request.mockImplementation(async () => slowRefresh.promise)

    const store = useDashboardStore()
    const first = store.refresh()
    await store.refresh()

    expect(mocks.request).toHaveBeenCalledTimes(1)
    slowRefresh.resolve(snapshot())
    await first
  })

  it('skips a runtime selection that is already current', async () => {
    routeRequests({})
    const store = useDashboardStore()
    await store.selectRuntime('codex')
    expect(mocks.request).not.toHaveBeenCalled()
  })
})

describe('settings draft merging', () => {
  async function initialized(overrides: Partial<AppSettings> = {}) {
    routeRequests({ 'settings.get': appSettings({ checkForUpdates: false, ...overrides }) })
    const store = useDashboardStore()
    await store.initialize()
    return store
  }

  it('starts clean and turns dirty on the first edit', async () => {
    const store = await initialized()
    expect(store.settingsDirty).toBe(false)

    store.settingsDraft!.uiScalePercent = 130
    expect(store.settingsDirty).toBe(true)
  })

  it('keeps an unsaved edit when the host pushes an unrelated change', async () => {
    const store = await initialized({ uiScalePercent: 110, autoRefreshMinutes: 5 })
    store.settingsDraft!.uiScalePercent = 130

    emit('settings.changed', appSettings({ uiScalePercent: 110, autoRefreshMinutes: 15 }))

    // The host's change is the new baseline...
    expect(store.settings!.autoRefreshMinutes).toBe(15)
    expect(store.settings!.uiScalePercent).toBe(110)
    // ...but the user's in-progress edit survives on top of it.
    expect(store.settingsDraft!.autoRefreshMinutes).toBe(15)
    expect(store.settingsDraft!.uiScalePercent).toBe(130)
  })

  it('adopts the host value wholesale when the draft is clean', async () => {
    const store = await initialized({ uiScalePercent: 110 })

    emit('settings.changed', appSettings({ uiScalePercent: 125 }))

    expect(store.settings!.uiScalePercent).toBe(125)
    expect(store.settingsDraft!.uiScalePercent).toBe(125)
    expect(store.settingsDirty).toBe(false)
  })

  it('sends only the changed keys as a patch', async () => {
    const store = await initialized({ uiScalePercent: 110, autoRefreshMinutes: 5 })
    store.settingsDraft!.uiScalePercent = 130
    await store.saveSettings()

    const call = mocks.request.mock.calls.find(([method]) => method === 'settings.update')
    expect(call).toBeDefined()
    expect(call![1]).toEqual({ patch: { uiScalePercent: 130 } })
  })

  it('preserves an edit made while the save was in flight', async () => {
    const save = deferred<AppSettings>()
    routeRequests({
      'settings.get': appSettings({ checkForUpdates: false, uiScalePercent: 110, autoRefreshMinutes: 5 }),
      'settings.update': () => save.promise,
    })
    const store = useDashboardStore()
    await store.initialize()

    store.settingsDraft!.uiScalePercent = 130
    const saving = store.saveSettings()

    // User keeps typing while the host round-trip is outstanding.
    store.settingsDraft!.autoRefreshMinutes = 20

    save.resolve(appSettings({ uiScalePercent: 130, autoRefreshMinutes: 5 }))
    await saving

    expect(store.settings!.autoRefreshMinutes).toBe(5)
    expect(store.settingsDraft!.autoRefreshMinutes).toBe(20)
    expect(store.settingsDraft!.uiScalePercent).toBe(130)
  })

  it('restores the draft from the saved settings on reset', async () => {
    const store = await initialized({ uiScalePercent: 110 })
    store.settingsDraft!.uiScalePercent = 130
    store.resetSettingsDraft()

    expect(store.settingsDraft!.uiScalePercent).toBe(110)
    expect(store.settingsDirty).toBe(false)
  })

  it('surfaces a save failure without dropping the draft', async () => {
    routeRequests({
      'settings.get': appSettings({ checkForUpdates: false }),
      'settings.update': () => Promise.reject(new Error('写入被拒绝')),
    })
    const store = useDashboardStore()
    await store.initialize()
    store.settingsDraft!.uiScalePercent = 130

    await store.saveSettings()

    expect(store.error).toBe('写入被拒绝')
    expect(store.settingsDraft!.uiScalePercent).toBe(130)
    expect(store.isUpdatingSettings).toBe(false)
  })
})

describe('status strip controls', () => {
  it('adopts host-pushed visibility changes when a temporary preview expires', async () => {
    routeRequests({ 'settings.get': appSettings({ checkForUpdates: false, statusStripEnabled: false }) })
    const store = useDashboardStore()
    await store.initialize()

    emit('statusStrip.stateChanged', {
      configuredEnabled: false,
      visible: false,
      positionLocked: false,
      hasManualPosition: false,
      positionMode: '跟随 Codex',
      displayName: '尚未显示',
      message: '状态条当前已关闭。',
    })

    expect(store.statusStripState).toMatchObject({ visible: false, message: '状态条当前已关闭。' })
  })

  it('previews only status-strip draft fields without saving the settings draft', async () => {
    routeRequests({
      'settings.get': appSettings({ checkForUpdates: false, statusStripPositionLocked: false }),
      'statusStrip.preview': {
        configuredEnabled: false,
        visible: true,
        positionLocked: true,
        hasManualPosition: false,
        positionMode: '跟随 Codex',
        displayName: '主显示器 · DISPLAY1',
        message: '预览已显示 12 秒；不会保存当前草稿。',
      },
    })
    const store = useDashboardStore()
    await store.initialize()
    store.settingsDraft!.statusStripQuotaMode = 'used'
    store.settingsDraft!.statusStripShowTodayTokens = false
    store.settingsDraft!.statusStripPositionLocked = true

    await store.previewStatusStrip()

    expect(mocks.request).toHaveBeenCalledWith('statusStrip.preview', {
      patch: {
        statusStripQuotaMode: 'used',
        statusStripShowTodayTokens: false,
        statusStripPositionLocked: true,
      },
    })
    expect(mocks.request).not.toHaveBeenCalledWith('settings.update', expect.anything())
    expect(store.settings!.statusStripPositionLocked).toBe(false)
    expect(store.settingsDirty).toBe(true)
    expect(store.statusStripState?.visible).toBe(true)
  })

  it('recovers the strip without changing its configured enabled setting', async () => {
    routeRequests({
      'settings.get': appSettings({ checkForUpdates: false, statusStripEnabled: false }),
      'statusStrip.recover': {
        configuredEnabled: false,
        visible: true,
        positionLocked: false,
        hasManualPosition: false,
        positionMode: '跟随 Codex',
        displayName: '主显示器 · DISPLAY1',
        message: '状态条已找回并临时显示 12 秒；启用后才会常驻。',
      },
    })
    const store = useDashboardStore()
    await store.initialize()

    await store.recoverStatusStrip()

    expect(mocks.request).toHaveBeenCalledWith('statusStrip.recover', undefined)
    expect(store.settings!.statusStripEnabled).toBe(false)
    expect(store.settingsDraft!.statusStripEnabled).toBe(false)
    expect(store.statusStripState).toMatchObject({ visible: true, configuredEnabled: false })
  })
})

describe('todos', () => {
  it('replaces the list with whatever the host returns', async () => {
    const updated = [{ id: 't1', text: '已完成', done: true, priority: 'high' as const, createdAt: '2026-07-14T00:00:00Z' }]
    routeRequests({ 'todos.add': updated })

    const store = useDashboardStore()
    const ok = await store.addTodo({ text: '已完成', priority: 'high' })

    expect(ok).toBe(true)
    expect(store.todos).toEqual(updated)
    expect(mocks.request).toHaveBeenCalledWith('todos.add', { text: '已完成', priority: 'high' })
  })

  it('reports failure and leaves the previous list intact', async () => {
    routeRequests({ 'todos.delete': () => Promise.reject(new Error('删除失败')) })
    const store = useDashboardStore()
    store.todos = [{ id: 't1', text: '保留', done: false, priority: 'normal', createdAt: '2026-07-14T00:00:00Z' }]

    const ok = await store.deleteTodo('t1')

    expect(ok).toBe(false)
    expect(store.error).toBe('删除失败')
    expect(store.todos).toHaveLength(1)
  })
})

describe('local operations', () => {
  it('adopts settings and todos returned by the operation', async () => {
    routeRequests({
      'data.restore': {
        success: true,
        message: '已恢复',
        settings: appSettings({ uiScalePercent: 95 }),
        todos: [{ id: 't9', text: '恢复项', done: false, priority: 'low', createdAt: '2026-07-14T00:00:00Z' }],
      },
    })

    const store = useDashboardStore()
    const ok = await store.runLocalOperation('data.restore')

    expect(ok).toBe(true)
    expect(store.operationStatus).toBe('已恢复')
    expect(store.settings!.uiScalePercent).toBe(95)
    expect(store.settingsDirty).toBe(false)
    expect(store.todos).toHaveLength(1)
  })

  it('refuses to run two operations at once', async () => {
    const slow = deferred<unknown>()
    mocks.request.mockImplementation(async () => slow.promise)

    const store = useDashboardStore()
    const first = store.runLocalOperation('diagnostics.export')
    const second = await store.runLocalOperation('data.backup')

    expect(second).toBe(false)
    expect(mocks.request).toHaveBeenCalledTimes(1)

    slow.resolve({ success: true, message: '完成' })
    await first
  })

  it('reports a thrown operation as a status message rather than an error banner', async () => {
    routeRequests({ 'diagnostics.export': () => Promise.reject(new Error('磁盘已满')) })
    const store = useDashboardStore()

    const ok = await store.runLocalOperation('diagnostics.export')

    expect(ok).toBe(false)
    expect(store.operationStatus).toBe('磁盘已满')
    expect(store.isRunningLocalOperation).toBe(false)
  })
})

describe('update check', () => {
  it('records a failed check as a status instead of throwing', async () => {
    routeRequests({ 'update.check': () => Promise.reject(new Error('网络不可达')) })
    const store = useDashboardStore()

    await store.checkForUpdates()

    expect(store.updateStatus).toMatchObject({
      currentVersion: 'unknown',
      isUpdateAvailable: false,
      status: '网络不可达',
    })
    expect(store.isCheckingUpdates).toBe(false)
  })

  it('ignores a second concurrent check', async () => {
    const slow = deferred<unknown>()
    mocks.request.mockImplementation(async () => slow.promise)

    const store = useDashboardStore()
    const first = store.checkForUpdates()
    await store.checkForUpdates()

    expect(mocks.request).toHaveBeenCalledTimes(1)
    slow.resolve({ currentVersion: '1', isUpdateAvailable: false, isPrerelease: false, checkedAt: '', status: 'ok' })
    await first
  })
})

describe('compact mode', () => {
  it('applies the host result to both settings and draft', async () => {
    routeRequests({ 'window.toggleCompact': { enabled: true } })
    const store = useDashboardStore()
    store.settings = appSettings({ compactMode: false })
    store.settingsDraft = appSettings({ compactMode: false })

    await store.toggleCompact()

    expect(store.compactMode).toBe(true)
    expect(store.settingsDraft!.compactMode).toBe(true)
    expect(store.settingsDirty).toBe(false)
  })

  it('follows a compact change pushed by the host', async () => {
    routeRequests({ 'settings.get': appSettings({ checkForUpdates: false, compactMode: false }) })
    const store = useDashboardStore()
    await store.initialize()

    emit('window.compactChanged', { enabled: true })

    expect(store.compactMode).toBe(true)
    expect(store.settingsDraft!.compactMode).toBe(true)
  })
})

describe('loadCombined', () => {
  const combined = { codex: runtimeRead(), claudeCode: runtimeRead() }

  it('fetches both runtimes and stores them without touching the single-runtime slice', async () => {
    const loaded = snapshot({ refreshedAt: '2026-07-14T00:00:00Z' })
    routeRequests({ 'usage.getSnapshot': loaded, 'usage.getCombined': combined })
    const store = useDashboardStore()
    await store.initialize()

    await store.loadCombined()

    expect(store.combined).toEqual(combined)
    expect(store.combinedError).toBeNull()
    expect(store.snapshot).toEqual(loaded)
  })

  it('does not refetch once loaded unless forced', async () => {
    routeRequests({ 'usage.getCombined': combined })
    const store = useDashboardStore()

    await store.loadCombined()
    await store.loadCombined()
    await store.loadCombined(true)

    // Without the cache every switch back to the tab would pay for two disk reads
    // that each have a 90-second budget.
    expect(mocks.request.mock.calls.filter(([method]) => method === 'usage.getCombined')).toHaveLength(2)
  })

  it('ignores a second call while one is already in flight', async () => {
    // The host echoes a settings.update back as settings.changed, so an invalidation
    // can arrive while the reload it triggered is still running.
    const slow = deferred<unknown>()
    routeRequests({ 'usage.getCombined': () => slow.promise })
    const store = useDashboardStore()

    const first = store.loadCombined()
    const second = store.loadCombined(true)
    slow.resolve(combined)
    await Promise.all([first, second])

    expect(mocks.request.mock.calls.filter(([method]) => method === 'usage.getCombined')).toHaveLength(1)
  })

  it('keeps a combined failure out of the dashboard error banner', async () => {
    routeRequests({
      'usage.getCombined': () => Promise.reject(new Error('读取超时')),
    })
    const store = useDashboardStore()
    await store.initialize()

    await store.loadCombined()

    expect(store.combinedError).toBe('读取超时')
    expect(store.error).toBeNull()
    expect(store.snapshot).not.toBeNull()
  })

  it('reloads when the host reports a settings change', async () => {
    routeRequests({ 'usage.getCombined': combined, 'settings.get': appSettings({ checkForUpdates: false }) })
    const store = useDashboardStore()
    await store.initialize()
    await store.loadCombined()

    emit('settings.changed', appSettings({ checkForUpdates: false, amountPerThousandCredits: 99 }))
    await Promise.resolve()
    await Promise.resolve()

    // Rates change both runtimes' figures, so the cached pair is stale.
    expect(mocks.request.mock.calls.filter(([method]) => method === 'usage.getCombined')).toHaveLength(2)
  })

  it('does not reload on a settings change before the tab has ever been opened', async () => {
    routeRequests({ 'settings.get': appSettings({ checkForUpdates: false }) })
    const store = useDashboardStore()
    await store.initialize()

    emit('settings.changed', appSettings({ checkForUpdates: false, amountPerThousandCredits: 99 }))
    await Promise.resolve()

    expect(mocks.request.mock.calls.filter(([method]) => method === 'usage.getCombined')).toHaveLength(0)
  })

  it('leaves a pending runtime switch alone', async () => {
    // The two use separate generation counters on purpose: a combined reload landing
    // mid-switch must not cancel the switch, nor be cancelled by it.
    const switchTarget = snapshot({ runtime: 'claudeCode' })
    routeRequests({ 'usage.getCombined': combined, 'runtime.select': switchTarget })
    const store = useDashboardStore()
    await store.initialize()

    const selecting = store.selectRuntime('claudeCode')
    await store.loadCombined()
    await selecting

    expect(store.combined).toEqual(combined)
    expect(store.snapshot?.runtime).toBe('claudeCode')
  })
})
