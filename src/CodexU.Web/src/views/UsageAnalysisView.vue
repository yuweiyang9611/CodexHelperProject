<script setup lang="ts">
import { computed } from 'vue'
import type { UsageAnalysisState } from '../composables/useUsageAnalysis'
import { analysisValue, featureLabel, sourceLabel } from '../analysisFormatting'
import TokenDetails from '../components/TokenDetails.vue'
import SkillUsageView from './SkillUsageView.vue'
import { useDashboardStore } from '../stores/dashboard'
import { compactNumber, currencyAmount } from '../format'
import type { DashboardSnapshot, UsageAnalysisGroup, UsageAnalysisTotals } from '../types'

const props = defineProps<{ state: UsageAnalysisState, snapshot: DashboardSnapshot }>()
const store = useDashboardStore()
const data = computed(() => props.state.result.value)
const conversion = computed(() => store.settings?.amountPerThousandCredits ?? 40)
const metric = computed(() => props.state.metric.value)
const value = (totals: UsageAnalysisTotals) => analysisValue(totals, metric.value, conversion.value)
const amount = (credits: number | null) => credits == null ? '金额未知' : currencyAmount(credits / 1000 * conversion.value)
const magnitude = (totals: UsageAnalysisTotals) => metric.value === 'tokens' ? totals.tokens : totals.creditsUsed ?? 0
const maximum = computed(() => Math.max(1, ...(data.value?.days.map(day => magnitude(day.totals)) ?? [])))
const sorted = (rows: UsageAnalysisGroup[]) => [...rows].sort((a, b) => magnitude(b.totals) - magnitude(a.totals))
const share = (totals: UsageAnalysisTotals) => {
  if (metric.value === 'amount' && totals.creditsUsed == null) return '未知'
  const total = data.value ? magnitude(data.value.totals) : 0
  return total > 0 ? `${(magnitude(totals) / total * 100).toFixed(1)}%` : '—'
}
const missingRates = computed(() => data.value?.missingRates?.map(row => `${row.model} · ${row.date}`) ?? [])
</script>
<template>
  <div class="analysis-view">
    <h2 class="sr-only">用量分析</h2>
    <div v-if="state.loading.value" class="analysis-state" role="status">正在读取所选范围的历史用量…</div>
    <div v-else-if="state.error.value" class="analysis-state" role="alert">{{ state.error.value }} <button type="button" @click="state.refresh">重试</button></div>
    <template v-else-if="data">
      <div class="analysis-summary" aria-live="polite">
        <div><span>{{ metric === 'tokens' ? '本机原始 Token' : 'API 等效金额' }}</span><strong>{{ value(data.totals) }}</strong></div>
        <div><span>会话组</span><strong>{{ data.sessionCount }}</strong></div>
        <div><span>未归属历史用量</span><strong>{{ compactNumber(data.totals.unattributedTokens) }} <small>Token</small></strong></div>
      </div>
      <p class="analysis-note" v-if="metric === 'amount'">金额按用量日期匹配费率，是参考估算而非账单。占比以已核算金额为分母；尚有 {{ compactNumber(data.totals.unratedTokens) }} Token 金额未知，不当作零。</p>
      <details class="inner-card"><summary>Token 分项与包含关系</summary><TokenDetails :totals="data.totals" :runtime="snapshot.runtime" /></details>
      <p v-if="!data.totals.tokens" class="analysis-state" role="status">所选范围暂无本机用量，调整日期、模型或项目后重试。</p>
      <article class="inner-card analysis-trend">
        <div class="analysis-heading"><h3>每日用量趋势</h3><span>选择某天查看会话</span></div>
        <div class="daily-scroll">
          <div class="daily-bars" :style="{ minWidth: `${Math.max(0, data.days.length * 14)}px` }">
            <button v-for="day in data.days" :key="day.date" type="button" :aria-label="`${day.date}，${value(day.totals)}${metric === 'tokens' ? ' Token' : ''}，筛选这一天`" :title="`${day.date} · ${value(day.totals)}`" @click="state.selectDate(day.date)">
              <span :style="{ height: `${magnitude(day.totals) / maximum * 100}%` }" /><i v-if="metric === 'amount' && day.totals.creditsUsed == null && day.totals.tokens" aria-hidden="true">?</i>
            </button>
          </div>
        </div>
        <div class="date-axis"><span>{{ data.days[0]?.date ?? '暂无日期' }}</span><span>{{ data.days.at(-1)?.date }}</span></div>
        <p class="analysis-note">按本机日期统计，仅显示有记录的日期。长时间范围可横向滚动；金额未知的日期以 ? 标记。</p>
      </article>
      <div class="analysis-ranks">
        <article v-for="dimension in (['models', 'features', 'projects'] as const)" :key="dimension" class="inner-card rank-card">
          <h3>{{ dimension === 'models' ? '模型分布' : dimension === 'features' ? '功能分布' : '项目排行' }}</h3>
          <div v-for="row in sorted(data[dimension])" :key="row.id" class="rank-row">
            <button v-if="dimension !== 'features'" type="button" @click="dimension === 'models' ? state.model.value = row.id : state.project.value = row.id" :title="`筛选 ${row.label}`">{{ row.label }}</button>
            <span v-else>{{ featureLabel(row.id) }}</span><strong>{{ value(row.totals) }}</strong><small>{{ share(row.totals) }}</small>
            <i><b :style="{ width: share(row.totals).endsWith('%') ? share(row.totals) : '0%' }" /></i>
          </div>
          <p v-if="!data[dimension].length" class="analysis-note">暂无数据</p>
        </article>
      </div>
      <section class="inner-card session-list" aria-label="会话用量明细">
        <div class="analysis-heading"><h3>会话用量明细</h3><span>汇总已去重的主会话和子代理贡献</span></div>
        <details v-for="group in data.sessions" :key="group.id" class="session-group">
          <summary><span>{{ group.title || group.id.slice(0, 16) }}<small>{{ group.from }}{{ group.to !== group.from ? ` 至 ${group.to}` : '' }} · {{ group.members.length }} 个来源会话</small></span><strong>{{ value(group.totals) }}</strong></summary>
          <article v-for="member in group.members" :key="member.id" class="session-member">
            <h4>{{ member.title || member.id.slice(0, 16) }} <small>{{ member.parentSessionId ? '子代理' : '会话自身' }}</small></h4>
            <p class="session-identity">ID：{{ member.id }}<br />项目：{{ member.project || '项目未知' }}<br />日期：{{ member.from }} 至 {{ member.to }}<br />来源：{{ member.sources.map(sourceLabel).join('、') }}</p>
            <p><strong>{{ value(member.totals) }}</strong></p>
            <TokenDetails :totals="member.totals" :runtime="snapshot.runtime" />
            <div class="contribution-table"><table><caption class="sr-only">每日模型贡献及适用费率</caption><thead><tr><th scope="col">日期 / 模型</th><th scope="col">Token</th><th scope="col">API 等效金额</th><th scope="col">适用费率</th></tr></thead>
              <tbody><tr v-for="(row, index) in member.contributions" :key="index"><th scope="row">{{ row.date }}<br />{{ row.model === 'unknown' ? '模型未知' : row.model }}<small>{{ featureLabel(row.feature) }} · {{ sourceLabel(row.source) }}</small></th><td>{{ compactNumber(row.tokens) }}</td><td>{{ amount(row.creditsUsed) }}<small v-if="row.explanation">{{ row.explanation }}</small></td><td><template v-if="row.rate"><span>{{ row.rate.catalogVersion || '自定义' }} · {{ row.rate.effectiveFrom || '基础费率' }} · {{ row.rate.source || '费率目录' }}</span><small>每百万 Token：输入 {{ amount(row.rate.inputCreditsPerMillion) }} / 缓存读取 {{ amount(row.rate.cachedInputCreditsPerMillion) }} / 输出 {{ amount(row.rate.outputCreditsPerMillion) }}</small></template><template v-else>无可用费率</template></td></tr></tbody></table></div>
          </article>
        </details>
        <p v-if="!data.sessions.length" class="analysis-note">此范围没有可归属的会话明细。</p>
        <div class="pagination"><button type="button" :disabled="data.page <= 1" @click="state.page.value--">上一页</button><span>第 {{ data.page }} / {{ Math.max(1, Math.ceil(data.sessionCount / data.pageSize)) }} 页 · {{ data.sessionCount }} 组</span><button type="button" :disabled="data.page * data.pageSize >= data.sessionCount" @click="state.page.value++">下一页</button></div>
      </section>
      <details v-if="data.unattributed.length" class="inner-card unknown-history"><summary>未归属历史用量 · {{ compactNumber(data.totals.unattributedTokens) }} Token</summary><p class="analysis-note">下列用量只有日汇总，无法还原真实会话；已确认的明细已保留分类，此处仅显示剩余差额。</p><div v-for="(row, index) in data.unattributed" :key="index" class="legacy-row"><strong>{{ row.date }} · {{ compactNumber(row.tokens) }} Token</strong><span>{{ row.explanation || '缺少可确认会话归属的原始日志。' }}</span></div></details>
      <details v-if="data.legacyEstimates.length" class="inner-card"><summary>旧版金额估值（单独保留）</summary><p class="analysis-note">这些是旧日汇总采集的完整估值，与上方已重算明细可能重叠，不加入或重复相加到 API 等效金额。</p><div v-for="row in data.legacyEstimates" :key="row.date" class="legacy-row"><strong>{{ row.date }} · {{ amount(row.creditsUsed) }}</strong><span>{{ row.explanation }}</span></div></details>
      <details v-if="missingRates.length" class="inner-card"><summary>所选范围缺少适用费率</summary><p class="analysis-note">在设置中为这些模型补充相应日期的费率：{{ missingRates.join('；') }}。未知历史差额不触发补费率提示。</p></details>
      <details class="inner-card secondary-analysis"><summary>Skill 与工具调用（累计辅助分析）</summary><p class="analysis-note">这些调用次数缺少可靠日期归属，展示当前工具在{{ store.settings?.defaultWorkspace ? `设置工作区 ${store.settings.defaultWorkspace}` : '本机全部项目' }}内的累计统计，不随上方日期、模型和项目筛选变化，也不计入用量总量。</p><SkillUsageView :snapshot="snapshot" /><h3>工具调用</h3><div class="tool-counts"><span v-for="tool in snapshot.tools" :key="tool.id">{{ tool.name }} <strong>{{ tool.count }}</strong></span><p v-if="!snapshot.tools.length">暂无可确认的工具调用记录</p></div></details>
      <details v-if="data.diagnostics.length" class="inner-card"><summary>本次统计来源说明</summary><p v-for="message in data.diagnostics" :key="message" class="analysis-note">{{ message }}</p></details>
    </template>
  </div>
