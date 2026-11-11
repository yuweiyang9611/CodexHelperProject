<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { resolveSubscription } from '../composables/useCombinedRuntime'
import { currencyAmount } from '../format'
import { CLAUDE_DESKTOP_QUERY_INSTRUCTION, isDesktopHistoryQuota, isDesktopQueryQuota, quotaSampleTime } from '../quotaPresentation'
import { useDashboardStore } from '../stores/dashboard'
import type { DashboardSnapshot } from '../types'

const props = defineProps<{ snapshot: DashboardSnapshot; showManualAmount?: boolean }>()
const store = useDashboardStore()
const isClaude = computed(() => props.snapshot.runtime === 'claudeCode')
const hasDesktopSample = computed(() => isDesktopHistoryQuota(props.snapshot.primaryQuota) || isDesktopHistoryQuota(props.snapshot.secondaryQuota))
const hasDesktopQuery = computed(() => isDesktopQueryQuota(props.snapshot.primaryQuota) || isDesktopQueryQuota(props.snapshot.secondaryQuota))
const subscription = computed(() => resolveSubscription('claudeCode', props.snapshot.account?.suggestedMonthlySubscriptionAmount, store.settings))
const missingSubscription = computed(() => subscription.value.source === 'unknown')
const showManualAmount = computed(() => props.showManualAmount && subscription.value.source === 'manual')
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
const oversizedTranscript = computed(() => props.snapshot.diagnostics.some(line => line.startsWith('Claude Desktop 原生额度：指定会话文件超过 32 MiB')))
const observedAt = computed(() => {
  const time = props.snapshot.claudeDesktopQueryObservedAt
  return time && Number.isFinite(Date.parse(time)) ? time : null
})
const copyState = ref<'ready' | 'copied' | 'manual'>('ready')
watch([queryFallbackStatus, observedAt], () => { copyState.value = 'ready' })
function copyWithGesture(): boolean {
  // Electron denies permission requests. A user-initiated copy event still
  // lets the focused renderer copy this fixed instruction to the clipboard.
  let wroteInstruction = false
  const writeInstruction = (event: ClipboardEvent) => {
    if (!event.clipboardData) return
    event.clipboardData.setData('text/plain', CLAUDE_DESKTOP_QUERY_INSTRUCTION)
    event.preventDefault()
    wroteInstruction = true
  }
  document.addEventListener('copy', writeInstruction)
  try {
    return document.execCommand?.('copy') === true && wroteInstruction
  } catch {
    return false
  } finally {
    document.removeEventListener('copy', writeInstruction)
  }
}
async function copyInstruction() {
  try {
    if (!copyWithGesture()) await navigator.clipboard.writeText(CLAUDE_DESKTOP_QUERY_INSTRUCTION)
    copyState.value = 'copied'
  } catch {
    copyState.value = 'manual'
  }
}
const quotaMessage = computed(() => {
  if (hasDesktopQuery.value) return 'Desktop 查询快照；源缓存最多 60 秒。请在绑定的 Desktop 会话再次调用 get_usage 更新，读取时超过 15 分钟则忽略。账号范围未确认，暂不作额度预测或提醒。'
  if (oversizedTranscript.value) return `绑定的会话文件超过 32 MiB。请在新的 Desktop 会话中查询额度，再到“设置与诊断”绑定新会话。${hasDesktopSample.value ? '当前显示历史采样。' : ''}`
  if (hasDesktopSample.value && queryFallbackStatus.value) return `get_usage 结果${queryFallbackStatus.value}，当前为历史采样；请在绑定的 Desktop 会话重新调用 get_usage。历史采样仅有百分比，不提供重置时间，超过 30 分钟会被忽略。`
  if (hasDesktopSample.value) return '本地 Claude Desktop 历史仅提供额度百分比，不提供重置时间或套餐；刷新时忽略超过 30 分钟的采样。'
  if (queryFallbackStatus.value) return `get_usage 结果${queryFallbackStatus.value}。请在绑定的 Desktop 会话重新调用 get_usage，再刷新本页。`
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
  <aside v-if="isClaude && (quotaMessage || missingSubscription || showManualAmount)" class="runtime-data-notice" role="note">
    <p v-if="quotaMessage">{{ quotaMessage }}</p>
    <div v-if="queryFallbackStatus && !oversizedTranscript" class="query-actions">
      <span v-if="observedAt">上次查询（本地时间） <time :datetime="observedAt">{{ quotaSampleTime(observedAt) }}</time></span>
      <button type="button" @click="copyInstruction">复制查询指令</button>
      <span role="status" aria-live="polite">{{ copyState === 'copied' ? '已复制，请粘贴到绑定的 Desktop 会话。' : copyState === 'manual' ? '请选中下方指令手动复制。' : '' }}</span>
      <input v-if="copyState === 'manual'" readonly aria-label="额度查询指令" :value="CLAUDE_DESKTOP_QUERY_INSTRUCTION" @focus="($event.target as HTMLInputElement).select()" />
    </div>
    <p v-if="showManualAmount">订阅月费 {{ currencyAmount(subscription.amount) }}（手动）</p>
    <p v-if="missingSubscription">订阅档位未识别，月费可在“设置与诊断”填写；自动估算或手动金额均不代表实际账单。</p>
  </aside>
</template>

<style scoped>
.runtime-data-notice { margin-top: 8px; color: var(--text-tertiary); font-size: 11px; line-height: 1.65; }
.runtime-data-notice p { margin: 4px 0 0; }
.query-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; }
.query-actions input { width: 100%; }
</style>
