import { onMounted, onUnmounted, ref, watch, type Ref } from 'vue'
import { host } from '../host'
import type { AntigravityQuotaSnapshot } from '../types'

/**
 * Whether an Antigravity quota view is on screen. Each query starts the agy CLI, so
 * polling follows what is visible: the provider's own page (or the compact view that
 * replaces it) and the combined page's Antigravity column — never settings.
 */
export function antigravityQuotaVisible(state: { antigravitySelected: boolean; compactMode: boolean; activeTab: string }): boolean {
  if (state.antigravitySelected && (state.compactMode || state.activeTab === 'antigravity')) return true
  return !state.compactMode && state.activeTab === 'combined'
}

export function useAntigravityQuota(active: Ref<boolean>, refreshMinutes: Ref<number>) {
  const quota = ref<AntigravityQuotaSnapshot | null>(null)
  const loading = ref(false)
  let timer: number | undefined
  let lastChecked: number | undefined
  let mounted = false

  function schedule() {
    window.clearTimeout(timer)
    if (!mounted || !active.value || document.hidden || loading.value) return
    const interval = Math.max(1, refreshMinutes.value) * 60_000
    const elapsed = lastChecked == null ? interval : Math.max(0, Date.now() - lastChecked)
    if (elapsed >= interval) void refresh()
    else timer = window.setTimeout(() => void refresh(), interval - elapsed)
  }

  /** `force` is a user's explicit refresh, which runs even while no view polls. */
  async function refresh(force = false) {
    if (!mounted || (!force && !active.value) || loading.value || document.hidden) return
    window.clearTimeout(timer)
    loading.value = true
    try {
      const result = await host.request<AntigravityQuotaSnapshot>('antigravity.getQuota')
      if (mounted) quota.value = result
    } catch {
      if (mounted) quota.value = { status: 'unavailable', groups: [] }
    } finally {
      if (mounted) {
        lastChecked = Date.now()
        loading.value = false
        schedule()
      }
    }
  }

  watch([active, refreshMinutes], schedule)
  onMounted(() => {
    mounted = true
    document.addEventListener('visibilitychange', schedule)
    schedule()
  })
  onUnmounted(() => {
    mounted = false
    window.clearTimeout(timer)
    document.removeEventListener('visibilitychange', schedule)
  })
  return { quota, loading, refresh }
}
