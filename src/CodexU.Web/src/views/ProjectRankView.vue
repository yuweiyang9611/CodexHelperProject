<script setup lang="ts">
import { computed, ref } from 'vue'
import { compactNumber, relativeTime } from '../format'
import { useEquivalentValue } from '../composables/useEquivalentValue'
import type { DashboardSnapshot, ProjectUsage } from '../types'
import UiIcon from '../components/UiIcon.vue'

const props = defineProps<{ snapshot: DashboardSnapshot }>()
const { money } = useEquivalentValue(computed<DashboardSnapshot | null>(() => props.snapshot))

type SortKey = 'cost' | 'tokens'
const sortBy = ref<SortKey>('cost')

/** Null cost means unknown, never free — see ProjectUsage.HasKnownCost. */
const hasCost = (project: ProjectUsage) => (project.creditsUsed ?? 0) > 0

// With nothing priced, a cost ranking would order every project at zero and read
// as a real result. Fall back to tokens and say why instead.
const costAvailable = computed(() => props.snapshot.projects.some(hasCost))
const activeSort = computed<SortKey>(() => (costAvailable.value ? sortBy.value : 'tokens'))

// Codex attributes projects in SQLite but prices usage from session logs with no
// project key, so its cost is apportioned by token share rather than measured.
const costIsEstimated = computed(() => props.snapshot.projects.some((project) => project.costIsEstimated))

const ranked = computed(() => [...props.snapshot.projects].sort((a, b) => activeSort.value === 'cost'
  // Unknown cost sinks below any known one rather than tying with a cheap project.
  ? (b.creditsUsed ?? -1) - (a.creditsUsed ?? -1)
  : b.tokens - a.tokens).slice(0, 8))

const maxProjectTokens = computed(() => Math.max(...props.snapshot.projects.map((project) => project.tokens), 1))
const maxProjectCredits = computed(() => Math.max(...props.snapshot.projects.map((project) => project.creditsUsed ?? 0), Number.MIN_VALUE))
const maxToolCount = computed(() => Math.max(...props.snapshot.tools.map((tool) => tool.count), 1))

function barWidth(project: ProjectUsage) {
  return activeSort.value === 'cost'
    ? (project.creditsUsed ?? 0) / maxProjectCredits.value * 100
    : project.tokens / maxProjectTokens.value * 100
}

const costLabel = computed(() => costIsEstimated.value ? '成本（估算）' : '成本')

function toolIcon(category?: string): 'terminal' | 'edit' | 'globe' | 'tool' {
  if (category === 'Terminal') return 'terminal'
  if (category === 'Edit') return 'edit'
  if (category === 'Web') return 'globe'
  return 'tool'
}
</script>

<template>
  <div class="rank-layout">
    <article class="inner-card rank-card project-rank">
      <div class="inner-heading">
        <div><span>项目统计</span><h3>项目用量排行</h3></div>
        <div v-if="costAvailable" class="rank-sort" role="group" aria-label="项目排序依据">
          <button type="button" :class="{ active: activeSort === 'cost' }" :aria-pressed="activeSort === 'cost'" @click="sortBy = 'cost'">{{ costLabel }}</button>
          <button type="button" :class="{ active: activeSort === 'tokens' }" :aria-pressed="activeSort === 'tokens'" @click="sortBy = 'tokens'">Token</button>
        </div>
        <em v-else>Token</em>
      </div>
      <div v-if="snapshot.projects.length" class="rank-list">
        <div v-for="(project, index) in ranked" :key="project.id" class="rank-row" :title="project.fullPath">
          <span class="rank-index">{{ String(index + 1).padStart(2, '0') }}</span>
          <div class="rank-info"><div><strong>{{ project.name }}</strong><small>{{ project.branch ?? '默认分支' }} · {{ project.threadCount }} 线程 · {{ relativeTime(project.lastActiveAt) }}</small></div><i><b :style="{ width: `${barWidth(project)}%` }" /></i></div>
          <div class="rank-value">
            <strong>{{ compactNumber(project.tokens) }}</strong>
            <!-- Absent cost is stated, not blanked: a missing figure beside real
                 tokens would otherwise look like a rendering gap. -->
            <small v-if="hasCost(project)" :class="{ estimated: project.costIsEstimated }">{{ money(project.creditsUsed) }}</small>
            <small v-else class="unknown">成本不可得</small>
          </div>
        </div>
      </div>
      <div v-else class="large-empty">暂无可归类项目</div>
      <p v-if="costIsEstimated" class="rank-note">成本按线程 token 占比分摊，非逐条计价</p>
    </article>
    <article class="inner-card rank-card tool-rank">
      <div class="inner-heading"><div><span>工具统计</span><h3>工具调用排行</h3></div><em>调用次数</em></div>
      <div v-if="snapshot.tools.length" class="compact-rank-list">
        <div v-for="tool in snapshot.tools" :key="tool.id" class="compact-rank-row">
          <span class="tool-icon"><UiIcon :name="toolIcon(tool.category)" :size="15" /></span>
          <div><strong>{{ tool.name }}</strong><i><b :style="{ width: `${tool.count / maxToolCount * 100}%` }" /></i></div><em>{{ tool.count }}</em>
        </div>
      </div>
      <div v-else class="large-empty">暂无工具调用记录</div>
    </article>
  </div>
</template>
