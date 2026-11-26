<script setup lang="ts">
import { computed, useId } from 'vue'
import type { AntigravityQuotaBucket, QuotaForecast, RateLimitWindow } from '../types'
import { exhaustionTime, resetTime } from '../format'
import { isDesktopQuota, isDesktopQueryQuota, quotaQueryTime, quotaSampleTime } from '../quotaPresentation'

const props = defineProps<{
  label: string
  quota?: RateLimitWindow
  bucket?: AntigravityQuotaBucket
  color: 'blue' | 'violet'
  now: number
  forecast?: QuotaForecast
}>()

const radius = 54
const circumference = 2 * Math.PI * radius
const gradientId = `quota-${useId()}`
const numberFormat = new Intl.NumberFormat('zh-CN', { maximumFractionDigits: 2 })
const percent = computed(() => props.bucket
  ? (props.bucket.disabled ? null : props.bucket.remainingPercent ?? null)
  : props.quota ? Math.round(props.quota.remainingPercent) : null)
const amount = computed(() => props.bucket && !props.bucket.disabled && percent.value == null
  ? props.bucket.remainingAmount ?? null : null)
const value = computed(() => {
  if (percent.value != null) {
    if (!props.bucket) return String(percent.value)
    return percent.value > 0 && percent.value < 0.01 ? '< 0.01' : numberFormat.format(percent.value)
  }
  return amount.value == null ? '--' : numberFormat.format(amount.value)
})
const dashOffset = computed(() => circumference * (1 - (percent.value ?? 0) / 100))
const isSample = computed(() => isDesktopQuota(props.quota))
const isQuery = computed(() => isDesktopQueryQuota(props.quota))
const resetAt = computed(() => props.bucket ? props.bucket.resetsAt : props.quota?.resetsAt)
const expired = computed(() => !!resetAt.value && Date.parse(resetAt.value) <= props.now)
const caption = computed(() => {
  if (props.bucket?.disabled) return '未启用'
  if (props.bucket && percent.value == null && amount.value == null) return '剩余额度未知'
  return isQuery.value ? '查询时剩余' : isSample.value ? '采样剩余' : expired.value ? '上次剩余' : '剩余'
})
const known = computed(() => percent.value != null || amount.value != null)
const describedValue = computed(() => known.value
  ? `${caption.value} ${value.value}${percent.value != null ? '%' : ''}`
  : props.bucket ? caption.value : '不可用')
const localReset = computed(() => {
  const reset = resetAt.value
  if (!reset || !Number.isFinite(Date.parse(reset))) return ''
  const date = new Date(reset)
  const today = date.toDateString() === new Date(props.now).toDateString()
  return date.toLocaleString('zh-CN', {
    month: today ? undefined : '2-digit', day: today ? undefined : '2-digit',
    hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  })
})

// A window that resets before it would run out is not news, however fast it is being
// consumed — showing a countdown for it would cry wolf at every busy stretch.
const burnDown = computed(() =>
  props.quota && !isSample.value && !expired.value && props.forecast?.exhaustsBeforeReset
    ? exhaustionTime(props.forecast.exhaustsAt, props.now)
    : '')

const burnRate = computed(() =>
  props.forecast ? `按最近用量每分钟消耗 ${props.forecast.percentPerMinute.toFixed(2)}%` : undefined)
</script>

