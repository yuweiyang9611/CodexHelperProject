<script setup lang="ts">
import { compactNumber } from '../format'
import type { AgentRuntime, UsageAnalysisTotals } from '../types'
const props = defineProps<{ totals: UsageAnalysisTotals, runtime: AgentRuntime }>()
const field = (name: 'inputTokens' | 'cachedInputTokens' | 'outputTokens' | 'reasoningOutputTokens') => props.totals.availableBreakdownFields?.includes(name) ? compactNumber(props.totals.breakdown[name]) : '未知'
</script>
<template>
  <div class="token-details">
    <dl v-if="totals.breakdownTokens > 0">
      <div><dt>输入（含缓存）</dt><dd>{{ field('inputTokens') }}</dd></div>
      <div><dt>其中缓存读取</dt><dd>{{ field('cachedInputTokens') }}</dd></div>
      <div v-if="runtime === 'claudeCode'"><dt>其中缓存写入</dt><dd>{{ totals.availableBreakdownFields?.includes('cacheWrite5mTokens') && totals.availableBreakdownFields?.includes('cacheWrite1hTokens') ? compactNumber(totals.breakdown.cacheWrite5mTokens + totals.breakdown.cacheWrite1hTokens) : '未知' }}</dd></div>
      <div><dt>输出</dt><dd>{{ field('outputTokens') }}</dd></div>
      <div v-if="runtime === 'codex'"><dt>其中推理输出</dt><dd>{{ field('reasoningOutputTokens') }}</dd></div>
    </dl>
    <p v-else>Token 分项不可用：仅保留总量，无法还原输入、缓存和输出。</p>
    <p v-if="totals.breakdownTokens > 0">分项覆盖 {{ compactNumber(totals.breakdownTokens) }} / {{ compactNumber(totals.tokens) }} Token。缓存已包含在输入中{{ runtime === 'codex' ? '，推理已包含在输出中；缓存写入细分不可用' : '；推理输出细分不可用' }}，不重复相加。</p>
  </div>
</template>
<style scoped>
dl { display: flex; flex-wrap: wrap; gap: 12px 22px; margin: 12px 0; } dt,p { color: var(--text-secondary); font-size: 11px; } dd { margin: 4px 0 0; font-size: 14px; color: var(--text-primary); } p { line-height: 1.7; }
</style>
