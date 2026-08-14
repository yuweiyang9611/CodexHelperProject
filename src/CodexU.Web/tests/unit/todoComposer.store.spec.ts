import { createPinia, setActivePinia } from 'pinia'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { localDateKey, TODO_DELETE_UNDO_MS, todoFilters, useTodoComposerStore } from '../../src/stores/todoComposer'
import { todoItem } from './fixtures'

beforeEach(() => {
  setActivePinia(createPinia())
})

afterEach(() => {
  useTodoComposerStore().$dispose()
  vi.useRealTimers()
})

describe('localDateKey', () => {
  it('reads the calendar day off the local clock rather than UTC', () => {
    // 00:30 in Asia/Tokyo (the pinned test zone) is still the previous day in UTC.
    const justAfterMidnight = new Date('2026-07-15T00:30:00+09:00')

    expect(localDateKey(justAfterMidnight)).toBe('2026-07-15')
    expect(justAfterMidnight.toISOString().slice(0, 10)).toBe('2026-07-14')
  })

  it('zero-pads month and day', () => {
    expect(localDateKey(new Date('2026-01-02T12:00:00+09:00'))).toBe('2026-01-02')
  })
})

describe('composer defaults', () => {
  it('starts empty, at normal priority, dated today and filtered to open items', () => {
    const composer = useTodoComposerStore()

    expect(composer.text).toBe('')
    expect(composer.priority).toBe('normal')
    expect(composer.dueDate).toBe(localDateKey())
    expect(composer.filter).toBe('open')
  })

  it('offers exactly the three filters the panel renders', () => {
    expect(todoFilters).toEqual(['open', 'today', 'all'])
  })
})

describe('in-progress input', () => {
  it('survives a panel teardown, which is the whole reason it lives in a store', () => {
    const typing = useTodoComposerStore()
    typing.text = '半句话'
    typing.priority = 'high'
    typing.dueDate = '2026-08-01'
    typing.filter = 'all'

    // The tab panel is rendered with v-if, so the next visit sets up from scratch.
    const remounted = useTodoComposerStore()

    expect(remounted.text).toBe('半句话')
    expect(remounted.priority).toBe('high')
    expect(remounted.dueDate).toBe('2026-08-01')
    expect(remounted.filter).toBe('all')
  })

  it('clears only the text after a successful add', () => {
    const composer = useTodoComposerStore()
    composer.text = '已提交'
    composer.priority = 'high'
    composer.dueDate = '2026-08-01'

    composer.clearText()

    // Priority and date are deliberately sticky: consecutive todos usually share them.
    expect(composer.text).toBe('')
    expect(composer.priority).toBe('high')
    expect(composer.dueDate).toBe('2026-08-01')
  })
})

describe('matches', () => {
  const open = todoItem({ id: 'open', done: false, dueDate: '2000-01-01' })
  const done = todoItem({ id: 'done', done: true, dueDate: '2000-01-01' })
  const dueToday = todoItem({ id: 'today', done: false, dueDate: localDateKey() })
  const doneToday = todoItem({ id: 'done-today', done: true, dueDate: localDateKey() })
  const undated = todoItem({ id: 'undated', done: false, dueDate: undefined })

  it('keeps only unfinished items under the open filter', () => {
    const composer = useTodoComposerStore()
    composer.filter = 'open'

    expect([open, done, dueToday, undated].filter((todo) => composer.matches(todo)).map((todo) => todo.id))
      .toEqual(['open', 'today', 'undated'])
  })

  it('keeps every item dated today under the today filter, finished or not', () => {
    const composer = useTodoComposerStore()
    composer.filter = 'today'

    expect([open, done, dueToday, doneToday, undated].filter((todo) => composer.matches(todo)).map((todo) => todo.id))
      .toEqual(['today', 'done-today'])
  })

  it('keeps everything under the all filter', () => {
    const composer = useTodoComposerStore()
    composer.filter = 'all'

    expect([open, done, dueToday, undated].every((todo) => composer.matches(todo))).toBe(true)
  })
})

