import { computed, type ComputedRef } from 'vue'
import { currencyAmount } from '../format'
import { useDashboardStore } from '../stores/dashboard'
import type { DashboardSnapshot } from '../types'

export function useEquivalentValue(snapshot: ComputedRef<DashboardSnapshot | null>) {
  const store = useDashboardStore()
  const currencySymbol = 'US$'
  const monthlyValue = computed(() => snapshot.value?.monthlyValue)
  const amountPerThousandCredits = computed(() => store.settings?.amountPerThousandCredits ?? 40)
  const valueProgress = computed(() => {
    const multiple = monthlyValue.value?.paybackMultiple
    if (multiple != null) return Math.min(100, multiple * 100)
    return monthlyValue.value?.paybackStatus === 'reached' ? 100 : null
  })
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
    monthlyValue,
    valueProgress,
    monthlyCreditBreakdown,
    money,
    amountMoney,
    creditTooltip,
  }
}
