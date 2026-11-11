import { computed, ref } from 'vue'
import { describe, expect, it } from 'vitest'
import { useEquivalentValue } from '../../src/composables/useEquivalentValue'
import type { DashboardSnapshot } from '../../src/types'
import { monthlyValue, snapshot } from './fixtures'

describe('backend monthly valuation', () => {
  it('uses the host projection and updates when a new snapshot is published', () => {
    const reported = monthlyValue({ subscriptionSource: 'unknown', subscriptionAmount: null,
      netAmount: null, paybackStatus: 'unknown', paybackMultiple: null })
    const source = ref<DashboardSnapshot | null>(snapshot({ monthlyValue: reported }))
    const value = useEquivalentValue(computed(() => source.value))
    expect(value.monthlyValue.value).toEqual(reported)
    expect(value.valueProgress.value).toBeNull()
    source.value = snapshot({ monthlyValue: monthlyValue({ subscriptionAmount: 0, paybackStatus: 'reached', paybackMultiple: null }) })
    expect(value.valueProgress.value).toBe(100)
  })

  it('formats the backend model amounts and applied rate metadata for the popover', () => {
    const source = ref<DashboardSnapshot | null>(snapshot({
      monthlyValue: monthlyValue({ models: [
        { model: 'example-model', total: 10, input: 2, cachedInput: 0.5, cacheWrite: 2.5, output: 5, cachedSavings: 4,
          rateVersions: [{ catalogVersion: 'v1', source: '内置目录', effectiveFrom: null, total: 4 },
            { catalogVersion: 'v2', source: '自定义目录', effectiveFrom: '2026-01-01', total: 6 }] },
      ] }),
    }))
    const value = useEquivalentValue(computed(() => source.value))
    expect(value.creditTooltip.value).toContain('example-model: US$10.00')
    expect(value.creditTooltip.value).toContain('缓存写入 US$2.50')
    expect(value.creditTooltip.value).toContain('缓存省下 US$4.00')
    expect(value.creditTooltip.value).toContain('v1（全部历史，内置目录）；v2（2026-01-01，自定义目录）')
    source.value = null
    expect(value.creditTooltip.value).toBe('')
  })
})