<template>
  <div class="quota-ring" :class="{ unavailable: percent == null, sampled: isSample, 'bucket-ring': bucket }">
    <svg viewBox="0 0 132 132" role="img" :aria-label="`${label} ${!bucket && !known ? `${caption} ` : ''}${describedValue}`">
      <title>{{ label }}额度{{ describedValue }}</title>
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
        v-if="percent != null"
        class="ring-progress"
        cx="66"
        cy="66"
        :r="radius"
        :stroke="`url(#${gradientId})`"
        :stroke-dasharray="circumference"
        :stroke-dashoffset="dashOffset"
      />
      <circle v-if="percent != null && (!bucket || percent > 0)" class="ring-glow" cx="66" cy="12" r="3" :fill="color === 'blue' ? '#8dceff' : '#ef9ddd'" />
    </svg>
    <div class="quota-content">
      <span class="quota-label">{{ label }}</span>
      <strong :class="{ 'bucket-value-long': bucket && value.length > 4 }">{{ value }}<small v-if="percent != null">%</small></strong>
      <span class="quota-caption">{{ caption }}</span>
    </div>
    <span v-if="isSample" class="sample-time">
      <time :datetime="quota?.measuredAt ?? undefined">{{ isQuery ? quotaQueryTime(quota?.measuredAt) : quotaSampleTime(quota?.measuredAt) }}</time>
      <span>{{ isQuery ? '本地时间 · 查询快照' : '本地时间 · 非实时' }}</span>
    </span>
    <span v-if="!bucket?.disabled || localReset" class="reset-time">
      <time v-if="localReset && (!expired || bucket)" :datetime="resetAt ?? undefined" :title="`本地重置时间 ${quotaSampleTime(resetAt)}`">{{ localReset }} · </time>{{ isSample && !resetAt ? '来源未提供重置时间' : resetTime(resetAt, now) }}
    </span>
    <span v-if="burnDown" class="burn-down" :title="burnRate">{{ burnDown }}</span>
  </div>
</template>

<style scoped>
.quota-ring { position: relative; width: var(--quota-ring-width, 156px); max-width: 100%; padding-bottom: 25px; text-align: center; }
.quota-ring:has(.burn-down) { padding-bottom: 42px; }
.quota-ring.sampled { padding-bottom: 59px; }
.sample-time { position: absolute; left: 0; right: 0; bottom: 20px; display: flex; flex-direction: column; color: var(--text-tertiary); font-size: 10px; line-height: 1.5; font-variant-numeric: tabular-nums; }
.quota-ring svg { width: var(--quota-ring-size, 132px); height: var(--quota-ring-size, 132px); max-width: 100%; transform: rotate(-90deg); filter: drop-shadow(0 10px 24px rgba(72, 91, 210, .22)); }
.ring-track, .ring-progress { fill: none; stroke-width: 9; }
.ring-track { stroke: rgba(255,255,255,.075); }
.ring-progress { stroke-linecap: round; transition: stroke-dashoffset .75s cubic-bezier(.2,.85,.25,1); }
.ring-glow { filter: drop-shadow(0 0 5px currentColor); }
.quota-content { position: absolute; top: var(--quota-ring-content-top, 25px); left: 0; right: 0; display: flex; flex-direction: column; align-items: center; }
.quota-label { color: var(--text-secondary); font-size: 12px; font-weight: 700; letter-spacing: .03em; }
strong { margin-top: 5px; color: var(--text-primary); font-size: var(--quota-ring-value-size, 33px); font-variant-numeric: tabular-nums; line-height: 1; }
strong small { margin-left: 1px; color: var(--text-secondary); font-size: 14px; }
.quota-caption { margin-top: 5px; color: var(--text-tertiary); font-size: 11px; }
.reset-time { position: absolute; left: 0; right: 0; bottom: 1px; color: var(--text-tertiary); font-size: 11px; }
.quota-ring:has(.burn-down) .reset-time { bottom: 18px; }
.burn-down { position: absolute; left: 0; right: 0; bottom: 1px; color: #ffb27a; font-size: 11px; font-weight: 600; }
.unavailable svg { filter: none; }
.bucket-ring { padding-bottom: 0; }
.bucket-ring .quota-content { padding-inline: 12px; }
.bucket-ring .quota-label { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.bucket-ring strong { max-width: 100%; overflow: hidden; text-overflow: ellipsis; }
.bucket-ring .bucket-value-long { font-size: var(--bucket-ring-long-value-size, 23px); }
.bucket-ring .reset-time { position: static; display: block; min-height: 20px; line-height: 1.4; }
</style>
