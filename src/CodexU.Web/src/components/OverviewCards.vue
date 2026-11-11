<script setup lang="ts">
import { computed, ref } from 'vue'
import QuotaRing from './QuotaRing.vue'
import ClaudeDataNotice from './ClaudeDataNotice.vue'
import UiIcon from './UiIcon.vue'
import { compactNumber, exhaustionTime, qualityLabel } from '../format'
import { useEquivalentValue } from '../composables/useEquivalentValue'
import { hasQuotaReset, isDesktopQuota } from '../quotaPresentation'
import { useQuotaClock } from '../composables/useQuotaClock'
import type { DashboardSnapshot } from '../types'

const props = defineProps<{ snapshot: DashboardSnapshot }>()
const now = useQuotaClock(() => [props.snapshot.primaryQuota?.resetsAt, props.snapshot.secondaryQuota?.resetsAt])
const valueExpanded = ref(false)
const snapshotRef = computed<DashboardSnapshot | null>(() => props.snapshot)
const {
  monthlyValue,
  valueProgress,
  amountMoney,
  creditTooltip,
} = useEquivalentValue(snapshotRef)

const amountAvailable = computed(() => monthlyValue.value?.knownAmount != null)
const lowerBound = computed(() => amountAvailable.value && monthlyValue.value?.isLowerBound ? '≥ ' : '')
const monthlyLabel = computed(() => monthlyValue.value?.subscriptionSource === 'manual' ? '手动'
  : monthlyValue.value?.subscriptionSource === 'auto' ? '自动' : '未知')

const tokenMetrics = computed(() => [
  { label: '今日', value: props.snapshot.tokens.today.tokens, quality: props.snapshot.tokens.today.quality },
  { label: '近 7 天', value: props.snapshot.tokens.sevenDays.tokens, quality: props.snapshot.tokens.sevenDays.quality },
  { label: '本月', value: props.snapshot.tokens.month.tokens, quality: props.snapshot.tokens.month.quality },
  { label: '累计', value: props.snapshot.tokens.lifetime.tokens, quality: props.snapshot.tokens.lifetime.quality },
])

const split = computed(() => props.snapshot.tokens.today.breakdown)
const quotaRisks = computed(() => [
  { label: '5 小时额度', quota: props.snapshot.primaryQuota, forecast: props.snapshot.primaryForecast },
  { label: '7 天额度', quota: props.snapshot.secondaryQuota, forecast: props.snapshot.secondaryForecast },
].filter((item) => !hasQuotaReset(item.quota, now.value) && !isDesktopQuota(item.quota) && item.forecast?.exhaustsBeforeReset)
  .map((item) => `${item.label}：${exhaustionTime(item.forecast?.exhaustsAt, now.value)}`))
const splitTotal = computed(() => {
  if (!split.value) return 1
  // Cache writes are their own slice of the input side. Leaving them out of the
  // denominator would renormalize the remaining bars to 100% and hide the slice
  // that dominates Claude usage — writes routinely exceed plain input by orders
  // of magnitude there.
  return Math.max(1, split.value.uncachedInputTokens
    + split.value.billableCachedInputTokens
    + (split.value.billableCacheWriteTokens ?? 0)
    + split.value.outputTokens)
})

function splitPercentage(value?: number | null): string {
  const percentage = Math.max(0, value ?? 0) / splitTotal.value * 100
  return `${percentage.toFixed(1)}%`
}
</script>

