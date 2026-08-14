<script setup lang="ts">
import { relativeTime } from '../format'
import { useDashboardStore } from '../stores/dashboard'
import { useTodoComposerStore } from '../stores/todoComposer'
import type { DashboardSnapshot, TaskColumnKind } from '../types'
import UiIcon from '../components/UiIcon.vue'

const props = defineProps<{ snapshot: DashboardSnapshot }>()
const emit = defineEmits<{ todoCreated: [] }>()
const store = useDashboardStore()
const composer = useTodoComposerStore()

const taskColumns = [
  { kind: 'active' as const, label: '进行中', icon: 'activity' as const },
  { kind: 'pending' as const, label: '待处理', icon: 'circle' as const },
  { kind: 'scheduled' as const, label: '定时', icon: 'clock' as const },
  { kind: 'done' as const, label: '完成', icon: 'check-square' as const },
]

function tasksFor(kind: TaskColumnKind) {
  return props.snapshot.tasks.filter((task) => task.kind === kind).slice(0, 3)
}

async function addTaskAsTodo(task: { id: string; title: string }) {
  if (await store.addTodo({ text: task.title, priority: 'normal', dueDate: composer.dueDate, threadId: task.id })) {
    emit('todoCreated')
  }
}
</script>

<template>
  <div class="task-board">
    <div v-for="column in taskColumns" :key="column.kind" class="task-column" :class="column.kind">
      <div class="column-heading"><UiIcon class="column-icon" :name="column.icon" :size="14" /><strong>{{ column.label }}</strong><em>{{ tasksFor(column.kind).length }}</em></div>
      <div v-if="tasksFor(column.kind).length" class="task-list">
        <article v-for="task in tasksFor(column.kind)" :key="task.id" class="task-item">
          <div class="task-project"><span>{{ task.project.slice(0, 1).toUpperCase() }}</span>{{ task.project }}</div>
          <h3>{{ task.title }}</h3>
          <div class="task-meta"><span>{{ task.detail ?? relativeTime(task.updatedAt) }}</span><button type="button" class="link-action" @click="addTaskAsTodo(task)">转待办</button></div>
        </article>
      </div>
      <div v-else class="empty-column"><span>·</span><p>暂无{{ column.label }}任务</p></div>
    </div>
  </div>
</template>
