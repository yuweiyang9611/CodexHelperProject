<script setup lang="ts">
import { useDashboardStore } from '../stores/dashboard'
import { computed } from 'vue'
import type { AgentRuntime, DashboardSnapshot } from '../types'
import UiIcon from './UiIcon.vue'

const props = defineProps<{
  snapshot: DashboardSnapshot
  provider: AgentRuntime | 'antigravity'
  refreshing: boolean
}>()
const emit = defineEmits<{
  openSettings: []
  selectProvider: [provider: AgentRuntime | 'antigravity']
  refresh: []
}>()
const store = useDashboardStore()
const isAntigravity = computed(() => props.provider === 'antigravity')
</script>

<template>
  <header class="app-header">
    <div class="dashboard-context">
      <strong>使用概览</strong>
      <span><i />数据仅在本机处理</span>
    </div>

    <div class="header-actions">
      <div class="runtime-switch" role="group" aria-label="数据运行时">
        <button type="button" :class="{ active: provider === 'codex' }" :aria-pressed="provider === 'codex'" @click="emit('selectProvider', 'codex')">
          <UiIcon class="runtime-logo codex-logo" name="sparkle" :size="14" /> Codex
        </button>
        <button type="button" :class="{ active: provider === 'claudeCode' }" :aria-pressed="provider === 'claudeCode'" @click="emit('selectProvider', 'claudeCode')">
          <UiIcon class="runtime-logo claude-logo" name="sun" :size="14" /> Claude Code
        </button>
        <button type="button" :class="{ active: isAntigravity }" :aria-pressed="isAntigravity" @click="emit('selectProvider', 'antigravity')">
          <UiIcon class="runtime-logo codex-logo" name="globe" :size="14" /> Antigravity
        </button>
      </div>
      <div v-if="!isAntigravity" class="account-pill">
        <span class="account-dot" :class="{ online: snapshot.account?.isAuthenticated }" />
        <div><strong>{{ snapshot.account?.planType?.toUpperCase() ?? 'LOCAL' }}</strong><small>{{ snapshot.account?.email ?? '本机记录' }}</small></div>
      </div>
      <span v-if="refreshing" class="refresh-status" role="status">正在刷新…</span>
      <button type="button" class="icon-button" title="刷新本机数据" aria-label="刷新本机数据" :aria-disabled="refreshing" @click="!refreshing && emit('refresh')"><UiIcon :class="{ spinning: refreshing }" name="refresh" /></button>
      <button type="button" class="icon-button" title="切换紧凑模式" aria-label="切换紧凑模式" :aria-pressed="store.compactMode" :aria-disabled="store.isRunningLocalOperation || store.isUpdatingSettings" @click="store.toggleCompact()"><UiIcon name="layout" /></button>
      <button type="button" class="icon-button" title="打开设置与诊断" aria-label="打开设置与诊断" @click="emit('openSettings')"><UiIcon name="settings" /></button>
    </div>
  </header>
</template>