<template>
  <section class="overview-grid">
    <div v-if="quotaRisks.length" class="quota-risk-notice" role="alert" aria-live="polite">
      <UiIcon name="alert" :size="18" />
      <div><strong>额度可能在重置前耗尽</strong><span>{{ quotaRisks.join('；') }}</span></div>
    </div>
    <article class="glass-card quota-card">
      <div class="card-heading">
        <div><span class="eyebrow">账户额度</span><h2>额度窗口</h2></div>
        <span class="quality-chip">{{ snapshot.primaryQuota || snapshot.secondaryQuota ? '本机额度' : '额度不可用' }}</span>
      </div>
      <div class="quota-rings">
        <QuotaRing
          label="5 小时"
          :quota="snapshot.primaryQuota"
          :now="now"
          color="blue"
          :forecast="snapshot.primaryForecast"
        />
        <div class="ring-divider" />
        <QuotaRing
          label="7 天"
          :quota="snapshot.secondaryQuota"
          :now="now"
          color="violet"
          :forecast="snapshot.secondaryForecast"
        />
      </div>
      <ClaudeDataNotice :snapshot="snapshot" :now="now" show-manual-amount />
    </article>

    <article class="glass-card token-card">
      <div class="card-heading">
        <div><span class="eyebrow">用量概览</span><h2>Token 用量</h2></div>
        <span class="quality-chip subtle">本机原始统计 · {{ qualityLabel(snapshot.tokens.today.quality) }}</span>
      </div>
      <div class="metric-grid">
        <div v-for="(metric, index) in tokenMetrics" :key="metric.label" class="metric" :class="`metric-${index}`">
          <span>{{ metric.label }}</span><strong>{{ compactNumber(metric.value) }}</strong><small>tokens</small>
        </div>
      </div>
      <div class="token-split">
        <div class="split-heading">
          <strong>本机原始构成（含缓存）</strong>
          <span>来自本机日志原始事件</span>
        </div>
        <div class="split-track">
          <i class="uncached" :style="{ width: `${(split?.uncachedInputTokens ?? 0) / splitTotal * 100}%` }" />
          <i class="cached" :style="{ width: `${(split?.billableCachedInputTokens ?? 0) / splitTotal * 100}%` }" />
          <!-- Omitted rather than zero-width: every slice carries min-width 1px,
               so a source without cache writes would still draw a stray sliver. -->
          <i v-if="split?.billableCacheWriteTokens" class="cache-write" :style="{ width: `${split.billableCacheWriteTokens / splitTotal * 100}%` }" />
          <i class="output" :style="{ width: `${(split?.outputTokens ?? 0) / splitTotal * 100}%` }" />
        </div>
        <div class="split-legend">
          <span><i class="dot uncached" />未缓存输入 {{ compactNumber(split?.uncachedInputTokens) }} · {{ splitPercentage(split?.uncachedInputTokens) }}</span>
          <span><i class="dot cached" />缓存读取 {{ compactNumber(split?.billableCachedInputTokens) }} · {{ splitPercentage(split?.billableCachedInputTokens) }}</span>
          <span v-if="split?.billableCacheWriteTokens"><i class="dot cache-write" />缓存写入 {{ compactNumber(split?.billableCacheWriteTokens) }} · {{ splitPercentage(split?.billableCacheWriteTokens) }}</span>
          <span><i class="dot output" />输出 {{ compactNumber(split?.outputTokens) }} · {{ splitPercentage(split?.outputTokens) }}</span>
        </div>
      </div>
    </article>

    <article class="glass-card value-card" :class="{ 'value-collapsed': !valueExpanded }">
      <span class="value-orbit" aria-hidden="true" />
      <div class="card-heading">
        <div><span class="eyebrow">价值估算</span><h2>本机原始 Token 按 API 价估算</h2></div>
        <div class="value-heading-actions">
          <div class="credit-info" tabindex="0" :aria-label="creditTooltip || '暂无可核算的模型数据'">
            <span class="info-icon">i</span>
            <div class="credit-popover">
              <div class="credit-popover-heading"><strong>本月金额明细</strong><span>按模型和 Token 类型核算后换算</span></div>
              <template v-for="model in monthlyValue?.models" :key="model.model">
                <div class="credit-model-row">
                  <strong>{{ model.model }}</strong>
                  <span>未缓存输入 {{ amountMoney(model.input) }}</span>
                  <span>缓存输入 {{ amountMoney(model.cachedInput) }}</span>
                  <span v-if="model.cacheWrite">缓存写入 {{ amountMoney(model.cacheWrite) }}</span>
                  <span>输出 {{ amountMoney(model.output) }}</span>
                  <em>{{ amountMoney(model.total) }}</em>
                </div>
                <div v-if="model.rateVersions?.length" class="credit-version-list">
                  <span v-for="version in model.rateVersions" :key="`${version.catalogVersion}-${version.effectiveFrom ?? 'baseline'}-${version.source}`">
                    {{ version.catalogVersion }} · {{ version.effectiveFrom ?? '全部历史' }} · {{ version.source }} · {{ amountMoney(version.total) }}
                  </span>
                </div>
              </template>
              <div v-if="!monthlyValue?.models?.length" class="credit-empty">暂无可核算的模型数据</div>
            </div>
          </div>
          <button class="value-toggle" type="button" :aria-expanded="valueExpanded" @click="valueExpanded = !valueExpanded">
            {{ valueExpanded ? '收起明细' : '展开明细' }}
            <UiIcon name="chevron" :size="14" :class="{ expanded: valueExpanded }" />
          </button>
        </div>
      </div>
      <div class="value-main"><span>本机原始 Token 按 API 列表价估算，不是账单金额</span><strong>{{ amountAvailable ? lowerBound + amountMoney(monthlyValue?.knownAmount, true) : '无法核算' }}</strong></div>
      <div v-if="valueExpanded" class="value-details">
        <div v-if="valueProgress != null" class="value-track"><i :style="{ width: `${valueProgress}%` }" /></div>
        <div class="value-summary-grid">
          <span><small>订阅月费 · {{ monthlyLabel }}</small><strong>{{ monthlyValue?.subscriptionAmount != null ? amountMoney(monthlyValue.subscriptionAmount) : '无法推算' }}</strong></span>
          <span><small>净等价值</small><strong>{{ monthlyValue?.netAmount != null ? lowerBound + amountMoney(monthlyValue.netAmount, true, true) : '无法计算' }}</strong></span>
          <span><small>回本倍数</small><strong>{{ monthlyValue?.paybackMultiple != null ? lowerBound + monthlyValue.paybackMultiple.toFixed(1) + '×' : '无法计算' }}</strong></span>
        </div>
        <div class="value-breakdown">
          <span><small>输入与写入</small><strong>{{ monthlyValue?.breakdown ? lowerBound + amountMoney(monthlyValue.breakdown.inputAndWrites, true) : '--' }}</strong></span>
          <span><small>缓存读取</small><strong>{{ monthlyValue?.breakdown ? lowerBound + amountMoney(monthlyValue.breakdown.cachedInput, true) : '--' }}</strong></span>
          <span><small>模型输出</small><strong>{{ monthlyValue?.breakdown ? lowerBound + amountMoney(monthlyValue.breakdown.output, true) : '--' }}</strong></span>
          <span class="saved"><small>缓存省下</small><strong>{{ monthlyValue?.breakdown ? lowerBound + amountMoney(monthlyValue.breakdown.cachedSavings, true) : '--' }}</strong></span>
        </div>
        <div class="value-bottom">
          <div>
            <span>本月本机原始 {{ compactNumber(snapshot.tokens.month.tokens) }} Token</span>
            <small v-if="monthlyValue?.coveragePercent != null">已核算 {{ monthlyValue.coveragePercent.toFixed(1) }}% · {{ monthlyValue?.models?.length ?? 0 }} 个模型</small>
            <small v-if="snapshot.tokens.month.unratedTokens">未核算 {{ compactNumber(snapshot.tokens.month.unratedTokens) }} Token<span v-if="snapshot.tokens.month.missingRateTokens"> · 缺费率 {{ compactNumber(snapshot.tokens.month.missingRateTokens) }}</span><span v-if="snapshot.tokens.month.incompleteBreakdownTokens"> · 缺计价分项 {{ compactNumber(snapshot.tokens.month.incompleteBreakdownTokens) }}</span></small>
          </div>
          <div>
            <span v-if="monthlyValue?.projectedAmount != null">预计本月 {{ amountMoney(monthlyValue.projectedAmount, true) }}</span>
            <strong v-if="monthlyValue?.paybackStatus === 'reached'">已回本 · 超出{{ lowerBound ? '至少' : '' }} {{ amountMoney(monthlyValue.netAmount, true) }}</strong>
            <strong v-else-if="monthlyValue?.paybackStatus === 'below' && monthlyValue.netAmount != null">待回本 · 尚差 {{ amountMoney(Math.abs(monthlyValue.netAmount), true) }}</strong>
            <strong v-else>暂无法判断是否回本</strong>
          </div>
        </div>
      </div>
    </article>
  </section>
