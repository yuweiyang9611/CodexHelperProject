<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { host } from '../host'
import type { ClaudeCliAuthStatus, ClaudeCliQuotaSchedule } from '../types'

const result = ref<ClaudeCliAuthStatus | null>(null)
const schedule = ref<ClaudeCliQuotaSchedule | null>(null)
// Saved times survive restarts, so one from another day carries its date.
function clock(value?: string | null): string | null {
  if (!value || !Number.isFinite(Date.parse(value))) return null
  const date = new Date(value)
  const time = date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  return date.toDateString() === new Date().toDateString()
    ? time
    : `${date.toLocaleDateString([], { month: 'numeric', day: 'numeric' })} ${time}`
}
// The background quota query's own state: a rate limit or a missing login is
// otherwise invisible, because the overview deliberately stays quiet about both.
const scheduleText = computed(() => {
  const value = schedule.value
  if (!value) return ''
  if (!value.enabled) return '后台额度查询已关闭。'
  const next = clock(value.nextAttemptAt)
  const nextText = next ? `，${next} 后再查询` : '，下次刷新时查询'
  const success = clock(value.lastSuccessAt)
  switch (value.lastStatus) {
    case 'fresh': return `后台额度查询：最近一次成功读取于 ${success ?? '未知时间'}${nextText}。`
    case 'noWindows': return `后台额度查询成功，但没有一般额度窗口${nextText}。`
    case 'notAvailable': return `后台额度查询：CLI 当前账号没有订阅额度（未登录、API 密钥或第三方服务）${nextText}；登录后点击“重新检查登录状态”可立即恢复。`
    case 'notInstalled': return `后台额度查询：未找到 Claude Code CLI${nextText}。`
    case 'unavailable': return `后台额度查询失败（可能被用量接口限流）${nextText}${success ? `；上次成功读取于 ${success}` : ''}。`
    default: return '后台额度查询尚未运行，将在下次刷新时进行。'
  }
})

async function loadSchedule() {
  try {
    const value = await host.request<ClaudeCliQuotaSchedule>('claude.getCliQuotaStatus')
    if (!disposed) schedule.value = value
  } catch {
    if (!disposed) schedule.value = null
  }
}
const checking = ref(false)
let disposed = false
// Turning the setting on or off, or a refresh finishing, changes the schedule.
const unsubscribers = ['settings.changed', 'usage.snapshotChanged']
  .map(method => host.on(method, () => void loadSchedule()))
onUnmounted(() => {
  disposed = true
  unsubscribers.forEach(unsubscribe => unsubscribe())
})

const statusLabel = computed(() => {
  if (checking.value || !result.value) return '正在检查…'
  return ({
    loggedIn: '已登录',
    loggedOut: '未登录',
    notInstalled: '未找到 CLI',
    unavailable: '暂时无法检查',
  } as const)[result.value?.status ?? 'unavailable']
})
const authLabel = computed(() => ({
  claudeAi: 'Claude 订阅账号',
  oauthToken: 'OAuth 令牌',
  apiKey: 'API 密钥',
  apiKeyHelper: 'API 密钥助手',
  thirdParty: '第三方服务',
  unknown: '认证方式未知',
  none: '',
} as const)[result.value?.authMethod ?? 'none'])
const sourceLabel = computed(() => ({
  standalone: '独立安装的 CLI',
  path: 'PATH 中的 CLI',
  desktopEmbedded: 'Desktop 内嵌 CLI',
  none: '',
} as const)[result.value?.source ?? 'none'])
const checkedAt = computed(() => {
  const value = result.value?.checkedAt
  return value && Number.isFinite(Date.parse(value)) ? value : null
})

async function check() {
  if (checking.value) return
  checking.value = true
  try {
    const value = await host.request<ClaudeCliAuthStatus>('claude.getAuthStatus')
    if (!disposed) result.value = value
  } catch {
    if (!disposed) result.value = {
      status: 'unavailable', authMethod: 'unknown', source: 'none', checkedAt: new Date().toISOString(),
    }
  } finally {
    if (!disposed) checking.value = false
  }
  // A confirmed login may have just ended a "not signed in" wait.
  await loadSchedule()
}
onMounted(() => void check())
</script>

<template>
  <section class="cli-auth-status" aria-label="Claude CLI 登录状态" :aria-busy="checking">
    <div class="cli-auth-heading">
      <strong>Claude Code CLI</strong>
      <span role="status" aria-live="polite" aria-atomic="true">{{ statusLabel }}</span>
      <button type="button" :disabled="checking || !result" @click="check">{{ checking || !result ? '正在检查…' : '重新检查登录状态' }}</button>
    </div>
    <p v-if="!checking && result?.status === 'loggedIn'" class="cli-auth-detail">
      {{ authLabel }}
    </p>
    <p v-else-if="!checking && result?.status === 'loggedOut'" class="cli-auth-detail">请通过官方 CLI 登录与 Desktop 相同的账号，再重新检查。</p>
    <p v-else-if="!checking && result?.status === 'notInstalled'" class="cli-auth-detail">请安装 Claude Code CLI，或确认 Claude Desktop 的内嵌 CLI 可用。</p>
    <p v-else-if="!checking && result?.status === 'unavailable'" class="cli-auth-detail">检查未能完成，请稍后重试。</p>
    <p v-if="!checking && sourceLabel" class="cli-auth-detail">检测来源 {{ sourceLabel }}</p>
    <p v-if="scheduleText" class="cli-auth-detail cli-quota-schedule">{{ scheduleText }}</p>
    <p class="setting-hint">此处显示 CLI 自身的登录状态。Desktop 登录与 CLI 登录分别检查。</p>
    <p v-if="checkedAt && !checking" class="cli-auth-time">检查于 <time :datetime="checkedAt">{{ new Date(checkedAt).toLocaleString() }}</time></p>
  </section>
</template>

<style scoped>
.cli-auth-status { margin-top: 12px; padding: 12px; border: 1px solid var(--stroke); border-radius: 9px; background: var(--surface-subtle); }
.cli-auth-heading { display: flex; align-items: center; flex-wrap: wrap; gap: 8px 12px; font-size: 12px; }
.cli-auth-heading strong { color: var(--text-primary); }
.cli-auth-heading [role="status"] { color: var(--text-secondary); }
.cli-auth-heading button { margin-left: auto; padding: 5px 10px; border: 1px solid var(--stroke); border-radius: 7px; color: var(--text-secondary); background: var(--surface-subtle); cursor: pointer; font-size: 11px; }
.cli-auth-heading button:disabled { opacity: .6; cursor: wait; }
.cli-auth-detail { margin: 8px 0 0; font-size: 12px; color: var(--text-secondary); }
.cli-auth-time { margin: 6px 0 0; font-size: 11px; color: var(--text-tertiary); }
.cli-auth-status .setting-hint { margin: 8px 0 0; }
</style>
