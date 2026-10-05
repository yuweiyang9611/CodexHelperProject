<script setup lang="ts">
import { computed, onMounted } from 'vue'
import QuotaRing from '../components/QuotaRing.vue'
import { compactNumber, currencyAmount, exhaustionTime, qualityLabel, relativeTime } from '../format'
import { RUNTIME_LABELS, useCombinedRuntime } from '../composables/useCombinedRuntime'
import { useDashboardStore } from '../stores/dashboard'
import type { AgentRuntime } from '../types'

const store = useDashboardStore()
const combined = computed(() => store.combined)
const summary = useCombinedRuntime(combined)

// Lazy: the two reads are expensive and most sessions never open this tab. The data
// lives in the store, so switching away and back does not pay for them again.
onMounted(() => void store.loadCombined())

const amountPerThousandCredits = computed(() => store.settings?.amountPerThousandCredits ?? 40)

function money(credits: number | null | undefined) {
  if (credits == null) return '--'
  return currencyAmount(credits / 1_000 * amountPerThousandCredits.value)
}

const PERIODS = [
  { key: 'today' as const, label: '今日' },
  { key: 'sevenDays' as const, label: '近 7 日' },
  { key: 'month' as const, label: '本月' },
]

const monthly = computed(() => summary.value?.periods.month ?? null)

const monthlyAmount = computed(() =>
  monthly.value ? monthly.value.creditsUsed / 1_000 * amountPerThousandCredits.value : null)

/**
 * Payback needs both sides of the ratio to describe the same set of runtimes. The
 * subscription total is complete only when every runtime in play resolved a price,
 * and the numerator only counts runtimes that were actually read — so a runtime that
 * contributes to one and not the other makes the ratio meaningless rather than
 * merely approximate.
 */
const payback = computed(() => {
  const current = summary.value
  const amount = monthlyAmount.value
  if (!current || amount == null || !current.subscription.isComplete) return null
  const subscription = current.subscription.amount ?? 0
  if (subscription <= 0) return null
  const contributors = [...current.periods.month.contributors].sort().join(',')
  const priced = current.subscription.perRuntime.map(row => row.runtime).sort().join(',')
  if (contributors !== priced) return null
  return { multiple: amount / subscription, net: amount - subscription, subscription }
})

const paybackSuppressedReason = computed(() => {
  const current = summary.value
  if (!current || payback.value) return null
  if (!current.subscription.isComplete) {
    const names = current.subscription.unknownRuntimes.map(runtime => RUNTIME_LABELS[runtime]).join('、')
    return `${names || '部分运行时'}的订阅月费无法推算，合计与回本倍数暂不显示`
  }
  return '两侧统计口径不一致（有运行时只贡献了其中一边），回本倍数暂不显示'
})

function runtimeLabel(runtime: AgentRuntime) {
  return RUNTIME_LABELS[runtime]
}
</script>

