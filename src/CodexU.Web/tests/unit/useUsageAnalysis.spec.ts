import { effectScope, nextTick, ref, type EffectScope } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentRuntime, UsageAnalysisRequest, UsageAnalysisResult } from '../../src/types'
import { createDemoAnalysis } from '../../src/demoAnalysis'
const request = vi.hoisted(() => vi.fn())
vi.mock('../../src/host', () => ({ host: { request } }))
import { dateRange, useUsageAnalysis } from '../../src/composables/useUsageAnalysis'
let scope: EffectScope
const flush = async () => { await nextTick(); await Promise.resolve(); await nextTick() }
beforeEach(() => {
  vi.useFakeTimers(); vi.setSystemTime(new Date(2026, 8, 22, 12))
  request.mockReset().mockImplementation(async (_method: string, payload: UsageAnalysisRequest) => createDemoAnalysis(payload, new Date()))
  scope = effectScope()
})
afterEach(() => { scope.stop(); vi.useRealTimers() })
describe('usage analysis filtering and source consistency', () => {
  it('calculates inclusive local-day periods across month and year boundaries', () => {
    expect(dateRange('30', new Date(2026, 0, 1, 0, 30))).toEqual({ from: '2025-12-03', to: '2026-01-01' })
    expect(dateRange('month', new Date(2024, 1, 29, 12))).toEqual({ from: '2024-02-01', to: '2024-02-29' })
    expect(dateRange('all')).toEqual({ from: null, to: null })
  })
  it('combines filters without losing the range and resets pagination', async () => {
    const state = scope.run(() => useUsageAnalysis(ref<AgentRuntime>('codex'), ref('ready')))!
    await flush()
    expect(request).toHaveBeenLastCalledWith('usage.query', expect.objectContaining({ from: '2026-08-24', to: '2026-09-22', page: 1 }))
    state.page.value = 2; await flush()
    state.model.value = 'gpt-6-astra'; state.project.value = 'D:\\Projects\\codexU'; await flush()
    expect(request).toHaveBeenLastCalledWith('usage.query', expect.objectContaining({ from: '2026-08-24', to: '2026-09-22', model: 'gpt-6-astra', project: 'D:\\Projects\\codexU', page: 1 }))
    expect(state.result.value!.projects.length).toBe(1)
    const calls = request.mock.calls.length
    state.metric.value = 'amount'; await flush()
    expect(request.mock.calls.length).toBe(calls)
  })
  it('drops stale runtime results and clears stale data while loading', async () => {
    const pending: ((value: UsageAnalysisResult) => void)[] = []
    request.mockImplementation(() => new Promise<UsageAnalysisResult>(resolve => pending.push(resolve)))
    const runtime = ref<AgentRuntime>('codex')
    const state = scope.run(() => useUsageAnalysis(runtime, ref('ready')))!
    runtime.value = 'claudeCode'; await flush()
    pending[1]!(createDemoAnalysis({ runtime: 'claudeCode', page: 1, pageSize: 25 }, new Date())); await flush()
    pending[0]!(createDemoAnalysis({ runtime: 'codex', page: 1, pageSize: 25 }, new Date())); await flush()
    expect(state.result.value!.runtime).toBe('claudeCode')
    state.period.value = 'all'; await flush()
    expect(state.result.value).toBeNull()
    expect(state.loading.value).toBe(true)
  })
  it('does not query an invalid custom range and recovers after correction', async () => {
    const state = scope.run(() => useUsageAnalysis(ref<AgentRuntime>('codex'), ref('ready')))!
    await flush(); const calls = request.mock.calls.length
    state.period.value = 'custom'; state.customFrom.value = '2026-09-23'; state.customTo.value = '2026-09-22'; await flush()
    expect(request.mock.calls.length).toBe(calls)
    expect(state.error.value).toContain('开始日期不能晚于结束日期')
    state.customFrom.value = '2026-09-22'; await flush()
    expect(state.error.value).toBeNull()
    expect(state.result.value).not.toBeNull()
  })
  it('advances rolling dates on refresh after local midnight', async () => {
    const revision = ref('one')
    scope.run(() => useUsageAnalysis(ref<AgentRuntime>('codex'), revision))
    await flush(); vi.setSystemTime(new Date(2026, 8, 23, 0, 1)); revision.value = 'two'; await flush()
    expect(request).toHaveBeenLastCalledWith('usage.query', expect.objectContaining({ from: '2026-08-25', to: '2026-09-23' }))
  })
})
