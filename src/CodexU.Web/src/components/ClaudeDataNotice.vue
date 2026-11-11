<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { currencyAmount } from '../format'
import { CLAUDE_DESKTOP_QUERY_INSTRUCTION, hasQuotaReset, quotaSampleTime } from '../quotaPresentation'
import type { DashboardSnapshot } from '../types'

const props = defineProps<{ snapshot: DashboardSnapshot; showManualAmount?: boolean; now: number }>()
const isClaude = computed(() => props.snapshot.runtime === 'claudeCode')
const state = computed(() => props.snapshot.claudeQuotaState)
const expiredQuery = computed(() => state.value?.source === 'desktopQuery'
  && [props.snapshot.primaryQuota, props.snapshot.secondaryQuota].some(quota => hasQuotaReset(quota, props.now)))
const subscription = computed(() => props.snapshot.monthlyValue)
const missingSubscription = computed(() => subscription.value?.subscriptionAmount == null)
const showManualAmount = computed(() => props.showManualAmount && subscription.value?.subscriptionSource === 'manual')
const observedAt = computed(() => {
  const time = props.snapshot.claudeDesktopQueryObservedAt
  return time && Number.isFinite(Date.parse(time)) ? time : null
})
const copyState = ref<'ready' | 'copied' | 'manual'>('ready')
watch([() => state.value?.queryStatus, expiredQuery, observedAt], () => { copyState.value = 'ready' })
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
  const quota = state.value
  if (quota?.queryStatus === 'noWindows') return 'Desktop 查询成功，最近一次查询没有仍有效的一般额度窗口。可在绑定的 Desktop 会话更新。'
  if (quota?.source === 'desktopQuery') return 'Desktop 查询快照；源缓存最多 60 秒。请在绑定的 Desktop 会话再次调用 get_usage 更新，读取时超过 15 分钟则忽略。账号范围未确认，暂不作额度预测或提醒。'
  const fallback = quota?.source === 'desktopHistory' ? '当前为历史采样' : quota?.source === 'statusLine' ? '当前为终端 statusLine 快照' : ''
  if (quota?.action === 'rebind') return `绑定的会话文件超过 32 MiB。请在新的 Desktop 会话中查询额度，再到“设置与诊断”绑定新会话。${fallback ? fallback + '。' : ''}`
  if (quota?.action === 'query') {
    const reason = quota.queryStatus === 'stale' ? 'get_usage 结果已过期' : quota.queryStatus === 'missingResult' ? '尚未找到 get_usage 查询结果' : 'get_usage 结果不可用'
    const historyHint = quota.source === 'desktopHistory' ? '历史采样仅有百分比，不提供重置时间，超过 30 分钟会被忽略。' : ''
    return `${reason}${fallback ? '，' + fallback : ''}；请在绑定的 Desktop 会话重新调用 get_usage，再刷新本页。${historyHint}`
  }
  if (quota?.source === 'desktopHistory') return '本地 Claude Desktop 历史仅提供额度百分比，不提供重置时间或套餐；刷新时忽略超过 30 分钟的采样。'
  if (quota?.source === 'statusLine' || props.snapshot.primaryQuota || props.snapshot.secondaryQuota) return ''
  if (quota?.historyExpired) return 'Desktop 历史采样已过期（超过 30 分钟），请在 Claude Desktop 中刷新用量后重新读取。'
  return '当前没有可用的 Claude 额度采样。已绑定 Desktop 会话时，请在该会话再次调用 get_usage。Desktop 历史只含百分比，超过 30 分钟会被忽略；终端 Claude Code 可通过 statusLine 接入。'
})
</script>

<template>
  <aside v-if="isClaude && (quotaMessage || missingSubscription || showManualAmount)" class="runtime-data-notice" role="note">
    <p v-if="quotaMessage">{{ quotaMessage }}</p>
    <div v-if="expiredQuery || state?.action === 'query'" class="query-actions">
      <span v-if="observedAt">上次查询（本地时间） <time :datetime="observedAt">{{ quotaSampleTime(observedAt) }}</time></span>
      <button type="button" @click="copyInstruction">复制查询指令</button>
      <span role="status" aria-live="polite">{{ copyState === 'copied' ? '已复制，请粘贴到绑定的 Desktop 会话。' : copyState === 'manual' ? '请选中下方指令手动复制。' : '' }}</span>
      <input v-if="copyState === 'manual'" readonly aria-label="额度查询指令" :value="CLAUDE_DESKTOP_QUERY_INSTRUCTION" @focus="($event.target as HTMLInputElement).select()" />
    </div>
    <p v-if="showManualAmount">订阅月费 {{ currencyAmount(subscription?.subscriptionAmount) }}（手动）</p>
    <p v-if="missingSubscription">订阅档位未识别，月费可在“设置与诊断”填写；自动估算或手动金额均不代表实际账单。</p>
  </aside>
</template>

<style scoped>
.runtime-data-notice { margin-top: 8px; color: var(--text-tertiary); font-size: 11px; line-height: 1.65; }
.runtime-data-notice p { margin: 4px 0 0; }
.query-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; }
.query-actions input { width: 100%; }
</style>
