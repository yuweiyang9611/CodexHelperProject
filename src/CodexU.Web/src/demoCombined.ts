import type { AgentRuntime, CombinedRuntime, CombinedRuntimeSummary, CombinedSnapshots, DashboardSnapshot, ProjectUsage } from './types'

// Only the browser demonstration calls this fixture. Its four known project rows
// and two known forecasts are fixed; native path matching and pricing run in .NET.
export function demoCombined(codex: DashboardSnapshot, claude: DashboardSnapshot, rate: number): CombinedSnapshots {
  const dollars = (credits: number) => credits * rate / 1000
  const antigravityTokens = { today: 14_706, sevenDays: 86_400, month: 240_000 } as const
  const period = (key: 'today' | 'sevenDays' | 'month') => ({
    tokens: codex.tokens[key].tokens + claude.tokens[key].tokens + antigravityTokens[key],
    unratedTokens: antigravityTokens[key],
    creditsUsed: codex.tokens[key].creditsUsed + claude.tokens[key].creditsUsed,
    amount: dollars(codex.tokens[key].creditsUsed + claude.tokens[key].creditsUsed),
    quality: 'detailed' as const,
    creditsAreLowerBound: true,
  })
  const month = period('month')
  const prices = [codex, claude].map(s => s.monthlyValue!)
  const unknown = [codex, claude].filter(s => s.monthlyValue?.subscriptionAmount == null).map(s => s.runtime)
  const subscription = unknown.length ? null : prices.reduce((sum, p) => sum + (p.subscriptionAmount ?? 0), 0)
  const makeProject = (runtime: AgentRuntime, first: ProjectUsage, second?: ProjectUsage, antigravity?: { tokens: number; threads: number }) => {
    const runtimes: CombinedRuntime[] = second ? ['codex', 'claudeCode'] : [runtime]
    const threadCounts: Partial<Record<CombinedRuntime, number>> = second
      ? { codex: first.threadCount, claudeCode: second.threadCount }
      : { [runtime]: first.threadCount }
    if (antigravity) {
      runtimes.push('antigravity')
      threadCounts.antigravity = antigravity.threads
    }
    const baseCostStatus = first.costStatus ?? 'unknown'
    return {
      id: first.id, name: first.name, branch: first.branch, lastActiveAt: first.lastActiveAt,
      tokens: first.tokens + (second?.tokens ?? 0) + (antigravity?.tokens ?? 0),
      amount: first.creditsUsed == null ? null : dollars(first.creditsUsed + (second?.creditsUsed ?? 0)),
      costStatus: antigravity && baseCostStatus === 'known' ? 'partial' as const : baseCostStatus,
      threadCounts, quality: first.quality,
      costIsEstimated: Boolean(first.costIsEstimated), runtimes,
    }
  }
  const total = claude.secondaryForecast ? 4 : 2
  const forecasts: CombinedRuntimeSummary['forecasts'] = [{
    runtime: 'codex', weekly: false, exhaustsAt: codex.primaryForecast!.exhaustsAt,
    validUntil: codex.primaryQuota?.resetsAt, predictable: total === 4 ? 2 : 1, total,
  }]
  if (claude.secondaryForecast) forecasts.push({ runtime: 'claudeCode', weekly: true,
    exhaustsAt: claude.secondaryForecast.exhaustsAt, validUntil: claude.secondaryQuota?.resetsAt, predictable: 1, total })
  const paybackReason = subscription == null ? 'unknownSubscription' as const
    : subscription === 0 ? 'zeroSubscription' as const
    : month.amount < subscription ? 'incompleteUsage' as const : 'none' as const
  return {
    codex: { snapshot: codex, contribution: 'present', readFailed: false },
    claudeCode: { snapshot: claude, contribution: 'present', readFailed: false },
    summary: {
      periods: { today: period('today'), sevenDays: period('sevenDays'), month },
      cost: { knownAmount: month.amount, isLowerBound: true, subscriptionAmount: subscription, unknownSubscriptions: unknown,
        hasManual: prices.some(p => p.subscriptionSource === 'manual'),
        paybackMultiple: paybackReason === 'none' && subscription ? month.amount / subscription : null,
        netAmount: paybackReason === 'none' && subscription ? month.amount - subscription : null,
        paybackReason },
      projects: [
        makeProject('codex', codex.projects[0]!, claude.projects[0]!, { tokens: antigravityTokens.month, threads: 2 }),
        makeProject('codex', codex.projects[2]!, claude.projects[1]!),
        makeProject('codex', codex.projects[1]!),
        makeProject('claudeCode', claude.projects[2]!),
      ],
      forecasts, diagnostics: [], refreshedAt: codex.refreshedAt, failedRuntimes: [],
    },
  }
}
