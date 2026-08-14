<script setup lang="ts">
import { useDashboardStore } from '../stores/dashboard'
import type { DashboardSnapshot } from '../types'
import UiIcon from './UiIcon.vue'

defineProps<{ snapshot: DashboardSnapshot }>()
const emit = defineEmits<{ openSettings: [] }>()
const store = useDashboardStore()
</script>

<template>
  <header class="app-header">
    <div class="dashboard-context">
      <strong>使用概览</strong>
      <span><i />数据仅在本机处理</span>
    </div>

    <div class="header-actions">
      <div class="runtime-switch" role="group" aria-label="数据运行时">
        <button type="button" :class="{ active: store.runtime === 'codex' }" :aria-pressed="store.runtime === 'codex'" @click="store.selectRuntime('codex')">
          <UiIcon class="runtime-logo codex-logo" name="sparkle" :size="14" /> Codex
        </button>
        <button type="button" :class="{ active: store.runtime === 'claudeCode' }" :aria-pressed="store.runtime === 'claudeCode'" @click="store.selectRuntime('claudeCode')">
          <UiIcon class="runtime-logo claude-logo" name="sun" :size="14" /> Claude Code
        </button>
      </div>
      <div class="account-pill">
        <span class="account-dot" :class="{ online: snapshot.account?.isAuthenticated }" />
        <div><strong>{{ snapshot.account?.planType?.toUpperCase() ?? 'LOCAL' }}</strong><small>{{ snapshot.account?.email ?? '本机记录' }}</small></div>
      </div>
      <span v-if="store.isRefreshing" class="refresh-status" role="status">正在刷新…</span>
      <button type="button" class="icon-button" title="刷新本机数据" aria-label="刷新本机数据" :disabled="store.isRefreshing" @click="store.refresh()"><UiIcon :class="{ spinning: store.isRefreshing }" name="refresh" /></button>
      <button type="button" class="icon-button" title="切换紧凑模式" aria-label="切换紧凑模式" :aria-pressed="store.compactMode" :aria-disabled="store.isRunningLocalOperation || store.isUpdatingSettings" @click="store.toggleCompact()"><UiIcon name="layout" /></button>
      <button type="button" class="icon-button" title="打开设置与诊断" aria-label="打开设置与诊断" @click="emit('openSettings')"><UiIcon name="settings" /></button>
    </div>
  </header>
</template>
