import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { createApp, type App } from 'vue'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import OverviewCards from '../../src/components/OverviewCards.vue'
import { quotaForecast, rateLimitWindow, snapshot, tokenBreakdown, tokenPeriod } from './fixtures'

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
})

function mountOverview(overviewSnapshot: ReturnType<typeof snapshot>) {
  const container = document.createElement('div')
  document.body.append(container)
  app = createApp(OverviewCards, { snapshot: overviewSnapshot })
  app.use(pinia)
  app.mount(container)
  return container
}

describe('OverviewCards local token activity', () => {
  it('explains Desktop sample limits and missing subscription identification beside the quota', () => {
    const container = mountOverview(snapshot({
      runtime: 'claudeCode',
      primaryQuota: rateLimitWindow({ source: 'claude-desktop-history', resetsAt: null, measuredAt: '2026-11-01T09:35:00Z' }),
    }))

    const quotaCard = container.querySelector('.quota-card')
    expect(quotaCard?.textContent).toContain('本地 Claude Desktop 历史仅提供额度百分比')
    expect(quotaCard?.textContent).toContain('超过 30 分钟')
    expect(quotaCard?.textContent).toContain('订阅档位未识别')
    expect(quotaCard?.textContent).toContain('设置与诊断')
    expect(quotaCard?.textContent).toContain('不代表实际账单')
  })

  it('summarizes an expired source without copying private diagnostic paths into the overview', () => {
    const container = mountOverview(snapshot({
      runtime: 'claudeCode',
      account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: 20 },
      diagnostics: ['Claude Desktop 额度：历史采样已过期（超过 30 分钟）：C:\\Users\\private-person\\usage.json'],
    }))

    const quotaCard = container.querySelector('.quota-card')
    expect(quotaCard?.textContent).toContain('Desktop 历史采样已过期')
    expect(quotaCard?.textContent).toContain('刷新用量')
    expect(quotaCard?.textContent).not.toContain('private-person')
    expect(quotaCard?.textContent).not.toContain('C:\\')
    expect(quotaCard?.querySelector('.ring-progress')).toBeNull()
  })

  it('uses a short safe message when no Claude quota source is available', () => {
    const container = mountOverview(snapshot({
      runtime: 'claudeCode',
      diagnostics: ['读取失败 C:\\Users\\private-person\\settings.json'],
    }))

    const quotaCard = container.querySelector('.quota-card')
    expect(quotaCard?.textContent).toContain('当前没有可用的 Claude 额度采样')
    expect(quotaCard?.textContent).toContain('终端 Claude Code')
    expect(quotaCard?.textContent).not.toContain('private-person')
  })

  it('does not turn a Desktop percentage sample into a quota exhaustion alert', () => {
    const container = mountOverview(snapshot({
      runtime: 'claudeCode',
      primaryQuota: rateLimitWindow({ source: 'claude-desktop-history', resetsAt: null }),
      primaryForecast: quotaForecast({ exhaustsBeforeReset: true }),
    }))

    expect(container.querySelector('.quota-risk-notice')).toBeNull()
    expect(container.querySelector('.burn-down')).toBeNull()
  })

  it('uses the local JSONL periods for every token metric', () => {
    const container = mountOverview(snapshot({
      tokens: {
        today: tokenPeriod({ tokens: 2_480_545_769 }),
        sevenDays: tokenPeriod({ tokens: 2_900_000_000 }),
        month: tokenPeriod({ tokens: 3_100_000_000 }),
        lifetime: tokenPeriod({ tokens: 3_758_082_435 }),
      },
    }))

    const tokenCard = container.querySelector('.token-card')
    expect(tokenCard?.textContent).toContain('本机原始统计')
    expect(tokenCard?.textContent).toContain('今日2.48B')
    expect(tokenCard?.textContent).toContain('近 7 天2.90B')
    expect(tokenCard?.textContent).toContain('本月3.10B')
    expect(tokenCard?.textContent).toContain('累计3.76B')
    expect(tokenCard?.textContent).toContain('来自本机日志原始事件')
    expect(tokenCard?.textContent).not.toContain('官方账户统计')
  })

  it('shows each token split as a share of the visible total', () => {
    const container = mountOverview(snapshot({
      tokens: {
        today: tokenPeriod({
          breakdown: tokenBreakdown({
            uncachedInputTokens: 500,
            billableCachedInputTokens: 300,
            outputTokens: 200,
          }),
        }),
        sevenDays: tokenPeriod(),
        month: tokenPeriod(),
        lifetime: tokenPeriod(),
      },
    }))

    const legend = container.querySelector('.split-legend')
    expect(legend?.textContent).toContain('未缓存输入 500 · 50.0%')
    expect(legend?.textContent).toContain('缓存读取 300 · 30.0%')
    expect(legend?.textContent).toContain('输出 200 · 20.0%')
  })

  it('includes cache writes in the token split denominator', () => {
    const container = mountOverview(snapshot({
      tokens: {
        today: tokenPeriod({
          breakdown: tokenBreakdown({
            uncachedInputTokens: 400,
            billableCachedInputTokens: 200,
            billableCacheWriteTokens: 200,
            outputTokens: 200,
          }),
        }),
        sevenDays: tokenPeriod(),
        month: tokenPeriod(),
        lifetime: tokenPeriod(),
      },
    }))

    const legend = container.querySelector('.split-legend')
    expect(legend?.textContent).toContain('未缓存输入 400 · 40.0%')
    expect(legend?.textContent).toContain('缓存读取 200 · 20.0%')
    expect(legend?.textContent).toContain('缓存写入 200 · 20.0%')
    expect(legend?.textContent).toContain('输出 200 · 20.0%')
  })
})