describe('reversible delete queue', () => {
  it('hides immediately and never commits when undone inside five seconds', async () => {
    vi.useFakeTimers()
    const composer = useTodoComposerStore()
    const commit = vi.fn<() => Promise<string | null>>().mockResolvedValue(null)
    const todo = todoItem({ id: 'undo-me', text: 'undo me' })

    expect(composer.queueDelete(todo, commit)).toBe(true)
    expect(composer.isPendingDelete(todo.id)).toBe(true)
    expect(composer.pendingDeletes[0]).toMatchObject({ todo, status: 'undoable' })

    vi.advanceTimersByTime(TODO_DELETE_UNDO_MS - 1)
    await Promise.resolve()
    expect(commit).not.toHaveBeenCalled()

    expect(composer.undoDelete(todo.id)).toBe(true)
    vi.advanceTimersByTime(1)
    await Promise.resolve()

    expect(commit).not.toHaveBeenCalled()
    expect(composer.isPendingDelete(todo.id)).toBe(false)
    expect(composer.deleteFeedback?.message).toBe('\u5df2\u64a4\u9500\u5220\u9664\u201cundo me\u201d')
  })

  it('keeps rapid delete commits ordered without losing either action', async () => {
    vi.useFakeTimers()
    const composer = useTodoComposerStore()
    let finishFirst!: (failure: string | null) => void
    let markSecondStarted!: () => void
    const secondStarted = new Promise<void>((resolve) => { markSecondStarted = resolve })
    const firstCommit = vi.fn(() => new Promise<string | null>((resolve) => { finishFirst = resolve }))
    const secondCommit = vi.fn(async () => {
      markSecondStarted()
      return null
    })

    composer.queueDelete(todoItem({ id: 'first', text: 'first' }), firstCommit)
    composer.queueDelete(todoItem({ id: 'second', text: 'second' }), secondCommit)
    vi.advanceTimersByTime(TODO_DELETE_UNDO_MS)
    await Promise.resolve()

    expect(firstCommit).toHaveBeenCalledOnce()
    expect(secondCommit).not.toHaveBeenCalled()
    expect(composer.pendingDeletes.every((pending) => pending.status === 'committing')).toBe(true)

    finishFirst(null)
    await secondStarted

    expect(secondCommit).toHaveBeenCalledOnce()
    await vi.waitFor(() => expect(composer.pendingDeletes).toHaveLength(0))
  })

  it('restores the hidden row and exposes the host reason when commit fails', async () => {
    vi.useFakeTimers()
    const composer = useTodoComposerStore()
    const todo = todoItem({ id: 'failed', text: 'keep me' })
    composer.queueDelete(todo, async () => '\u78c1\u76d8\u53ea\u8bfb')

    vi.advanceTimersByTime(TODO_DELETE_UNDO_MS)
    await Promise.resolve()
    await Promise.resolve()

    expect(composer.isPendingDelete(todo.id)).toBe(false)
    expect(composer.deleteFeedback).toEqual({
      kind: 'error',
      message: '\u5220\u9664\u201ckeep me\u201d\u5931\u8d25\uff0c\u5f85\u529e\u5df2\u6062\u590d\uff1a\u78c1\u76d8\u53ea\u8bfb',
    })
  })

  it('survives a panel remount but cancels unfinished timers when Pinia is disposed', async () => {
    vi.useFakeTimers()
    const firstMount = useTodoComposerStore()
    const commit = vi.fn<() => Promise<string | null>>().mockResolvedValue(null)
    firstMount.queueDelete(todoItem({ id: 'tab-switch' }), commit)

    const remounted = useTodoComposerStore()
    expect(remounted.isPendingDelete('tab-switch')).toBe(true)

    remounted.$dispose()
    vi.advanceTimersByTime(TODO_DELETE_UNDO_MS)
    await Promise.resolve()
    expect(commit).not.toHaveBeenCalled()
  })
})