</template>
<style scoped>
.analysis-view { display: grid; gap: 16px; }
details.inner-card { min-height: 0; }
.analysis-state { padding: 30px 10px; text-align: center; color: var(--text-secondary); }
.analysis-summary { display: flex; flex-wrap: wrap; gap: 22px 42px; padding: 12px 0; }
.analysis-summary span { display: block; color: var(--text-secondary); font-size: 12px; margin-bottom: 7px; }
.analysis-summary strong { font-size: 26px; color: var(--text-primary); }.analysis-summary small { font-size: 12px; }
.analysis-note { color: var(--text-secondary); font-size: 11px; line-height: 1.8; margin: 8px 0; }
h3 { margin: 0 0 12px; font-size: 14px; } h4 { margin: 0; font-size: 13px; }
.analysis-heading { display: flex; flex-wrap: wrap; gap: 8px 20px; align-items: baseline; justify-content: space-between; }.analysis-heading span { color: var(--text-secondary); font-size: 11px; }
.daily-scroll { overflow-x: auto; }.daily-bars { display: flex; align-items: stretch; gap: 3px; height: 150px; border-bottom: 1px solid var(--stroke); margin-top: 20px; }
.daily-bars button { display: flex; position: relative; align-items: end; justify-content: center; flex: 1; min-width: 10px; padding: 0; border: 0; background: transparent; cursor: pointer; border-radius: 4px; }.daily-bars button:hover { background: var(--surface-subtle); }.daily-bars span { min-height: 1px; width: 70%; max-width: 40px; background: var(--blue); border-radius: 3px 3px 0 0; }.daily-bars i { position: absolute; bottom: 5px; color: var(--text-secondary); }
.date-axis { display: flex; justify-content: space-between; color: var(--text-secondary); font-size: 10px; margin-top: 8px; }
.analysis-ranks { display: grid; grid-template-columns: repeat(3,minmax(0,1fr)); gap: 14px; }.rank-card { max-height: 400px; overflow-y: auto; }.rank-row { display: grid; grid-template-columns: minmax(0,1fr) auto; gap: 5px 8px; padding: 8px 0; font-size: 11px; }.rank-row button,.rank-row > span { overflow-wrap: anywhere; text-align: left; color: var(--text-primary); }.rank-row button { padding: 0; border: 0; background: transparent; text-decoration: underline; cursor: pointer; font: inherit; }.rank-row strong { font-weight: 500; }.rank-row small { color: var(--text-secondary); }.rank-row i { grid-column: 1/-1; height: 3px; background: var(--surface-subtle); }.rank-row b { display: block; height: 100%; background: var(--violet); }
summary { cursor: pointer; color: var(--text-primary); font-size: 12px; } .session-group { border-top: 1px solid var(--stroke); padding: 13px 0; }.session-group summary { display: flex; gap: 15px; justify-content: space-between; align-items: center; }.session-group summary::before { content: '▸'; }.session-group[open] > summary::before { content: '▾'; }.session-group summary > span { flex: 1; min-width: 0; overflow-wrap: anywhere; }.session-group small { display: block; font-size: 10px; color: var(--text-secondary); margin-top: 6px; }.session-member { background: var(--surface-subtle); padding: 15px; margin: 12px 0 0; border-radius: 10px; }.session-identity { overflow-wrap: anywhere; font-size: 11px; color: var(--text-secondary); line-height: 1.8; }.contribution-table { overflow-x: auto; }table { width: 100%; border-collapse: collapse; font-size: 11px; }th,td { text-align: left; padding: 9px 8px; border-bottom: 1px solid var(--stroke); color: var(--text-secondary); font-weight: 400; }thead th { white-space: nowrap; }tbody th { color: var(--text-primary); }
.pagination { display: flex; gap: 15px; align-items: center; justify-content: center; margin-top: 14px; font-size: 11px; color: var(--text-secondary); }.pagination button,.analysis-state button { color: var(--text-primary); background: var(--surface-strong); border: 1px solid var(--stroke); border-radius: 7px; padding: 7px 10px; cursor: pointer; }button:disabled { opacity: .4; cursor: default; }.legacy-row { display: grid; gap: 6px; padding: 10px 0; font-size: 11px; border-top: 1px solid var(--stroke); }.legacy-row span { color: var(--text-secondary); }.tool-counts { display: flex; gap: 12px; flex-wrap: wrap; font-size: 12px; color: var(--text-secondary); }
@container (max-width: 760px) { .analysis-ranks { grid-template-columns: 1fr; } }
</style>
