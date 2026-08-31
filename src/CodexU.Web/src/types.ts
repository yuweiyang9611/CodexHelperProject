export type AgentRuntime = 'codex' | 'claudeCode'
export type DataQuality = 'detailed' | 'partial' | 'approximate' | 'unavailable'
export type TaskColumnKind = 'active' | 'pending' | 'scheduled' | 'done'

export interface RateLimitWindow {
  usedPercent: number
  windowDurationMinutes?: number
  resetsAt?: string
  remainingPercent: number
  /** When the figure was measured, where the source can say. */
  measuredAt?: string
}

/** Where a quota window is heading at the pace it has actually been consumed. */
export interface QuotaForecast {
  percentPerMinute: number
  timeToExhaustion: string
  exhaustsAt: string
  /** False when the window resets before it would run out — nothing to worry about. */
  exhaustsBeforeReset: boolean
  measuredOver: string
}

/**
 * One runtime's half of a combined read. `readFailed` comes from the host because a
 * failed read and an uninstalled runtime are otherwise indistinguishable — both are
 * an empty snapshot — and only one of them means usage is missing from the totals.
 */
export interface RuntimeReadResult {
  snapshot: DashboardSnapshot
  readFailed: boolean
  failureMessage?: string
}

export interface CombinedSnapshots {
  codex: RuntimeReadResult
  claudeCode: RuntimeReadResult
}

export interface AccountSnapshot {
  accountType?: string
  planType?: string
  email?: string
  isAuthenticated: boolean
  /** Which vendor's plan-price table produced suggestedMonthlySubscriptionAmount. */
  runtime?: AgentRuntime
  suggestedMonthlySubscriptionAmount?: number
}

export interface TokenBreakdown {
  inputTokens: number
  cachedInputTokens: number
  outputTokens: number
  reasoningOutputTokens: number
  totalTokens: number
  billableCachedInputTokens: number
  uncachedInputTokens: number
  visibleTotalTokens: number
  cacheWrite5mTokens: number
  cacheWrite1hTokens: number
  billableCacheWrite5mTokens: number
  billableCacheWrite1hTokens: number
  billableCacheWriteTokens: number
}

export interface TokenPeriod {
  tokens: number
  breakdown: TokenBreakdown
  creditsUsed: number
  unratedTokens: number
  creditsByModel: ModelCreditUsage[]
  quality: DataQuality
}

export interface ModelCreditUsage {
  model: string
  tokens: TokenBreakdown
  inputCredits: number
  cachedInputCredits: number
  /** Cache writes bill above base input — 5 minute at 1.25x, 1 hour at 2x. */
  cacheWriteCredits: number
  outputCredits: number
  cachedSavingsCredits: number
  totalCredits: number
  rateVersions?: RateCreditUsage[]
}

export interface RateCreditUsage {
  catalogVersion: string
  source: string
  effectiveFrom?: string | null
  tokens: TokenBreakdown
  inputCredits: number
  cachedInputCredits: number
  cacheWriteCredits: number
  outputCredits: number
  cachedSavingsCredits: number
  totalCredits: number
}

export interface TokenSummary {
  today: TokenPeriod
  sevenDays: TokenPeriod
  month: TokenPeriod
  lifetime: TokenPeriod
}

export interface TaskItem {
  id: string
  title: string
  project: string
  updatedAt?: string
  tokens?: number
  kind: TaskColumnKind
  detail?: string
}

export interface DailyUsage {
  date: string
  tokens: number
  creditsUsed: number
  quality: DataQuality
}

export interface ProjectUsage {
  id: string
  name: string
  fullPath?: string
  tokens: number
  threadCount: number
  lastActiveAt?: string
  branch?: string
  /** Absent or zero means the cost is unknown, never that it was free. */
  creditsUsed?: number
  /** True when the cost was apportioned by token share rather than measured. */
  costIsEstimated?: boolean
  hasKnownCost?: boolean
  quality: DataQuality
}

export interface RankedUsage {
  id: string
  name: string
  count: number
  estimatedTokens?: number
  creditsUsed?: number
  category?: string
}

export interface ModelUsage {
  model: string
  tokens: number
  eventCount: number
}

export interface GoalItem {
  id: string
  objective: string
  status: string
  tokenBudget?: number
  tokensUsed: number
  timeUsedSeconds: number
  updatedAt?: string
}

export interface TaskLifecycleStats {
  started: number
  completed: number
  aborted: number
  durationMilliseconds: number
  longestDurationMilliseconds: number
}

