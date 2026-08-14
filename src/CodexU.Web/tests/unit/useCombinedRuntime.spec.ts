import { describe, expect, it } from 'vitest'
import {
  classifyRuntime,
  combineSubscription,
  combinedDiagnostics,
  earliestExhaustion,
  mergeProjects,
  mergeTokenPeriods,
  normalizeProjectKey,
  oldestRefreshedAt,
  summarize,
  toEntries,
  worstQuality,
  type RuntimeEntry,
} from '../../src/composables/useCombinedRuntime'
import type { AgentRuntime, CombinedSnapshots, DataQuality } from '../../src/types'
import { projectUsage, quotaForecast, rateLimitWindow, runtimeRead, snapshot, tokenPeriod } from './fixtures'

function entry(
  runtime: AgentRuntime,
  overrides: Partial<RuntimeEntry> = {},
): RuntimeEntry {
  return {
    runtime,
    snapshot: snapshot({ runtime }),
    contribution: 'present',
    ...overrides,
  }
}

function period(tokens: number, credits: number, unrated = 0, quality: DataQuality = 'detailed') {
  return tokenPeriod({ tokens, creditsUsed: credits, unratedTokens: unrated, quality })
}

describe('classifyRuntime', () => {
  it('separates a failed read from a runtime the user does not have', () => {
    // Both arrive as an empty snapshot. Only the failed one means usage is missing
    // from the totals, and nothing in the snapshot itself can tell them apart —
    // which is why readFailed has to travel on the wire.
    const failed = runtimeRead({ readFailed: true, failureMessage: 'IO error' })
    const absent = runtimeRead({ readFailed: false })

    expect(classifyRuntime(failed)).toBe('failed')
    expect(classifyRuntime(absent)).toBe('absent')
  })

  it('counts a runtime as present when it has usage or an account', () => {
    expect(classifyRuntime(runtimeRead({
      snapshot: snapshot({ tokens: { ...snapshot().tokens, month: period(500, 1) } }),
    }))).toBe('present')
    expect(classifyRuntime(runtimeRead({
      snapshot: snapshot({ account: { isAuthenticated: true } }),
    }))).toBe('present')
  })
})

describe('mergeTokenPeriods', () => {
  it('sums tokens, credits and unrated tokens', () => {
    const merged = mergeTokenPeriods([
      { runtime: 'codex', period: period(1_000, 10, 100), contribution: 'present' },
      { runtime: 'claudeCode', period: period(3_000, 40, 200), contribution: 'present' },
    ])

    expect(merged.tokens).toBe(4_000)
    expect(merged.creditsUsed).toBe(50)
    expect(merged.unratedTokens).toBe(300)
    expect(merged.contributors).toEqual(['codex', 'claudeCode'])
  })

  it('marks credits as a lower bound whenever either side has unpriced tokens', () => {
    // The Codex SQLite fallback reports zero credits with every token unrated. A sum
    // that is entirely the other vendor's must not read as a complete two-vendor total.
    const withUnrated = mergeTokenPeriods([
      { runtime: 'codex', period: period(1_000, 0, 1_000), contribution: 'present' },
      { runtime: 'claudeCode', period: period(3_000, 40), contribution: 'present' },
    ])
    const fullyPriced = mergeTokenPeriods([
      { runtime: 'codex', period: period(1_000, 10), contribution: 'present' },
      { runtime: 'claudeCode', period: period(3_000, 40), contribution: 'present' },
    ])

    expect(withUnrated.creditsAreLowerBound).toBe(true)
    expect(fullyPriced.creditsAreLowerBound).toBe(false)
  })

  it('keeps a present runtime detailed when the other is simply not installed', () => {
    const merged = mergeTokenPeriods([
      { runtime: 'codex', period: period(1_000, 10), contribution: 'present' },
      { runtime: 'claudeCode', period: period(0, 0, 0, 'unavailable'), contribution: 'absent' },
    ])

    expect(merged.quality).toBe('detailed')
    expect(merged.missing).toEqual([])
  })

  it('never reports a total as detailed when a runtime failed to read', () => {
    // The fatal case: a failed Claude read leaves real usage out of the sum. Reporting
    // the surviving Codex half as '详细' presents an incomplete figure with full
    // confidence — exactly the kind of number this view exists to avoid.
    const merged = mergeTokenPeriods([
      { runtime: 'codex', period: period(1_000, 10), contribution: 'present' },
      { runtime: 'claudeCode', period: period(0, 0, 0, 'unavailable'), contribution: 'failed' },
    ])

    expect(merged.quality).toBe('partial')
    expect(merged.missing).toEqual(['claudeCode'])
    expect(merged.tokens).toBe(1_000)
  })

  it('takes the worst quality among contributors', () => {
    const merged = mergeTokenPeriods([
      { runtime: 'codex', period: period(1_000, 10, 0, 'approximate'), contribution: 'present' },
      { runtime: 'claudeCode', period: period(3_000, 40, 0, 'detailed'), contribution: 'present' },
    ])

    expect(merged.quality).toBe('approximate')
  })

  it('reports nothing rather than precision when no runtime contributed', () => {
    const merged = mergeTokenPeriods([
      { runtime: 'codex', period: period(0, 0, 0, 'unavailable'), contribution: 'absent' },
      { runtime: 'claudeCode', period: period(0, 0, 0, 'unavailable'), contribution: 'absent' },
    ])

    expect(merged.quality).toBe('unavailable')
    expect(merged.contributors).toEqual([])
  })
})