<template>
  <!-- Single root: App.vue's id / role="tabpanel" / aria-labelledby / tabindex fall
       through onto this element, as they do for every other panel view. -->
  <section class="combined-view">
    <div v-if="store.isLoadingCombined && !summary" class="combined-state" role="status" aria-live="polite">
      正在同时读取 Codex 与 Claude Code 的本机数据…
    </div>

    <div v-else-if="store.combinedError && !summary" class="combined-state combined-state-error" role="alert">
      <strong>双运行时数据读取失败</strong>
      <span>{{ store.combinedError }}</span>
      <button type="button" @click="store.loadCombined(true)">重试</button>
    </div>

    <template v-else-if="summary">
      <div v-if="summary.failedRuntimes.length" class="combined-banner" role="alert">
        {{ summary.failedRuntimes.map(runtimeLabel).join('、') }} 本次未能读取，下面的合计不包含它的用量。
      </div>

      <div class="combined-columns">
        <article v-for="entry in summary.entries" :key="entry.runtime" class="combined-runtime-column glass-card">
          <header>
            <strong>{{ runtimeLabel(entry.runtime) }}</strong>
            <span v-if="entry.contribution === 'failed'" class="combined-tag combined-tag-error">读取失败</span>
            <span v-else-if="entry.contribution === 'absent'" class="combined-tag">未发现本机用量</span>
            <span v-else class="combined-tag">{{ entry.snapshot.account?.planType?.toUpperCase() ?? '本机' }}</span>
          </header>

          <div class="combined-quota-pair">
            <QuotaRing
              label="5 小时"
              :quota="entry.snapshot.primaryQuota"
              color="blue"
              :forecast="entry.snapshot.primaryForecast"
            />
            <QuotaRing
              label="7 天"
              :quota="entry.snapshot.secondaryQuota"
              color="violet"
              :forecast="entry.snapshot.secondaryForecast"
            />
          </div>

          <dl class="combined-runtime-facts">
            <div><dt>本月本机原始 token</dt><dd>{{ compactNumber(entry.snapshot.tokens.month.tokens) }}</dd></div>
            <div><dt>本机原始 token API 估算</dt><dd>{{ money(entry.snapshot.tokens.month.creditsUsed) }}</dd></div>
            <div><dt>累计本机原始 token</dt><dd>{{ compactNumber(entry.snapshot.tokens.lifetime.tokens) }}</dd></div>
            <div>
              <dt>订阅月费</dt>
              <dd>{{ entry.snapshot.account?.suggestedMonthlySubscriptionAmount != null
                ? currencyAmount(entry.snapshot.account.suggestedMonthlySubscriptionAmount)
                : '无法推算' }}</dd>
            </div>
            <div><dt>统计精度</dt><dd>{{ qualityLabel(entry.snapshot.tokens.month.quality) }}</dd></div>
          </dl>
        </article>
      </div>

      <article class="combined-card glass-card">
        <h2>合计本机原始用量</h2>
        <div class="combined-metrics">
          <div v-for="period in PERIODS" :key="period.key" class="combined-metric">
            <span class="combined-metric-label">{{ period.label }}</span>
            <strong>{{ compactNumber(summary.periods[period.key].tokens) }}</strong>
            <span class="combined-metric-sub">
              {{ summary.periods[period.key].creditsAreLowerBound ? '≥ ' : '' }}{{ money(summary.periods[period.key].creditsUsed) }}
              · {{ qualityLabel(summary.periods[period.key].quality) }}
            </span>
          </div>
        </div>
        <p class="combined-note">
          合计仅包含本次成功读取的本机日志；这里不混入 Codex 官方账户活动。
          累计 token 不做合并，因为两个运行时保存日志的范围可能不同，相加容易误导。
        </p>
      </article>

      <article class="combined-card glass-card">
        <h2>合计成本</h2>
        <div class="combined-metrics">
          <div class="combined-metric">
            <span class="combined-metric-label">本机原始 Token 按 API 价估算</span>
            <strong>{{ monthly?.creditsAreLowerBound ? '≥ ' : '' }}{{ currencyAmount(monthlyAmount) }}</strong>
            <span class="combined-metric-sub">基于本机原始 Token，按 API 列表价折算，不是账单金额</span>
          </div>
          <div class="combined-metric">
            <span class="combined-metric-label">订阅月费合计</span>
            <strong>{{ summary.subscription.isComplete ? currencyAmount(summary.subscription.amount) : '无法推算' }}</strong>
            <span class="combined-metric-sub">
              {{ summary.subscription.perRuntime
                .map(row => `${runtimeLabel(row.runtime)} ${row.amount != null ? currencyAmount(row.amount) : '未知'}`)
                .join(' · ') }}
            </span>
          </div>
          <div v-if="payback" class="combined-metric">
            <span class="combined-metric-label">回本倍数</span>
            <strong>{{ payback.multiple.toFixed(1) }}×</strong>
            <span class="combined-metric-sub">净等价 {{ currencyAmount(payback.net, 'US$', false, true) }}</span>
          </div>
        </div>
        <p v-if="paybackSuppressedReason" class="combined-note">{{ paybackSuppressedReason }}</p>
      </article>

      <article class="combined-card glass-card">
        <h2>额度最先耗尽</h2>
        <template v-if="summary.earliest">
          <p class="combined-earliest">
            <strong>{{ runtimeLabel(summary.earliest.runtime) }} · {{ summary.earliest.windowLabel }}</strong>
            <span>{{ exhaustionTime(summary.earliest.forecast.exhaustsAt) }}</span>
          </p>
          <p class="combined-note">
            {{ summary.earliest.total }} 个额度窗口中有 {{ summary.earliest.predictable }} 个可预测，
            其余窗口样本不足或会在耗尽前重置。四个窗口是四份独立额度，不做相加或平均。
          </p>
        </template>
        <p v-else class="combined-note">
          暂无窗口会在重置前耗尽，或样本还不足以预测。四个窗口是四份独立额度，不做相加或平均。
        </p>
      </article>

      <article class="combined-card glass-card">
        <h2>项目合并排行</h2>
        <table class="combined-table">
          <thead>
            <tr><th scope="col">项目</th><th scope="col">运行时</th><th scope="col">Token</th><th scope="col">成本</th><th scope="col">最近活动</th></tr>
          </thead>
          <tbody>
            <tr v-for="project in summary.projects.slice(0, 12)" :key="project.runtimes.join('-') + project.id">
              <th scope="row">{{ project.name }}<small v-if="project.branch"> · {{ project.branch }}</small></th>
              <td>{{ project.runtimes.map(runtimeLabel).join(' + ') }}</td>
              <td>{{ compactNumber(project.tokens) }}</td>
              <td>
                {{ project.creditsUsed != null ? money(project.creditsUsed) : '成本不可得' }}
                <small v-if="project.costIsEstimated && project.creditsUsed != null">（估算）</small>
              </td>
              <td>{{ relativeTime(project.lastActiveAt) }}</td>
            </tr>
          </tbody>
        </table>
        <p class="combined-note">
          按项目绝对路径合并，路径大小写与分隔符差异已归一。各运行时的排行榜本身已截断，
          因此这里是两份榜单的合并结果，不等于全量项目，也不等于累计 token。
          Codex 的成本按 token 占比分摊，标记为“估算”，其绝对值偏高。
        </p>
      </article>

      <details class="combined-card glass-card combined-omissions">
        <summary>本视图不合并的内容</summary>
        <ul>
          <li>额度百分比：两个厂商的窗口是彼此独立的额度，分母不同、重置时钟不同，相加或求平均不代表任何东西。</li>
          <li>累计 token：Codex 以云端账户累计兜底（可能含其他机器），Claude 只统计本机现存 transcript（已轮转的日期不计）。</li>
          <li>Token 构成条：Codex 不记录缓存写入，Claude 不记录推理输出，合并后的分段会结构性地只来自一侧。</li>
          <li>费率覆盖率：两侧口径不同，单一数字会让完全未定价的一侧被另一侧掩盖。覆盖率按运行时分别显示。</li>
          <li>工具榜：同一件事 Codex 记为 exec_command、Claude 记为 Bash，按名称合并会把终端操作拆成两行。</li>
          <li>任务、目标、索引状态：Claude 侧结构性为空，补零会把 Codex 独有的活动说成两个运行时的合计。</li>
          <li>日用量曲线：Codex 的序列可能来自 SQLite 兜底，会把一个会话的全部 token 压在一天上。</li>
        </ul>
      </details>

      <p class="combined-footnote">
        数据刷新于 {{ relativeTime(summary.refreshedAt) }}（取两侧较早者）。
        <button type="button" class="combined-refresh" @click="store.loadCombined(true)" :disabled="store.isLoadingCombined">
          {{ store.isLoadingCombined ? '读取中…' : '重新读取' }}
        </button>
      </p>
    </template>
  </section>
