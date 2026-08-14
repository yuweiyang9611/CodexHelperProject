import { defineStore } from 'pinia'
import { computed, onScopeDispose, ref } from 'vue'
import type { TodoItem, TodoMutation } from '../types'

export const todoFilters = ['open', 'today', 'all'] as const
export const TODO_DELETE_UNDO_MS = 5_000

export type TodoFilter = (typeof todoFilters)[number]

export type TodoDeleteFeedback = {
  kind: 'success' | 'error'
  message: string
}

export type PendingTodoDelete = {
  todo: TodoItem
  deadline: number
  status: 'undoable' | 'committing'
}

type CommitTodoDelete = () => Promise<string | null>

/**
 * The calendar day as the user sees it. `toISOString()` would shift the key by a
 * day for anyone east or west of UTC, so the parts are read off the local clock.
 */
export function localDateKey(date = new Date()) {
  const year = date.getFullYear()
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

/**
 * Half-finished input and reversible todo actions that must survive tab teardown.
 * The todo panel is rendered with `v-if`, while this Pinia store remains alive.
 */
export const useTodoComposerStore = defineStore('todoComposer', () => {
  const text = ref('')
  const priority = ref<TodoMutation['priority']>('normal')
  const dueDate = ref(localDateKey())
  const filter = ref<TodoFilter>('open')
  const pendingDeletes = ref<PendingTodoDelete[]>([])
  const deleteFeedback = ref<TodoDeleteFeedback | null>(null)
  const deleteTimers = new Map<string, ReturnType<typeof setTimeout>>()
  const deleteCommitters = new Map<string, CommitTodoDelete>()
  let deleteCommitChain = Promise.resolve()

  const hasCommittingDeletes = computed(() =>
    pendingDeletes.value.some((pending) => pending.status === 'committing'))

  /** Whether a todo belongs in the list under the selected filter. */
  function matches(todo: TodoItem) {
    if (filter.value === 'all') return true
    if (filter.value === 'today') return todo.dueDate === localDateKey()
    return !todo.done
  }

  /** Clears the text after a successful add; priority and date remain sticky. */
  function clearText() {
    text.value = ''
  }

  function isPendingDelete(id: string) {
    return pendingDeletes.value.some((pending) => pending.todo.id === id)
  }

  /**
   * Optimistically hides a todo while preserving a five-second undo window.
   * Host commits are chained so rapid deletes are applied in request order.
   */
  function queueDelete(todo: TodoItem, commit: CommitTodoDelete) {
    if (isPendingDelete(todo.id)) return false

    const deadline = Date.now() + TODO_DELETE_UNDO_MS
    pendingDeletes.value.push({ todo: { ...todo }, deadline, status: 'undoable' })
    deleteCommitters.set(todo.id, commit)
    deleteFeedback.value = null
    deleteTimers.set(todo.id, setTimeout(() => beginDeleteCommit(todo.id), TODO_DELETE_UNDO_MS))
    return true
  }

  function undoDelete(id: string) {
    const pending = pendingDeletes.value.find((item) => item.todo.id === id)
    if (!pending || pending.status !== 'undoable') return false

    clearDeleteTimer(id)
    deleteCommitters.delete(id)
    pendingDeletes.value = pendingDeletes.value.filter((item) => item.todo.id !== id)
    deleteFeedback.value = {
      kind: 'success',
      message: `\u5df2\u64a4\u9500\u5220\u9664\u201c${pending.todo.text}\u201d`,
    }
    return true
  }

  function beginDeleteCommit(id: string) {
    const pending = pendingDeletes.value.find((item) => item.todo.id === id)
    if (!pending || pending.status !== 'undoable') return

    clearDeleteTimer(id)
    pending.status = 'committing'
    deleteCommitChain = deleteCommitChain.then(() => finishDeleteCommit(id))
  }

  async function finishDeleteCommit(id: string) {
    const pending = pendingDeletes.value.find((item) => item.todo.id === id)
    const commit = deleteCommitters.get(id)
    if (!pending || !commit) return

    let failure: string | null = null
    try {
      failure = await commit()
    } catch (reason) {
      failure = reason instanceof Error ? reason.message : String(reason)
    }

    deleteCommitters.delete(id)
    pendingDeletes.value = pendingDeletes.value.filter((item) => item.todo.id !== id)
    deleteFeedback.value = failure
      ? {
          kind: 'error',
          message: `\u5220\u9664\u201c${pending.todo.text}\u201d\u5931\u8d25\uff0c\u5f85\u529e\u5df2\u6062\u590d\uff1a${failure}`,
        }
      : { kind: 'success', message: `\u5df2\u5220\u9664\u201c${pending.todo.text}\u201d` }
  }

  function clearDeleteFeedback() {
    deleteFeedback.value = null
  }

  function clearDeleteTimer(id: string) {
    const timer = deleteTimers.get(id)
    if (timer !== undefined) clearTimeout(timer)
    deleteTimers.delete(id)
  }

  // Disposing the application cancels unfinished countdowns instead of turning
  // shutdown into an unexpected write. Component teardown does not dispose Pinia.
  onScopeDispose(() => {
    deleteTimers.forEach((timer) => clearTimeout(timer))
    deleteTimers.clear()
    deleteCommitters.clear()
  })

  return {
    text, priority, dueDate, filter,
    pendingDeletes, deleteFeedback, hasCommittingDeletes,
    matches, clearText, isPendingDelete, queueDelete, undoDelete, clearDeleteFeedback,
  }
})
