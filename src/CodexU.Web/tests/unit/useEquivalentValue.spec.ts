import { createPinia, setActivePinia } from 'pinia'
import { computed, ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useEquivalentValue } from '../../src/composables/useEquivalentValue'
import { useDashboardStore } from '../../src/stores/dashboard'
import type { AppSettings, DashboardSnapshot } from '../../src/types'
import { appSettings, modelCredits, snapshot, tokenBreakdown, tokenPeriod } from './fixtures'

function mount(current: DashboardSnapshot | null, settings: Partial<AppSettings> = {}) {
  const store = useDashboardStore()
  store.settings = appSettings(settings)
  const source = ref(current)
  return {
    store,
    source,
    value: useEquivalentValue(computed(() => source.value)),
  }
}

beforeEach(() => {
  setActivePinia(createPinia())
})

afterEach(() => {
  vi.useRealTimers()
})

describe('monthly amount', () => {
  it('is zero when there is no snapshot', () => {
    const { value } = mount(null)
    expect(value.monthlyCredits.value).toBe(0)
    expect(value.monthlyAmount.value).toBe(0)
  })

  it('converts credits at the configured rate', () => {
    const { value } = mount(
      snapshot({
        tokens: {
          today: tokenPeriod(),
          sevenDays: tokenPeriod(),
          month: tokenPeriod({ creditsUsed: 12_500 }),
          lifetime: tokenPeriod(),
        },
      }),
    )
    // 12,500 credits / 1,000 * US$40.
    expect(value.monthlyAmount.value).toBe(500)
  })

  it('follows a changed rate setting', () => {
    const { store, value } = mount(
      snapshot({
        tokens: {
          today: tokenPeriod(),
          sevenDays: tokenPeriod(),
          month: tokenPeriod({ creditsUsed: 1_000 }),
          lifetime: tokenPeriod(),
        },
      }),
    )
    expect(value.monthlyAmount.value).toBe(40)

    store.settings = appSettings({ amountPerThousandCredits: 10 })
    expect(value.monthlyAmount.value).toBe(10)
  })
})

