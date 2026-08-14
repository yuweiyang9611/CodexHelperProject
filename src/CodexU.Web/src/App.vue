<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import AppHeader from './components/AppHeader.vue'
import OverviewCards from './components/OverviewCards.vue'
import UiIcon from './components/UiIcon.vue'
import CombinedRuntimeView from './views/CombinedRuntimeView.vue'
import DiagnosticsSettings from './views/DiagnosticsSettings.vue'
import ProjectRankView from './views/ProjectRankView.vue'
import SkillUsageView from './views/SkillUsageView.vue'
import TodayTasksView from './views/TodayTasksView.vue'
import TodoGoalsView from './views/TodoGoalsView.vue'
import UsageTrendView from './views/UsageTrendView.vue'
import { useRecentUsage } from './composables/useRecentUsage'
import { useThemePreference } from './composables/useThemePreference'
import { useViewportScale } from './composables/useViewportScale'
import { compactNumber, relativeTime } from './format'
import { useDashboardStore } from './stores/dashboard'
import { host } from './host'

const store = useDashboardStore()
const activeTab = ref<'today' | 'todos' | 'usage' | 'projects' | 'skills' | 'combined' | 'diagnostics'>('today')
const snapshot = computed(() => store.snapshot)
const { layoutStyle } = useViewportScale()
const { isLightTheme } = useThemePreference()
// Only the tab summary needs it here; the usage panel derives the rest itself.
const { lastSevenTotal } = useRecentUsage(snapshot)

const tabs = [
  { id: 'today' as const, icon: 'check-square' as const, label: '今日任务' },
  { id: 'todos' as const, icon: 'list' as const, label: '待办与目标' },
  { id: 'usage' as const, icon: 'activity' as const, label: '用量趋势' },
  { id: 'projects' as const, icon: 'folder' as const, label: '项目排行' },
  { id: 'skills' as const, icon: 'sparkle' as const, label: 'Skill 使用' },
  { id: 'combined' as const, icon: 'compare' as const, label: '双运行时' },
  { id: 'diagnostics' as const, icon: 'settings' as const, label: '设置与诊断' },
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
  if (!store.error && store.snapshot && store.settings && document.querySelector('.overview-grid')) {
    await host.request('app.ready')
  }
})
</script>

<template>
  <div class="viewport-frame">
    <a class="skip-link" href="#dashboard-content">跳转到主内容</a>
    <main id="dashboard-content" class="app-shell" :class="{ compact: store.compactMode, light: isLightTheme }" :style="layoutStyle" tabindex="-1">
    <h1 class="sr-only">codexU 本地用量仪表盘</h1>
    <div v-if="store.isLoading" class="page-loading" role="status" aria-live="polite">
      <div class="loading-orbit"><i /><i /><i /></div>
      <strong>正在读取本机 Codex 数据</strong>
      <span>仅在本机解析统计字段；不保存、不展示、不上传正文</span>
    </div>

    <div v-else-if="!snapshot" class="page-loading page-error" role="alert">
      <strong>本机数据读取失败</strong>
      <span>{{ store.error ?? '尚未获得可显示的数据快照' }}</span>
      <button type="button" @click="store.initialize()">重新读取</button>
    </div>

    <template v-else-if="snapshot">
      <AppHeader :snapshot="snapshot" @open-settings="activeTab = 'diagnostics'" />

      <div v-if="store.error" class="notice error-notice" role="alert" aria-live="assertive" aria-atomic="true">{{ store.error }}</div>
      <div v-if="store.updateStatus?.isUpdateAvailable" class="notice update-notice">
        <span>{{ store.updateStatus.status }}</span>
        <button type="button" @click="store.openReleasePage()">查看发布</button>
      </div>

      <OverviewCards :snapshot="snapshot" />

      <section class="dashboard-section glass-card">
        <div class="dashboard-toolbar">
          <nav class="tabs" role="tablist" aria-label="仪表盘视图">
            <button
              v-for="(tab, index) in tabs"
              :id="`tab-${tab.id}`"
              :key="tab.id"
              type="button"
              role="tab"
              :aria-controls="`panel-${tab.id}`"
              :aria-selected="activeTab === tab.id"
              :tabindex="activeTab === tab.id ? 0 : -1"
              :class="{ active: activeTab === tab.id }"
              @click="activeTab = tab.id"
              @keydown="selectTabFromKeyboard($event, index)"
            >
              <UiIcon :name="tab.icon" :size="15" />{{ tab.label }}
            </button>
          </nav>
          <div class="tab-summary">
            <template v-if="activeTab === 'today'">今日共 <strong>{{ snapshot.tasks.length }}</strong> 项</template>
            <template v-else-if="activeTab === 'usage'">近 7 日 <strong>{{ compactNumber(lastSevenTotal) }}</strong></template>
            <template v-else-if="activeTab === 'projects'">已归类 <strong>{{ snapshot.projects.length }}</strong> 个项目</template>
            <template v-else-if="activeTab === 'todos'">未完成 <strong>{{ store.todos.filter(todo => !todo.done).length }}</strong> 项</template>
            <template v-else-if="activeTab === 'skills'">发现 <strong>{{ snapshot.skills.length }}</strong> 个 Skill</template>
            <template v-else-if="activeTab === 'combined'">Codex + Claude Code 并列</template>
            <template v-else>数据源 <strong>{{ snapshot.diagnostics.length }}</strong> 项</template>
          </div>
        </div>

        <!-- Each panel component renders the panel element itself, so the tab
             wiring below falls through onto that element unchanged. -->
        <TodayTasksView v-if="activeTab === 'today'" id="panel-today" :snapshot="snapshot" role="tabpanel" aria-labelledby="tab-today" tabindex="0" @todo-created="activeTab = 'todos'" />

        <TodoGoalsView v-else-if="activeTab === 'todos'" id="panel-todos" :snapshot="snapshot" role="tabpanel" aria-labelledby="tab-todos" tabindex="0" />

        <UsageTrendView v-else-if="activeTab === 'usage'" id="panel-usage" :snapshot="snapshot" role="tabpanel" aria-labelledby="tab-usage" tabindex="0" />

        <ProjectRankView v-else-if="activeTab === 'projects'" id="panel-projects" :snapshot="snapshot" role="tabpanel" aria-labelledby="tab-projects" tabindex="0" />

        <SkillUsageView v-else-if="activeTab === 'skills'" id="panel-skills" :snapshot="snapshot" role="tabpanel" aria-labelledby="tab-skills" tabindex="0" />

        <!-- The only view without a :snapshot — it reads store.combined, which holds
             both runtimes, rather than the single selected-runtime snapshot. -->
        <CombinedRuntimeView v-else-if="activeTab === 'combined'" id="panel-combined" role="tabpanel" aria-labelledby="tab-combined" tabindex="0" />

        <DiagnosticsSettings v-else id="panel-diagnostics" :snapshot="snapshot" role="tabpanel" aria-labelledby="tab-diagnostics" tabindex="0" />
      </section>

      <footer class="app-footer">
        <div><i class="status-light" /><span>数据仅在本机处理</span><span class="footer-divider" />{{ snapshot.diagnostics[0] ?? '精细 token 事件已就绪' }}</div>
        <span>刷新于 {{ relativeTime(snapshot.refreshedAt) }}</span>
      </footer>
    </template>
    </main>
  </div>
</template>
