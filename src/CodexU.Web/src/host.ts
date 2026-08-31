import type { AppSettings, DashboardSnapshot, IpcEnvelope, StatusStripControlState, TodoItem, TodoMutation } from './types'
import { DEMO_HOST_CAPABILITIES } from './hostCapabilities'

type ElectronEventListener = (method: string, payload: unknown) => void

interface ElectronHostBridge {
  request(method: string, payload?: object): Promise<unknown>
  onEvent(listener: ElectronEventListener): () => void
}

declare global {
  interface Window {
    codexU?: ElectronHostBridge
    chrome?: {
      webview?: {
        postMessage(message: unknown): void
        addEventListener(type: 'message', listener: (event: MessageEvent) => void): void
      }
    }
  }
}

type EventHandler = (payload: unknown) => void

const demoParameters = new URLSearchParams(window.location.search)
const visualTestMode = demoParameters.get('visualTest') === '1'
const fixedDemoNow = new Date('2026-07-14T12:00:00+09:00')
const interactiveHostMethods = new Set([
  'rates.export',
  'rates.import',
  'rates.reset',
  'data.exportAggregates',
  'data.backup',
  'data.restore',
  'diagnostics.export',
  'diagnostics.rebuildIndex',
])

function demoNow(): Date {
  return visualTestMode ? new Date(fixedDemoNow) : new Date()
}

class HostBridge {
  private mockSettings: AppSettings = {
    theme: 'dark', showSubagents: false, compactMode: false, statusStripEnabled: false, statusStripPositionLocked: false, desktopMode: false,
    closeToTray: true,
    startAtLogin: false, notificationsEnabled: true, quotaForecastAlertsEnabled: true, fiveHourAlertPercent: 20,
    sevenDayAlertPercent: 20, autoRefreshMinutes: 5, incrementalIndexEnabled: true,
    uiScalePercent: 110,
    amountPerThousandCredits: 40,
    creditCurrencySymbol: 'US$',
    codexMonthlySubscriptionAmount: 200,
    claudeMonthlySubscriptionAmount: 20,
    codexAutoDetectSubscriptionAmount: true,
    claudeAutoDetectSubscriptionAmount: true,
    checkForUpdates: true,
    includePrereleaseUpdates: false,
    monthlyAmountAlert: 0,
    minimumRateCoverageAlertPercent: 80,
    globalHotKey: 'Ctrl+U',
    statusStripQuotaMode: 'remaining',
    statusStripShowTodayTokens: true,
    customModelRates: [],
    isRateCatalogPinned: false,
  }

  private mockTodos: TodoItem[] = []

  private mockStatusStripState: StatusStripControlState = {
    configuredEnabled: false,
    visible: false,
    positionLocked: false,
    hasManualPosition: false,
    positionMode: '跟随 Codex',
    displayName: '主显示器 · DISPLAY1',
    message: '状态条当前已关闭。',
  }

  private readonly pending = new Map<string, {
    resolve: (value: unknown) => void
    reject: (reason: Error) => void
    timeout?: number
  }>()

  private readonly listeners = new Map<string, Set<EventHandler>>()
  private unsubscribeElectronEvents?: () => void

