import { computed, onScopeDispose, ref, watch, type Ref } from 'vue'
import { host } from '../host'
import type { AgentRuntime, UsageAnalysisRequest, UsageAnalysisResult } from '../types'

export type UsagePeriod = 'today' | '7' | '30' | 'month' | 'all' | 'custom'
export type UsageMetric = 'tokens' | 'amount'

export function localDate(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`
}

export function dateRange(period: UsagePeriod, now = new Date()): { from: string | null, to: string | null } {
  if (period === 'all') return { from: null, to: null }
  const start = new Date(now)
  if (period === 'month') start.setDate(1)
  else start.setDate(start.getDate() - (period === '7' ? 6 : period === '30' ? 29 : 0))
  return { from: localDate(start), to: localDate(now) }
}

export function useUsageAnalysis(runtime: Ref<AgentRuntime>, revision: Ref<unknown>) {
  const period = ref<UsagePeriod>('30')
  const customFrom = ref(dateRange('30').from!)
  const customTo = ref(localDate(new Date()))
  const model = ref('')
  const project = ref('')
  const metric = ref<UsageMetric>('tokens')
  const page = ref(1)
  const result = ref<UsageAnalysisResult | null>(null)
  const options = ref<{ models: string[], projects: { id: string, label: string }[] }>({ models: [], projects: [] })
  const loading = ref(false)
  const error = ref<string | null>(null)
  let generation = 0
  let disposed = false
  const range = computed(() => {
    // A host refresh after midnight advances rolling periods without resetting filters.
    void revision.value
    return period.value === 'custom' ? { from: customFrom.value, to: customTo.value } : dateRange(period.value)
  })
  const validation = computed(() => period.value === 'custom' && (!customFrom.value || !customTo.value || customFrom.value > customTo.value)
    ? '请选择有效的开始和结束日期；开始日期不能晚于结束日期。' : null)
  const request = computed<UsageAnalysisRequest>(() => ({ runtime: runtime.value,
    ...range.value, model: model.value || null, project: project.value || null, page: page.value, pageSize: 25 }))

  async function refresh() {
    const current = ++generation
    result.value = null
    error.value = validation.value
    if (validation.value) { loading.value = false; return }
    if (!revision.value) { loading.value = false; return }
    loading.value = true
    try {
      const data = await host.request<UsageAnalysisResult>('usage.query', request.value)
      if (current === generation && !disposed) {
        result.value = data
        options.value = { models: data.availableModels, projects: data.availableProjects }
      }
    } catch (reason) {
      if (current === generation && !disposed) error.value = reason instanceof Error ? reason.message : String(reason)
    } finally { if (current === generation && !disposed) loading.value = false }
  }
  watch(runtime, () => { model.value = ''; project.value = ''; page.value = 1; options.value = { models: [], projects: [] } }, { flush: 'sync' })
  watch([period, customFrom, customTo, model, project], () => { page.value = 1 }, { flush: 'sync' })
  watch([request, revision], refresh, { immediate: true })
  onScopeDispose(() => { disposed = true; generation++ })
  function selectDate(date: string) { customFrom.value = date; customTo.value = date; period.value = 'custom' }
  return { period, customFrom, customTo, model, project, metric, page, result, options, loading, error, range, refresh, selectDate }
}

export type UsageAnalysisState = ReturnType<typeof useUsageAnalysis>
