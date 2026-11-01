<script setup lang="ts">
import { computed } from 'vue'
import { isDesktopHistoryQuota, isDesktopQueryQuota } from '../quotaPresentation'
import type { DashboardSnapshot } from '../types'

const props = defineProps<{ snapshot: DashboardSnapshot }>()
const isClaude = computed(() => props.snapshot.runtime === 'claudeCode')
const hasDesktopSample = computed(() => isDesktopHistoryQuota(props.snapshot.primaryQuota) || isDesktopHistoryQuota(props.snapshot.secondaryQuota))
const hasDesktopQuery = computed(() => isDesktopQueryQuota(props.snapshot.primaryQuota) || isDesktopQueryQuota(props.snapshot.secondaryQuota))
const missingSubscription = computed(() => props.snapshot.account?.suggestedMonthlySubscriptionAmount == null)
// Only classify fixed reader reasons. Do not display diagnostic suffixes, which
// can include paths or identifiers supplied by another source.
const unavailableQueryReasons = [
  '未找到匹配的原生工具结果',
  '指定会话文件超过 32 MiB 安全上限',
  '指定会话文件正在更新',
  '会话包含超过 4 MiB 的行',
  '会话尾行尚未写完',
  '会话记录格式无效',
  '会话身份或采样字段存在歧义',
  '会话身份与指定文件不一致',
  '会话消息字段存在歧义',
  '会话消息角色无效',
  '原生工具调用标识无效、重复或待配对数量超限',
  '最新原生结果缺少带时区的有效采样时间',
  '最新原生工具结果不可用',
  '最新原生结果采样时间在未来',
  '最新结果没有仍有效的一般额度窗口',
  '指定会话无法安全读取',
]
const queryFallbackStatus = computed(() => {
  const diagnostics = props.snapshot.diagnostics
  if (diagnostics.some(line => line.startsWith('Claude Desktop 原生额度：最新原生结果已过期'))) return '已过期'
  if (diagnostics.some(line => unavailableQueryReasons.some(reason => line.startsWith(`Claude Desktop 原生额度：${reason}`)))) return '不可用'
  return null
})
const quotaMessage = computed(() => {
  if (hasDesktopQuery.value) return 'Desktop 查询快照；源缓存最多 60 秒。请在绑定的 Desktop 会话再次调用 get_usage 更新，读取时超过 15 分钟则忽略。账号范围未确认，暂不作额度预测或提醒。'
  if (hasDesktopSample.value && queryFallbackStatus.value) return `get_usage 结果${queryFallbackStatus.value}，当前为历史采样；请在绑定的 Desktop 会话重新调用 get_usage。历史采样仅有百分比，不提供重置时间，超过 30 分钟会被忽略。`
  if (hasDesktopSample.value) return '本地 Claude Desktop 历史仅提供额度百分比，不提供重置时间或套餐；刷新时忽略超过 30 分钟的采样。'
  if (props.snapshot.primaryQuota || props.snapshot.secondaryQuota) return ''
  // Diagnostics can contain local paths. Match a known reason but never copy the
  // raw diagnostic into the account card.
  if (props.snapshot.diagnostics.some(line => line.includes('Claude Desktop 额度：历史采样已过期'))) {
    return 'Desktop 历史采样已过期（超过 30 分钟），请在 Claude Desktop 中刷新用量后重新读取。'
  }
  return '当前没有可用的 Claude 额度采样。已绑定 Desktop 会话时，请在该会话再次调用 get_usage。Desktop 历史只含百分比，超过 30 分钟会被忽略；终端 Claude Code 可通过 statusLine 接入。'
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