describe('worstQuality', () => {
  it('orders detailed above partial above approximate above unavailable', () => {
    expect(worstQuality(['detailed', 'partial'])).toBe('partial')
    expect(worstQuality(['partial', 'approximate'])).toBe('approximate')
    expect(worstQuality(['approximate', 'unavailable'])).toBe('unavailable')
    expect(worstQuality([])).toBe('unavailable')
  })
})

describe('combineSubscription', () => {
  function withPlan(runtime: AgentRuntime, amount: number | null) {
    return entry(runtime, {
      snapshot: snapshot({
        runtime,
        account: {
          isAuthenticated: true,
          ...(amount == null ? {} : { suggestedMonthlySubscriptionAmount: amount }),
        },
      }),
    })
  }

  it('adds up what each runtime actually costs', () => {
    const combined = combineSubscription([withPlan('codex', 200), withPlan('claudeCode', 20)])

    expect(combined.amount).toBe(220)
    expect(combined.isComplete).toBe(true)
    expect(combined.unknownRuntimes).toEqual([])
  })

  it('reports an unknown total rather than a low one when a price cannot be resolved', () => {
    // Coercing the unknown side to zero would understate the outlay and inflate the
    // payback multiple — the number would look better than reality, in the direction
    // a user is least likely to question.
    const combined = combineSubscription([withPlan('codex', 200), withPlan('claudeCode', null)])

    expect(combined.amount).toBeNull()
    expect(combined.isComplete).toBe(false)
    expect(combined.unknownRuntimes).toEqual(['claudeCode'])
  })

  it('ignores a runtime the user does not have', () => {
    const combined = combineSubscription([
      withPlan('codex', 200),
      entry('claudeCode', { contribution: 'absent' }),
    ])

    expect(combined.amount).toBe(200)
    expect(combined.isComplete).toBe(true)
  })

  it('treats a failed runtime as unknown, never as free', () => {
    const combined = combineSubscription([
      withPlan('codex', 200),
      entry('claudeCode', { contribution: 'failed' }),
    ])

    expect(combined.amount).toBeNull()
    expect(combined.unknownRuntimes).toEqual(['claudeCode'])
  })
})

