<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import type { ComponentPublicInstance } from 'vue'
import { compactNumber } from '../format'
import { useDashboardStore } from '../stores/dashboard'
import { todoFilters, useTodoComposerStore } from '../stores/todoComposer'
import type { DashboardSnapshot, TodoItem } from '../types'

defineProps<{ snapshot: DashboardSnapshot }>()

type Feedback = { kind: 'success' | 'error'; message: string }

const store = useDashboardStore()
const composer = useTodoComposerStore()
const addBusy = ref(false)
const editBusyId = ref<string | null>(null)
const toggleBusyIds = ref(new Set<string>())
const clearBusy = ref(false)
const operationFeedback = ref<Feedback | null>(null)
const editingId = ref<string | null>(null)
const editText = ref('')
const editValidationError = ref<string | null>(null)
const editInput = ref<HTMLInputElement | null>(null)
const editButtons = new Map<string, HTMLButtonElement>()
const clearConfirmationOpen = ref(false)
const clearTrigger = ref<HTMLButtonElement | null>(null)
const clearConfirmButton = ref<HTMLButtonElement | null>(null)

const filteredTodos = computed(() => store.todos.filter((todo) =>
  !composer.isPendingDelete(todo.id) && composer.matches(todo)))
const openTodoCount = computed(() => store.todos.filter((todo) =>
  !todo.done && !composer.isPendingDelete(todo.id)).length)
const completedCount = computed(() => store.todos.filter((todo) =>
  todo.done && !composer.isPendingDelete(todo.id)).length)
const immediateMutationBusy = computed(() =>
  addBusy.value || editBusyId.value !== null || toggleBusyIds.value.size > 0 || clearBusy.value)
const mutationBlocked = computed(() => immediateMutationBusy.value || composer.hasCommittingDeletes)

watch(completedCount, (count) => {
  if (count === 0) clearConfirmationOpen.value = false
})

function beginFeedback() {
  operationFeedback.value = null
  composer.clearDeleteFeedback()
}

function failureMessage(action: string) {
  return `${action}失败：${store.error ?? '请稍后重试'}`
}

async function submitTodo() {
  const text = composer.text.trim()
  if (!text || mutationBlocked.value) return

  beginFeedback()
  addBusy.value = true
  try {
    const added = await store.addTodo({
      text,
      priority: composer.priority,
      dueDate: composer.dueDate || undefined,
    })
    if (added) {
      composer.clearText()
      operationFeedback.value = { kind: 'success', message: `已添加“${text}”` }
    } else {
      operationFeedback.value = { kind: 'error', message: failureMessage('添加') }
    }
  } finally {
    addBusy.value = false
  }
}

function setEditButtonRef(id: string, value: Element | ComponentPublicInstance | null) {
  if (value instanceof HTMLButtonElement) editButtons.set(id, value)
  else editButtons.delete(id)
}

function setEditInput(value: Element | ComponentPublicInstance | null) {
  editInput.value = value instanceof HTMLInputElement ? value : null
}

async function focusEditButton(id: string) {
  await nextTick()
  editButtons.get(id)?.focus()
}

async function startEdit(todo: TodoItem) {
  if (mutationBlocked.value) return
  beginFeedback()
  editingId.value = todo.id
  editText.value = todo.text
  editValidationError.value = null
  await nextTick()
  editInput.value?.focus()
  editInput.value?.select()
}

async function cancelEdit(todo: TodoItem) {
  if (editBusyId.value === todo.id) return
  editingId.value = null
  editValidationError.value = null
  await focusEditButton(todo.id)
}

function handleEditKeydown(event: KeyboardEvent, todo: TodoItem) {
  if (event.isComposing) return
  if (event.key === 'Enter') {
    event.preventDefault()
    void saveEdit(todo)
  } else if (event.key === 'Escape') {
    event.preventDefault()
    void cancelEdit(todo)
  }
}

