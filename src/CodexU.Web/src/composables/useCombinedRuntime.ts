import { computed, type ComputedRef, type Ref } from 'vue'
import type {
  AgentRuntime,
  CombinedSnapshots,
  DashboardSnapshot,
  DataQuality,
  ProjectUsage,
  QuotaForecast,
  RuntimeReadResult,
  TokenPeriod,
} from '../types'

/**
 * Every rule about what may and may not be combined across the two runtimes lives
 * here, as pure exported functions, so each one can be tested on its own.
 *
 * The governing principle: never produce a number that looks authoritative and is
 * not. Two vendors' quota windows are separate allowances with separate denominators
 * and separate clocks, so nothing about them is ever added or averaged. Sums are only
 * taken over quantities that are genuinely the same unit, and a sum that is missing a
 * contributor says so rather than reading as complete.
 */

export const RUNTIME_LABELS: Record<AgentRuntime, string> = {
  codex: 'Codex',
  claudeCode: 'Claude Code',
}

export type RuntimeContribution =
  /** Read succeeded and the runtime has usage to contribute. */
  | 'present'
  /** Read succeeded and found nothing — the user does not use this runtime. */
  | 'absent'
  /** Read failed. Usage exists but is missing from the totals. */
  | 'failed'

export interface RuntimeEntry {
  runtime: AgentRuntime
  snapshot: DashboardSnapshot
  contribution: RuntimeContribution
  failureMessage?: string
}

const QUALITY_ORDER: DataQuality[] = ['detailed', 'partial', 'approximate', 'unavailable']

/**
 * Classifies a half of the combined read.
 *
 * `readFailed` has to come from the host rather than being inferred from
 * `DataQuality.Unavailable`: a failed read and a runtime the user has never installed
 * both arrive as an empty snapshot with every period unavailable. Inferring would
 * silently drop a failed runtime's usage and stamp the remaining half '详细'.
 */
export function classifyRuntime(result: RuntimeReadResult): RuntimeContribution {
  if (result.readFailed) return 'failed'
  const { snapshot } = result
  const hasUsage = snapshot.tokens.lifetime.tokens > 0
    || snapshot.tokens.month.tokens > 0
    || snapshot.account != null
  return hasUsage ? 'present' : 'absent'
}

export function toEntries(combined: CombinedSnapshots): RuntimeEntry[] {
  return ([['codex', combined.codex], ['claudeCode', combined.claudeCode]] as const).map(
    ([runtime, result]) => ({
      runtime,
      snapshot: result.snapshot,
      contribution: classifyRuntime(result),
      failureMessage: result.failureMessage,
    }),
  )
}

export function worstQuality(qualities: DataQuality[]): DataQuality {
  if (qualities.length === 0) return 'unavailable'
  return qualities.reduce((worst, quality) =>
    QUALITY_ORDER.indexOf(quality) > QUALITY_ORDER.indexOf(worst) ? quality : worst)
}

export interface MergedTokenPeriod {
  tokens: number
  creditsUsed: number
  unratedTokens: number
  quality: DataQuality
  /** Runtimes whose numbers are in the sum. */
  contributors: AgentRuntime[]
  /** Runtimes whose usage is missing from the sum because their read failed. */
  missing: AgentRuntime[]
  /** True when unpriced tokens mean the credit figure is a floor, not a total. */
  creditsAreLowerBound: boolean
}

/**
 * Sums one period across the runtimes that contributed. Never averages.
 *
 * A failed runtime is excluded from the sum and named in `missing`, and drags the
 * quality down to at best `partial` — a total that is missing a whole runtime is not
 * a detailed measurement of anything, however precise its surviving half.
 */
export function mergeTokenPeriods(
  entries: { runtime: AgentRuntime; period: TokenPeriod; contribution: RuntimeContribution }[],
): MergedTokenPeriod {
  const contributing = entries.filter(entry => entry.contribution === 'present')
  const missing = entries.filter(entry => entry.contribution === 'failed').map(entry => entry.runtime)

  const tokens = contributing.reduce((total, entry) => total + entry.period.tokens, 0)
  const creditsUsed = contributing.reduce((total, entry) => total + entry.period.creditsUsed, 0)
  const unratedTokens = contributing.reduce((total, entry) => total + entry.period.unratedTokens, 0)

  let quality = worstQuality(contributing.map(entry => entry.period.quality))
  if (missing.length > 0 && quality !== 'unavailable') {
    quality = worstQuality([quality, 'partial'])
  }

  return {
    tokens,
    creditsUsed,
    unratedTokens,
    quality,
    contributors: contributing.map(entry => entry.runtime),
    missing,
    creditsAreLowerBound: unratedTokens > 0,
  }
}

