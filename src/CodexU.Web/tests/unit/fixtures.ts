import type {
  AppSettings,
  DashboardSnapshot,
  ModelCreditUsage,
  ProjectUsage,
  QuotaForecast,
  RateLimitWindow,
  RuntimeReadResult,
  TodoItem,
  TokenBreakdown,
  TokenPeriod,
} from '../../src/types'

export function todoItem(overrides: Partial<TodoItem> = {}): TodoItem {
  return {
    id: 't1',
    text: '写测试',
    done: false,
    priority: 'normal',
    createdAt: '2026-07-14T00:00:00Z',
    ...overrides,
  }
}

export function tokenBreakdown(overrides: Partial<TokenBreakdown> = {}): TokenBreakdown {
  return {
    inputTokens: 0,
    cachedInputTokens: 0,
    outputTokens: 0,
    reasoningOutputTokens: 0,
    totalTokens: 0,
    billableCachedInputTokens: 0,
    uncachedInputTokens: 0,
    visibleTotalTokens: 0,
    cacheWrite5mTokens: 0,
    cacheWrite1hTokens: 0,
    billableCacheWrite5mTokens: 0,
    billableCacheWrite1hTokens: 0,
    billableCacheWriteTokens: 0,
    ...overrides,
  }
}

export function modelCredits(overrides: Partial<ModelCreditUsage> = {}): ModelCreditUsage {
  return {
    model: 'gpt-5.2',
    tokens: tokenBreakdown(),
    inputCredits: 0,
    cachedInputCredits: 0,
    cacheWriteCredits: 0,
    outputCredits: 0,
    cachedSavingsCredits: 0,
    totalCredits: 0,
    ...overrides,
  }
}

export function tokenPeriod(overrides: Partial<TokenPeriod> = {}): TokenPeriod {
  return {
    tokens: 0,
    breakdown: tokenBreakdown(),
    creditsUsed: 0,
    unratedTokens: 0,
    creditsByModel: [],
    quality: 'detailed',
    ...overrides,
  }
}

export function snapshot(overrides: Partial<DashboardSnapshot> = {}): DashboardSnapshot {
  return {
    runtime: 'codex',
    refreshedAt: '2026-07-14T12:00:00Z',
    tokens: {
      today: tokenPeriod(),
      sevenDays: tokenPeriod(),
      month: tokenPeriod(),
      lifetime: tokenPeriod(),
    },
    tasks: [],
    dailyUsage: [],
    projects: [],
    tools: [],
    skills: [],
    sources: [],
    models: [],
    goals: [],
    taskLifecycle: {
      started: 0,
      completed: 0,
      aborted: 0,
      durationMilliseconds: 0,
      longestDurationMilliseconds: 0,
    },
    indexStatus: {
      enabled: true,
      reusedFiles: 0,
      incrementalFiles: 0,
      parsedFiles: 0,
      totalFiles: 0,
    },
    diagnostics: [],
    ...overrides,
  }
}

export function projectUsage(overrides: Partial<ProjectUsage> = {}): ProjectUsage {
  return {
    id: 'p1',
    name: 'App',
    fullPath: 'D:\\Repo\\App',
    tokens: 1_000,
    threadCount: 1,
    quality: 'detailed',
    ...overrides,
  }
}

export function rateLimitWindow(overrides: Partial<RateLimitWindow> = {}): RateLimitWindow {
  return {
    usedPercent: 40,
    remainingPercent: 60,
    windowDurationMinutes: 300,
    resetsAt: '2026-07-14T17:00:00Z',
    ...overrides,
  }
}

export function quotaForecast(overrides: Partial<QuotaForecast> = {}): QuotaForecast {
  return {
    percentPerMinute: 1,
    timeToExhaustion: '01:00:00',
    exhaustsAt: '2026-07-14T13:00:00Z',
    exhaustsBeforeReset: true,
    measuredOver: '00:30:00',
    ...overrides,
  }
}

export function runtimeRead(overrides: Partial<RuntimeReadResult> = {}): RuntimeReadResult {
  return {
    snapshot: snapshot(),
    readFailed: false,
    ...overrides,
  }
}

export function appSettings(overrides: Partial<AppSettings> = {}): AppSettings {
  return {
    theme: 'dark',
    showSubagents: false,
    compactMode: false,
    statusStripEnabled: false,
    statusStripPositionLocked: false,
    desktopMode: false,
    closeToTray: true,
    startAtLogin: false,
    notificationsEnabled: true,
    quotaForecastAlertsEnabled: true,
    fiveHourAlertPercent: 20,
    sevenDayAlertPercent: 20,
    autoRefreshMinutes: 5,
    incrementalIndexEnabled: true,
    uiScalePercent: 110,
    amountPerThousandCredits: 40,
    creditCurrencySymbol: 'US$',
    codexMonthlySubscriptionAmount: 200,
    claudeMonthlySubscriptionAmount: 20,
    codexAutoDetectSubscriptionAmount: true,
    claudeAutoDetectSubscriptionAmount: true,
    checkForUpdates: true,
    includePrereleaseUpdates: false,
    monthlyAmountAlert: 0,
    minimumRateCoverageAlertPercent: 80,
    globalHotKey: 'Ctrl+U',
    statusStripQuotaMode: 'remaining',
    statusStripShowTodayTokens: true,
    customModelRates: [],
    isRateCatalogPinned: false,
    ...overrides,
  }
}
