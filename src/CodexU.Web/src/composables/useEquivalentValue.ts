import { computed, type ComputedRef } from 'vue'
import { currencyAmount } from '../format'
import { useDashboardStore } from '../stores/dashboard'
import type { DashboardSnapshot } from '../types'

export function useEquivalentValue(snapshot: ComputedRef<DashboardSnapshot | null>) {
  const store = useDashboardStore()
  const currencySymbol = 'US$'
  const monthlyCredits = computed(() => snapshot.value?.tokens.month.creditsUsed ?? 0)
  const amountPerThousandCredits = computed(() => store.settings?.amountPerThousandCredits ?? 40)
  const monthlyAmount = computed(() => monthlyCredits.value / 1_000 * amountPerThousandCredits.value)
  const suggestedSubscriptionAmount = computed(() =>
    snapshot.value?.account?.suggestedMonthlySubscriptionAmount ?? null)
  const isClaude = computed(() => snapshot.value?.runtime === 'claudeCode')
  // Per runtime, like the amounts. A shared flag meant editing one vendor's amount
  // switched the other vendor off auto-detection, throwing away a price it had
  // reliably detected.
  const autoDetectEnabled = computed(() => isClaude.value
    ? store.settings?.claudeAutoDetectSubscriptionAmount ?? true
    : store.settings?.codexAutoDetectSubscriptionAmount ?? true)
  const subscriptionAmountIsAuto = computed(() =>
    autoDetectEnabled.value && suggestedSubscriptionAmount.value != null)
  // The manual fallback is per runtime. A single shared field defaulted to 200 — a
  // ChatGPT price — and Claude's plan is only priceable when the statusline snapshot
  // exists, so a Claude user routinely fell through to a US$200 subscription and a
  // payback multiple computed against it.
  const manualSubscriptionAmount = computed(() => isClaude.value
    ? store.settings?.claudeMonthlySubscriptionAmount ?? 20
    : store.settings?.codexMonthlySubscriptionAmount ?? 200)
  const subscriptionAmount = computed(() => subscriptionAmountIsAuto.value
    ? suggestedSubscriptionAmount.value!
    : manualSubscriptionAmount.value)
  const subscriptionSourceLabel = computed(() => {
    if (subscriptionAmountIsAuto.value) {
      const plan = snapshot.value?.account?.planType?.toUpperCase() ?? '本机套餐'
      return `根据 ${plan} 本机套餐标识自动推算，不代表实际账单`
    }
    return autoDetectEnabled.value
      ? '当前套餐无法可靠推算，使用设置中的手动备用值'
      : '使用设置中的手动值'
  })
  const netEquivalentAmount = computed(() => monthlyAmount.value - subscriptionAmount.value)
  const paybackMultiple = computed(() => subscriptionAmount.value > 0
    ? monthlyAmount.value / subscriptionAmount.value
    : null)
  const valueProgress = computed(() => subscriptionAmount.value > 0
    ? Math.min(100, Math.max(0, monthlyAmount.value / subscriptionAmount.value * 100))
    : 100)
  const monthlyCreditBreakdown = computed(() => (snapshot.value?.tokens.month.creditsByModel ?? []).reduce(
    (total, model) => ({
      input: total.input + model.inputCredits,
      cached: total.cached + model.cachedInputCredits,
      // Kept separate from input: writes bill at 1.25x (5 minute) or 2x (1 hour),
      // so folding them into input would both understate cost and mislabel it.
      cacheWrite: total.cacheWrite + (model.cacheWriteCredits ?? 0),
      output: total.output + model.outputCredits,
      saved: total.saved + model.cachedSavingsCredits,
    }),
    { input: 0, cached: 0, cacheWrite: 0, output: 0, saved: 0 },
  ))
  const ratedTokens = computed(() => (snapshot.value?.tokens.month.creditsByModel ?? [])
    .reduce((total, model) => total + model.tokens.visibleTotalTokens, 0))
  const rateCoverage = computed(() => {
    const total = ratedTokens.value + (snapshot.value?.tokens.month.unratedTokens ?? 0)
    return total > 0 ? ratedTokens.value / total * 100 : 0
  })
  const estimatedMonthAmount = computed(() => {
    const now = new Date()
    const elapsedDays = Math.max(1 / 24, now.getDate() - 1
      + (now.getHours() + now.getMinutes() / 60) / 24)
    const daysInMonth = new Date(now.getFullYear(), now.getMonth() + 1, 0).getDate()
    return monthlyAmount.value / elapsedDays * daysInMonth
  })

  function money(value?: number | null, compact = false) {
    if (value == null) return '--'
    return currencyAmount(
      value / 1_000 * amountPerThousandCredits.value,
      currencySymbol,
      compact,
    )
  }

  function amountMoney(value?: number | null, compact = false, showPositiveSign = false) {
    return currencyAmount(value, currencySymbol, compact, showPositiveSign)
  }

  const creditTooltip = computed(() => snapshot.value?.tokens.month.creditsByModel
    .map((model) => {
      const versions = model.rateVersions?.map((version) =>
        `${version.catalogVersion}（${version.effectiveFrom ?? '全部历史'}，${version.source}）`).join('；')
      const cacheWrite = model.cacheWriteCredits
        ? ` / 缓存写入 ${money(model.cacheWriteCredits)}`
        : ''
      return `${model.model}: ${money(model.totalCredits)}（普通输入 ${money(model.inputCredits)} / 缓存输入 ${money(model.cachedInputCredits)}${cacheWrite} / 输出 ${money(model.outputCredits)} / 缓存省下 ${money(model.cachedSavingsCredits)}）${versions ? `；采用费率 ${versions}` : ''}`
    })
    .join('\n') ?? '')

  return {
    monthlyCredits,
    amountPerThousandCredits,
    monthlyAmount,
    suggestedSubscriptionAmount,
    subscriptionAmountIsAuto,
    subscriptionAmount,
    subscriptionSourceLabel,
    netEquivalentAmount,
    paybackMultiple,
    valueProgress,
    monthlyCreditBreakdown,
    rateCoverage,
    estimatedMonthAmount,
    money,
    amountMoney,
    creditTooltip,
  }
}
