import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { createApp, nextTick, type App } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import OverviewCards from '../../src/components/OverviewCards.vue'
import { CLAUDE_DESKTOP_QUERY_INSTRUCTION } from '../../src/quotaPresentation'
import { useDashboardStore } from '../../src/stores/dashboard'
import { appSettings, quotaForecast, rateLimitWindow, snapshot, tokenBreakdown, tokenPeriod } from './fixtures'

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
  it('directs an oversized transcript to a new binding', () => {
    const card = mountOverview(snapshot({
      runtime: 'claudeCode', diagnostics: ['Claude Desktop 原生额度：指定会话文件超过 32 MiB 安全上限'],
    })).querySelector('.quota-card')!
    expect(card.textContent).toContain('绑定新会话')
    expect(card.querySelector('.query-actions')).toBeNull()
  })
  it('shows the saved monthly amount, including zero, until automatic detection is enabled', async () => {
    const store = useDashboardStore(pinia)
    store.settings = appSettings({ claudeAutoDetectSubscriptionAmount: false, claudeMonthlySubscriptionAmount: 100 })
    const card = mountOverview(snapshot({ runtime: 'claudeCode' })).querySelector('.quota-card')!
    expect(card.textContent).toContain('订阅月费 US$100.00（手动）')
    expect(card.textContent).not.toContain('订阅档位未识别')
    store.settings.claudeMonthlySubscriptionAmount = 0
    await nextTick()
    expect(card.textContent).toContain('订阅月费 US$0.00（手动）')
    store.settings.claudeAutoDetectSubscriptionAmount = true
    await nextTick()
    expect(card.textContent).toContain('订阅档位未识别')
  })

  it('copies only the fixed query instruction and displays the paired query time beside stale fallback data', async () => {
    const queryTime = '2026-11-01T09:00:00Z'
    const fallbackTime = '2026-11-01T10:01:00Z'
    const card = mountOverview(snapshot({
      runtime: 'claudeCode',
      claudeDesktopQueryObservedAt: queryTime,
      primaryQuota: rateLimitWindow({ source: 'claude-desktop-history', measuredAt: fallbackTime }),
      diagnostics: ['Claude Desktop 原生额度：最新原生结果已过期；sensitive-session-id'],
    })).querySelector('.quota-card')!
    const update = card.querySelector('.query-actions')!
    expect(update.querySelector('time')?.dateTime).toBe(queryTime)
    expect(update.textContent).not.toContain(fallbackTime)
    const write = vi.spyOn(navigator.clipboard, 'writeText').mockResolvedValue()
    try {
      update.querySelector('button')!.click()
      await vi.waitFor(() => expect(update.textContent).toContain('已复制'))
      expect(write).toHaveBeenCalledWith(CLAUDE_DESKTOP_QUERY_INSTRUCTION)
      write.mockRejectedValueOnce(new Error('denied'))
      update.querySelector('button')!.click()
      await vi.waitFor(() => expect(update.querySelector('input')?.value).toBe(CLAUDE_DESKTOP_QUERY_INSTRUCTION))
    } finally {
      write.mockRestore()
    }
  })
  it.each([
    ['最新原生结果已过期（超过 15 分钟），请在 Desktop 重新获取用量', 'get_usage 结果已过期'],
    ['指定会话无法安全读取，未使用其中的旧结果', 'get_usage 结果不可用'],
  ])('explains a fallback from a bound query without exposing raw diagnostics (%s)', (reason, expected) => {
    const container = mountOverview(snapshot({
      runtime: 'claudeCode',
      primaryQuota: rateLimitWindow({ source: 'claude-desktop-history', resetsAt: null, measuredAt: '2026-11-01T09:35:00Z' }),
      diagnostics: [`Claude Desktop 原生额度：${reason}；C:\\Users\\private-person\\session.jsonl id=private-session`],
    }))

    const quotaCard = container.querySelector('.quota-card')
    expect(quotaCard?.textContent).toContain(expected)
    expect(quotaCard?.textContent).toContain('当前为历史采样')
    expect(quotaCard?.textContent).toContain('请在绑定的 Desktop 会话重新调用 get_usage')
    expect(quotaCard?.textContent).not.toContain('private-person')
    expect(quotaCard?.querySelector('.query-actions time')).toBeNull()
    expect(quotaCard?.textContent).not.toContain('private-session')
    expect(quotaCard?.textContent).not.toContain('C:\\')
  })

  it('explains how a bound Desktop query snapshot is refreshed without claiming the reset is missing', () => {
    const container = mountOverview(snapshot({
      runtime: 'claudeCode',
      primaryQuota: rateLimitWindow({ source: 'claude-desktop-get-usage', measuredAt: '2026-11-01T09:35:00Z', resetsAt: '2026-11-01T10:40:00Z' }),
    }))

    const quotaCard = container.querySelector('.quota-card')
    expect(quotaCard?.textContent).toContain('Desktop 查询快照')
    expect(quotaCard?.textContent).toContain('60 秒')
    expect(quotaCard?.textContent).toContain('15 分钟')
    expect(quotaCard?.textContent).toContain('get_usage')
    expect(quotaCard?.textContent).toContain('账号范围未确认')
    expect(quotaCard?.textContent).not.toContain('历史仅提供额度百分比')
  })

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
    expect(quotaCard?.querySelector('.query-actions')).toBeNull()
    expect(quotaCard?.textContent).not.toContain('private-person')
  })

  it.each(['claude-desktop-history', 'claude-desktop-get-usage'])(
    'does not turn an unscoped Desktop snapshot into a quota exhaustion alert (%s)', (source) => {
      const container = mountOverview(snapshot({
        runtime: 'claudeCode',
        primaryQuota: rateLimitWindow({ source, resetsAt: source === 'claude-desktop-get-usage' ? '2026-11-01T22:00:00Z' : null }),
        primaryForecast: quotaForecast({ exhaustsBeforeReset: true }),
      }))

      expect(container.querySelector('.quota-risk-notice')).toBeNull()
      expect(container.querySelector('.burn-down')).toBeNull()
    },
  )

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