  constructor() {
    if (!this.isNative) {
      const requestedTheme = demoParameters.get('theme')
      if (requestedTheme === 'dark' || requestedTheme === 'light' || requestedTheme === 'system') {
        this.mockSettings.theme = requestedTheme
      }
      const requestedScale = Number(demoParameters.get('scale'))
      if (Number.isFinite(requestedScale) && requestedScale >= 90 && requestedScale <= 140) {
        this.mockSettings.uiScalePercent = requestedScale
      }
      this.mockSettings.compactMode = demoParameters.get('compact') === '1'
      if (demoParameters.get('pinnedRates') === '1') {
        this.mockSettings.customModelRates = [
          {
            model: 'gpt-5.2',
            inputCreditsPerMillion: 43.75,
            cachedInputCreditsPerMillion: 4.375,
            outputCreditsPerMillion: 350,
            effectiveFrom: null,
            source: '历史费率归档',
            catalogVersion: 'archive-2026.01',
            matchMode: 'exact',
          },
          {
            model: 'gpt-5.4',
            inputCreditsPerMillion: 62.5,
            cachedInputCreditsPerMillion: 6.25,
            outputCreditsPerMillion: 375,
            effectiveFrom: null,
            source: '历史费率归档',
            catalogVersion: 'archive-2026.01',
            matchMode: 'exact',
          },
        ]
        this.mockSettings.isRateCatalogPinned = true
        this.mockSettings.pinnedRateCatalogVersion = 'archive-2026.01'
        this.mockSettings.pinnedRateCatalogSource = '历史费率归档'
        this.mockSettings.pinnedRateCatalogBaseVersion = '2026.01.0'
      }
      if (visualTestMode) this.mockSettings.checkForUpdates = false
    }

    // Electron owns its IPC channel through the context-isolated preload API.
    // When both bridges exist during migration, never bind the legacy channel or
    // one host event could be delivered twice.
    if (window.codexU) return

    window.chrome?.webview?.addEventListener('message', (event) => {
      if (!event.data || typeof event.data !== 'object') return
      const envelope = event.data as IpcEnvelope
      if (envelope.version !== 1) return
      if (envelope.type === 'response' && envelope.id) {
        const request = this.pending.get(envelope.id)
        if (!request) return
        if (request.timeout !== undefined) window.clearTimeout(request.timeout)
        this.pending.delete(envelope.id)
        if (envelope.ok) request.resolve(envelope.payload)
        else request.reject(new Error(envelope.error?.message ?? 'Host request failed'))
        return
      }

      if (envelope.type === 'event' && envelope.method) {
        this.dispatchEvent(envelope.method, envelope.payload)
      }
    })
  }

  get isNative(): boolean {
    return Boolean(window.codexU || window.chrome?.webview)
  }

  request<T>(method: string, payload: object = {}): Promise<T> {
    const electron = window.codexU
    if (electron) return electron.request(method, payload) as Promise<T>

    const webview = window.chrome?.webview
    if (!webview) return this.mockRequest<T>(method, payload)

    const id = crypto.randomUUID()
    return new Promise<T>((resolve, reject) => {
      // usage.getCombined reads both runtimes behind one gate, each with the host's
      // own 90s budget, and the gate may already be held by an auto-refresh — so the
      // real worst case is three consecutive 90s reads, not two.
      const timeoutMilliseconds = method === 'usage.getCombined'
        ? 300_000
        : method.startsWith('usage.') || method === 'runtime.select' ? 120_000 : 30_000
      const timeout = interactiveHostMethods.has(method)
        ? undefined
        : window.setTimeout(() => {
          this.pending.delete(id)
          reject(new Error(`Host request timed out: ${method}`))
        }, timeoutMilliseconds)
      this.pending.set(id, {
        resolve: resolve as (value: unknown) => void,
        reject,
        timeout,
      })
      try {
        webview.postMessage({ version: 1, id, type: 'request', method, payload })
      } catch (reason) {
        if (timeout !== undefined) window.clearTimeout(timeout)
        this.pending.delete(id)
        reject(reason instanceof Error ? reason : new Error(String(reason)))
      }
    })
  }

  on(method: string, handler: EventHandler): () => void {
    const handlers = this.listeners.get(method) ?? new Set<EventHandler>()
    handlers.add(handler)
    this.listeners.set(method, handlers)

    try {
      this.subscribeToElectronEvents()
    } catch (reason) {
      handlers.delete(handler)
      if (handlers.size === 0) this.listeners.delete(method)
      throw reason
    }

    let subscribed = true
    return () => {
      if (!subscribed) return
      subscribed = false
      handlers.delete(handler)
      if (handlers.size === 0) this.listeners.delete(method)

      if (this.listeners.size === 0 && this.unsubscribeElectronEvents) {
        this.unsubscribeElectronEvents()
        this.unsubscribeElectronEvents = undefined
      }
    }
  }

  private subscribeToElectronEvents(): void {
    if (!window.codexU || this.unsubscribeElectronEvents) return
    this.unsubscribeElectronEvents = window.codexU.onEvent((method, payload) => {
      this.dispatchEvent(method, payload)
    })
  }