async function saveEdit(todo: TodoItem) {
  if (editBusyId.value || composer.hasCommittingDeletes) return
  const text = editText.value.trim()
  if (!text) {
    editValidationError.value = '待办内容不能为空'
    await nextTick()
    editInput.value?.focus()
    return
  }

  beginFeedback()
  editValidationError.value = null
  editBusyId.value = todo.id
  try {
    const updated = await store.updateTodo({
      id: todo.id,
      text,
      priority: todo.priority,
      dueDate: todo.dueDate,
      threadId: todo.threadId,
    })
    if (updated) {
      editingId.value = null
      operationFeedback.value = { kind: 'success', message: `已保存“${text}”` }
      editBusyId.value = null
      await focusEditButton(todo.id)
    } else {
      operationFeedback.value = { kind: 'error', message: failureMessage('保存') }
      editBusyId.value = null
      await nextTick()
      editInput.value?.focus()
    }
  } finally {
    if (editBusyId.value === todo.id) editBusyId.value = null
  }
}

async function toggleTodo(todo: TodoItem) {
  if (mutationBlocked.value) return
  beginFeedback()
  toggleBusyIds.value = new Set([...toggleBusyIds.value, todo.id])
  try {
    const toggled = await store.toggleTodo(todo.id)
    operationFeedback.value = toggled
      ? { kind: 'success', message: todo.done ? `已将“${todo.text}”标记为未完成` : `已完成“${todo.text}”` }
      : { kind: 'error', message: failureMessage('更新状态') }
  } finally {
    const remaining = new Set(toggleBusyIds.value)
    remaining.delete(todo.id)
    toggleBusyIds.value = remaining
  }
}

function queueTodoDelete(todo: TodoItem) {
  if (mutationBlocked.value) return
  operationFeedback.value = null
  composer.queueDelete(todo, async () => {
    const deleted = await store.deleteTodo(todo.id)
    return deleted ? null : (store.error ?? '请稍后重试')
  })
}

async function openClearConfirmation() {
  if (completedCount.value === 0 || mutationBlocked.value || composer.pendingDeletes.length > 0) return
  beginFeedback()
  clearConfirmationOpen.value = true
  await nextTick()
  clearConfirmButton.value?.focus()
}

async function cancelClearConfirmation() {
  clearConfirmationOpen.value = false
  await nextTick()
  clearTrigger.value?.focus()
}

async function clearCompletedTodos() {
  if (completedCount.value === 0 || mutationBlocked.value) return
  const count = completedCount.value
  beginFeedback()
  clearBusy.value = true
  try {
    const cleared = await store.clearCompletedTodos()
    clearConfirmationOpen.value = false
    operationFeedback.value = cleared
      ? { kind: 'success', message: `已清理 ${count} 项已完成待办` }
      : { kind: 'error', message: failureMessage('清理') }
    if (!cleared) await nextTick(() => clearTrigger.value?.focus())
  } finally {
    clearBusy.value = false
  }
}
</script>

