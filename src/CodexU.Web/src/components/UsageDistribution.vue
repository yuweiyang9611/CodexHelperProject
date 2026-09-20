<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { compactNumber } from '../format'
import type { DailyUsage } from '../types'
import { buildUsageDistribution, type DistributionDimension } from '../usageDistribution'

const props = defineProps<{ days: DailyUsage[], refreshedAt: string }>()
const period = ref<7 | 30>(7)
const dimension = ref<DistributionDimension>('feature')
const activeDay = ref<string | null>(null)
const highlighted = ref<string | null>(null)
const chart = computed(() => buildUsageDistribution(props.days, props.refreshedAt, period.value, dimension.value))
const detail = computed(() => chart.value.days.find(day => day.date === activeDay.value))
const percentage = (value: number) => value > 0 && value < 0.1 ? '<0.1%' : `${value.toFixed(1)}%`
watch([period, dimension, () => props.days], () => { activeDay.value = null; highlighted.value = null })
</script>

<template>
  <article class="inner-card distribution-card" aria-label="用量分布">
    <div class="distribution-heading">
      <div><h3>用量分布</h3><p>查看用量分布及使用情况随时间的变化</p></div>
      <div class="distribution-controls">
        <div class="distribution-period" role="group" aria-label="统计时间范围">
          <button v-for="value in ([7, 30] as const)" :key="value" type="button" :aria-pressed="period === value" @click="period = value">{{ value }} 天</button>
        </div>
        <select v-model="dimension" aria-label="用量分组"><option value="feature">按功能</option><option value="model">按模型</option></select>
      </div>
    </div>
    <div class="distribution-summary" aria-live="polite">近 {{ period }} 天 · {{ compactNumber(chart.total) }} Token</div>
    <div class="distribution-plot" @mouseleave="activeDay = null">
      <div class="distribution-grid" aria-hidden="true"><i v-for="line in 4" :key="line" /></div>
      <span class="distribution-axis-max" aria-hidden="true">{{ compactNumber(chart.max === 1 && !chart.total ? 0 : chart.max) }}</span>
      <div class="distribution-bars">
        <button v-for="day in chart.days" :key="day.date" type="button" class="distribution-day"
          :aria-label="`${day.date}，${day.total.toLocaleString()} Token${chart.series.filter(s => day.values.get(s.id)).map(s => `，${s.label} ${day.values.get(s.id)?.toLocaleString()}`).join('')}`"
          @mouseenter="activeDay = day.date" @focus="activeDay = day.date" @blur="activeDay = null" @click="activeDay = day.date">
          <span class="distribution-stack" :style="{ height: `${day.total / chart.max * 100}%` }" aria-hidden="true">
            <span v-for="series in chart.series" :key="series.id" :style="{ height: `${day.total ? (day.values.get(series.id) ?? 0) / day.total * 100 : 0}%`, background: series.color, opacity: highlighted && highlighted !== series.id ? 0.2 : 0.8 }" />
          </span>
        </button>
      </div>
      <div v-if="detail" class="distribution-tooltip" role="status">
        <strong>{{ detail.label }} · {{ compactNumber(detail.total) }} Token</strong>
        <span v-for="series in chart.series.filter(s => detail?.values.get(s.id))" :key="series.id">{{ series.label }} <b>{{ compactNumber(detail.values.get(series.id)) }}</b></span>
        <span v-if="!detail.total">暂无用量</span>
      </div>
      <p v-if="!chart.total" class="distribution-empty" role="status">此期间暂无本地用量记录</p>
    </div>
    <div class="distribution-dates" aria-hidden="true"><span>{{ chart.days[0]?.label }}</span><span>{{ chart.days[Math.floor((period - 1) / 2)]?.label }}</span><span>{{ chart.days[period - 1]?.label }}</span></div>
    <div v-if="chart.series.length" class="distribution-legend" aria-label="分类占比">
      <button v-for="series in chart.series" :key="series.id" type="button" :aria-pressed="highlighted === series.id"
        :style="{ '--series-color': series.color }" :class="{ muted: highlighted && highlighted !== series.id }"
        :title="`${series.label} · ${series.tokens.toLocaleString()} Token`" @click="highlighted = highlighted === series.id ? null : series.id">
        <span>{{ series.label }}</span><strong>{{ percentage(series.percent) }}</strong>
      </button>
    </div>
    <p class="distribution-note">占比按所选期间的本地 Token 计算。功能依据会话来源识别；自动审查、记忆更新等细项暂无法独立统计，缺少归属的记录计入未分类。</p>
  </article>