export interface CombinedSubscription {
  amount: number | null
  isComplete: boolean
  /** Runtimes that have usage but no resolvable monthly price. */
  unknownRuntimes: AgentRuntime[]
  perRuntime: { runtime: AgentRuntime; amount: number | null }[]
}

/**
 * Adds up what the user actually pays across the runtimes in play.
 *
 * Only reads each runtime's own `suggestedMonthlySubscriptionAmount`, derived from that
 * vendor's price table. It does not fall back to the manual per-runtime settings: those
 * are figures the user typed, and folding a typed guess into a headline total that also
 * drives a payback multiple would present an assumption with the same authority as a
 * detected price. One unresolvable runtime makes the whole total unknown rather than
 * low — an understated total would flatter the payback ratio, which is the direction a
 * reader is least likely to question.
 */
export function combineSubscription(entries: RuntimeEntry[]): CombinedSubscription {
  const inPlay = entries.filter(entry => entry.contribution !== 'absent')
  const perRuntime = inPlay.map(entry => ({
    runtime: entry.runtime,
    amount: entry.contribution === 'failed'
      ? null
      : entry.snapshot.account?.suggestedMonthlySubscriptionAmount ?? null,
  }))
  const unknownRuntimes = perRuntime.filter(row => row.amount == null).map(row => row.runtime)
  const isComplete = inPlay.length > 0 && unknownRuntimes.length === 0

  return {
    amount: isComplete ? perRuntime.reduce((total, row) => total + (row.amount ?? 0), 0) : null,
    isComplete,
    unknownRuntimes,
    perRuntime,
  }
}

/**
 * Reduces a project path to a key two runtimes can agree on.
 *
 * Returns null rather than guessing whenever the path cannot identify a project:
 * a relative or blank path (Codex's unattributed threads), or a path inside Claude's
 * own transcript store, which is where Claude keeps its records rather than where the
 * work happened. A wrong answer here silently invents a shared project, so the
 * failure mode is deliberately "do not merge".
 */
