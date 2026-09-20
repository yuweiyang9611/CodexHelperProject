import type { DailyUsage } from './types'

export type DistributionDimension = 'feature' | 'model'
const palette = ['#9874ec', '#f28b43', '#57bf83', '#e379b3', '#67a5e8', '#d3af50', '#53b9b4', '#bc89d4']
const features: Record<string, { label: string, color: string }> = {
  tasks: { label: '任务', color: palette[0]! },
  subagents: { label: '子代理', color: palette[2]! },
  unknown: { label: '未分类', color: '#8a95a8' },
}
const validTokens = (value: number) => Number.isFinite(value) && value > 0 ? value : 0

/** Calendar windows include today and missing days; lifetime rankings cannot supply daily shares. */
export function buildUsageDistribution(days: DailyUsage[], end: string, count: 7 | 30, dimension: DistributionDimension) {
  const date = new Date(end)
  const endDate = new Date(date.getFullYear(), date.getMonth(), date.getDate(), 12)
  const byDate = new Map(days.map(day => [day.date, day]))
  const totals = new Map<string, number>()
  const buckets = Array.from({ length: count }, (_, index) => {
    const current = new Date(endDate)
    current.setDate(current.getDate() - (count - 1 - index))
    const key = `${current.getFullYear()}-${String(current.getMonth() + 1).padStart(2, '0')}-${String(current.getDate()).padStart(2, '0')}`
    const day = byDate.get(key)
    const total = validTokens(day?.tokens ?? 0)
    const values = new Map<string, number>()
    let assigned = 0
    const slices = day?.distribution ?? []
    // Old or inconsistent hosts must not make shares exceed the measured total.
    if (slices.reduce((sum, slice) => sum + validTokens(slice.tokens), 0) <= total) {
      for (const slice of slices) {
        const tokens = validTokens(slice.tokens)
        const id = dimension === 'feature'
          ? (features[slice.feature] ? slice.feature : 'unknown')
          : (slice.model?.trim().toLowerCase() || 'unknown')
        values.set(id, (values.get(id) ?? 0) + tokens)
        assigned += tokens
      }
    }
    if (assigned < total) values.set('unknown', (values.get('unknown') ?? 0) + total - assigned)
    for (const [id, tokens] of values) if (tokens > 0) totals.set(id, (totals.get(id) ?? 0) + tokens)
    return { date: key, label: `${current.getMonth() + 1}月${current.getDate()}日`, total, values }
  })
  const total = buckets.reduce((sum, day) => sum + day.total, 0)
  // Assign model colors by name order so changing the window does not reorder colors by usage rank.
  const colors = new Map([...totals.keys()].sort().map((id, i) => [id, palette[i % palette.length]!]))
  const series = [...totals.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).map(([id, tokens]) => ({
    id, tokens, percent: total ? tokens / total * 100 : 0,
    label: dimension === 'feature' ? features[id]!.label : id === 'unknown' ? '未知模型' : id,
    color: dimension === 'feature' ? features[id]!.color : id === 'unknown' ? '#8a95a8' : colors.get(id)!,
  }))
  return { days: buckets, series, total, max: Math.max(1, ...buckets.map(day => day.total)) }
}