</template>

<style scoped>
.distribution-card { margin-bottom: 16px; }
.distribution-heading { display: flex; align-items: center; justify-content: space-between; gap: 16px; flex-wrap: wrap; }
h3 { margin: 0; color: var(--text-primary); font-size: 15px; }
.distribution-heading p { margin: 5px 0 0; color: var(--text-secondary); font-size: 12px; }
.distribution-controls { display: flex; gap: 10px; align-items: center; }
.distribution-period { display: flex; padding: 3px; border-radius: 10px; background: var(--surface-subtle); border: 1px solid var(--stroke); }
.distribution-period button { padding: 5px 12px; border: 0; border-radius: 7px; background: transparent; color: var(--text-secondary); cursor: pointer; font-size: 12px; }
.distribution-period button[aria-pressed="true"] { background: var(--surface-strong); color: var(--text-primary); box-shadow: 0 1px 5px #0001; }
select { font: inherit; font-size: 12px; color: var(--text-primary); background: var(--surface-strong); border: 1px solid var(--stroke); border-radius: 9px; padding: 7px 28px 7px 12px; }
.distribution-summary { margin: 18px 0 12px; font-size: 11px; color: var(--text-secondary); }
.distribution-plot { position: relative; height: 164px; padding-top: 20px; }
.distribution-grid { position: absolute; inset: 20px 0 0; display: flex; flex-direction: column; justify-content: space-between; pointer-events: none; }
.distribution-grid i { border-bottom: 1px solid var(--stroke-subtle); }
.distribution-axis-max { position: absolute; top: 0; left: 0; font-size: 10px; color: var(--text-secondary); }
.distribution-bars { position: relative; display: flex; height: 100%; align-items: stretch; gap: 4px; }
.distribution-day { flex: 1; min-width: 0; padding: 0; border: 0; background: transparent; display: flex; align-items: flex-end; justify-content: center; cursor: pointer; border-radius: 4px 4px 0 0; }
.distribution-day:hover, .distribution-day:focus-visible { background: var(--surface-subtle); }
.distribution-stack { display: flex; flex-direction: column-reverse; width: 60%; max-width: 38px; border-radius: 4px 4px 0 0; overflow: hidden; }
.distribution-stack > span { width: 100%; flex-shrink: 0; }
.distribution-dates { display: flex; justify-content: space-between; margin: 8px 0 20px; font-size: 11px; color: var(--text-secondary); }
.distribution-legend { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 14px 22px; max-height: 132px; overflow-y: auto; padding: 3px; }
.distribution-legend button { text-align: left; background: transparent; color: var(--text-primary); border: 0; border-left: 3px solid var(--series-color); border-radius: 2px; padding: 0 0 0 10px; min-width: 0; cursor: pointer; }
.distribution-legend span { display: block; overflow-wrap: anywhere; font-size: 11px; }
.distribution-legend strong { display: block; margin-top: 4px; font-size: 16px; font-weight: 600; }
.distribution-legend .muted { opacity: .5; }
.distribution-tooltip { position: absolute; top: 12px; right: 12px; min-width: 180px; max-width: 85%; max-height: 148px; overflow-y: auto; padding: 10px 12px; border: 1px solid var(--stroke); border-radius: 9px; background: var(--surface-strong); color: var(--text-primary); box-shadow: 0 4px 20px #0002; font-size: 11px; pointer-events: none; }
.distribution-tooltip strong { display: block; margin-bottom: 6px; }
.distribution-tooltip span { display: flex; justify-content: space-between; gap: 16px; overflow-wrap: anywhere; }
.distribution-empty { position: absolute; inset: 40% 0 auto; text-align: center; color: var(--text-secondary); font-size: 12px; pointer-events: none; }
.distribution-note { margin: 18px 0 0; color: var(--text-secondary); font-size: 10px; line-height: 1.7; }
@container (max-width: 600px) { .distribution-legend { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
</style>
