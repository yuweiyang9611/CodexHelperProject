<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import AppHeader from './components/AppHeader.vue'
import OverviewCards from './components/OverviewCards.vue'
import AnalysisFilters from './components/AnalysisFilters.vue'
import UiIcon from './components/UiIcon.vue'
import CombinedRuntimeView from './views/CombinedRuntimeView.vue'
import DiagnosticsSettings from './views/DiagnosticsSettings.vue'
import UsageAnalysisView from './views/UsageAnalysisView.vue'
import { useUsageAnalysis } from './composables/useUsageAnalysis'
import { useThemePreference } from './composables/useThemePreference'
import { useViewportScale } from './composables/useViewportScale'
import { relativeTime } from './format'
import { useDashboardStore } from './stores/dashboard'
import { host } from './host'
const store = useDashboardStore()
const activeTab = ref<'overview' | 'usage' | 'diagnostics'>('overview')
const compare = ref(false)
const snapshot = computed(() => store.snapshot)
const { layoutStyle } = useViewportScale()
const { isLightTheme } = useThemePreference()
const analysis = useUsageAnalysis(computed(() => store.runtime), computed(() => store.snapshot?.refreshedAt))
const tabs = [
  { id: 'overview' as const, icon: 'activity' as const, label: '总览' },
  { id: 'usage' as const, icon: 'activity' as const, label: '用量分析' },
  { id: 'diagnostics' as const, icon: 'settings' as const, label: '设置' },
]
async function selectTabFromKeyboard(event: KeyboardEvent, index: number) {
  const lastIndex = tabs.length - 1
  let nextIndex = index
  if (event.key === 'ArrowRight') nextIndex = index === lastIndex ? 0 : index + 1
  else if (event.key === 'ArrowLeft') nextIndex = index === 0 ? lastIndex : index - 1
  else if (event.key === 'Home') nextIndex = 0
  else if (event.key === 'End') nextIndex = lastIndex
  else return
  event.preventDefault()
  activeTab.value = tabs[nextIndex].id
  await nextTick()
  document.getElementById(`tab-${tabs[nextIndex].id}`)?.focus()
}
onMounted(async () => {
  await store.initialize()
  await nextTick()
  if (!store.error && store.snapshot && store.settings) await host.request('app.ready')
})
</script>
<template>
  <div class="viewport-frame">
    <a class="skip-link" href="#dashboard-content">跳转到主内容</a>
    <main id="dashboard-content" class="app-shell" :class="{ compact: store.compactMode, light: isLightTheme }" :style="layoutStyle" tabindex="-1">
      <h1 class="sr-only">codexU 本地用量仪表盘</h1>
      <div v-if="store.isLoading" class="page-loading" role="status" aria-live="polite"><div class="loading-orbit"><i /><i /><i /></div><strong>正在读取本机用量</strong><span>仅在本机解析统计字段；不保存、不展示、不上传正文</span></div>
      <div v-else-if="!snapshot" class="page-loading page-error" role="alert"><strong>本机数据读取失败</strong><span>{{ store.error ?? '尚未获得可显示的数据快照' }}</span><button type="button" @click="store.initialize()">重新读取</button></div>
      <template v-else>
        <AppHeader :snapshot="snapshot" @open-settings="activeTab = 'diagnostics'" />
        <div v-if="store.error" class="notice error-notice" role="alert" aria-live="assertive" aria-atomic="true">{{ store.error }}</div>
        <div v-if="store.updateStatus?.isUpdateAvailable" class="notice update-notice"><span>{{ store.updateState?.phase === 'ready' ? `v${store.updateState.version} 已下载，可重启更新` : store.updateStatus.status }}</span><button v-if="store.updateState?.phase === 'ready'" type="button" :disabled="store.isInstallingUpdate || store.isRunningLocalOperation || store.isUpdatingSettings" @click="store.installUpdate()">重启并更新</button><button type="button" @click="store.openReleasePage()">查看发布</button></div>
        <section class="dashboard-section glass-card">
          <div class="dashboard-toolbar">
            <nav class="tabs" role="tablist" aria-label="仪表盘视图"><button v-for="(tab, index) in tabs" :id="`tab-${tab.id}`" :key="tab.id" type="button" role="tab" :aria-controls="`panel-${tab.id}`" :aria-selected="activeTab === tab.id" :tabindex="activeTab === tab.id ? 0 : -1" :class="{ active: activeTab === tab.id }" @click="activeTab = tab.id" @keydown="selectTabFromKeyboard($event, index)"><UiIcon :name="tab.icon" :size="15" />{{ tab.label }}</button></nav>
            <span class="tab-summary">本机用量 · 账户额度独立展示</span>
          </div>
          <AnalysisFilters v-if="activeTab !== 'diagnostics'" :state="analysis" />
          <section v-if="activeTab === 'overview'" id="panel-overview" role="tabpanel" aria-labelledby="tab-overview" tabindex="0">
            <div class="overview-actions"><button type="button" :aria-pressed="compare" @click="compare = !compare">{{ compare ? '返回当前工具' : '并排比较两种工具' }}</button><button v-if="!compare" type="button" @click="activeTab = 'usage'">查看用量分析与会话 →</button></div>
            <CombinedRuntimeView v-if="compare" :state="analysis" />
            <template v-else><p v-if="analysis.loading.value" role="status">正在读取所选范围…</p><p v-else-if="analysis.error.value" role="alert">{{ analysis.error.value }} <button type="button" @click="analysis.refresh">重试</button></p><OverviewCards :snapshot="snapshot" :analysis="analysis.result.value" /></template>
          </section>
          <UsageAnalysisView v-else-if="activeTab === 'usage'" id="panel-usage" :snapshot="snapshot" :state="analysis" role="tabpanel" aria-labelledby="tab-usage" tabindex="0" />
          <DiagnosticsSettings v-else id="panel-diagnostics" :snapshot="snapshot" role="tabpanel" aria-labelledby="tab-diagnostics" tabindex="0" />
        </section>
        <footer class="app-footer"><div><i class="status-light" /><span>数据仅在本机处理 · 历史长期留存</span></div><span>刷新于 {{ relativeTime(snapshot.refreshedAt) }}</span></footer>
      </template>
    </main>
  </div>
</template>
<style scoped>
.analysis-filters { margin: 0 18px 18px; }
#panel-overview, #panel-usage { padding: 0 18px 18px; }
.overview-actions { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 12px; margin-bottom: 16px; }
.overview-actions button { padding: 8px 12px; border: 1px solid var(--stroke); border-radius: 8px; background: var(--surface-subtle); color: var(--text-secondary); font-size: 12px; cursor: pointer; }
.overview-actions button[aria-pressed=true] { color: var(--text-primary); border-color: var(--blue); }
</style>
