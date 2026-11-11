<script setup lang="ts">
import { ref, watch } from 'vue'
import { host } from '../host'
import { CLAUDE_DESKTOP_QUERY_INSTRUCTION, quotaQueryTime } from '../quotaPresentation'
import type { ClaudeTranscriptValidation } from '../types'

defineProps<{ disabled: boolean }>()
const path = defineModel<string | null>()
const pending = ref<'pick' | 'validate' | null>(null)
const feedback = ref('')
let revision = 0

watch(path, () => {
  revision++
  feedback.value = ''
}, { flush: 'sync' })

async function pickFile() {
  const initialRevision = revision
  pending.value = 'pick'
  try {
    const selected = await host.request<string | null>('settings.pickClaudeTranscript')
    if (selected && revision === initialRevision) path.value = selected
  } catch {
    if (revision === initialRevision) feedback.value = '无法打开文件选择器，可手动填写会话文件的绝对路径。'
  } finally {
    pending.value = null
  }
}

async function validate() {
  const candidate = path.value?.trim()
  if (!candidate) return
  const initialRevision = revision
  pending.value = 'validate'
  feedback.value = ''
  try {
    const result = await host.request<ClaudeTranscriptValidation>('settings.validateClaudeTranscript', { path: candidate })
    if (revision === initialRevision) {
      feedback.value = result.observedAt ? `${result.message} ${quotaQueryTime(result.observedAt)}` : result.message
    }
  } catch {
    if (revision === initialRevision) feedback.value = '验证请求失败，请重试。'
  } finally {
    pending.value = null
  }
}
</script>

<template>
  <details class="desktop-usage-binding">
    <summary>高级：绑定 Desktop 额度会话</summary>
    <div class="settings-grid settings-grid-wide">
      <label class="settings-field-wide">Claude Desktop 额度会话文件<input v-model="path" :disabled="disabled" placeholder="选择或填写 Desktop 会话 JSONL 的绝对路径；留空不绑定" /></label>
    </div>
    <div class="rate-actions">
      <button type="button" :disabled="disabled || !!pending" @click="pickFile">选择文件…</button>
      <button type="button" :disabled="disabled || !!pending || !path?.trim()" @click="validate">{{ pending === 'validate' ? '正在验证…' : '验证绑定' }}</button>
    </div>
    <p role="status" aria-live="polite" aria-atomic="true" class="binding-feedback">{{ feedback }}</p>
    <div class="settings-grid settings-grid-wide">
      <label class="settings-field-wide">复制到该 Desktop 会话的更新指令<input readonly aria-label="Desktop 额度更新指令" :value="CLAUDE_DESKTOP_QUERY_INSTRUCTION" /></label>
    </div>
    <p class="setting-hint">仅读取绑定会话已有的 get_usage 工具结果，不会自动发送消息。更新需要你在该 Desktop 会话再次调用工具；源缓存最多 60 秒，读取时超过 15 分钟的结果会被忽略。</p>
  </details>
</template>

<style scoped>
.desktop-usage-binding { margin-top: 10px; }
.desktop-usage-binding summary { color: var(--text-secondary); font-size: 11px; cursor: pointer; }
.binding-feedback { margin: 8px 0; font-size: 12px; color: var(--text-secondary); }
.binding-feedback:empty { margin: 0; }
</style>
