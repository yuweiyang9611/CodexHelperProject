import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { createApp, nextTick, type App } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import OverviewCards from '../../src/components/OverviewCards.vue'
import { CLAUDE_DESKTOP_QUERY_INSTRUCTION } from '../../src/quotaPresentation'
import { monthlyValue, quotaForecast, rateLimitWindow, snapshot, tokenBreakdown, tokenPeriod } from './fixtures'

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
  it('ticks to the reported reset without refreshing the query or replacing its percentage', async () => {
    vi.useFakeTimers()
    try {
      vi.setSystemTime(new Date('2026-11-01T14:27:00+09:00'))
      const card = mountOverview(snapshot({
        runtime: 'claudeCode',
        claudeQuotaState: { source: 'desktopQuery', queryStatus: 'fresh', historyExpired: false, action: 'none' },
        primaryQuota: rateLimitWindow({ source: 'claude-desktop-get-usage', remainingPercent: 73,
          resetsAt: '2026-11-01T14:30:15+09:00' }),
      })).querySelector('.quota-card')!
      const resetText = () => card.querySelector('.reset-time')?.textContent
      expect(resetText()).toContain('14:30 · 4 分钟后重置')
      await vi.advanceTimersByTimeAsync(60_000)
      expect(resetText()).toContain('14:30 · 3 分钟后重置')
      await vi.advanceTimersByTimeAsync(135_000)
      expect(resetText()).toContain('已到重置时间，等待新快照')
      expect(card.querySelector('.quota-content strong')?.textContent).toBe('73%')
      expect(card.querySelector('.query-actions button')?.textContent).toBe('复制查询指令')
    } finally {
      vi.useRealTimers()
    }
  })

  it('catches up immediately when the window becomes visible after a reset', async () => {
    vi.useFakeTimers()
    try {
      vi.setSystemTime(new Date('2026-11-01T14:27:00+09:00'))
      const card = mountOverview(snapshot({ primaryQuota: rateLimitWindow({ resetsAt: '2026-11-01T14:30:00+09:00' }) }))
      vi.setSystemTime(new Date('2026-11-01T14:31:00+09:00'))
      document.dispatchEvent(new Event('visibilitychange'))
      await nextTick()
      expect(card.querySelector('.reset-time')?.textContent).toBe('已到重置时间，等待新快照')
      expect(card.querySelector('.quota-caption')?.textContent).toBe('上次剩余')
      expect(card.querySelector('.query-actions')).toBeNull()
    } finally {
      vi.useRealTimers()
    }
  })

  it('directs an oversized transcript to a new binding', () => {
    const card = mountOverview(snapshot({
      runtime: 'claudeCode',
      claudeQuotaState: { source: 'none', queryStatus: 'tooLarge', historyExpired: false, action: 'rebind' },
    })).querySelector('.quota-card')!
    expect(card.textContent).toContain('绑定新会话')
    expect(card.querySelector('.query-actions')).toBeNull()
  })

  it('keeps unbound or older snapshots from offering an action based on diagnostic text', () => {
    const card = mountOverview(snapshot({
      runtime: 'claudeCode',
      diagnostics: ['Claude Desktop 原生额度：最新原生结果已过期；private-session'],
    })).querySelector('.quota-card')!
    expect(card.querySelector('.query-actions')).toBeNull()
    expect(card.textContent).not.toContain('private-session')
  })

  it('offers a new query for a valid response with no current windows', () => {
    const card = mountOverview(snapshot({
      runtime: 'claudeCode',
      claudeQuotaState: { source: 'desktopQuery', queryStatus: 'noWindows', historyExpired: false, action: 'query' },
    })).querySelector('.quota-card')!
    expect(card.textContent).toContain('查询成功')
    expect(card.querySelector('.query-actions button')?.textContent).toBe('复制查询指令')
  })
  it.each([0, 100])('shows the resolved manual monthly amount (%s)', amount => {
    const card = mountOverview(snapshot({ runtime: 'claudeCode', monthlyValue: monthlyValue({ subscriptionAmount: amount }) })).querySelector('.quota-card')!
    expect(card.textContent).toContain(`订阅月费 US$${amount.toFixed(2)}（手动）`)
    expect(card.textContent).not.toContain('订阅档位未识别')
  })

  it.each([
    [monthlyValue(), 'US$80.00', '待回本 · 尚差 US$20.00'],
    [monthlyValue({ isLowerBound: true, paybackStatus: 'unknown', netAmount: null, paybackMultiple: null, projectedAmount: null }), '≥ US$80.00', '暂无法判断是否回本'],
    [monthlyValue({ knownAmount: 120, isLowerBound: true, paybackStatus: 'reached', netAmount: 20, paybackMultiple: 1.2, projectedAmount: null }), '≥ US$120.00', '已回本 · 超出至少 US$20.00'],
    [monthlyValue({ subscriptionAmount: null, subscriptionSource: 'unknown', paybackStatus: 'unknown', netAmount: null, paybackMultiple: null }), 'US$80.00', '暂无法判断是否回本'],
  ] as const)('renders the host amount and payback status (%j)', async (reported, amount, status) => {
    const card = mountOverview(snapshot({ monthlyValue: reported })).querySelector('.value-card')!
    expect(card.querySelector('.value-main strong')?.textContent).toBe(amount)
    card.querySelector<HTMLElement>('.value-toggle')!.click()
    await nextTick()
    expect(card.querySelector('.value-bottom strong')?.textContent).toBe(status)
    if (reported.projectedAmount == null) expect(card.textContent).not.toContain('预计本月')
    if (reported.subscriptionSource === 'unknown') expect(card.textContent).toContain('无法推算')
  })

  it('copies only the fixed query instruction and displays the paired query time beside stale fallback data', async () => {
    const queryTime = '2026-11-01T09:00:00Z'
    const fallbackTime = '2026-11-01T10:01:00Z'
    const card = mountOverview(snapshot({
      runtime: 'claudeCode',
      claudeDesktopQueryObservedAt: queryTime,
      claudeQuotaState: { source: 'desktopHistory', queryStatus: 'stale', historyExpired: false, action: 'query' },
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
    ['stale', 'get_usage 结果已过期'],
    ['unavailable', 'get_usage 结果不可用'],
  ] as const)('explains a fallback from a bound query without exposing raw diagnostics (%s)', (queryStatus, expected) => {
    const container = mountOverview(snapshot({
      runtime: 'claudeCode',
      primaryQuota: rateLimitWindow({ source: 'claude-desktop-history', resetsAt: null, measuredAt: '2026-11-01T09:35:00Z' }),
      claudeQuotaState: { source: 'desktopHistory', queryStatus, historyExpired: false, action: 'query' },
      diagnostics: ['C:\\Users\\private-person\\session.jsonl id=private-session'],
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
      claudeQuotaState: { source: 'desktopQuery', queryStatus: 'fresh', historyExpired: false, action: 'none' },
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
      claudeQuotaState: { source: 'desktopHistory', queryStatus: 'unbound', historyExpired: false, action: 'none' },
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
      claudeQuotaState: { source: 'none', queryStatus: 'unbound', historyExpired: true, action: 'none' },
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