export interface IndexStatus {
  enabled: boolean
  reusedFiles: number
  incrementalFiles: number
  parsedFiles: number
  totalFiles: number
  updatedAt?: string
}

export interface AppSettings {
  codexHome?: string
  codexExecutable?: string
  defaultWorkspace?: string
  theme: 'dark' | 'light' | 'system'
  showSubagents: boolean
  compactMode: boolean
  statusStripEnabled: boolean
  statusStripPositionLocked: boolean
  desktopMode: boolean
  closeToTray: boolean
  startAtLogin: boolean
  notificationsEnabled: boolean
  quotaForecastAlertsEnabled: boolean
  fiveHourAlertPercent: number
  sevenDayAlertPercent: number
  autoRefreshMinutes: number
  incrementalIndexEnabled: boolean
  uiScalePercent: number
  amountPerThousandCredits: number
  creditCurrencySymbol: string
  /** Manual fallbacks, per runtime — the same plan name prices differently per vendor. */
  codexMonthlySubscriptionAmount: number
  claudeMonthlySubscriptionAmount: number
  /** Also per runtime: one flag would let editing one vendor's amount disable the other's auto-detection. */
  codexAutoDetectSubscriptionAmount: boolean
  claudeAutoDetectSubscriptionAmount: boolean
  checkForUpdates: boolean
  includePrereleaseUpdates: boolean
  monthlyAmountAlert: number
  minimumRateCoverageAlertPercent: number
  globalHotKey: string
  statusStripQuotaMode: 'remaining' | 'used'
  statusStripShowTodayTokens: boolean
  customModelRates: ModelCreditRate[]
  isRateCatalogPinned: boolean
  pinnedRateCatalogVersion?: string
  pinnedRateCatalogSource?: string
  pinnedRateCatalogBaseVersion?: string
}

export interface ModelCreditRate {
  model: string
  inputCreditsPerMillion: number
  cachedInputCreditsPerMillion: number
  outputCreditsPerMillion: number
  effectiveFrom?: string | null
  source?: string
  catalogVersion?: string
  matchMode?: 'exact' | 'prefix'
}

export interface RateCatalogInfo {
  schemaVersion: number
  catalogVersion: string
  source: string
  publishedOn: string
  rateCount: number
}

export interface RateCatalogSnapshot {
  builtIn: RateCatalogInfo
  builtInRates: ModelCreditRate[]
}

export interface UpdateCheckResult {
  currentVersion: string
  latestVersion?: string
  isUpdateAvailable: boolean
  isPrerelease: boolean
  releaseName?: string
  releaseUrl?: string
  publishedAt?: string
  checkedAt: string
  status: string
  notes?: string
}

export interface LocalOperationResult {
  success: boolean
  message: string
  path?: string
  settings?: AppSettings
  todos?: TodoItem[]
}

export interface StatusStripControlState {
  configuredEnabled: boolean
  visible: boolean
  positionLocked: boolean
  hasManualPosition: boolean
  positionMode: string
  displayName: string
  message: string
}

export interface InitializeResult {
  appVersion: string
  platform: string
  theme: string
  isPackaged: boolean
  capabilities: string[]
}

export interface TodoItem {
  id: string
  text: string
  done: boolean
  priority: 'low' | 'normal' | 'high'
  dueDate?: string
  threadId?: string
  createdAt: string
  updatedAt?: string
}

export interface TodoMutation {
  id?: string
  text: string
  priority: 'low' | 'normal' | 'high'
  dueDate?: string
  threadId?: string
}

export interface DashboardSnapshot {
  runtime: AgentRuntime
  refreshedAt: string
  account?: AccountSnapshot
  primaryQuota?: RateLimitWindow
  secondaryQuota?: RateLimitWindow
  primaryForecast?: QuotaForecast
  secondaryForecast?: QuotaForecast
  tokens: TokenSummary
  tasks: TaskItem[]
  dailyUsage: DailyUsage[]
  projects: ProjectUsage[]
  tools: RankedUsage[]
  skills: RankedUsage[]
  sources: RankedUsage[]
  models: ModelUsage[]
  goals: GoalItem[]
  taskLifecycle: TaskLifecycleStats
  indexStatus: IndexStatus
  diagnostics: string[]
}

export interface IpcEnvelope {
  version: number
  id?: string
  type: 'request' | 'response' | 'event'
  method?: string
  ok?: boolean
  payload?: unknown
  error?: { code: string; message: string }
}
