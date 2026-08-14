import { computed, ref } from 'vue'
import { describe, expect, it } from 'vitest'
import { useRecentUsage } from '../../src/composables/useRecentUsage'
import { snapshot } from './fixtures'

describe('useRecentUsage', () => {
  it('builds both seven-day windows only from local daily usage', () => {
    const dailyUsage = Array.from({ length: 15 }, (_, index) => ({
      date: `2026-08-${String(index + 1).padStart(2, '0')}`,
      tokens: index + 1,
      creditsUsed: index + 1,
      quality: 'detailed' as const,
    }))
    const source = ref(snapshot({ dailyUsage }))
    const recent = useRecentUsage(computed(() => source.value))

    expect(recent.lastSeven.value.map((day) => day.tokens)).toEqual([9, 10, 11, 12, 13, 14, 15])
    expect(recent.previousSeven.value.map((day) => day.tokens)).toEqual([2, 3, 4, 5, 6, 7, 8])
    expect(recent.lastSevenTotal.value).toBe(84)
    expect(recent.previousSevenTotal.value).toBe(35)
    expect(recent.peakDay.value?.tokens).toBe(15)
  })
})