describe('normalizeProjectKey', () => {
  it('reconciles case and separator differences between the two runtimes', () => {
    // Codex writes Windows paths, Claude may write forward slashes and lower case.
    // Without this the user's main repo shows up as two separate rows.
    const keys = ['D:\\Repo\\App', 'd:/repo/app', 'D:\\\\Repo\\\\App\\', 'd:\\repo\\app']
      .map(normalizeProjectKey)

    expect(new Set(keys).size).toBe(1)
    expect(keys[0]).toBe('d:\\repo\\app')
  })

  it('refuses to key anything that is not an absolute path', () => {
    // Codex's unattributed threads carry a blank path. Merging them would fuse
    // unrelated work into one fabricated project.
    expect(normalizeProjectKey(undefined)).toBeNull()
    expect(normalizeProjectKey('')).toBeNull()
    expect(normalizeProjectKey('   ')).toBeNull()
    expect(normalizeProjectKey('未归类')).toBeNull()
    expect(normalizeProjectKey('relative\\path')).toBeNull()
  })

  it('refuses to key a path inside Claude transcript storage', () => {
    // That directory is where Claude keeps its records, not where the work happened.
    expect(normalizeProjectKey('C:\\Users\\me\\.claude\\projects\\-d--repo--app')).toBeNull()
  })

  it('keeps a UNC prefix intact', () => {
    expect(normalizeProjectKey('\\\\server\\share\\repo')).toBe('\\\\server\\share\\repo')
  })
})

describe('mergeProjects', () => {
  function withProjects(runtime: AgentRuntime, projects: ReturnType<typeof projectUsage>[]) {
    return entry(runtime, { snapshot: snapshot({ runtime, projects }) })
  }

  it('merges the same repo seen by both runtimes into one row', () => {
    const merged = mergeProjects([
      withProjects('codex', [projectUsage({ fullPath: 'D:\\Repo\\App', tokens: 1_000, threadCount: 3 })]),
      withProjects('claudeCode', [projectUsage({ fullPath: 'd:/repo/app', tokens: 4_000, threadCount: 5 })]),
    ])

    expect(merged).toHaveLength(1)
    expect(merged[0].tokens).toBe(5_000)
    expect(merged[0].runtimes).toEqual(['codex', 'claudeCode'])
    expect(merged[0].perRuntimeThreads).toEqual([
      { runtime: 'codex', threadCount: 3 },
      { runtime: 'claudeCode', threadCount: 5 },
    ])
  })

  it('leaves single-runtime projects unmerged and runtime-tagged', () => {
    const merged = mergeProjects([
      withProjects('codex', [projectUsage({ id: 'a', fullPath: 'D:\\Repo\\A', tokens: 2_000 })]),
      withProjects('claudeCode', [projectUsage({ id: 'b', fullPath: 'D:\\Repo\\B', tokens: 1_000 })]),
    ])

    expect(merged).toHaveLength(2)
    expect(merged.map(row => row.runtimes)).toEqual([['codex'], ['claudeCode']])
  })

  it('does not merge two different repos that share a directory name', () => {
    const merged = mergeProjects([
      withProjects('codex', [projectUsage({ name: 'app', fullPath: 'D:\\One\\app' })]),
      withProjects('claudeCode', [projectUsage({ name: 'app', fullPath: 'D:\\Two\\app' })]),
    ])

    expect(merged).toHaveLength(2)
  })

  it('reports an unknown merged cost as unknown rather than as a smaller number', () => {
    // The project view elsewhere treats a null cost as unknown and never as free.
    // (a ?? 0) + (b ?? 0) would render an unknown as a real, and smaller, figure.
    const merged = mergeProjects([
      withProjects('codex', [projectUsage({ fullPath: 'D:\\Repo\\App', creditsUsed: undefined })]),
      withProjects('claudeCode', [projectUsage({ fullPath: 'D:\\Repo\\App', creditsUsed: 500 })]),
    ])

    expect(merged[0].creditsUsed).toBeUndefined()
  })

  it('sums cost only when both sides know theirs', () => {
    const merged = mergeProjects([
      withProjects('codex', [projectUsage({ fullPath: 'D:\\Repo\\App', creditsUsed: 100 })]),
      withProjects('claudeCode', [projectUsage({ fullPath: 'D:\\Repo\\App', creditsUsed: 500 })]),
    ])

    expect(merged[0].creditsUsed).toBe(600)
  })

  it('takes the estimated flag from the contributing rows, not from the runtime', () => {
    // A Codex row with nothing priced carries no estimate to flag; labelling an
    // absent number 'estimated' claims more than is known.
    const flagged = mergeProjects([
      withProjects('codex', [projectUsage({ fullPath: 'D:\\Repo\\App', creditsUsed: 100, costIsEstimated: true })]),
      withProjects('claudeCode', [projectUsage({ fullPath: 'D:\\Repo\\App', creditsUsed: 500 })]),
    ])
    const unflagged = mergeProjects([
      withProjects('codex', [projectUsage({ fullPath: 'D:\\Repo\\App', creditsUsed: 100, costIsEstimated: false })]),
      withProjects('claudeCode', [projectUsage({ fullPath: 'D:\\Repo\\App', creditsUsed: 500 })]),
    ])

    expect(flagged[0].costIsEstimated).toBe(true)
    expect(unflagged[0].costIsEstimated).toBe(false)
  })

  it('takes the branch from whichever runtime was active more recently', () => {
    const merged = mergeProjects([
      withProjects('codex', [projectUsage({ fullPath: 'D:\\Repo\\App', branch: 'old', lastActiveAt: '2026-07-01T00:00:00Z' })]),
      withProjects('claudeCode', [projectUsage({ fullPath: 'D:\\Repo\\App', branch: 'new', lastActiveAt: '2026-07-20T00:00:00Z' })]),
    ])

    expect(merged[0].branch).toBe('new')
    expect(merged[0].lastActiveAt).toBe('2026-07-20T00:00:00Z')
  })

  it('excludes a failed runtime rather than treating its absence as no projects', () => {
    const merged = mergeProjects([
      withProjects('codex', [projectUsage({ fullPath: 'D:\\Repo\\App' })]),
      entry('claudeCode', { contribution: 'failed' }),
    ])

    expect(merged).toHaveLength(1)
    expect(merged[0].runtimes).toEqual(['codex'])
  })
})

