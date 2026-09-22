<script setup lang="ts">
import { computed, onScopeDispose, ref, watch } from 'vue'
import QuotaRing from '../components/QuotaRing.vue'
import { useDashboardStore } from '../stores/dashboard'
import { analysisValue } from '../analysisFormatting'
import { host } from '../host'
import type { UsageAnalysisState } from '../composables/useUsageAnalysis'
import type { AgentRuntime, UsageAnalysisResult } from '../types'
const props = defineProps<{ state: UsageAnalysisState }>()
const store = useDashboardStore()
const data = ref<Partial<Record<AgentRuntime, UsageAnalysisResult>>>({})
const failures = ref<Partial<Record<AgentRuntime, string>>>({})
const loading = ref(false)
let generation = 0
let disposed = false
const runtimes: AgentRuntime[] = ['codex', 'claudeCode']
const request = computed(() => ({ ...props.state.range.value, model: props.state.model.value || null, project: props.state.project.value || null }))
async function refresh() {
  const current = ++generation
  data.value = {}; failures.value = {}; loading.value = true
  void store.loadCombined(true)
  await Promise.all(runtimes.map(async runtime => {
    try {
      const result = await host.request<UsageAnalysisResult>('usage.query', { ...request.value, runtime, page: 1, pageSize: 1 })
      if (current === generation && !disposed) data.value[runtime] = result
    } catch (reason) { if (current === generation && !disposed) failures.value[runtime] = reason instanceof Error ? reason.message : String(reason) }
  }))
  if (current === generation && !disposed) loading.value = false
}
watch([request, () => store.snapshot?.refreshedAt], refresh, { immediate: true })
onScopeDispose(() => { disposed = true; generation++ })
const amount = (runtime: AgentRuntime) => data.value[runtime] ? analysisValue(data.value[runtime]!.totals, 'amount', store.settings?.amountPerThousandCredits ?? 40) : '—'
const tokens = (runtime: AgentRuntime) => data.value[runtime] ? analysisValue(data.value[runtime]!.totals, 'tokens', 1) : '—'
</script>
<template>
  <section class="combined-view">
    <p class="combined-note">两种工具使用相同日期、模型和项目筛选，分别统计本机用量。账户额度独立，不能相加或平均。</p>
    <p v-if="loading" role="status">正在读取两种工具…</p>
    <div class="combined-columns">
      <article v-for="runtime in runtimes" :key="runtime" class="combined-runtime-column glass-card">
        <h2>{{ runtime === 'codex' ? 'Codex' : 'Claude Code' }}</h2>
        <p v-if="failures[runtime]" role="alert">{{ failures[runtime] }} <button type="button" @click="refresh">重试</button></p>
        <div class="combined-quota-pair"><QuotaRing label="5 小时" :quota="store.combined?.[runtime]?.snapshot.primaryQuota" color="blue" /><QuotaRing label="7 天" :quota="store.combined?.[runtime]?.snapshot.secondaryQuota" color="violet" /></div>
        <dl class="combined-runtime-facts"><div><dt>所选范围本机 Token</dt><dd>{{ tokens(runtime) }}</dd></div><div><dt>API 等效金额</dt><dd>{{ amount(runtime) }}</dd></div><div><dt>会话组</dt><dd>{{ data[runtime]?.sessionCount ?? '—' }}</dd></div></dl>
        <p class="combined-note">API 等效金额仅作参考，≥ 表示仍有未核算用量。来源缺失的字段保持未知。</p>
      </article>
    </div>
  </section>
</template>
<style scoped>
.combined-columns { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 16px; }.combined-runtime-column { padding: 18px; }h2 { font-size: 16px; margin: 0 0 14px; }.combined-quota-pair { display: flex; justify-content: center; gap: 10px; flex-wrap: wrap; }.combined-runtime-facts { display: flex; flex-wrap: wrap; gap: 16px 24px; margin-top: 18px; }dt,.combined-note { color: var(--text-secondary); font-size: 11px; line-height: 1.8; }dd { margin: 5px 0; font-size: 20px; color: var(--text-primary); }button { color: var(--text-primary); background: var(--surface-subtle); border: 1px solid var(--stroke); padding: 6px; }
@container (max-width: 650px) { .combined-columns { grid-template-columns: 1fr; } }
</style>