describe('subscription amount resolution', () => {
  const withSuggestion = (suggested?: number) => snapshot({
    account: {
      isAuthenticated: true,
      planType: 'pro',
      suggestedMonthlySubscriptionAmount: suggested,
    },
  })

  it('prefers the auto-detected plan amount', () => {
    const { value } = mount(withSuggestion(200))
    expect(value.subscriptionAmountIsAuto.value).toBe(true)
    expect(value.subscriptionAmount.value).toBe(200)
    expect(value.subscriptionSourceLabel.value).toContain('PRO')
  })

  it('falls back to the manual value when the plan cannot be inferred', () => {
    const { value } = mount(withSuggestion(undefined), { codexMonthlySubscriptionAmount: 123 })
    expect(value.subscriptionAmountIsAuto.value).toBe(false)
    expect(value.subscriptionAmount.value).toBe(123)
    expect(value.subscriptionSourceLabel.value).toBe('当前套餐无法可靠推算，使用设置中的手动备用值')
  })

  it('uses the manual value when auto-detect is switched off, even if a suggestion exists', () => {
    const { value } = mount(withSuggestion(200), {
      codexAutoDetectSubscriptionAmount: false,
      codexMonthlySubscriptionAmount: 77,
    })
    expect(value.subscriptionAmountIsAuto.value).toBe(false)
    expect(value.subscriptionAmount.value).toBe(77)
    expect(value.subscriptionSourceLabel.value).toBe('使用设置中的手动值')
  })

  it('takes the manual fallback from the runtime on screen', () => {
    // The bug this replaces: one shared field defaulting to 200, a ChatGPT price.
    // Claude's plan is only auto-priceable when the statusline snapshot exists, so a
    // Claude user routinely fell through to a US$200 subscription — and to a payback
    // multiple computed against a bill they do not pay.
    const settings = { codexMonthlySubscriptionAmount: 200, claudeMonthlySubscriptionAmount: 20 }
    const unpriceable = (runtime: 'codex' | 'claudeCode') => snapshot({
      runtime,
      account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: undefined },
    })

    expect(mount(unpriceable('codex'), settings).value.subscriptionAmount.value).toBe(200)
    expect(mount(unpriceable('claudeCode'), settings).value.subscriptionAmount.value).toBe(20)
  })

  it('keeps the two manual fallbacks independent', () => {
    const settings = { codexMonthlySubscriptionAmount: 200, claudeMonthlySubscriptionAmount: 100 }
    const claude = snapshot({
      runtime: 'claudeCode',
      account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: undefined },
    })

    // A Max subscriber setting 100 for Claude must not have it read as Codex's 200.
    expect(mount(claude, settings).value.subscriptionAmount.value).toBe(100)
  })

  it('keeps each runtime on its own auto-detect flag', () => {
    // A shared flag meant typing into the box labelled 'Claude Code 订阅月费' also
    // switched Codex off auto-detection, so the Codex tab abandoned a price the app
    // server had reliably reported and fell back to a stale manual number.
    const settings = {
      codexAutoDetectSubscriptionAmount: true,
      claudeAutoDetectSubscriptionAmount: false,
      codexMonthlySubscriptionAmount: 77,
      claudeMonthlySubscriptionAmount: 88,
    }
    const priced = (runtime: 'codex' | 'claudeCode', amount: number) => snapshot({
      runtime,
      account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: amount },
    })

    // Codex keeps auto-detection and its detected price.
    const codex = mount(priced('codex', 200), settings).value
    expect(codex.subscriptionAmountIsAuto.value).toBe(true)
    expect(codex.subscriptionAmount.value).toBe(200)

    // Claude, switched off, uses its own manual figure — not Codex's.
    const claude = mount(priced('claudeCode', 20), settings).value
    expect(claude.subscriptionAmountIsAuto.value).toBe(false)
    expect(claude.subscriptionAmount.value).toBe(88)
  })

  it('labels an unknown plan without inventing a name', () => {
    const { value } = mount(
      snapshot({ account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: 20 } }),
    )
    expect(value.subscriptionSourceLabel.value).toContain('本机套餐')
  })
})

describe('net value and payback', () => {
  const monthOf = (creditsUsed: number) => snapshot({
    account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: 200 },
    tokens: {
      today: tokenPeriod(),
      sevenDays: tokenPeriod(),
      month: tokenPeriod({ creditsUsed }),
      lifetime: tokenPeriod(),
    },
  })

  it('reports a loss before break-even', () => {
    const { value } = mount(monthOf(2_500)) // US$100 against a US$200 plan.
    expect(value.netEquivalentAmount.value).toBe(-100)
    expect(value.paybackMultiple.value).toBe(0.5)
    expect(value.valueProgress.value).toBe(50)
  })

  it('reports a gain past break-even and clamps the progress bar at 100', () => {
    const { value } = mount(monthOf(20_000)) // US$800 against a US$200 plan.
    expect(value.netEquivalentAmount.value).toBe(600)
    expect(value.paybackMultiple.value).toBe(4)
    expect(value.valueProgress.value).toBe(100)
  })

  it('avoids dividing by a zero subscription amount', () => {
    const { value } = mount(
      snapshot({
        account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: 0 },
        tokens: {
          today: tokenPeriod(),
          sevenDays: tokenPeriod(),
          month: tokenPeriod({ creditsUsed: 1_000 }),
          lifetime: tokenPeriod(),
        },
      }),
      { codexAutoDetectSubscriptionAmount: false, codexMonthlySubscriptionAmount: 0 },
    )
    expect(value.paybackMultiple.value).toBeNull()
    expect(value.valueProgress.value).toBe(100)
  })
})

