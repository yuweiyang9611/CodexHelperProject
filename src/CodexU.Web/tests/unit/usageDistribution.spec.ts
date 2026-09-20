import { describe, expect, it } from 'vitest'
import { buildUsageDistribution } from '../../src/usageDistribution'
import type { DailyUsage } from '../../src/types'

const day = (date: string, tokens: number, distribution?: DailyUsage['distribution']): DailyUsage => ({ date, tokens, distribution, creditsUsed: 0, quality: 'detailed' })
const end = '2026-09-20T12:00:00+09:00'

describe('usage distribution', () => {
  it('uses inclusive calendar windows, fills gaps and excludes future and older data', () => {
    const days = [day('2026-09-13', 10), day('2026-09-14', 20), day('2026-09-20', 30), day('2026-09-21', 999)]
    const week = buildUsageDistribution(days, end, 7, 'feature')
    expect(week.days).toHaveLength(7)
    expect(week.days[0]?.date).toBe('2026-09-14')
    expect(week.days[1]?.total).toBe(0)
    expect(week.total).toBe(50)
    const month = buildUsageDistribution(days, end, 30, 'model')
    expect(month.days[0]?.date).toBe('2026-08-22')
    expect(month.total).toBe(60)
  })

  it('sums disjoint slices by feature or model and calculates shares over the selected period', () => {
    const days = [day('2026-09-20', 200, [
      { model: 'GPT-6-ASTRA', feature: 'tasks', tokens: 100 },
      { model: 'gpt-6-astra', feature: 'subagents', tokens: 50 },
      { model: 'gpt-5.6-sol', feature: 'tasks', tokens: 50 },
    ])]
    const features = buildUsageDistribution(days, end, 7, 'feature')
    expect(features.series.map(s => [s.id, s.tokens, s.percent])).toEqual([['tasks', 150, 75], ['subagents', 50, 25]])
    const models = buildUsageDistribution(days, end, 7, 'model')
    expect(models.series.map(s => [s.id, s.tokens, s.percent])).toEqual([['gpt-6-astra', 150, 75], ['gpt-5.6-sol', 50, 25]])
  })

  it('keeps missing attribution in the denominator and does not invent splits for legacy data', () => {
    const chart = buildUsageDistribution([day('2026-09-19', 50), day('2026-09-20', 100, [
      { model: 'm', feature: 'tasks', tokens: 60 }, { model: 'm', feature: 'future-feature', tokens: 10 },
    ])], end, 7, 'feature')
    expect(chart.series.map(s => [s.id, s.tokens, s.percent])).toEqual([['unknown', 90, 60], ['tasks', 60, 40]])
  })

  it('handles empty windows and rejects over-attribution without NaN or shares above 100%', () => {
    expect(buildUsageDistribution([], end, 7, 'model').series).toEqual([])
    const chart = buildUsageDistribution([day('2026-09-20', 10, [{ model: 'm', feature: 'tasks', tokens: 20 }])], end, 7, 'feature')
    expect(chart.series.map(s => [s.id, s.percent])).toEqual([['unknown', 100]])
  })
})
