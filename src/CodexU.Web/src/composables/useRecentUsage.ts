import { computed, type ComputedRef } from 'vue'
import type { DashboardSnapshot } from '../types'

/**
 * The trailing seven-day window and the one before it. Shared because the tab
 * summary in the shell and the usage panel must never disagree on the total.
 */
export function useRecentUsage(snapshot: ComputedRef<DashboardSnapshot | null>) {
  const lastSeven = computed(() => snapshot.value?.dailyUsage.slice(-7) ?? [])
  const previousSeven = computed(() => snapshot.value?.dailyUsage.slice(-14, -7) ?? [])
  const lastSevenTotal = computed(() => lastSeven.value.reduce((sum, day) => sum + day.tokens, 0))
  const previousSevenTotal = computed(() => previousSeven.value.reduce((sum, day) => sum + day.tokens, 0))
  const trendChange = computed(() => previousSevenTotal.value
    ? ((lastSevenTotal.value - previousSevenTotal.value) / previousSevenTotal.value) * 100
    : null)
  const peakDay = computed(() => [...lastSeven.value].sort((a, b) => b.tokens - a.tokens)[0])

  return { lastSeven, previousSeven, lastSevenTotal, previousSevenTotal, trendChange, peakDay }
}
