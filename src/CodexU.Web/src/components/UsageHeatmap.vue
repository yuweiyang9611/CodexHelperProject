<script setup lang="ts">
import { computed } from 'vue'
import type { DailyUsage } from '../types'
import { compactNumber, creditAmount } from '../format'

const props = defineProps<{
  days: DailyUsage[]
  amountPerThousandCredits: number
  currencySymbol: string
}>()

const nonZero = computed(() => props.days.map((day) => day.tokens).filter(Boolean).sort((a, b) => a - b))
const thresholds = computed(() => {
  const values = nonZero.value
  if (!values.length) return [1, 2, 3, 4]
  return [.25, .5, .75, .9].map((percentile) => values[Math.min(values.length - 1, Math.floor(values.length * percentile))])
})
const summary = computed(() => {
  const activeDays = props.days.filter((day) => day.tokens > 0)
  const total = activeDays.reduce((sum, day) => sum + day.tokens, 0)
  return `近半年使用热力图，${activeDays.length} 个活跃日，共 ${compactNumber(total)} tokens`
})

function level(tokens: number) {
  if (!tokens) return 0
  const index = thresholds.value.findIndex((threshold) => tokens <= threshold)
  return index === -1 ? 4 : index + 1
}

function tooltip(day: DailyUsage) {
  return `${day.date} · ${compactNumber(day.tokens)} tokens · ${creditAmount(day.creditsUsed, props.amountPerThousandCredits, props.currencySymbol)}`
}
</script>

<template>
  <div class="heatmap-shell" role="img" :aria-label="summary">
    <div class="weekday-labels" aria-hidden="true"><span>一</span><span>三</span><span>五</span><span>日</span></div>
    <div class="heatmap">
      <span
        v-for="day in days"
        :key="day.date"
        class="heat-day"
        :class="`level-${level(day.tokens)}`"
        :title="tooltip(day)"
        aria-hidden="true"
      />
    </div>
    <div class="heat-legend" aria-hidden="true"><span>少</span><i v-for="value in 5" :key="value" :class="`level-${value - 1}`" /><span>多</span></div>
  </div>
</template>

<style scoped>
.heatmap-shell { position: relative; padding: 8px 4px 24px 24px; overflow: hidden; }
.heatmap { display: grid; grid-template-rows: repeat(7, 10px); grid-auto-flow: column; grid-auto-columns: 10px; gap: 4px; min-width: 360px; }
.heat-day { width: 10px; height: 10px; border-radius: 2.5px; background: rgba(255,255,255,.06); box-shadow: inset 0 0 0 1px rgba(255,255,255,.025); }
.level-1 { background: #292b65; }
.level-2 { background: #3f3d91; }
.level-3 { background: #5f51c6; }
.level-4 { background: #846af0; box-shadow: 0 0 8px rgba(132,106,240,.25); }
.weekday-labels { position: absolute; left: 0; top: 7px; height: 94px; display: flex; flex-direction: column; justify-content: space-between; color: var(--text-tertiary); font-size: 11px; }
.heat-legend { position: absolute; right: 5px; bottom: 2px; display: flex; align-items: center; gap: 4px; color: var(--text-tertiary); font-size: 11px; }
.heat-legend i { width: 9px; height: 9px; border-radius: 2px; background: rgba(255,255,255,.06); }
</style>
