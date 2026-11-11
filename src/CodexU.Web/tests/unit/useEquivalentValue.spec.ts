import { createPinia, setActivePinia } from 'pinia'
import { computed, ref } from 'vue'
import { beforeEach, describe, expect, it } from 'vitest'
import { useEquivalentValue } from '../../src/composables/useEquivalentValue'
import { useDashboardStore } from '../../src/stores/dashboard'
import type { AppSettings, DashboardSnapshot } from '../../src/types'
import { appSettings, modelCredits, monthlyValue, snapshot, tokenBreakdown, tokenPeriod } from './fixtures'

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

describe('backend monthly valuation', () => {
  it('uses the host projection and does not replace an unknown price with a local setting', () => {
    const reported = monthlyValue({ subscriptionSource: 'unknown', subscriptionAmount: null,
      netAmount: null, paybackStatus: 'unknown', paybackMultiple: null })
    const { value, source } = mount(snapshot({ monthlyValue: reported }), { codexMonthlySubscriptionAmount: 20 })
    expect(value.monthlyValue.value).toEqual(reported)
    expect(value.valueProgress.value).toBeNull()
    source.value = snapshot({ monthlyValue: monthlyValue({ subscriptionAmount: 0, paybackStatus: 'reached', paybackMultiple: null }) })
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