export function normalizeProjectKey(fullPath: string | undefined | null): string | null {
  if (!fullPath) return null
  const slashed = fullPath.trim().toLowerCase().replace(/\//g, '\\')
  // Collapse repeated separators, but keep a leading UNC '\\' — it is part of the
  // path's identity, not a duplicate.
  const collapsed = slashed.startsWith('\\\\')
    ? `\\\\${slashed.slice(2).replace(/\\{2,}/g, '\\')}`
    : slashed.replace(/\\{2,}/g, '\\')
  const normalized = collapsed.replace(/(?!^)\\+$/, '')
  if (!normalized) return null
  const isAbsolute = /^[a-z]:\\/.test(normalized) || normalized.startsWith('\\\\') || /^[a-z]:$/.test(normalized)
  if (!isAbsolute) return null
  if (normalized.includes('\\.claude\\projects\\')) return null
  return normalized
}

export interface MergedProject extends ProjectUsage {
  runtimes: AgentRuntime[]
  perRuntimeThreads: { runtime: AgentRuntime; threadCount: number }[]
}

/**
 * Merges the two project rankings on absolute path.
 *
 * Cost is summed only when both contributing sides know their cost — the project
 * elsewhere in this app treats a null cost as unknown and never as zero, and
 * `(a ?? 0) + (b ?? 0)` would render an unknown as a real, smaller figure.
 * `costIsEstimated` is taken from the contributing rows' own flags rather than from
 * the runtime tag, because a Codex row with no priced credits carries no estimate to
 * flag.
 */
export function mergeProjects(entries: RuntimeEntry[]): MergedProject[] {
  const merged = new Map<string, MergedProject>()
  const standalone: MergedProject[] = []

  for (const entry of entries) {
    if (entry.contribution !== 'present') continue
    for (const project of entry.snapshot.projects) {
      const key = normalizeProjectKey(project.fullPath)
      const row: MergedProject = {
        ...project,
        runtimes: [entry.runtime],
        perRuntimeThreads: [{ runtime: entry.runtime, threadCount: project.threadCount }],
      }
      if (key === null) {
        standalone.push(row)
        continue
      }

      const existing = merged.get(key)
      if (!existing) {
        merged.set(key, row)
        continue
      }

      const newer = (project.lastActiveAt ?? '') > (existing.lastActiveAt ?? '') ? project : existing
      merged.set(key, {
        ...existing,
        tokens: existing.tokens + project.tokens,
        threadCount: existing.threadCount + project.threadCount,
        creditsUsed: existing.creditsUsed != null && project.creditsUsed != null
          ? existing.creditsUsed + project.creditsUsed
          : undefined,
        costIsEstimated: Boolean(existing.costIsEstimated) || Boolean(project.costIsEstimated),
        lastActiveAt: newer.lastActiveAt,
        branch: newer.branch,
        quality: worstQuality([existing.quality, project.quality]),
        runtimes: [...existing.runtimes, entry.runtime],
        perRuntimeThreads: [
          ...existing.perRuntimeThreads,
          { runtime: entry.runtime, threadCount: project.threadCount },
        ],
      })
    }
  }

  return [...merged.values(), ...standalone].sort((left, right) => right.tokens - left.tokens)
}

export interface EarliestExhaustion {
  runtime: AgentRuntime
  windowLabel: string
  forecast: QuotaForecast
  /** How many of the windows on screen could be forecast at all. */
  predictable: number
  total: number
}

/**
 * Names the single window that runs out first — the one genuinely cross-runtime fact
 * about quota.
 *
 * Only windows that would actually be exhausted before they reset are candidates:
 * `exhaustsAt` is computed unconditionally, so without this filter a window that
 * resets in an hour and "exhausts" in two would outrank one that really does run dry.
 * The count of predictable windows travels with the answer, because a lazily-loaded
 * runtime often has too little history for a forecast and "earliest of four" would
 * otherwise be claimed on the strength of one.
 */
export function earliestExhaustion(entries: RuntimeEntry[]): EarliestExhaustion | null {
  const windows = entries
    .filter(entry => entry.contribution === 'present')
    .flatMap(entry => [
      { runtime: entry.runtime, windowLabel: '5 小时', quota: entry.snapshot.primaryQuota, forecast: entry.snapshot.primaryForecast },
      { runtime: entry.runtime, windowLabel: '7 天', quota: entry.snapshot.secondaryQuota, forecast: entry.snapshot.secondaryForecast },
    ])
    .filter(row => row.quota != null)

  const candidates = windows.filter(row => row.forecast?.exhaustsBeforeReset)
  if (candidates.length === 0) return null

  const earliest = candidates.reduce((best, row) =>
    row.forecast!.exhaustsAt < best.forecast!.exhaustsAt ? row : best)

  return {
    runtime: earliest.runtime,
    windowLabel: earliest.windowLabel,
    forecast: earliest.forecast!,
    predictable: candidates.length,
    total: windows.length,
  }
}

/**
 * Concatenates diagnostics with a runtime prefix and no cross-runtime dedupe — two
 * runtimes reporting the same message are two separate facts about two separate data
 * sources.
 */
export function combinedDiagnostics(entries: RuntimeEntry[]): string[] {
  return entries.flatMap(entry =>
    entry.snapshot.diagnostics.map(line => `${RUNTIME_LABELS[entry.runtime]}：${line}`))
}

/** The combined view is only as fresh as its stalest half. */
export function oldestRefreshedAt(entries: RuntimeEntry[]): string | null {
  const stamps = entries
    .filter(entry => entry.contribution === 'present')
    .map(entry => entry.snapshot.refreshedAt)
    .filter(Boolean)
  return stamps.length > 0 ? stamps.reduce((oldest, stamp) => (stamp < oldest ? stamp : oldest)) : null
}

export interface CombinedRuntimeSummary {
  entries: RuntimeEntry[]
  periods: Record<'today' | 'sevenDays' | 'month', MergedTokenPeriod>
  subscription: CombinedSubscription
  projects: MergedProject[]
  earliest: EarliestExhaustion | null
  diagnostics: string[]
  refreshedAt: string | null
  failedRuntimes: AgentRuntime[]
}

const PERIOD_KEYS = ['today', 'sevenDays', 'month'] as const

/**
 * Note the absence of a combined `lifetime`. Codex's lifetime total is floored by an
 * account-wide cloud figure that can include other machines, while Claude's counts
 * only the transcripts still on disk and silently loses rotated days. Adding one to
 * the other produces a headline number whose two halves reach past and fall short of
 * the same machine. Lifetime is shown per runtime instead.
 */
export function summarize(combined: CombinedSnapshots): CombinedRuntimeSummary {
  const entries = toEntries(combined)
  const periods = Object.fromEntries(
    PERIOD_KEYS.map(key => [
      key,
      mergeTokenPeriods(entries.map(entry => ({
        runtime: entry.runtime,
        period: entry.snapshot.tokens[key],
        contribution: entry.contribution,
      }))),
    ]),
  ) as CombinedRuntimeSummary['periods']

  return {
    entries,
    periods,
    subscription: combineSubscription(entries),
    projects: mergeProjects(entries),
    earliest: earliestExhaustion(entries),
    diagnostics: combinedDiagnostics(entries),
    refreshedAt: oldestRefreshedAt(entries),
    failedRuntimes: entries.filter(entry => entry.contribution === 'failed').map(entry => entry.runtime),
  }
}

export function useCombinedRuntime(
  combined: Ref<CombinedSnapshots | null> | ComputedRef<CombinedSnapshots | null>,
): ComputedRef<CombinedRuntimeSummary | null> {
  return computed(() => (combined.value ? summarize(combined.value) : null))
}
