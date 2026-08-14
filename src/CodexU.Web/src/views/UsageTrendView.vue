<script setup lang="ts">
import { computed } from 'vue'
import TrendChart from '../components/TrendChart.vue'
import UsageHeatmap from '../components/UsageHeatmap.vue'
import { useEquivalentValue } from '../composables/useEquivalentValue'
import { useRecentUsage } from '../composables/useRecentUsage'
import { compactNumber, qualityLabel } from '../format'
import { useDashboardStore } from '../stores/dashboard'
import type { DashboardSnapshot } from '../types'

const props = defineProps<{ snapshot: DashboardSnapshot }>()
const store = useDashboardStore()
const snapshotRef = computed<DashboardSnapshot | null>(() => props.snapshot)
const { money } = useEquivalentValue(snapshotRef)
const { lastSeven, lastSevenTotal, trendChange, peakDay } = useRecentUsage(snapshotRef)

const maxModelTokens = computed(() => Math.max(...props.snapshot.models.map((model) => model.tokens), 1))

/**
 * Task-lifecycle counters exist only for Codex. The Claude reader has no equivalent
 * to report and emits an all-zero struct, which rendered as "0 启动 / 0 完成 / 0 中止"
 * beside real token charts — a measured claim about activity nobody measured. This is
 * the same 补零 the combined view refuses to do; the single-runtime view should not do
 * it either.
 */
const providesTaskLifecycle = computed(() => props.snapshot.runtime === 'codex')

/**
 * Reuse and incremental-read counts describe an index. Claude has none, and Codex
 * reports zeros for both when incremental indexing is switched off — in neither case
 * is a zero a measurement.
 */
const indexSummary = computed(() => props.snapshot.indexStatus.enabled
  ? `${props.snapshot.indexStatus.reusedFiles} 复用 · ${props.snapshot.indexStatus.incrementalFiles} 续读`
  : props.snapshot.runtime === 'codex' ? '未启用增量索引' : '该运行时无索引')
const longestActiveStreak = computed(() => {
  let longest = 0
  let current = 0
  for (const day of props.snapshot.dailyUsage) {
    current = day.tokens > 0 ? current + 1 : 0
    longest = Math.max(longest, current)
  }
  return longest
})
</script>

<template>
  <div class="usage-pane">
    <div class="usage-layout">
      <article class="inner-card heatmap-card">
        <div class="inner-heading"><div><span>活跃度</span><h3>最近半年本机原始用量</h3></div><em>{{ qualityLabel(snapshot.dailyUsage[0]?.quality) }}</em></div>
        <UsageHeatmap :days="snapshot.dailyUsage" :amount-per-thousand-credits="store.settings?.amountPerThousandCredits ?? 40" currency-symbol="US$" />
      </article>
      <article class="inner-card seven-day-card">
        <div class="inner-heading"><div><span>近 7 天</span><h3>近期趋势</h3></div><em :class="{ positive: (trendChange ?? 0) >= 0 }">{{ trendChange == null ? '新增使用' : `${trendChange >= 0 ? '+' : ''}${trendChange.toFixed(0)}%` }}</em></div>
        <TrendChart :days="snapshot.dailyUsage" />
        <div class="trend-stats"><span><small>日均</small><strong>{{ compactNumber(lastSevenTotal / 7) }}</strong></span><span><small>峰值</small><strong>{{ compactNumber(peakDay?.tokens) }}</strong></span><span><small>等效金额</small><strong>{{ money(lastSeven.reduce((sum, day) => sum + day.creditsUsed, 0)) }}</strong></span></div>
      </article>
    </div>
    <div class="analytics-insights">
      <article class="inner-card model-card"><div class="inner-heading"><div><span>本机日志</span><h3>本机原始模型 Token 分布</h3></div><em>{{ snapshot.models.length }} 个模型</em></div><div class="model-list"><div v-for="model in snapshot.models.slice(0, 8)" :key="model.model"><span><strong>{{ model.model }}</strong><em>{{ compactNumber(model.tokens) }}</em></span><i><b :style="{ width: `${model.tokens / maxModelTokens * 100}%` }" /></i></div></div></article>
      <article class="inner-card lifecycle-card"><div class="inner-heading"><div><span>任务生命周期</span><h3>任务运行情况</h3></div><em>{{ indexSummary }}</em></div><div v-if="providesTaskLifecycle" class="lifecycle-grid"><span><strong>{{ snapshot.taskLifecycle.started }}</strong><small>启动</small></span><span><strong>{{ snapshot.taskLifecycle.completed }}</strong><small>完成</small></span><span><strong>{{ snapshot.taskLifecycle.aborted }}</strong><small>中止</small></span></div><p v-else class="lifecycle-unavailable">该运行时不记录任务生命周期，此处不以 0 代替。</p><div class="source-chips"><span v-for="source in snapshot.sources" :key="source.id">{{ source.name }} {{ source.count }}</span><span>最长连续 {{ longestActiveStreak }} 天</span><span v-if="providesTaskLifecycle">最长单次 {{ Math.round(snapshot.taskLifecycle.longestDurationMilliseconds / 60000) }} 分钟</span></div></article>
    </div>
  </div>
</template>