</template>

<style scoped>
.combined-view { display: flex; flex-direction: column; gap: 14px; }
.combined-state { display: flex; flex-direction: column; align-items: center; gap: 8px; padding: 40px 16px; color: var(--text-secondary); }
.combined-state-error strong { color: var(--text-primary); }
.combined-state button, .combined-refresh { padding: 4px 12px; border: 1px solid rgba(255,255,255,.16); border-radius: 7px; background: transparent; color: var(--text-secondary); cursor: pointer; }
.combined-banner { padding: 9px 13px; border-radius: 9px; background: rgba(255,150,90,.12); color: #ffb27a; font-size: 12px; font-weight: 600; }
.combined-columns { display: grid; grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); gap: 14px; }
.combined-runtime-column { padding: 16px; }
.combined-runtime-column header { display: flex; align-items: center; gap: 9px; margin-bottom: 10px; }
.combined-runtime-column header strong { color: var(--text-primary); font-size: 14px; }
.combined-tag { padding: 2px 8px; border-radius: 999px; background: rgba(255,255,255,.07); color: var(--text-tertiary); font-size: 11px; }
.combined-tag-error { background: rgba(255,110,110,.14); color: #ff9a9a; }
.combined-quota-pair { display: flex; justify-content: center; gap: 6px; flex-wrap: wrap; }
.combined-runtime-facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(118px, 1fr)); gap: 9px; margin: 12px 0 0; }
.combined-runtime-facts div { display: flex; flex-direction: column; gap: 3px; }
.combined-runtime-facts dt { color: var(--text-tertiary); font-size: 11px; }
.combined-runtime-facts dd { margin: 0; color: var(--text-primary); font-size: 14px; font-variant-numeric: tabular-nums; }
.combined-card { padding: 16px; }
.combined-card h2 { margin: 0 0 11px; color: var(--text-secondary); font-size: 12px; font-weight: 700; letter-spacing: .04em; text-transform: uppercase; }
.combined-metrics { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 13px; }
.combined-metric { display: flex; flex-direction: column; gap: 4px; }
.combined-metric-label { color: var(--text-tertiary); font-size: 11px; }
.combined-metric strong { color: var(--text-primary); font-size: 22px; font-variant-numeric: tabular-nums; line-height: 1.1; }
.combined-metric-sub { color: var(--text-secondary); font-size: 11px; }
.combined-note { margin: 11px 0 0; color: var(--text-tertiary); font-size: 11px; line-height: 1.65; }
.combined-earliest { display: flex; align-items: baseline; gap: 10px; margin: 0; }
.combined-earliest strong { color: var(--text-primary); font-size: 16px; }
.combined-earliest span { color: #ffb27a; font-size: 13px; font-weight: 600; }
.combined-table { width: 100%; border-collapse: collapse; font-size: 12px; }
.combined-table th, .combined-table td { padding: 7px 9px; text-align: left; border-bottom: 1px solid rgba(255,255,255,.05); }
.combined-table thead th { color: var(--text-tertiary); font-size: 11px; font-weight: 600; }
.combined-table tbody th { color: var(--text-primary); font-weight: 600; }
.combined-table tbody td { color: var(--text-secondary); font-variant-numeric: tabular-nums; }
.combined-table small { color: var(--text-tertiary); font-weight: 400; }
.combined-omissions summary { color: var(--text-secondary); font-size: 12px; font-weight: 600; cursor: pointer; }
.combined-omissions ul { margin: 11px 0 0; padding-left: 18px; color: var(--text-tertiary); font-size: 11px; line-height: 1.75; }
.combined-footnote { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin: 0; color: var(--text-tertiary); font-size: 11px; }
</style>