  private dispatchEvent(method: string, payload: unknown): void {
    this.listeners.get(method)?.forEach((handler) => handler(payload))
  }

  private async mockRequest<T>(method: string, payload: object): Promise<T> {
    await new Promise((resolve) => window.setTimeout(resolve, 180))
    if (method === 'app.initialize') {
      return {
        appVersion: `${__APP_VERSION__}-dev`,
        platform: 'browser',
        theme: 'dark',
        isPackaged: false,
        capabilities: [...DEMO_HOST_CAPABILITIES],
      } as T
    }
    if (method === 'runtime.select') {
      return createDemoSnapshot((payload as { runtime: 'codex' | 'claudeCode' }).runtime) as T
    }
    if (method === 'settings.get') return { ...this.mockSettings } as T
    if (method === 'settings.update') {
      const patch = (payload as { patch?: Partial<AppSettings> }).patch
      if (!patch) throw new Error('设置更新缺少 patch')
      this.mockSettings = { ...this.mockSettings, ...patch }
      this.mockSettings.uiScalePercent = Math.min(140, Math.max(90, this.mockSettings.uiScalePercent))
      this.mockSettings.autoRefreshMinutes = Math.min(60, Math.max(1, this.mockSettings.autoRefreshMinutes))
      this.mockStatusStripState = {
        ...this.mockStatusStripState,
        configuredEnabled: this.mockSettings.statusStripEnabled,
        visible: this.mockSettings.statusStripEnabled,
        positionLocked: this.mockSettings.statusStripPositionLocked,
        message: this.mockSettings.statusStripEnabled ? '状态条运行正常。' : '状态条当前已关闭。',
      }
      return { ...this.mockSettings } as T
    }
    if (method === 'statusStrip.getState') return { ...this.mockStatusStripState } as T
    if (method === 'statusStrip.preview') {
      const patch = (payload as { patch?: Partial<AppSettings> }).patch ?? {}
      this.mockStatusStripState = {
        ...this.mockStatusStripState,
        visible: true,
        positionLocked: patch.statusStripPositionLocked ?? this.mockSettings.statusStripPositionLocked,
        message: '预览已显示 12 秒；不会保存当前草稿。',
      }
      return { ...this.mockStatusStripState } as T
    }
    if (method === 'statusStrip.recover') {
      this.mockStatusStripState = {
        ...this.mockStatusStripState,
        visible: true,
        hasManualPosition: false,
        positionMode: '跟随 Codex',
        message: this.mockSettings.statusStripEnabled
          ? '状态条已移回当前可见工作区。'
          : '状态条已找回并临时显示 12 秒；启用后才会常驻。',
      }
      return { ...this.mockStatusStripState } as T
    }
    if (method === 'rates.getCatalog') {
      return {
        builtIn: {
          schemaVersion: 1,
          catalogVersion: '2026.07.1',
          source: '用户提供的 OpenAI Credits 参考表',
          publishedOn: '2026-07-14',
          rateCount: 11,
        },
        // A representative slice of the real catalog rather than an empty list:
        // the rate editor seeds a new row from these, so an empty array would
        // leave the browser demo — and the visual baseline — exercising the
        // zero-filled path the defaults exist to replace. Credits are USD/M x 25.
        builtInRates: [
          { model: 'claude-opus-5', inputCreditsPerMillion: 125, cachedInputCreditsPerMillion: 12.5, outputCreditsPerMillion: 625, effectiveFrom: null, source: 'Anthropic 公布的 Claude API 价目', catalogVersion: 'anthropic-2026.07.1', matchMode: 'exact' },
          { model: 'claude-sonnet-5', inputCreditsPerMillion: 50, cachedInputCreditsPerMillion: 5, outputCreditsPerMillion: 250, effectiveFrom: null, source: 'Anthropic 公布的 Claude API 价目', catalogVersion: 'anthropic-2026.07.1', matchMode: 'exact' },
          { model: 'claude-sonnet-5', inputCreditsPerMillion: 75, cachedInputCreditsPerMillion: 7.5, outputCreditsPerMillion: 375, effectiveFrom: '2026-09-01', source: 'Anthropic 公布的 Claude API 价目（Sonnet 5 首发优惠到期）', catalogVersion: 'anthropic-2026.09.1', matchMode: 'exact' },
          { model: 'claude-haiku-4-5', inputCreditsPerMillion: 25, cachedInputCreditsPerMillion: 2.5, outputCreditsPerMillion: 125, effectiveFrom: null, source: 'Anthropic 公布的 Claude API 价目', catalogVersion: 'anthropic-2026.07.1', matchMode: 'exact' },
          { model: 'gpt-5.2', inputCreditsPerMillion: 43.75, cachedInputCreditsPerMillion: 4.375, outputCreditsPerMillion: 350, effectiveFrom: null, source: '用户提供的 OpenAI Credits 参考表', catalogVersion: '2026.07.1', matchMode: 'exact' },
        ],
      } as T
    }
    if (method === 'rates.reset') {
      this.mockSettings.customModelRates = []
      this.mockSettings.isRateCatalogPinned = false
      delete this.mockSettings.pinnedRateCatalogVersion
      delete this.mockSettings.pinnedRateCatalogSource
      delete this.mockSettings.pinnedRateCatalogBaseVersion
      return { success: true, message: '浏览器演示模式：已恢复默认费率', settings: { ...this.mockSettings } } as T
    }
    if (method === 'window.toggleCompact') {
      this.mockSettings.compactMode = !this.mockSettings.compactMode
      return { enabled: this.mockSettings.compactMode } as T
    }
    if (method === 'todos.list') return this.mockTodos as T
    if (method === 'todos.add') {
      const mutation = payload as TodoMutation
      this.mockTodos.unshift({
        id: crypto.randomUUID(), text: mutation.text, done: false,
        priority: mutation.priority, dueDate: mutation.dueDate,
        threadId: mutation.threadId, createdAt: new Date().toISOString(),
      })
      return this.mockTodos as T
    }
    if (method === 'todos.toggle') {
      const id = (payload as { id: string }).id
      this.mockTodos = this.mockTodos.map((item) => item.id === id ? { ...item, done: !item.done } : item)
      return this.mockTodos as T
    }
    if (method === 'todos.update') {
      const mutation = payload as TodoMutation
      this.mockTodos = this.mockTodos.map((item) => item.id === mutation.id
        ? { ...item, text: mutation.text, priority: mutation.priority, dueDate: mutation.dueDate, updatedAt: new Date().toISOString() }
        : item)
      return this.mockTodos as T
    }
    if (method === 'todos.delete') {
      const id = (payload as { id: string }).id
      this.mockTodos = this.mockTodos.filter((item) => item.id !== id)
      return this.mockTodos as T
    }
    if (method === 'todos.clearCompleted') {
      this.mockTodos = this.mockTodos.filter((item) => !item.done)
      return this.mockTodos as T
    }
    if (method === 'update.check') {
      return {
        currentVersion: `${__APP_VERSION__}-dev`, latestVersion: __APP_VERSION__,
        isUpdateAvailable: false, isPrerelease: false,
        releaseUrl: 'https://github.com/yuweiyang9611/CodexHelperProject/releases',
        checkedAt: new Date().toISOString(), status: '当前已是最新版本',
      } as T
    }
    if (method === 'update.openRelease') return { opened: true } as T
    if (method === 'data.exportAggregates' || method === 'data.backup' || method === 'data.restore'
      || method === 'rates.export' || method === 'rates.import'
      || method === 'diagnostics.export' || method === 'diagnostics.rebuildIndex') {
      return { success: true, message: '浏览器演示模式：操作已模拟' } as T
    }
    // Ahead of the usage. prefix branch below, which would otherwise swallow it and
    // hand the combined view a bare single-runtime snapshot.
    if (method === 'usage.getCombined') {
      return {
        codex: { snapshot: createDemoSnapshot('codex'), readFailed: false },
        claudeCode: { snapshot: createDemoSnapshot('claudeCode'), readFailed: false },
      } as T
    }
    if (method.startsWith('usage.')) return createDemoSnapshot('codex') as T
    return {} as T
  }
}

