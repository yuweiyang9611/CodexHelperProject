import { createApp, nextTick, type App } from 'vue'
import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import CombinedRuntimeView from '../../src/views/CombinedRuntimeView.vue'
import { useDashboardStore } from '../../src/stores/dashboard'
import { monthlyValue, appSettings, rateLimitWindow, runtimeRead, snapshot, tokenPeriod } from './fixtures'

import type { CombinedRuntimeSummary } from '../../src/types'

function summary(cost: Partial<CombinedRuntimeSummary['cost']> = {}): CombinedRuntimeSummary {
  const period = { tokens: 100, creditsUsed: 100, unratedTokens: 0, amount: 4, quality: 'detailed' as const, creditsAreLowerBound: false }
  return { periods: { today: period, sevenDays: period, month: period },
    cost: { knownAmount: 80, isLowerBound: false, subscriptionAmount: 300, unknownSubscriptions: [],
      hasManual: true, paybackReason: 'none', ...cost },
    projects: [], diagnostics: [], forecasts: [], failedRuntimes: [] }
}

let pinia: Pinia
let app: App | null

beforeEach(() => {
  pinia = createPinia()
  setActivePinia(pinia)
  app = null
})

afterEach(() => {
  app?.unmount()
  document.body.replaceChildren()
  useDashboardStore(pinia).$dispose()
})

describe('combined subscription amounts', () => {
  it.each([
    ['claude-desktop-history', '本地 Claude Desktop 历史仅提供额度百分比', '30 分钟'],
    ['claude-desktop-get-usage', 'Desktop 查询快照', '15 分钟'],
  ])('shows Desktop source limits and subscription guidance in the Claude column (%s)', (source, notice, expiry) => {
    const store = useDashboardStore(pinia)
    store.settings = appSettings()
    store.combined = {
      summary: summary(),
      codex: runtimeRead({ snapshot: snapshot({ runtime: 'codex', primaryQuota: rateLimitWindow() }) }),
      claudeCode: runtimeRead({ snapshot: snapshot({
        runtime: 'claudeCode',
        primaryQuota: rateLimitWindow({ source, resetsAt: source === 'claude-desktop-get-usage' ? '2026-11-01T22:00:00Z' : null, measuredAt: '2026-11-01T09:35:00Z' }),
        claudeQuotaState: source === 'claude-desktop-get-usage'
          ? { source: 'desktopQuery', queryStatus: 'fresh', historyExpired: false, action: 'none' }
          : { source: 'desktopHistory', queryStatus: 'unbound', historyExpired: false, action: 'none' },
        tokens: { ...snapshot().tokens, month: tokenPeriod({ tokens: 100 }) },
      }) }),
    }
    const container = document.createElement('div')
    document.body.append(container)
    app = createApp(CombinedRuntimeView)
    app.use(pinia)
    app.mount(container)

    const columns = container.querySelectorAll('.combined-runtime-column')
    expect(columns[1]?.textContent).toContain(notice)
    expect(columns[1]?.textContent).toContain(expiry)
    expect(columns[1]?.textContent).toContain('订阅档位未识别')
    expect(columns[1]?.textContent).toContain('设置与诊断')
    expect(columns[0]?.querySelector('.runtime-data-notice')).toBeNull()
  })

  it('shows a combined lower bound and only reports payback when the known amount reaches the price', async () => {
    const store = useDashboardStore(pinia)
    const partial = monthlyValue({ knownAmount: 80, isLowerBound: true, subscriptionAmount: 100,
      paybackStatus: 'unknown', netAmount: null, paybackMultiple: null })
    store.combined = {
      summary: summary({ isLowerBound: true, subscriptionAmount: 100, paybackReason: 'incompleteUsage' }),
      codex: runtimeRead({ snapshot: snapshot({ runtime: 'codex' }) }),
      claudeCode: runtimeRead({ snapshot: snapshot({ runtime: 'claudeCode',
        tokens: { ...snapshot().tokens, month: tokenPeriod({ tokens: 100 }) }, monthlyValue: partial }) }),
    }
    const container = document.createElement('div')
    app = createApp(CombinedRuntimeView)
    app.use(pinia)
    app.mount(container)
    const costs = [...container.querySelectorAll('.combined-card')].find(card => card.querySelector('h2')?.textContent === '合计成本')!
    expect(costs.textContent).toContain('≥ US$80.00')
    expect(costs.textContent).toContain('暂无法判断是否回本')
    expect(costs.textContent).not.toContain('0.8×')

    store.combined.summary = summary({ knownAmount: 120, isLowerBound: true, subscriptionAmount: 100, paybackMultiple: 1.2, netAmount: 20 })
    store.combined.claudeCode.snapshot.monthlyValue = { ...partial, knownAmount: 120, netAmount: 20,
      paybackMultiple: 1.2, paybackStatus: 'reached' }
    await nextTick()
    expect(costs.textContent).toContain('≥ 1.2×')
    expect(costs.textContent).toContain('净等价 ≥ +US$20.00')
  })

  it('updates manual amounts from saved settings and labels the individual and combined estimates', async () => {
    const store = useDashboardStore(pinia)
    store.settings = appSettings({
      claudeAutoDetectSubscriptionAmount: false,
      claudeMonthlySubscriptionAmount: 100,
    })
    store.combined = {
      summary: summary(),
      codex: runtimeRead({ snapshot: snapshot({
        runtime: 'codex', account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: 200 },
        monthlyValue: monthlyValue({ subscriptionSource: 'auto', subscriptionAmount: 200 }),
      }) }),
      claudeCode: runtimeRead({ snapshot: snapshot({
        runtime: 'claudeCode', monthlyValue: monthlyValue(), tokens: { ...snapshot().tokens, month: tokenPeriod({ tokens: 100 }) },
        primaryQuota: rateLimitWindow({ source: 'claude-desktop-get-usage', resetsAt: '2026-11-01T22:00:00Z' }),
      }) }),
    }
    const container = document.createElement('div')
    document.body.append(container)
    app = createApp(CombinedRuntimeView)
    app.use(pinia)
    app.mount(container)

    const claude = container.querySelectorAll('.combined-runtime-column')[1]!
    const costs = [...container.querySelectorAll('.combined-card')]
      .find(card => card.querySelector('h2')?.textContent === '合计成本')!
    expect(claude.textContent).toContain('US$100.00 · 手动')
    expect(claude.textContent).not.toContain('订阅档位未识别')
    expect(costs.textContent).toContain('US$300.00')
    expect(costs.textContent).toContain('含手动值')
    expect(costs.textContent).toContain('不代表实际账单')

    store.combined.summary = summary({ subscriptionAmount: 400 })
    store.combined.claudeCode.snapshot.monthlyValue = monthlyValue({ subscriptionAmount: 200 })
    await nextTick()
    expect(claude.textContent).toContain('US$200.00 · 手动')
    expect(costs.textContent).toContain('US$400.00')

    store.combined.summary = summary({ subscriptionAmount: null, hasManual: false, unknownSubscriptions: ['claudeCode'], paybackReason: 'unknownSubscription' })
    store.combined.claudeCode.snapshot.monthlyValue = monthlyValue({ subscriptionSource: 'unknown', subscriptionAmount: null })
    await nextTick()
    expect(claude.textContent).toContain('无法推算')
    expect(claude.textContent).toContain('订阅档位未识别')
    expect(costs.textContent).not.toContain('US$400.00')
    expect(costs.textContent).not.toContain('含手动值')
  })
})
