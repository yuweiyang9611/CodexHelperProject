<script setup lang="ts">
import { computed } from 'vue'
import type { QuotaForecast, RateLimitWindow } from '../types'
import { exhaustionTime, resetTime } from '../format'

const props = defineProps<{
  label: string
  quota?: RateLimitWindow
  color: 'blue' | 'violet'
  forecast?: QuotaForecast
}>()

const radius = 54
const circumference = 2 * Math.PI * radius
const percent = computed(() => Math.round(props.quota?.remainingPercent ?? 0))
const dashOffset = computed(() => circumference * (1 - percent.value / 100))
const gradientId = computed(() => `quota-${props.color}-${props.label.replace(/\W/g, '')}`)

// A window that resets before it would run out is not news, however fast it is being
// consumed — showing a countdown for it would cry wolf at every busy stretch.
const burnDown = computed(() =>
  props.quota && props.forecast?.exhaustsBeforeReset
    ? exhaustionTime(props.forecast.exhaustsAt)
    : '')

const burnRate = computed(() =>
  props.forecast ? `按最近用量每分钟消耗 ${props.forecast.percentPerMinute.toFixed(2)}%` : undefined)
</script>

<template>
  <div class="quota-ring" :class="{ unavailable: !quota }">
    <svg viewBox="0 0 132 132" role="img" :aria-label="`${label} 剩余 ${quota ? percent : '不可用'}${quota ? '%' : ''}`">
      <title>{{ label }}额度{{ quota ? `剩余 ${percent}%` : '不可用' }}</title>
      <defs>
        <linearGradient :id="gradientId" x1="0" y1="0" x2="1" y2="1">
          <template v-if="color === 'blue'">
            <stop offset="0" stop-color="#88c8ff" />
            <stop offset="1" stop-color="#4d6fff" />
          </template>
          <template v-else>
            <stop offset="0" stop-color="#f3a4dd" />
            <stop offset="1" stop-color="#7b5cff" />
          </template>
        </linearGradient>
      </defs>
      <circle class="ring-track" cx="66" cy="66" :r="radius" />
      <circle
        v-if="quota"
        class="ring-progress"
        cx="66"
        cy="66"
        :r="radius"
        :stroke="`url(#${gradientId})`"
        :stroke-dasharray="circumference"
        :stroke-dashoffset="dashOffset"
      />
      <circle v-if="quota" class="ring-glow" cx="66" cy="12" r="3" :fill="color === 'blue' ? '#8dceff' : '#ef9ddd'" />
    </svg>
    <div class="quota-content">
      <span class="quota-label">{{ label }}</span>
      <strong>{{ quota ? percent : '--' }}<small v-if="quota">%</small></strong>
      <span class="quota-caption">剩余</span>
    </div>
    <span class="reset-time">{{ resetTime(quota?.resetsAt) }}</span>
    <span v-if="burnDown" class="burn-down" :title="burnRate">{{ burnDown }}</span>
  </div>
</template>

<style scoped>
.quota-ring { position: relative; width: 156px; padding-bottom: 25px; text-align: center; }
.quota-ring:has(.burn-down) { padding-bottom: 42px; }
.quota-ring svg { width: 132px; height: 132px; transform: rotate(-90deg); filter: drop-shadow(0 10px 24px rgba(72, 91, 210, .22)); }
.ring-track, .ring-progress { fill: none; stroke-width: 9; }
.ring-track { stroke: rgba(255,255,255,.075); }
.ring-progress { stroke-linecap: round; transition: stroke-dashoffset .75s cubic-bezier(.2,.85,.25,1); }
.ring-glow { filter: drop-shadow(0 0 5px currentColor); }
.quota-content { position: absolute; top: 25px; left: 0; right: 0; display: flex; flex-direction: column; align-items: center; }
.quota-label { color: var(--text-secondary); font-size: 12px; font-weight: 700; letter-spacing: .03em; }
strong { margin-top: 5px; color: var(--text-primary); font-size: 33px; font-variant-numeric: tabular-nums; line-height: 1; }
strong small { margin-left: 1px; color: var(--text-secondary); font-size: 14px; }
.quota-caption { margin-top: 5px; color: var(--text-tertiary); font-size: 11px; }
.reset-time { position: absolute; left: 0; right: 0; bottom: 1px; color: var(--text-tertiary); font-size: 11px; }
.quota-ring:has(.burn-down) .reset-time { bottom: 18px; }
.burn-down { position: absolute; left: 0; right: 0; bottom: 1px; color: #ffb27a; font-size: 11px; font-weight: 600; }
.unavailable svg { filter: none; }
</style>
