import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { host } from '../host'
import type { AgentRuntime, AppSettings, CombinedSnapshots, DashboardSnapshot, InitializeResult, LocalOperationResult, RateCatalogSnapshot, StatusStripControlState, TodoItem, TodoMutation, UpdateCheckResult } from '../types'

export const useDashboardStore = defineStore('dashboard', () => {
  const snapshot = ref<DashboardSnapshot | null>(null)
  const isLoading = ref(true)
  const isRefreshing = ref(false)
  const error = ref<string | null>(null)
  const settings = ref<AppSettings | null>(null)
  const settingsDraft = ref<AppSettings | null>(null)
  const todos = ref<TodoItem[]>([])
  const updateStatus = ref<UpdateCheckResult | null>(null)
  const rateCatalog = ref<RateCatalogSnapshot | null>(null)
  const isCheckingUpdates = ref(false)
  const isRunningLocalOperation = ref(false)
  const isUpdatingSettings = ref(false)
  const operationStatus = ref<string | null>(null)
  const statusStripState = ref<StatusStripControlState | null>(null)
  const isControllingStatusStrip = ref(false)
  const appVersion = ref('development')
  const combined = ref<CombinedSnapshots | null>(null)
  const isLoadingCombined = ref(false)
  const combinedError = ref<string | null>(null)
  let listenersBound = false
  let snapshotOperationGeneration = 0
  // Declared alongside the snapshot generation, not at module scope: the store's setup
  // function re-runs per Pinia instance, and a counter that outlived it would let a
  // promise leaked from one test satisfy the latest-generation check in the next.
  // Deliberately separate from snapshotOperationGeneration — a combined load must not
  // cancel a pending runtime switch, nor be cancelled by one.
  let combinedGeneration = 0
  let pendingRuntime: AgentRuntime | null = null

  const runtime = computed(() => snapshot.value?.runtime ?? 'codex')
  const compactMode = computed(() => settings.value?.compactMode ?? false)
  const settingsDirty = computed(() => JSON.stringify(settingsDraft.value) !== JSON.stringify(settings.value))

  function cloneSettings(value: AppSettings): AppSettings {
    return {
      ...value,
      customModelRates: (value.customModelRates ?? []).map((rate) => ({ ...rate })),
    }
  }

  function copySettings(value: AppSettings): AppSettings {
    const copied = cloneSettings(value)
    return {
      ...copied,
      isRateCatalogPinned: copied.isRateCatalogPinned ?? false,
      customModelRates: copied.customModelRates.map((rate) => ({
        ...rate,
        effectiveFrom: rate.effectiveFrom || null,
        matchMode: rate.matchMode ?? 'exact',
      })),
    }
  }

  function errorMessage(reason: unknown): string {
    return reason instanceof Error ? reason.message : String(reason)
  }

  function beginSnapshotOperation(): number {
    snapshotOperationGeneration += 1
    return snapshotOperationGeneration
  }

  function isLatestSnapshotOperation(generation: number): boolean {
    return generation === snapshotOperationGeneration
  }

  function applyHostSettings(changed: AppSettings) {
    const previous = settings.value
    const draft = settingsDraft.value
    if (!previous || !draft || !settingsDirty.value) {
      settings.value = copySettings(changed)
      settingsDraft.value = copySettings(changed)
      return
    }

    const merged = { ...changed } as Record<string, unknown>
    const previousValues = previous as unknown as Record<string, unknown>
    const draftValues = draft as unknown as Record<string, unknown>
    for (const key of Object.keys(draftValues)) {
      if (JSON.stringify(draftValues[key]) !== JSON.stringify(previousValues[key])) {
        merged[key] = draftValues[key]
      }
    }

    settings.value = copySettings(changed)
    settingsDraft.value = copySettings(merged as unknown as AppSettings)
  }

  function applySavedSettings(changed: AppSettings, submittedDraft: AppSettings) {
    const currentDraft = settingsDraft.value
    const merged = { ...changed } as Record<string, unknown>
    if (currentDraft) {
      const currentValues = currentDraft as unknown as Record<string, unknown>
      const submittedValues = submittedDraft as unknown as Record<string, unknown>
      for (const key of Object.keys(currentValues)) {
        if (JSON.stringify(currentValues[key]) !== JSON.stringify(submittedValues[key])) {
          merged[key] = currentValues[key]
        }
      }
    }

    settings.value = copySettings(changed)
    settingsDraft.value = copySettings(merged as unknown as AppSettings)
  }

  async function initialize() {
    isLoading.value = true
    error.value = null
    if (!listenersBound) {
      listenersBound = true
      host.on('usage.snapshotChanged', (payload) => {
        const changed = payload as DashboardSnapshot
        if (pendingRuntime !== null && changed.runtime !== pendingRuntime) return
        snapshotOperationGeneration += 1
        pendingRuntime = null
        snapshot.value = changed
        isRefreshing.value = false
      })
      host.on('usage.refreshStarted', () => { isRefreshing.value = true })
      host.on('usage.refreshFailed', (payload) => {
        if (pendingRuntime !== null) return
        snapshotOperationGeneration += 1
        error.value = (payload as { message?: string })?.message ?? '刷新失败'
        isRefreshing.value = false
      })
      host.on('settings.changed', (payload) => {
        applyHostSettings(payload as AppSettings)
        // Rates, workspace and subagent settings change both runtimes' figures.
        // Invalidated here and nowhere else: the host echoes a settings.update back as
        // this event, so also invalidating in saveSettings would fire two combined
        // reads for one save — four gated disk reads against one frontend timeout.
        if (combined.value) void loadCombined(true)
      })
      host.on('statusStrip.stateChanged', (payload) => {
        statusStripState.value = payload as StatusStripControlState
      })
      host.on('window.compactChanged', (payload) => {
        const enabled = Boolean((payload as { enabled?: boolean })?.enabled)
        if (settings.value) settings.value.compactMode = enabled
        if (settingsDraft.value) settingsDraft.value.compactMode = enabled
      })
    }

    const failures: string[] = []
    try {
      const initialized = await host.request<InitializeResult>('app.initialize')
      appVersion.value = initialized.appVersion
    } catch (reason) {
      failures.push(`宿主初始化失败：${errorMessage(reason)}`)
    }

    const snapshotGeneration = beginSnapshotOperation()
    const [snapshotResult, settingsResult, todosResult, rateCatalogResult, statusStripResult] = await Promise.allSettled([
      host.request<DashboardSnapshot>('usage.getSnapshot'),
      host.request<AppSettings>('settings.get'),
      host.request<TodoItem[]>('todos.list'),
      host.request<RateCatalogSnapshot>('rates.getCatalog'),
      host.request<StatusStripControlState>('statusStrip.getState'),
    ])
    if (isLatestSnapshotOperation(snapshotGeneration)) {
      if (snapshotResult.status === 'fulfilled') snapshot.value = snapshotResult.value
      else failures.push(`用量读取失败：${errorMessage(snapshotResult.reason)}`)
    }
    if (settingsResult.status === 'fulfilled') {
      settings.value = copySettings(settingsResult.value)
      settingsDraft.value = copySettings(settingsResult.value)
      if (settingsResult.value.checkForUpdates) void checkForUpdates(false)
    } else {
      failures.push(`设置读取失败：${errorMessage(settingsResult.reason)}`)
    }
    if (todosResult.status === 'fulfilled') todos.value = todosResult.value
    else failures.push(`待办读取失败：${errorMessage(todosResult.reason)}`)
    if (rateCatalogResult.status === 'fulfilled') rateCatalog.value = rateCatalogResult.value
    else failures.push(`费率目录读取失败：${errorMessage(rateCatalogResult.reason)}`)
    if (statusStripResult.status === 'fulfilled') statusStripState.value = statusStripResult.value
    else failures.push(`状态条状态读取失败：${errorMessage(statusStripResult.reason)}`)

    error.value = failures.length ? failures.join('；') : null
    isLoading.value = false
  }

  async function saveSettings() {
    if (!settingsDraft.value || isUpdatingSettings.value || isRunningLocalOperation.value) return
    if (!settings.value) return
    isUpdatingSettings.value = true
    error.value = null
    try {
      const baseline = copySettings(settings.value)
      const submittedDraft = cloneSettings(settingsDraft.value)
      const draft = copySettings(submittedDraft)
      const patch: Partial<AppSettings> = {}
      const patchValues = patch as Record<string, unknown>
      const baselineValues = baseline as unknown as Record<string, unknown>
      const draftValues = draft as unknown as Record<string, unknown>
      for (const key of Object.keys(draftValues)) {
        if (JSON.stringify(draftValues[key]) !== JSON.stringify(baselineValues[key])) {
          patchValues[key] = draftValues[key]
        }
      }

      const saved = await host.request<AppSettings>('settings.update', { patch })
      applySavedSettings(saved, submittedDraft)
      await refreshStatusStripState()
      await refresh()
    } catch (reason) {
      error.value = errorMessage(reason)
    } finally {
      isUpdatingSettings.value = false
    }
  }

  function resetSettingsDraft() {
    if (settings.value) settingsDraft.value = copySettings(settings.value)
  }

  async function refreshStatusStripState() {
    try {
      statusStripState.value = await host.request<StatusStripControlState>('statusStrip.getState')
    } catch (reason) {
      statusStripState.value = {
        configuredEnabled: settings.value?.statusStripEnabled ?? false,
        visible: false,
        positionLocked: settings.value?.statusStripPositionLocked ?? false,
        hasManualPosition: false,
        positionMode: '状态未知',
        displayName: '状态未知',
        message: errorMessage(reason),
      }
    }
  }

  async function previewStatusStrip() {
    if (!settingsDraft.value || isControllingStatusStrip.value || isUpdatingSettings.value) return
    isControllingStatusStrip.value = true
    try {
      const patch: Partial<AppSettings> = {
        statusStripQuotaMode: settingsDraft.value.statusStripQuotaMode,
        statusStripShowTodayTokens: settingsDraft.value.statusStripShowTodayTokens,
        statusStripPositionLocked: settingsDraft.value.statusStripPositionLocked,
      }
      statusStripState.value = await host.request<StatusStripControlState>('statusStrip.preview', { patch })
    } catch (reason) {
      error.value = errorMessage(reason)
    } finally {
      isControllingStatusStrip.value = false
    }
  }

  async function recoverStatusStrip() {
    if (isControllingStatusStrip.value || isUpdatingSettings.value) return
    isControllingStatusStrip.value = true
    try {
      statusStripState.value = await host.request<StatusStripControlState>('statusStrip.recover')
    } catch (reason) {
      error.value = errorMessage(reason)
    } finally {
      isControllingStatusStrip.value = false
    }
  }

  async function toggleCompact() {
    if (isRunningLocalOperation.value || isUpdatingSettings.value) return
    isUpdatingSettings.value = true
    error.value = null
    try {
      const result = await host.request<{ enabled: boolean }>('window.toggleCompact')
      if (settings.value) settings.value.compactMode = result.enabled
      if (settingsDraft.value) settingsDraft.value.compactMode = result.enabled
    } catch (reason) {
      error.value = errorMessage(reason)
    } finally {
      isUpdatingSettings.value = false
    }
  }

  /**
   * Loads both runtimes for the combined view.
   *
   * Lazy and cached: the two reads are expensive, so a tab switch back to a view that
   * already has data must not pay for them again. Failures land in `combinedError`
   * alone — a combined read that fails is no reason to take over the dashboard's error
   * banner for data the user can still see.
   */
  async function loadCombined(force = false) {
    if (combined.value && !force) return
    if (isLoadingCombined.value) return
    const generation = (combinedGeneration += 1)
    isLoadingCombined.value = true
    combinedError.value = null
    try {
      const result = await host.request<CombinedSnapshots>('usage.getCombined')
      if (generation !== combinedGeneration) return
      combined.value = result
    } catch (reason) {
      if (generation !== combinedGeneration) return
      combinedError.value = errorMessage(reason)
    } finally {
      if (generation === combinedGeneration) isLoadingCombined.value = false
    }
  }

  async function mutateTodos(method: string, payload: object = {}): Promise<boolean> {
    error.value = null
    try {
      // Always publish a fresh array. The in-browser bridge can legally return the
      // same backing array it just mutated; assigning that identity to a ref again
      // would leave computed counts and filtered rows stale.
      todos.value = [...await host.request<TodoItem[]>(method, payload)]
      return true
    } catch (reason) {
      error.value = errorMessage(reason)
      return false
    }
  }

  async function addTodo(mutation: TodoMutation) { return mutateTodos('todos.add', mutation) }

  async function updateTodo(mutation: TodoMutation) { return mutateTodos('todos.update', mutation) }

  async function toggleTodo(id: string) { return mutateTodos('todos.toggle', { id }) }

  async function deleteTodo(id: string) { return mutateTodos('todos.delete', { id }) }

  async function clearCompletedTodos() { return mutateTodos('todos.clearCompleted') }

  async function checkForUpdates(force = true) {
    if (isCheckingUpdates.value) return
    isCheckingUpdates.value = true
    try {
      updateStatus.value = await host.request<UpdateCheckResult>('update.check', { force })
    } catch (reason) {
      updateStatus.value = {
        currentVersion: 'unknown', isUpdateAvailable: false, isPrerelease: false,
        checkedAt: new Date().toISOString(), status: errorMessage(reason),
      }
    } finally {
      isCheckingUpdates.value = false
    }
  }

  async function openReleasePage() {
    try {
      await host.request('update.openRelease')
    } catch (reason) {
      error.value = errorMessage(reason)
    }
  }

  async function runLocalOperation(method: string, payload: object = {}) {
    if (isRunningLocalOperation.value || isUpdatingSettings.value) return false
    isRunningLocalOperation.value = true
    operationStatus.value = null
    try {
      const result = await host.request<LocalOperationResult>(method, payload)
      operationStatus.value = result.message
      if (result.settings) {
        settings.value = copySettings(result.settings)
        settingsDraft.value = copySettings(result.settings)
      }
      if (result.todos) todos.value = result.todos
      return result.success
    } catch (reason) {
      operationStatus.value = errorMessage(reason)
      return false
    } finally {
      isRunningLocalOperation.value = false
    }
  }

  async function refresh() {
    if (isRefreshing.value) return
    const generation = beginSnapshotOperation()
    isRefreshing.value = true
    error.value = null
    try {
      const refreshed = await host.request<DashboardSnapshot>('usage.refresh')
      if (isLatestSnapshotOperation(generation)) snapshot.value = refreshed
    } catch (reason) {
      if (isLatestSnapshotOperation(generation)) error.value = errorMessage(reason)
    } finally {
      if (isLatestSnapshotOperation(generation)) isRefreshing.value = false
    }
  }

  async function selectRuntime(nextRuntime: AgentRuntime) {
    if (nextRuntime === runtime.value && pendingRuntime === null) return
    const generation = beginSnapshotOperation()
    pendingRuntime = nextRuntime
    isRefreshing.value = true
    error.value = null
    try {
      const selected = await host.request<DashboardSnapshot>('runtime.select', { runtime: nextRuntime })
      if (isLatestSnapshotOperation(generation)) snapshot.value = selected
    } catch (reason) {
      if (isLatestSnapshotOperation(generation)) error.value = errorMessage(reason)
    } finally {
      if (isLatestSnapshotOperation(generation)) {
        pendingRuntime = null
        isRefreshing.value = false
      }
    }
  }

  return {
    snapshot, settings, settingsDraft, settingsDirty, todos, updateStatus, rateCatalog, isCheckingUpdates, isRunningLocalOperation, isUpdatingSettings, operationStatus, statusStripState, isControllingStatusStrip, appVersion,
    isLoading, isRefreshing, error, runtime, compactMode,
    combined, isLoadingCombined, combinedError, loadCombined,
    initialize, refresh, selectRuntime, saveSettings, resetSettingsDraft, toggleCompact, previewStatusStrip, recoverStatusStrip, refreshStatusStripState,
    addTodo, updateTodo, toggleTodo, deleteTodo, clearCompletedTodos, checkForUpdates, openReleasePage, runLocalOperation,
  }
})