describe('credit breakdown', () => {
  it('sums each credit class across models', () => {
    const { value } = mount(
      snapshot({
        tokens: {
          today: tokenPeriod(),
          sevenDays: tokenPeriod(),
          month: tokenPeriod({
            creditsByModel: [
              modelCredits({
                model: 'gpt-5.2',
                inputCredits: 10,
                cachedInputCredits: 2,
                cacheWriteCredits: 7,
                outputCredits: 30,
                cachedSavingsCredits: 5,
              }),
              modelCredits({
                model: 'gpt-5.2-mini',
                inputCredits: 1,
                cachedInputCredits: 3,
                cacheWriteCredits: 8,
                outputCredits: 4,
                cachedSavingsCredits: 6,
              }),
            ],
          }),
          lifetime: tokenPeriod(),
        },
      }),
    )

    expect(value.monthlyCreditBreakdown.value).toEqual({
      input: 11,
      cached: 5,
      cacheWrite: 15,
      output: 34,
      saved: 11,
    })
  })

  it('treats a source without cache-write credits as zero rather than NaN', () => {
    // Codex reports no cache-write split, so the field is absent on its rows.
    const { value } = mount(
      snapshot({
        tokens: {
          today: tokenPeriod(),
          sevenDays: tokenPeriod(),
          month: tokenPeriod({
            creditsByModel: [
              { ...modelCredits({ inputCredits: 4 }), cacheWriteCredits: undefined as unknown as number },
            ],
          }),
          lifetime: tokenPeriod(),
        },
      }),
    )

    expect(value.monthlyCreditBreakdown.value.cacheWrite).toBe(0)
    expect(value.monthlyCreditBreakdown.value.input).toBe(4)
  })

  it('returns zeroes rather than NaN with no snapshot', () => {
    const { value } = mount(null)
    expect(value.monthlyCreditBreakdown.value).toEqual({ input: 0, cached: 0, cacheWrite: 0, output: 0, saved: 0 })
  })
})

describe('rate coverage', () => {
  const coverageOf = (visibleTotalTokens: number, unratedTokens: number) => snapshot({
    tokens: {
      today: tokenPeriod(),
      sevenDays: tokenPeriod(),
      month: tokenPeriod({
        unratedTokens,
        creditsByModel: [
          modelCredits({ tokens: tokenBreakdown({ visibleTotalTokens }) }),
        ],
      }),
      lifetime: tokenPeriod(),
    },
  })

  it('is the rated share of all counted tokens', () => {
    const { value } = mount(coverageOf(750, 250))
    expect(value.rateCoverage.value).toBe(75)
  })

  it('is 100 when nothing is unrated', () => {
    const { value } = mount(coverageOf(1_000, 0))
    expect(value.rateCoverage.value).toBe(100)
  })

  it('is 0 rather than NaN when there are no tokens at all', () => {
    const { value } = mount(coverageOf(0, 0))
    expect(value.rateCoverage.value).toBe(0)
  })
})

describe('month-to-date projection', () => {
  const monthOf = (creditsUsed: number) => snapshot({
    tokens: {
      today: tokenPeriod(),
      sevenDays: tokenPeriod(),
      month: tokenPeriod({ creditsUsed }),
      lifetime: tokenPeriod(),
    },
  })

  it('extrapolates the current burn rate to the end of the month', () => {
    vi.useFakeTimers()
    // Local time is Asia/Tokyo: noon on the 15th of a 31-day month.
    vi.setSystemTime(new Date('2026-07-15T03:00:00Z'))

    const { value } = mount(monthOf(10_000)) // US$400 so far.
    // 14.5 days elapsed, 31 days in July.
    expect(value.estimatedMonthAmount.value).toBeCloseTo(400 / 14.5 * 31, 6)
  })

  it('does not divide by zero on the first instant of the month', () => {
    vi.useFakeTimers()
    // Asia/Tokyo midnight on the 1st.
    vi.setSystemTime(new Date('2026-06-30T15:00:00Z'))

    const { value } = mount(monthOf(1_000))
    expect(Number.isFinite(value.estimatedMonthAmount.value)).toBe(true)
    // Floor of 1/24 day elapsed over a 31-day July.
    expect(value.estimatedMonthAmount.value).toBeCloseTo(40 * 24 * 31, 6)
  })

  it('uses the real length of a short month', () => {
    vi.useFakeTimers()
    // Asia/Tokyo noon on 15 February 2026 (28 days).
    vi.setSystemTime(new Date('2026-02-15T03:00:00Z'))

    const { value } = mount(monthOf(10_000))
    expect(value.estimatedMonthAmount.value).toBeCloseTo(400 / 14.5 * 28, 6)
  })
})