describe('earliestExhaustion', () => {
  function withQuota(
    runtime: AgentRuntime,
    primary: { exhaustsAt: string; exhaustsBeforeReset: boolean } | null,
  ) {
    return entry(runtime, {
      snapshot: snapshot({
        runtime,
        primaryQuota: rateLimitWindow(),
        primaryForecast: primary ? quotaForecast(primary) : undefined,
      }),
    })
  }

  it('names the window that runs out first, with its runtime', () => {
    const earliest = earliestExhaustion([
      withQuota('codex', { exhaustsAt: '2026-07-14T15:00:00Z', exhaustsBeforeReset: true }),
      withQuota('claudeCode', { exhaustsAt: '2026-07-14T13:00:00Z', exhaustsBeforeReset: true }),
    ])

    expect(earliest?.runtime).toBe('claudeCode')
    expect(earliest?.windowLabel).toBe('5 小时')
  })

  it('ignores a window that resets before it would run out', () => {
    // exhaustsAt is computed unconditionally, so without this filter a window that
    // never actually runs dry can outrank one that does.
    const earliest = earliestExhaustion([
      withQuota('codex', { exhaustsAt: '2026-07-14T15:00:00Z', exhaustsBeforeReset: true }),
      withQuota('claudeCode', { exhaustsAt: '2026-07-14T13:00:00Z', exhaustsBeforeReset: false }),
    ])

    expect(earliest?.runtime).toBe('codex')
  })

  it('reports how many windows could be forecast at all', () => {
    // 'Earliest of four' claimed on the strength of one measurable window would
    // overstate what the view can actually see.
    const earliest = earliestExhaustion([
      withQuota('codex', { exhaustsAt: '2026-07-14T15:00:00Z', exhaustsBeforeReset: true }),
      withQuota('claudeCode', null),
    ])

    expect(earliest?.predictable).toBe(1)
    expect(earliest?.total).toBe(2)
  })

  it('says nothing when no window would run out before it resets', () => {
    expect(earliestExhaustion([withQuota('codex', null), withQuota('claudeCode', null)])).toBeNull()
  })
})