function period(tokens: number, quality: 'detailed' | 'unavailable' = 'detailed') {
  const inputTokens = Math.round(tokens * 0.67)
  const cachedInputTokens = Math.round(tokens * 0.43)
  const outputTokens = Math.round(tokens * 0.33)
  const uncachedInputTokens = inputTokens - cachedInputTokens
  const creditsUsed = uncachedInputTokens / 1_000_000 * 125
    + cachedInputTokens / 1_000_000 * 12.5
    + outputTokens / 1_000_000 * 750
  const breakdown = {
    inputTokens,
    cachedInputTokens,
    outputTokens,
    reasoningOutputTokens: Math.round(tokens * 0.08),
    totalTokens: tokens,
    billableCachedInputTokens: cachedInputTokens,
    uncachedInputTokens,
    visibleTotalTokens: tokens,
    // The browser demo models a Codex-shaped source, which reports no cache-write
    // split, so every write slice stays zero and pricing matches plain input.
    cacheWrite5mTokens: 0,
    cacheWrite1hTokens: 0,
    billableCacheWrite5mTokens: 0,
    billableCacheWrite1hTokens: 0,
    billableCacheWriteTokens: 0,
  }
  const inputCredits = uncachedInputTokens / 1_000_000 * 125
  const cachedInputCredits = cachedInputTokens / 1_000_000 * 12.5
  const outputCredits = outputTokens / 1_000_000 * 750
  const cachedSavingsCredits = cachedInputTokens / 1_000_000 * (125 - 12.5)
  return {
    tokens,
    breakdown,
    creditsUsed,
    unratedTokens: 0,
    creditsByModel: [{
      model: 'gpt-5.6-sol',
      tokens: breakdown,
      inputCredits,
      cachedInputCredits,
      cacheWriteCredits: 0,
      outputCredits,
      cachedSavingsCredits,
      totalCredits: creditsUsed,
      rateVersions: [{
        catalogVersion: '2026.07.1',
        source: '用户提供的 OpenAI Credits 参考表',
        effectiveFrom: null,
        tokens: breakdown,
        inputCredits,
        cachedInputCredits,
        cacheWriteCredits: 0,
        outputCredits,
        cachedSavingsCredits,
        totalCredits: creditsUsed,
      }],
    }],
    quality,
  }
}

