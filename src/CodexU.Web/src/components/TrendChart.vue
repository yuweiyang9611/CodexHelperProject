<script setup lang="ts">
import { computed } from 'vue'
import type { DailyUsage } from '../types'

const props = defineProps<{ days: DailyUsage[] }>()
const recent = computed(() => props.days.slice(-7))
const max = computed(() => Math.max(...recent.value.map((day) => day.tokens), 1))
const points = computed(() => recent.value.map((day, index) => {
  const x = 8 + index * 38.6
  const y = 82 - (day.tokens / max.value) * 66
  return { x, y, day }
}))
const line = computed(() => points.value.map((point) => `${point.x},${point.y}`).join(' '))
const area = computed(() => `8,88 ${line.value} 239.6,88`)
const summary = computed(() => recent.value.length
  ? `近 7 日 token 趋势，总计 ${recent.value.reduce((sum, day) => sum + day.tokens, 0).toLocaleString()}，峰值 ${max.value.toLocaleString()}`
  : '近 7 日暂无 token 使用记录')
</script>

<template>
  <svg class="trend-chart" viewBox="0 0 248 102" preserveAspectRatio="none" role="img" :aria-label="summary">
    <title>{{ summary }}</title>
    <defs>
      <linearGradient id="trend-area" x1="0" y1="0" x2="0" y2="1">
        <stop offset="0" stop-color="#7a66f4" stop-opacity=".34" />
        <stop offset="1" stop-color="#7a66f4" stop-opacity="0" />
      </linearGradient>
      <linearGradient id="trend-line" x1="0" y1="0" x2="1" y2="0">
        <stop offset="0" stop-color="#72b8ff" />
        <stop offset="1" stop-color="#9c72ff" />
      </linearGradient>
    </defs>
    <line v-for="y in [22, 44, 66, 88]" :key="y" x1="0" :y1="y" x2="248" :y2="y" class="grid-line" aria-hidden="true" />
    <polygon :points="area" fill="url(#trend-area)" />
    <polyline :points="line" fill="none" stroke="url(#trend-line)" stroke-width="2.4" stroke-linejoin="round" stroke-linecap="round" />
    <circle v-for="point in points" :key="point.day.date" :cx="point.x" :cy="point.y" r="3" class="trend-point">
      <title>{{ point.day.date }} · {{ point.day.tokens.toLocaleString() }} tokens</title>
    </circle>
  </svg>
</template>

<style scoped>
.trend-chart { width: 100%; height: 112px; overflow: visible; }
.grid-line { stroke: rgba(255,255,255,.055); stroke-width: 1; }
.trend-point { fill: #b9a8ff; stroke: #24203e; stroke-width: 2; }
</style>