</template>

<style scoped>
.quota-risk-notice { grid-column: 1 / -1; display: flex; align-items: center; gap: 10px; padding: 11px 14px; border: 1px solid rgba(244,168,108,.3); border-radius: 12px; color: #ffd8bc; background: rgba(164,83,43,.12); }
.quota-risk-notice > div { display: flex; align-items: baseline; gap: 8px; min-width: 0; }
.quota-risk-notice strong { flex: none; font-size: 12px; }
.quota-risk-notice span { overflow: hidden; color: #e8aa7b; font-size: 11px; text-overflow: ellipsis; white-space: nowrap; }
.value-heading-actions { display: flex; align-items: center; gap: 8px; }
.value-toggle { display: inline-flex; align-items: center; gap: 4px; padding: 4px 7px; border: 1px solid var(--stroke); border-radius: 7px; color: var(--text-secondary); background: var(--surface-subtle); cursor: pointer; font-size: 11px; }
.value-toggle .ui-icon { transition: transform .18s ease; }
.value-toggle .ui-icon.expanded { transform: rotate(180deg); }
.value-card.value-collapsed { min-height: 145px !important; }
.value-card.value-collapsed .value-main { margin-top: 19px; }
.split-heading { display: flex; align-items: baseline; justify-content: space-between; gap: 8px; margin-bottom: 7px; }
.split-heading strong { flex: none; color: var(--text-secondary); font-size: 10px; }
.split-heading span { color: var(--text-tertiary); font-size: 9px; text-align: right; }
@media (max-width: 620px) {
  .quota-risk-notice > div { display: block; }
  .quota-risk-notice span { display: block; margin-top: 3px; white-space: normal; }
  .value-toggle { font-size: 0; }
}
</style>