describe('combinedDiagnostics and oldestRefreshedAt', () => {
  it('prefixes each line with its runtime and keeps duplicates apart', () => {
    // Two runtimes reporting the same message are two facts about two data sources.
    const lines = combinedDiagnostics([
      entry('codex', { snapshot: snapshot({ diagnostics: ['读取成功'] }) }),
      entry('claudeCode', { snapshot: snapshot({ diagnostics: ['读取成功'] }) }),
    ])

    expect(lines).toEqual(['Codex：读取成功', 'Claude Code：读取成功'])
  })

  it('reports the staler of the two refresh times', () => {
    const oldest = oldestRefreshedAt([
      entry('codex', { snapshot: snapshot({ refreshedAt: '2026-07-14T12:00:00Z' }) }),
      entry('claudeCode', { snapshot: snapshot({ refreshedAt: '2026-07-14T11:00:00Z' }) }),
    ])

    expect(oldest).toBe('2026-07-14T11:00:00Z')
  })
})

describe('summarize', () => {
  const combined: CombinedSnapshots = {
    codex: runtimeRead({
      snapshot: snapshot({
        runtime: 'codex',
        account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: 200 },
        tokens: {
          today: period(1_000, 10),
          sevenDays: period(5_000, 50),
          month: period(20_000, 200),
          lifetime: period(900_000, 9_000),
        },
      }),
    }),
    claudeCode: runtimeRead({
      snapshot: snapshot({
        runtime: 'claudeCode',
        account: { isAuthenticated: true, suggestedMonthlySubscriptionAmount: 20 },
        tokens: {
          today: period(3_000, 30),
          sevenDays: period(15_000, 150),
          month: period(60_000, 600),
          lifetime: period(100_000, 1_000),
        },
      }),
    }),
  }

  it('exposes only additive periods and never a combined lifetime', () => {
    // Codex's lifetime is floored by an account-wide cloud figure; Claude's counts
    // only transcripts still on disk. One reaches past this machine, the other falls
    // short of it, so their sum describes nothing.
    const result = summarize(combined)

    expect(Object.keys(result.periods).sort()).toEqual(['month', 'sevenDays', 'today'])
    expect(result.periods.month.tokens).toBe(80_000)
    expect(result.subscription.amount).toBe(220)
  })

  it('exposes no combined quota, plan or token-split figure', () => {
    // A naming tripwire, not a proof: it catches a future `combinedQuota` or
    // `blendedCoverage` being added at the top level, which is where such a mistake
    // would most naturally land. The value-level guarantee is the per-rule tests above.
    const result = summarize(combined) as unknown as Record<string, unknown>

    const offending = Object.keys(result).filter(key =>
      /quota|usedpercent|remainingpercent|plan|breakdown|split|coverage/i.test(key))
    expect(offending).toEqual([])
  })

  it('carries both runtimes through as entries so each half stays attributable', () => {
    const result = summarize(combined)

    expect(result.entries.map(item => item.runtime)).toEqual(['codex', 'claudeCode'])
    expect(result.failedRuntimes).toEqual([])
  })

  it('names a failed runtime and leaves its usage out of the totals', () => {
    const result = summarize({
      ...combined,
      claudeCode: runtimeRead({ readFailed: true, failureMessage: '读取超时' }),
    })

    expect(result.failedRuntimes).toEqual(['claudeCode'])
    expect(result.periods.month.tokens).toBe(20_000)
    expect(result.periods.month.quality).toBe('partial')
    expect(result.subscription.isComplete).toBe(false)
  })
})

describe('toEntries', () => {
  it('keeps the failure message alongside the runtime it belongs to', () => {
    const entries = toEntries({
      codex: runtimeRead(),
      claudeCode: runtimeRead({ readFailed: true, failureMessage: '读取超时' }),
    })

    expect(entries[1].contribution).toBe('failed')
    expect(entries[1].failureMessage).toBe('读取超时')
  })
})
