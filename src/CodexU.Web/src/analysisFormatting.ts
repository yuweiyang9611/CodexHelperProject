import { compactNumber, currencyAmount } from './format'
import type { UsageAnalysisTotals } from './types'
import type { UsageMetric } from './composables/useUsageAnalysis'

export function analysisValue(totals: UsageAnalysisTotals, metric: UsageMetric, conversion: number): string {
  if (metric === 'tokens') return compactNumber(totals.tokens)
  if (totals.creditsUsed == null) return '金额未知'
  return `${totals.unratedTokens > 0 ? '≥ ' : ''}${currencyAmount(totals.creditsUsed / 1000 * conversion)}`
}
export function sourceLabel(source: string): string {
  return ({ live: '原始日志', retained: '历史留存', conflict: '来源冲突，保留可信历史', legacy: '旧日汇总' } as Record<string, string>)[source] ?? '来源未知'
}
export function featureLabel(feature: string): string {
  return ({ tasks: '主会话', subagents: '子代理', unknown: '未分类', 'agent-created-tasks': '代理创建会话', 'auto-review': '自动审查', 'memory-updates': '记忆更新', 'proactive-suggestions': '主动建议' } as Record<string, string>)[feature] ?? feature
}