function createDemoSnapshot(runtime: 'codex' | 'claudeCode'): DashboardSnapshot {
  const today = demoNow()
  const now = today.getTime()
  // The combined view puts the two runtimes side by side, so demo data that differed
  // only in the account would render as two identical columns and read as a bug. The
  // Codex figures are left exactly as they were, since every other baseline is built
  // from them; only the Claude side diverges.
  const isClaude = runtime === 'claudeCode'
  const scale = (value: number) => (isClaude ? Math.round(value * 0.62) : value)
  const dailyUsage = Array.from({ length: 182 }, (_, index) => {
    const date = new Date(today)
    date.setDate(today.getDate() - (181 - index))
    const wave = Math.max(0, Math.sin(index * 0.31) * 0.65 + ((index * 37) % 100) / 165 - 0.18)
    const tokens = index % 11 === 0 ? 0 : Math.round(wave * 2_800_000)
    return {
      date: date.toISOString().slice(0, 10),
      tokens,
      creditsUsed: tokens / 1_000_000 * 282.875,
      quality: 'detailed' as const,
    }
  })

  return {
    runtime,
    refreshedAt: today.toISOString(),
    // "pro" is a different price per vendor — US$20 on Claude, US$200 on ChatGPT —
    // so the demo account has to follow the runtime it is describing rather than
    // showing a ChatGPT subscription behind the Claude Code toggle.
    account: runtime === 'claudeCode'
      ? {
        accountType: 'claude-code', planType: 'pro', email: 'local@claude', isAuthenticated: true,
        suggestedMonthlySubscriptionAmount: 20,
      }
      : {
        accountType: 'chatgpt', planType: 'pro', email: 'local@codex', isAuthenticated: true,
        suggestedMonthlySubscriptionAmount: 200,
      },
    primaryQuota: isClaude
      ? { usedPercent: 46, remainingPercent: 54, windowDurationMinutes: 300, resetsAt: new Date(now + 158 * 60_000).toISOString() }
      : { usedPercent: 28, remainingPercent: 72, windowDurationMinutes: 300, resetsAt: new Date(now + 93 * 60_000).toISOString() },
    secondaryQuota: isClaude
      ? { usedPercent: 22, remainingPercent: 78, windowDurationMinutes: 10080, resetsAt: new Date(now + 5.2 * 86_400_000).toISOString() }
      : { usedPercent: 41, remainingPercent: 59, windowDurationMinutes: 10080, resetsAt: new Date(now + 4.4 * 86_400_000).toISOString() },
    // One window of each kind on each runtime: some run out before they reset and show
    // a countdown, some reset first and must stay silent. Across the two runtimes that
    // gives the combined view two predictable windows out of four, so its "earliest
    // exhausts" line and its coverage count are both exercised by the baseline.
    primaryForecast: isClaude
      ? {
        percentPerMinute: 0.35,
        timeToExhaustion: '02:34:00',
        exhaustsAt: new Date(now + 154 * 60_000).toISOString(),
        exhaustsBeforeReset: false,
        measuredOver: '01:00:00',
      }
      : {
        percentPerMinute: 2.05,
        timeToExhaustion: '00:35:00',
        exhaustsAt: new Date(now + 35 * 60_000).toISOString(),
        exhaustsBeforeReset: true,
        measuredOver: '00:45:00',
      },
    secondaryForecast: isClaude
      ? {
        percentPerMinute: 0.018,
        timeToExhaustion: '3.00:00:00',
        exhaustsAt: new Date(now + 3 * 86_400_000).toISOString(),
        exhaustsBeforeReset: true,
        measuredOver: '01:30:00',
      }
      : {
        percentPerMinute: 0.004,
        timeToExhaustion: '6.03:00:00',
        exhaustsAt: new Date(now + 6.1 * 86_400_000).toISOString(),
        exhaustsBeforeReset: false,
        measuredOver: '01:30:00',
      },
    tokens: {
      today: period(scale(3_840_210)),
      sevenDays: period(scale(18_460_830)),
      month: period(scale(52_830_140)),
      lifetime: period(scale(482_670_930)),
    },
    tasks: [
      { id: '1', title: '复刻 codexU Windows 主界面', project: 'CodexHelperProject', updatedAt: today.toISOString(), tokens: 1_420_000, kind: 'active' },
      { id: '2', title: '实现 WebView2 类型化 IPC', project: 'CodexHelperProject', updatedAt: new Date(now - 22 * 60_000).toISOString(), tokens: 640_000, kind: 'pending' },
      { id: '3', title: '每日使用统计', project: 'Automation', updatedAt: today.toISOString(), kind: 'scheduled', detail: '每天 09:00' },
      { id: '4', title: '完成技术设计方案', project: 'CodexHelperProject', updatedAt: new Date(now - 55 * 60_000).toISOString(), tokens: 310_000, kind: 'done' },
    ],
    dailyUsage,
    projects: [
      // Cost deliberately does not track token order: SafeGuard leans on an
      // expensive model, so it outranks CodexHelperProject by cost while trailing it
      // by tokens. Proportional figures would make the two sorts identical and hide
      // whether the cost ranking works at all.
      // Paths deliberately differ in case and separator between the two runtimes:
      // the combined view merges on normalized absolute path, and demo data that
      // agreed exactly would never exercise the normalization it depends on. One
      // project is shared, the rest are runtime-specific, and Claude carries a row
      // under its own transcript store to exercise the guard that keeps that from
      // being mistaken for a project.
      ...(runtime === 'claudeCode'
        ? [
          { id: '1', name: 'CodexHelperProject', fullPath: 'd:/repo/codexhelperproject', tokens: 9_120_000, threadCount: 12, branch: 'main', creditsUsed: 2870.5, quality: 'detailed' as const, lastActiveAt: today.toISOString() },
          { id: '2', name: 'QuantitativeTrading', fullPath: 'D:\\Repo\\QuantitativeTrading', tokens: 5_940_000, threadCount: 7, branch: 'main', creditsUsed: 1680.28, quality: 'detailed' as const, lastActiveAt: new Date(now - 86_400_000).toISOString() },
          { id: '3', name: '-d--repo--notes', fullPath: 'C:\\Users\\demo\\.claude\\projects\\-d--repo--notes', tokens: 640_000, threadCount: 3, quality: 'partial' as const, lastActiveAt: new Date(now - 172_800_000).toISOString() },
        ]
        : [
          { id: '1', name: 'CodexHelperProject', fullPath: 'D:\\Repo\\CodexHelperProject', tokens: 12_480_000, threadCount: 18, branch: 'main', creditsUsed: 3530.28, costIsEstimated: true, quality: 'detailed' as const, lastActiveAt: today.toISOString() },
          { id: '2', name: 'SafeGuard', fullPath: 'D:\\Repo\\SafeGuard', tokens: 8_240_000, threadCount: 11, branch: 'develop', creditsUsed: 5120.44, costIsEstimated: true, quality: 'detailed' as const, lastActiveAt: new Date(now - 5_400_000).toISOString() },
          { id: '3', name: 'QuantitativeTrading', fullPath: 'D:\\Repo\\QuantitativeTrading', tokens: 5_940_000, threadCount: 7, branch: 'main', creditsUsed: 1680.28, costIsEstimated: true, quality: 'detailed' as const, lastActiveAt: new Date(now - 86_400_000).toISOString() },
        ]),
    ],
    tools: [
      { id: 'exec_command', name: '终端', count: 186, category: 'Terminal' },
      { id: 'apply_patch', name: '代码编辑', count: 92, category: 'Edit' },
      { id: 'web', name: '浏览/检索', count: 64, category: 'Web' },
      { id: 'plan', name: '计划', count: 21, category: 'Planning' },
    ],
    skills: [
      { id: 'github', name: 'github', count: 18, category: 'Skill' },
      { id: 'openai-docs', name: 'openai-docs', count: 12, category: 'Skill' },
      { id: 'documents', name: 'documents', count: 7, category: 'Skill' },
    ],
    sources: [
      { id: 'main', name: '主任务', count: 82, category: 'Source' },
      { id: 'subagent', name: '子代理', count: 31, category: 'Source' },
      { id: 'automation', name: '自动化', count: 13, category: 'Source' },
    ],
    // Model ids are vendor-specific; showing gpt-* under the Claude toggle made the
    // demo — and the visual baseline built from it — look like a wiring bug.
    models: isClaude
      ? [
        { model: 'claude-opus-5', tokens: 19_500_000, eventCount: 96 },
        { model: 'claude-haiku-4-5', tokens: 9_100_000, eventCount: 214 },
      ]
      : [
        { model: 'gpt-5.6-sol', tokens: 31_400_000, eventCount: 128 },
        { model: 'gpt-5.4', tokens: 14_700_000, eventCount: 61 },
      ],
    goals: [
      { id: 'goal-1', objective: '完成 codexU Windows 功能对齐', status: 'active', tokenBudget: 120_000, tokensUsed: 46_000, timeUsedSeconds: 5400, updatedAt: today.toISOString() },
    ],
    // Structurally empty for Claude, matching the reader: it records no task
    // lifecycle and keeps no index. Demo data that invented numbers here would hide
    // the very case the view now has to handle without padding zeros.
    taskLifecycle: isClaude
      ? { started: 0, completed: 0, aborted: 0, durationMilliseconds: 0, longestDurationMilliseconds: 0 }
      : { started: 126, completed: 104, aborted: 8, durationMilliseconds: 42_000_000, longestDurationMilliseconds: 1_620_000 },
    indexStatus: isClaude
      ? { enabled: false, reusedFiles: 0, incrementalFiles: 0, parsedFiles: 118, totalFiles: 118, updatedAt: today.toISOString() }
      : { enabled: true, reusedFiles: 42, incrementalFiles: 1, parsedFiles: 0, totalFiles: 43, updatedAt: today.toISOString() },
    diagnostics: [],
  }
}

export const host = new HostBridge()
