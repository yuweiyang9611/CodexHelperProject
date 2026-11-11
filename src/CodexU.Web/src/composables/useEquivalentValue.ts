import { computed, type ComputedRef } from 'vue'
import { currencyAmount } from '../format'
import type { DashboardSnapshot } from '../types'

export function useEquivalentValue(snapshot: ComputedRef<DashboardSnapshot | null>) {
  const monthlyValue = computed(() => snapshot.value?.monthlyValue)
  const valueProgress = computed(() => {
    const multiple = monthlyValue.value?.paybackMultiple
    if (multiple != null) return Math.min(100, multiple * 100)
    return monthlyValue.value?.paybackStatus === 'reached' ? 100 : null
  })
  function amountMoney(value?: number | null, compact = false, showPositiveSign = false) {
    return currencyAmount(value, 'US$', compact, showPositiveSign)
  }
  const creditTooltip = computed(() => monthlyValue.value?.models?.map(model => {
    const versions = model.rateVersions.map(version =>
      `${version.catalogVersion}（${version.effectiveFrom ?? '全部历史'}，${version.source}）`).join('；')
    const cacheWrite = model.cacheWrite ? ` / 缓存写入 ${amountMoney(model.cacheWrite)}` : ''
    return `${model.model}: ${amountMoney(model.total)}（普通输入 ${amountMoney(model.input)} / 缓存输入 ${amountMoney(model.cachedInput)}${cacheWrite} / 输出 ${amountMoney(model.output)} / 缓存省下 ${amountMoney(model.cachedSavings)}）${versions ? `；采用费率 ${versions}` : ''}`
  }).join('\n') ?? '')

  return { monthlyValue, valueProgress, amountMoney, creditTooltip }
}