<template>
  <div class="todo-goal-layout">
    <article class="inner-card todo-manager" :aria-busy="immediateMutationBusy">
      <div class="inner-heading">
        <div><span>待办事项</span><h3>我的待办</h3></div>
        <em>{{ openTodoCount }} 项未完成</em>
      </div>

      <form class="todo-form" @submit.prevent="submitTodo">
        <input v-model="composer.text" maxlength="160" placeholder="今天要完成什么？" aria-label="待办内容" :disabled="mutationBlocked" />
        <select v-model="composer.priority" aria-label="待办优先级" :disabled="mutationBlocked">
          <option value="normal">普通</option><option value="high">重要</option><option value="low">稍后</option>
        </select>
        <input v-model="composer.dueDate" type="date" aria-label="待办日期" :disabled="mutationBlocked" />
        <button type="submit" :disabled="!composer.text.trim() || mutationBlocked">{{ addBusy ? '添加中…' : '添加' }}</button>
      </form>

      <div class="todo-filters">
        <button
          v-for="filter in todoFilters"
          :key="filter"
          type="button"
          :class="{ active: composer.filter === filter }"
          :aria-pressed="composer.filter === filter"
          @click="composer.filter = filter"
        >{{ filter === 'open' ? '未完成' : filter === 'today' ? '今天' : '全部' }}</button>
        <button
          ref="clearTrigger"
          type="button"
          class="clear"
          :disabled="completedCount === 0 || mutationBlocked || composer.pendingDeletes.length > 0"
          :aria-expanded="clearConfirmationOpen"
          aria-controls="clear-completed-confirmation"
          :title="composer.pendingDeletes.length > 0 ? '请先撤销删除或等待删除完成' : undefined"
          @click="openClearConfirmation"
        >清理已完成（{{ completedCount }}）</button>
      </div>

      <div
        v-if="clearConfirmationOpen"
        id="clear-completed-confirmation"
        class="clear-confirmation"
        role="alertdialog"
        aria-modal="false"
        aria-labelledby="clear-completed-title"
        aria-describedby="clear-completed-description"
        @keydown.esc.prevent="cancelClearConfirmation"
      >
        <div>
          <strong id="clear-completed-title">确认清理 {{ completedCount }} 项待办？</strong>
          <small id="clear-completed-description">清理后无法撤销。</small>
        </div>
        <button ref="clearConfirmButton" type="button" class="danger-action" :disabled="clearBusy" @click="clearCompletedTodos">{{ clearBusy ? '清理中…' : '确认清理' }}</button>
        <button type="button" :disabled="clearBusy" @click="cancelClearConfirmation">取消</button>
      </div>

      <p
        v-if="operationFeedback"
        class="todo-feedback"
        :class="operationFeedback.kind"
        :role="operationFeedback.kind === 'error' ? 'alert' : 'status'"
      >{{ operationFeedback.message }}</p>
      <p
        v-if="composer.deleteFeedback"
        class="todo-feedback dismissible"
        :class="composer.deleteFeedback.kind"
        :role="composer.deleteFeedback.kind === 'error' ? 'alert' : 'status'"
      >
        <span>{{ composer.deleteFeedback.message }}</span>
        <button type="button" aria-label="关闭删除提示" @click="composer.clearDeleteFeedback">关闭</button>
      </p>

      <div v-if="composer.pendingDeletes.length" class="undo-stack" aria-label="待办删除操作">
        <div v-for="pending in composer.pendingDeletes" :key="pending.todo.id" class="undo-delete" role="status">
          <span v-if="pending.status === 'undoable'">已暂时移除“{{ pending.todo.text }}”</span>
          <span v-else>正在删除“{{ pending.todo.text }}”…</span>
          <button v-if="pending.status === 'undoable'" type="button" @click="composer.undoDelete(pending.todo.id)">撤销（5 秒）</button>
        </div>
      </div>

      <div class="todo-list">
        <div
          v-for="todo in filteredTodos"
          :key="todo.id"
          class="todo-row"
          :class="{ done: todo.done, high: todo.priority === 'high' }"
          :aria-busy="editBusyId === todo.id || toggleBusyIds.has(todo.id)"
        >
          <button
            type="button"
            class="todo-check"
            :aria-label="todo.done ? `标记 ${todo.text} 为未完成` : `标记 ${todo.text} 为已完成`"
            :disabled="mutationBlocked || editingId === todo.id"
            @click="toggleTodo(todo)"
          >{{ toggleBusyIds.has(todo.id) ? '…' : todo.done ? '✓' : '' }}</button>

          <form v-if="editingId === todo.id" class="todo-inline-edit" @submit.prevent="saveEdit(todo)">
            <div>
              <input
                :ref="setEditInput"
                v-model="editText"
                maxlength="160"
                :aria-label="`编辑待办：${todo.text}`"
                :aria-invalid="Boolean(editValidationError)"
                :aria-describedby="editValidationError ? `todo-edit-error-${todo.id}` : undefined"
                :disabled="editBusyId === todo.id"
                @keydown="handleEditKeydown($event, todo)"
              />
              <small v-if="editValidationError" :id="`todo-edit-error-${todo.id}`" class="edit-error">{{ editValidationError }}</small>
            </div>
            <button type="submit" :disabled="editBusyId === todo.id">{{ editBusyId === todo.id ? '保存中…' : '保存' }}</button>
            <button type="button" :disabled="editBusyId === todo.id" @click="cancelEdit(todo)">取消</button>
          </form>

          <template v-else>
            <div><strong>{{ todo.text }}</strong><small>{{ todo.priority === 'high' ? '重要' : todo.priority === 'low' ? '稍后' : '普通' }} · {{ todo.dueDate ?? '无截止日期' }}</small></div>
            <button :ref="(element) => setEditButtonRef(todo.id, element)" type="button" :disabled="mutationBlocked" @click="startEdit(todo)">编辑</button>
            <button type="button" :disabled="mutationBlocked" @click="queueTodoDelete(todo)">删除</button>
          </template>
        </div>
        <div v-if="!filteredTodos.length" class="large-empty">当前筛选下没有待办</div>
      </div>
    </article>

    <article class="inner-card goal-list">
      <div class="inner-heading"><div><span>目标进度</span><h3>长期目标</h3></div><em>{{ snapshot.goals.length }}</em></div>
      <div v-for="goal in snapshot.goals" :key="goal.id" class="goal-row">
        <strong>{{ goal.objective }}</strong><span>{{ goal.status || '未知状态' }} · {{ compactNumber(goal.tokensUsed) }} tokens</span>
        <i v-if="goal.tokenBudget"><b :style="{ width: `${Math.min(100, goal.tokensUsed / goal.tokenBudget * 100)}%` }" /></i>
      </div>
      <div v-if="!snapshot.goals.length" class="large-empty">暂无 Goals 数据</div>
    </article>
  </div>