describe('money helpers', () => {
  it('converts credits to money and passes through plain amounts', () => {
    const { value } = mount(null)
    expect(value.money(1_000)).toBe('US$40.00')
    expect(value.money(null)).toBe('--')
    expect(value.amountMoney(1_000)).toBe('US$1,000.00')
    expect(value.amountMoney(1_000, false, true)).toBe('+US$1,000.00')
  })
})

describe('credit tooltip', () => {
  it('is empty without a snapshot', () => {
    const { value } = mount(null)
    expect(value.creditTooltip.value).toBe('')
  })

  it('lists each model on its own line', () => {
    const { value } = mount(
      snapshot({
        tokens: {
          today: tokenPeriod(),
          sevenDays: tokenPeriod(),
          month: tokenPeriod({
            creditsByModel: [
              modelCredits({ model: 'gpt-5.2', totalCredits: 1_000 }),
              modelCredits({ model: 'gpt-5.2-mini', totalCredits: 500 }),
            ],
          }),
          lifetime: tokenPeriod(),
        },
      }),
    )

    const lines = value.creditTooltip.value.split('\n')
    expect(lines).toHaveLength(2)
    expect(lines[0]).toContain('gpt-5.2: US$40.00')
    expect(lines[1]).toContain('gpt-5.2-mini: US$20.00')
    expect(lines[0]).not.toContain('采用费率')
  })

  it('appends the rate versions actually applied', () => {
    const { value } = mount(
      snapshot({
        tokens: {
          today: tokenPeriod(),
          sevenDays: tokenPeriod(),
          month: tokenPeriod({
            creditsByModel: [
              modelCredits({
                model: 'gpt-5.2',
                totalCredits: 1_000,
                rateVersions: [
                  {
                    catalogVersion: '2026.01.0',
                    source: '内置目录',
                    effectiveFrom: '2026-01-01',
                    tokens: tokenBreakdown(),
                    inputCredits: 0,
                    cachedInputCredits: 0,
                    outputCredits: 0,
                    cacheWriteCredits: 0,
                    cachedSavingsCredits: 0,
                    totalCredits: 1_000,
                  },
                ],
              }),
            ],
          }),
          lifetime: tokenPeriod(),
        },
      }),
    )

    expect(value.creditTooltip.value).toContain('采用费率 2026.01.0（2026-01-01，内置目录）')
  })

  it('labels an open-ended rate version as covering all history', () => {
    const { value } = mount(
      snapshot({
        tokens: {
          today: tokenPeriod(),
          sevenDays: tokenPeriod(),
          month: tokenPeriod({
            creditsByModel: [
              modelCredits({
                model: 'gpt-5.2',
                rateVersions: [
                  {
                    catalogVersion: 'archive-2026.01',
                    source: '历史费率归档',
                    effectiveFrom: null,
                    tokens: tokenBreakdown(),
                    inputCredits: 0,
                    cachedInputCredits: 0,
                    outputCredits: 0,
                    cacheWriteCredits: 0,
                    cachedSavingsCredits: 0,
                    totalCredits: 0,
                  },
                ],
              }),
            ],
          }),
          lifetime: tokenPeriod(),
        },
      }),
    )

    expect(value.creditTooltip.value).toContain('全部历史')
  })
})
