<script setup lang="ts">
import { computed } from 'vue'
import { isDesktopQuota } from '../quotaPresentation'
import type { DashboardSnapshot } from '../types'

const props = defineProps<{ snapshot: DashboardSnapshot }>()
const isClaude = computed(() => props.snapshot.runtime === 'claudeCode')
const hasDesktopSample = computed(() => isDesktopQuota(props.snapshot.primaryQuota) || isDesktopQuota(props.snapshot.secondaryQuota))
const missingSubscription = computed(() => props.snapshot.account?.suggestedMonthlySubscriptionAmount == null)
const quotaMessage = computed(() => {
  if (hasDesktopSample.value) return '本地 Claude Desktop 历史仅提供额度百分比，不提供重置时间或套餐；刷新时忽略超过 30 分钟的采样。'
  if (props.snapshot.primaryQuota || props.snapshot.secondaryQuota) return ''
  // Diagnostics can contain local paths. Match a known reason but never copy the
  // raw diagnostic into the account card.
  if (props.snapshot.diagnostics.some(line => line.includes('Claude Desktop 额度：历史采样已过期'))) {
    return 'Desktop 历史采样已过期（超过 30 分钟），请在 Claude Desktop 中刷新用量后重新读取。'
  }
  return '当前没有可用的 Claude 额度采样。Desktop 本地历史只含百分比，超过 30 分钟的采样会被忽略；终端 Claude Code 可通过 statusLine 接入。'
})
</script>

<template>
  <aside v-if="isClaude && (quotaMessage || missingSubscription)" class="runtime-data-notice" role="note">
    <p v-if="quotaMessage">{{ quotaMessage }}</p>
    <p v-if="missingSubscription">订阅档位未识别，月费可在“设置与诊断”填写；自动估算或手动金额均不代表实际账单。</p>
  </aside>
</template>

<style scoped>
.runtime-data-notice { margin-top: 8px; color: var(--text-tertiary); font-size: 11px; line-height: 1.65; }
.runtime-data-notice p { margin: 4px 0 0; }
</style>