</template>

<style scoped>
button:disabled,
input:disabled,
select:disabled {
  cursor: not-allowed;
  opacity: .48;
}

.clear-confirmation,
.undo-delete,
.todo-feedback {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
  padding: 8px 10px;
  border: 1px solid var(--stroke-subtle);
  border-radius: 9px;
  color: var(--text-secondary);
  background: var(--surface-subtle);
  font-size: 11px;
}

.clear-confirmation > div,
.undo-delete span,
.todo-feedback span {
  min-width: 0;
  flex: 1;
}

.clear-confirmation strong,
.clear-confirmation small {
  display: block;
}

.clear-confirmation small {
  margin-top: 2px;
  color: var(--text-tertiary);
}

.clear-confirmation button,
.undo-delete button,
.todo-feedback button,
.todo-inline-edit button {
  padding: 5px 8px;
  border: 1px solid var(--stroke);
  border-radius: 7px;
  color: var(--text-secondary);
  background: transparent;
  cursor: pointer;
  font-size: 11px;
}

.clear-confirmation .danger-action {
  color: #ffc0c5;
  border-color: rgba(240, 117, 127, .35);
  background: rgba(240, 117, 127, .1);
}

.todo-feedback {
  justify-content: space-between;
}

.todo-feedback.success {
  color: #9fe1c3;
  border-color: rgba(85, 215, 161, .22);
  background: rgba(85, 215, 161, .07);
}

.todo-feedback.error {
  color: #ffc0c5;
  border-color: rgba(240, 117, 127, .25);
  background: rgba(240, 117, 127, .08);
}

.undo-stack {
  display: grid;
  gap: 5px;
}

.undo-delete button {
  margin-left: auto;
  color: #b9d8ff;
  border-color: rgba(101, 167, 255, .3);
  background: rgba(101, 167, 255, .09);
}

.todo-inline-edit {
  display: grid;
  grid-column: 2 / -1;
  grid-template-columns: minmax(0, 1fr) auto auto;
  gap: 6px;
  align-items: start;
}

.todo-inline-edit input {
  width: 100%;
  min-width: 0;
  padding: 6px 8px;
  border: 1px solid rgba(101, 167, 255, .45);
  border-radius: 7px;
  color: var(--text-primary);
  background: rgba(6, 10, 19, .45);
  font: inherit;
  font-size: 12px;
}

.edit-error {
  display: block;
  margin-top: 4px;
  color: #ff9ca4;
  font-size: 11px;
}

:global(.app-shell.light) .todo-inline-edit input {
  background: rgba(255, 255, 255, .68);
}
</style>
