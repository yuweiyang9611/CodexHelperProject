import { createApp, nextTick, type App } from 'vue'
import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import CombinedRuntimeView from '../../src/views/CombinedRuntimeView.vue'
import { useDashboardStore } from '../../src/stores/dashboard'
import { appSettings, rateLimitWindow, runtimeRead, snapshot, tokenPeriod } from './fixtures'

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
  it('shows the same Desktop source limits and subscription guidance in the Claude column', () => {
    const store = useDashboardStore(pinia)
    store.settings = appSettings()
    store.combined = {
      codex: runtimeRead({ snapshot: snapshot({ runtime: 'codex', primaryQuota: rateLimitWindow() }) }),
      claudeCode: runtimeRead({ snapshot: snapshot({
        runtime: 'claudeCode',
        primaryQuota: rateLimitWindow({ source: 'claude-desktop-history', resetsAt: null, measuredAt: '2026-11-01T09:35:00Z' }),
        tokens: { ...snapshot().tokens, month: tokenPeriod({ tokens: 100 }) },
      }) }),
    }
    const container = document.createElement('div')
    document.body.append(container)
    app = createApp(CombinedRuntimeView)
    app.use(pinia)
    app.mount(container)

    const columns = container.querySelectorAll('.combined-runtime-column')
    expect(columns[1]?.textContent).toContain('本地 Claude Desktop 历史仅提供额度百分比')
    expect(columns[1]?.textContent).toContain('超过 30 分钟')
    expect(columns[1]?.textContent).toContain('订阅档位未识别')
    expect(columns[1]?.textContent).toContain('设置与诊断')
    expect(columns[0]?.querySelector('.runtime-data-notice')).toBeNull()
  })

  it('updates manual amounts from saved settings and labels the individual and combined estimates', async () => {
    const store = useDashboardStore(pinia)
    store.settings = appSettings({
      claudeAutoDetectSubscriptionAmount: false,
      claudeMonthlySubscriptionAmount: 100,
    })
    store.combined = {
      codex: runtimeRead({ snapshot: snapshot({
        runtime: 'codex', account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: 200 },
      }) }),
      claudeCode: runtimeRead({ snapshot: snapshot({
        runtime: 'claudeCode', tokens: { ...snapshot().tokens, month: tokenPeriod({ tokens: 100 }) },
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
    expect(costs.textContent).toContain('US$300.00')
    expect(costs.textContent).toContain('含手动值')
    expect(costs.textContent).toContain('不代表实际账单')

    store.settings.claudeMonthlySubscriptionAmount = 200
    await nextTick()
    expect(claude.textContent).toContain('US$200.00 · 手动')
    expect(costs.textContent).toContain('US$400.00')

    store.settings.claudeAutoDetectSubscriptionAmount = true
    await nextTick()
    expect(claude.textContent).toContain('无法推算')
    expect(costs.textContent).not.toContain('US$400.00')
    expect(costs.textContent).not.toContain('含手动值')
  })
})
