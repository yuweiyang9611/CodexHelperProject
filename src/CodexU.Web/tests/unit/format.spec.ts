import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  compactNumber,
  creditAmount,
  currencyAmount,
  exhaustionTime,
  qualityLabel,
  relativeTime,
  resetTime,
} from '../../src/format'

afterEach(() => {
  vi.useRealTimers()
})

describe('qualityLabel', () => {
  it('names each quality level', () => {
    expect(qualityLabel('detailed')).toBe('精细统计')
    expect(qualityLabel('partial')).toBe('部分数据')
    expect(qualityLabel('approximate')).toBe('近似统计')
  })

  it('falls back to the no-data label for unavailable and missing values', () => {
    expect(qualityLabel('unavailable')).toBe('暂无数据')
    expect(qualityLabel(undefined)).toBe('暂无数据')
  })
})

describe('compactNumber', () => {
  it('renders a placeholder for missing values', () => {
    expect(compactNumber(null)).toBe('--')
    expect(compactNumber(undefined)).toBe('--')
  })

  it('keeps small values un-abbreviated', () => {
    expect(compactNumber(0)).toBe('0')
    expect(compactNumber(999)).toBe('999')
  })

  it('switches unit exactly at each threshold', () => {
    expect(compactNumber(999)).toBe('999')
    expect(compactNumber(1_000)).toBe('1.00K')
    expect(compactNumber(999_999)).toBe('1000K')
    expect(compactNumber(1_000_000)).toBe('1.00M')
    expect(compactNumber(1_000_000_000)).toBe('1.00B')
  })

  it('reduces precision as the mantissa grows', () => {
    expect(compactNumber(1_500)).toBe('1.50K')
    expect(compactNumber(15_000)).toBe('15.0K')
    expect(compactNumber(150_000)).toBe('150K')
  })

  it('rounds sub-thousand fractions to a whole number', () => {
    expect(compactNumber(12.4)).toBe('12')
    expect(compactNumber(12.6)).toBe('13')
  })
})

describe('creditAmount', () => {
  it('renders a placeholder only for null-ish values', () => {
    expect(creditAmount(null)).toBe('--')
    expect(creditAmount(undefined)).toBe('--')
  })

  it('applies the default 1000-credit rate', () => {
    // 1,000 credits at US$40 per 1,000 credits.
    expect(creditAmount(1_000)).toBe('US$40.00')
    expect(creditAmount(2_500)).toBe('US$100.00')
  })

  it('honours a custom rate and symbol', () => {
    expect(creditAmount(1_000, 25, '¥')).toBe('¥25.00')
  })

  it('clamps negative credits to zero', () => {
    expect(creditAmount(-5_000)).toBe('US$0.00')
  })

  it('falls back to the default rate when the rate is unusable', () => {
    expect(creditAmount(1_000, 0)).toBe('US$40.00')
    expect(creditAmount(1_000, -10)).toBe('US$40.00')
    expect(creditAmount(1_000, Number.NaN)).toBe('US$40.00')
    expect(creditAmount(1_000, Number.POSITIVE_INFINITY)).toBe('US$40.00')
  })

  it('falls back to US$ when the symbol is blank', () => {
    expect(creditAmount(1_000, 40, '')).toBe('US$40.00')
  })

  it('treats a non-finite credit value as zero', () => {
    expect(creditAmount(Number.NaN)).toBe('US$0.00')
    expect(creditAmount(Number.POSITIVE_INFINITY)).toBe('US$0.00')
  })
})

