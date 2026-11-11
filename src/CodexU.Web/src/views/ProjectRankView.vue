<script setup lang="ts">
import { computed, ref } from 'vue'
import { compactNumber, currencyAmount, relativeTime } from '../format'
import type { DashboardSnapshot } from '../types'
import UiIcon from '../components/UiIcon.vue'

const props = defineProps<{ snapshot: DashboardSnapshot }>()

type SortKey = 'cost' | 'tokens'
const sortBy = ref<SortKey>('cost')

const ranking = computed(() => props.snapshot.projectRanking)
const costAvailable = computed(() => ranking.value?.hasCost)
const activeSort = computed<SortKey>(() => (costAvailable.value ? sortBy.value : 'tokens'))
const ranked = computed(() => {
  if (!ranking.value) return []
  const entries = activeSort.value === 'cost' ? ranking.value.byCost : ranking.value.byTokens
  const rows = entries.map(row => ({ project: props.snapshot.projects[row.projectIndex]!, amount: row.amount }))
  const magnitude = (row: typeof rows[number]) => activeSort.value === 'cost' ? row.amount ?? 0 : row.project.tokens
  const max = rows.length ? magnitude(rows[0]!) : 0
  return rows.map(row => ({ ...row, width: max > 0 ? magnitude(row) / max * 100 : 0 }))
})
const maxToolCount = computed(() => Math.max(...props.snapshot.tools.map((tool) => tool.count), 1))
const costLabel = computed(() => ranking.value?.hasEstimatedCost ? '成本（估算）' : '成本')

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
      <div v-if="ranked.length" class="rank-list">
        <div v-for="({ project, amount, width }, index) in ranked" :key="project.id" class="rank-row" :title="project.fullPath">
          <span class="rank-index">{{ String(index + 1).padStart(2, '0') }}</span>
          <div class="rank-info"><div><strong>{{ project.name }}</strong><small>{{ project.branch ?? '默认分支' }} · {{ project.threadCount }} 线程 · {{ relativeTime(project.lastActiveAt) }}</small></div><i><b :style="{ width: `${width}%` }" /></i></div>
          <div class="rank-value">
            <strong>{{ compactNumber(project.tokens) }}</strong>
            <!-- Absent cost is stated, not blanked: a missing figure beside real
                 tokens would otherwise look like a rendering gap. -->
            <small v-if="amount != null" :class="{ estimated: project.costIsEstimated }">{{ project.costStatus === 'partial' ? '≥ ' : '' }}{{ currencyAmount(amount) }}</small>
            <small v-else class="unknown">成本不可得</small>
          </div>
        </div>
      </div>
      <div v-else class="large-empty">{{ ranking ? '暂无可归类项目' : '项目排行不可用' }}</div>
      <p v-if="ranking?.hasEstimatedCost" class="rank-note">成本按线程 token 占比分摊，非逐条计价</p>
      <p v-if="ranked.some(row => row.project.costStatus === 'partial')" class="rank-note">≥ 表示仅计入已核算金额，成本排行按该金额排序。</p>
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
