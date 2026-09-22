<script setup lang="ts">
import { computed, ref } from 'vue'
import QuotaRing from './QuotaRing.vue'
import TokenDetails from './TokenDetails.vue'
import { compactNumber, currencyAmount } from '../format'
import { useEquivalentValue } from '../composables/useEquivalentValue'
import { useDashboardStore } from '../stores/dashboard'
import { readPreference } from '../preferences'
import { analysisValue } from '../analysisFormatting'
import type { DashboardSnapshot, UsageAnalysisResult } from '../types'
const props = defineProps<{ snapshot: DashboardSnapshot, analysis: UsageAnalysisResult | null }>()
const store = useDashboardStore()
const valueExpanded = ref(false)
const showSubscription = ref(readPreference('subscriptionComparison') === 'true')
const { subscriptionAmount, subscriptionSourceLabel, subscriptionAmountIsAuto } = useEquivalentValue(computed(() => props.snapshot))
const amount = computed(() => props.analysis ? analysisValue(props.analysis.totals, 'amount', store.settings?.amountPerThousandCredits ?? 40) : '—')
const missingRates = computed(() => [...new Set(props.analysis?.missingRates?.map(row => row.model) ?? [])])
</script>
<template>
  <section class="overview-grid">
    <article class="glass-card quota-card">
      <div class="card-heading"><div><span class="eyebrow">账户额度 · 独立于本机筛选</span><h2>额度窗口</h2></div><span class="quality-chip">{{ snapshot.primaryQuota || snapshot.secondaryQuota ? '账户来源' : '额度不可用' }}</span></div>
      <div class="quota-rings"><QuotaRing label="5 小时" :quota="snapshot.primaryQuota" color="blue" :forecast="snapshot.primaryForecast" /><div class="ring-divider" /><QuotaRing label="7 天" :quota="snapshot.secondaryQuota" color="violet" :forecast="snapshot.secondaryForecast" /></div>
      <p v-if="!snapshot.primaryQuota && !snapshot.secondaryQuota" class="overview-note">来源未提供账户额度，不根据本机 Token 或金额推算。</p>
    </article>
    <article class="glass-card token-card">
      <div class="card-heading"><div><span class="eyebrow">当前筛选范围</span><h2>本机原始 Token</h2></div><span class="quality-chip subtle">原始日志及长期留存</span></div>
      <div class="metric-grid">
        <div class="metric metric-0"><span>总量</span><strong>{{ analysis ? compactNumber(analysis.totals.tokens) : '—' }}</strong><small>tokens</small></div>
        <div class="metric metric-1"><span>模型</span><strong>{{ analysis?.models.length ?? '—' }}</strong><small>个分类</small></div>
        <div class="metric metric-2"><span>会话组</span><strong>{{ analysis?.sessionCount ?? '—' }}</strong><small>去重汇总</small></div>
        <div class="metric metric-3"><span>未归属历史</span><strong>{{ analysis ? compactNumber(analysis.totals.unattributedTokens) : '—' }}</strong><small>tokens</small></div>
      </div>
      <TokenDetails v-if="analysis" :totals="analysis.totals" :runtime="snapshot.runtime" />
    </article>
    <article class="glass-card value-card" :class="{ 'value-collapsed': !valueExpanded }">
      <div class="card-heading"><div><span class="eyebrow">金额参考 · 当前筛选范围</span><h2>API 等效金额</h2></div><button class="value-toggle" type="button" :aria-expanded="valueExpanded" @click="valueExpanded = !valueExpanded">{{ valueExpanded ? '收起明细' : '展开明细' }}</button></div>
      <div class="value-main"><span>按用量发生日期匹配费率，不是订阅扣费或 API 账单</span><strong>{{ amount }}</strong></div>
      <div v-if="valueExpanded && analysis" class="value-details">
        <p class="overview-note">已核算 {{ compactNumber(analysis.totals.ratedTokens) }} / {{ compactNumber(analysis.totals.tokens) }} Token；{{ compactNumber(analysis.totals.unratedTokens) }} Token 金额未知。≥ 表示仅包含已核算部分。</p>
        <p v-if="missingRates.length" class="overview-note">所选范围缺少适用日期的费率：{{ missingRates.join('、') }}。请在设置中补充，日期明细可在会话分析查看。</p>
        <div v-if="showSubscription" class="value-summary-grid"><span :title="subscriptionSourceLabel"><small>订阅月费参考 · {{ subscriptionAmountIsAuto ? '自动识别' : '手动设置' }}</small><strong>{{ currencyAmount(subscriptionAmount) }}</strong></span><span><small>所选期间 API 等效金额</small><strong>{{ amount }}</strong></span></div>
        <p v-if="showSubscription" class="overview-note">订阅月费是整月价格，所选日期范围可能不足或超过一个月；对比不代表实际收益。</p>
        <p v-if="analysis.legacyEstimates.length" class="overview-note">另有 {{ analysis.legacyEstimates.length }} 天旧版金额估值，单独保留在分析页，避免与重算明细重复相加。</p>
        <p class="overview-note">已知明细随费率修正重新计算；未知金额不视为零。适用费率和数据来源在会话明细中展示。</p>
      </div>
    </article>
  </section>
</template>
<style scoped>
.overview-note { color: var(--text-secondary); font-size: 11px; line-height: 1.8; }
.value-toggle { border: 1px solid var(--stroke); background: var(--surface-subtle); color: var(--text-secondary); border-radius: 7px; padding: 6px 9px; cursor: pointer; font-size: 11px; }
.value-card.value-collapsed { min-height: 145px !important; }
.value-main strong { overflow-wrap: anywhere; }
</style>