describe('currencyAmount', () => {
  it('renders a placeholder for missing or non-finite values', () => {
    expect(currencyAmount(null)).toBe('--')
    expect(currencyAmount(undefined)).toBe('--')
    expect(currencyAmount(Number.NaN)).toBe('--')
    expect(currencyAmount(Number.POSITIVE_INFINITY)).toBe('--')
  })

  it('formats to two decimals by default', () => {
    expect(currencyAmount(1_234.5)).toBe('US$1,234.50')
  })

  it('renders negatives with the sign before the symbol', () => {
    expect(currencyAmount(-42)).toBe('-US$42.00')
  })

  it('adds a positive sign only when asked and only above zero', () => {
    expect(currencyAmount(42, 'US$', false, true)).toBe('+US$42.00')
    expect(currencyAmount(0, 'US$', false, true)).toBe('US$0.00')
    expect(currencyAmount(-42, 'US$', false, true)).toBe('-US$42.00')
  })

  it('abbreviates only in compact mode past each threshold', () => {
    expect(currencyAmount(1_500, 'US$', true)).toBe('US$1.50K')
    expect(currencyAmount(2_500_000, 'US$', true)).toBe('US$2.50M')
    // Below the K threshold compact mode keeps full precision.
    expect(currencyAmount(999, 'US$', true)).toBe('US$999.00')
    // Without compact mode nothing is abbreviated.
    expect(currencyAmount(1_500)).toBe('US$1,500.00')
  })

  it('abbreviates the magnitude of negatives, keeping the sign outside', () => {
    expect(currencyAmount(-2_500, 'US$', true)).toBe('-US$2.50K')
  })
})

describe('relativeTime', () => {
  it('describes missing input', () => {
    expect(relativeTime(null)).toBe('暂无时间')
    expect(relativeTime(undefined)).toBe('暂无时间')
    expect(relativeTime('')).toBe('暂无时间')
  })

  it('bucketed by minute, hour and day', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-07-14T12:00:00Z'))

    expect(relativeTime('2026-07-14T11:59:30Z')).toBe('刚刚')
    expect(relativeTime('2026-07-14T11:55:00Z')).toBe('5 分钟前')
    expect(relativeTime('2026-07-14T09:00:00Z')).toBe('3 小时前')
    expect(relativeTime('2026-07-12T12:00:00Z')).toBe('2 天前')
  })

  it('never reports a negative age for future timestamps', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-07-14T12:00:00Z'))

    expect(relativeTime('2026-07-14T18:00:00Z')).toBe('刚刚')
  })
})

describe('resetTime', () => {
  it('describes missing input', () => {
    expect(resetTime(null)).toBe('重置时间不可用')
    expect(resetTime(undefined)).toBe('重置时间不可用')
  })

  it('treats past and present as imminent', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-07-14T12:00:00Z'))

    expect(resetTime('2026-07-14T12:00:00Z')).toBe('即将重置')
    expect(resetTime('2026-07-14T11:00:00Z')).toBe('即将重置')
  })

  it('bucketed by minute, hour and day', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-07-14T12:00:00Z'))

    expect(resetTime('2026-07-14T12:30:00Z')).toBe('30 分钟后重置')
    expect(resetTime('2026-07-14T15:00:00Z')).toBe('3 小时后重置')
    // 48 hours is the hour/day boundary.
    expect(resetTime('2026-07-16T11:00:00Z')).toBe('47 小时后重置')
    expect(resetTime('2026-07-16T12:00:00Z')).toBe('2 天后重置')
  })
})

describe('exhaustionTime', () => {
  it('renders nothing when there is no projection', () => {
    expect(exhaustionTime(null)).toBe('')
    expect(exhaustionTime(undefined)).toBe('')
    expect(exhaustionTime('not a date')).toBe('')
  })

  it('counts down from the current clock, not from the snapshot', () => {
    // The projection is stored as an absolute moment so the countdown keeps
    // shrinking between refreshes rather than freezing at the value the backend
    // computed.
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-07-14T12:00:00Z'))
    expect(exhaustionTime('2026-07-14T12:40:00Z')).toBe('预计 40 分钟后耗尽')

    vi.setSystemTime(new Date('2026-07-14T12:25:00Z'))
    expect(exhaustionTime('2026-07-14T12:40:00Z')).toBe('预计 15 分钟后耗尽')
  })

  it('bucketed by minute, hour and day', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-07-14T12:00:00Z'))

    expect(exhaustionTime('2026-07-14T15:00:00Z')).toBe('预计 3 小时后耗尽')
    expect(exhaustionTime('2026-07-16T12:00:00Z')).toBe('预计 2 天后耗尽')
  })

  it('treats a projection that has already passed as imminent', () => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-07-14T12:00:00Z'))

    expect(exhaustionTime('2026-07-14T11:50:00Z')).toBe('预计即将耗尽')
  })
})
