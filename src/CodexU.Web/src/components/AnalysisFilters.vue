<script setup lang="ts">
import { computed } from 'vue'
import type { UsageAnalysisState, UsagePeriod } from '../composables/useUsageAnalysis'
const props = defineProps<{ state: UsageAnalysisState }>()
const periods: { id: UsagePeriod, label: string }[] = [
  { id: 'today', label: '今日' }, { id: '7', label: '近 7 天' }, { id: '30', label: '近 30 天' },
  { id: 'month', label: '本月' }, { id: 'all', label: '全部历史' }, { id: 'custom', label: '自定义' },
]
const models = computed(() => props.state.options.value.models)
const projects = computed(() => props.state.options.value.projects)
</script>
<template>
  <section class="analysis-filters" aria-label="本机用量筛选">
    <div class="period-picker" role="group" aria-label="统计时间范围">
      <button v-for="item in periods" :key="item.id" type="button" :aria-pressed="state.period.value === item.id" @click="state.period.value = item.id">{{ item.label }}</button>
    </div>
    <div class="filter-fields">
      <template v-if="state.period.value === 'custom'">
        <label>开始日期<input v-model="state.customFrom.value" type="date" /></label>
        <label>结束日期<input v-model="state.customTo.value" type="date" /></label>
      </template>
      <label>模型<select v-model="state.model.value"><option value="">全部模型</option><option v-for="model in models" :key="model" :value="model">{{ model === 'unknown' ? '模型未知' : model }}</option></select></label>
      <label>项目<select v-model="state.project.value"><option value="">全部项目</option><option v-for="project in projects" :key="project.id" :value="project.id">{{ project.label }}</option></select></label>
      <label>指标<select v-model="state.metric.value"><option value="tokens">原始 Token</option><option value="amount">API 等效金额</option></select></label>
    </div>
    <p>本机可读取及已留存的用量 · {{ state.range.value.from ?? '最早留存日期' }} 至 {{ state.range.value.to ?? '最新留存日期' }}。账户额度独立显示。</p>
  </section>
</template>
<style scoped>
.analysis-filters { padding: 17px 0; border-bottom: 1px solid var(--stroke); margin-bottom: 18px; }
.period-picker,.filter-fields { display: flex; flex-wrap: wrap; gap: 8px; }
.filter-fields { margin-top: 12px; gap: 12px; }
button,select,input { font: inherit; color: var(--text-primary); border: 1px solid var(--stroke); background: var(--surface-strong); border-radius: 8px; padding: 8px 10px; min-height: 34px; }
button { cursor: pointer; font-size: 12px; }
button[aria-pressed=true] { border-color: var(--blue); background: #3f55cf; color: white; }
label { display: grid; gap: 5px; min-width: 130px; max-width: 100%; color: var(--text-secondary); font-size: 11px; }
select { max-width: 260px; min-width: 0; }
p { font-size: 11px; color: var(--text-secondary); line-height: 1.7; margin-bottom: 0; }
</style>
