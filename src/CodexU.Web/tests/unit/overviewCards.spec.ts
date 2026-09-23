import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { createApp, nextTick, type App } from 'vue'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import OverviewCards from '../../src/components/OverviewCards.vue'
import { createDemoAnalysis } from '../../src/demoAnalysis'
import { snapshot } from './fixtures'
let pinia: Pinia
let app: App | null
beforeEach(() => { pinia = createPinia(); setActivePinia(pinia); app = null; localStorage.clear() })
afterEach(() => { app?.unmount(); document.body.replaceChildren() })
function mountOverview(analysis = createDemoAnalysis({ runtime: 'codex', page: 1, pageSize: 25, from: '2026-07-14', to: '2026-07-14' }, new Date('2026-07-14T12:00:00+09:00'))) {
  const container = document.createElement('div'); document.body.append(container)
  app = createApp(OverviewCards, { snapshot: snapshot(), analysis }); app.use(pinia); app.mount(container)
  return container
}
describe('OverviewCards uses unified analysis', () => {
  it('shows queried totals rather than the independent snapshot periods', () => {
    const container = mountOverview()
    expect(container.querySelector('.token-card')?.textContent).toContain('总量58.0K')
    expect(container.querySelector('.token-card')?.textContent).toContain('会话组1')
    expect(container.querySelector('.token-card')?.textContent).toContain('缓存已包含在输入中')
    expect(container.querySelector('.token-card')?.textContent).toContain('其中推理输出未知')
    expect(container.querySelector('.quota-card')?.textContent).toContain('账户额度 · 独立于本机筛选')
  })
  it('never renders unavailable old token components or unknown amount as zero', () => {
    const analysis = createDemoAnalysis({ runtime: 'codex', page: 1, pageSize: 25, model: 'unknown' }, new Date('2026-07-14T12:00:00+09:00'))
    analysis.totals.availableBreakdownFields = []
    const container = mountOverview(analysis)
    expect(container.querySelector('.value-main')?.textContent).toContain('金额未知')
    expect(container.textContent).toContain('Token 分项不可用')
    expect(container.textContent).not.toContain('输入（含缓存）0')
  })
  it('labels a partially priced estimate as a lower bound and keeps legacy estimate separate', async () => {
    const analysis = createDemoAnalysis({ runtime: 'codex', page: 1, pageSize: 25 }, new Date('2026-07-14T12:00:00+09:00'))
    const container = mountOverview(analysis)
    expect(container.querySelector('.value-main')?.textContent).toContain('≥')
    ;(container.querySelector('.value-toggle') as HTMLButtonElement).click(); await nextTick()
    expect(container.textContent).toContain('旧版金额估值')
    expect(container.textContent).not.toMatch(/回本|净等价|净收益/)
    expect(container.querySelector('.value-summary-grid')).toBeNull()
  })
  it('shows subscription comparison only after the optional preference is enabled', async () => {
    localStorage.setItem('codexu.subscriptionComparison', 'true')
    const container = mountOverview()
    ;(container.querySelector('.value-toggle') as HTMLButtonElement).click(); await nextTick()
    expect(container.textContent).toContain('订阅月费参考')
    expect(container.textContent).toContain('所选日期范围可能不足或超过一个月')
  })
  it('uses missing-rate diagnostics across all pages rather than treating any unknown amount as a rate gap', async () => {
    const analysis = createDemoAnalysis({ runtime: 'codex', page: 1, pageSize: 25 }, new Date('2026-07-14T12:00:00+09:00'))
    analysis.totals.creditsUsed = null
    analysis.totals.unratedTokens = analysis.totals.tokens
    analysis.missingRates = [{ model: 'unpriced-model', date: '2026-07-01', tokens: 42 }]
    const container = mountOverview(analysis)
    ;(container.querySelector('.value-toggle') as HTMLButtonElement).click(); await nextTick()
    expect(container.textContent).toContain('所选范围缺少适用日期的费率：unpriced-model')
    expect(container.textContent).not.toContain('费率：gpt-6-astra')
  })
})
