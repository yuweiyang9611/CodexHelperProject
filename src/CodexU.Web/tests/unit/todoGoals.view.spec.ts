import { createPinia, setActivePinia, type Pinia } from 'pinia'
import { createApp, nextTick, type App } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import TodoGoalsView from '../../src/views/TodoGoalsView.vue'
import { useDashboardStore } from '../../src/stores/dashboard'
import { useTodoComposerStore } from '../../src/stores/todoComposer'
import { snapshot, todoItem } from './fixtures'

let pinia: Pinia
let mountedApps: App[]

beforeEach(() => {
  pinia = createPinia()
  setActivePinia(pinia)
  mountedApps = []
})

afterEach(() => {
  mountedApps.forEach((app) => app.unmount())
  document.body.replaceChildren()
  useTodoComposerStore(pinia).$dispose()
})

function mountView() {
  const container = document.createElement('div')
  document.body.append(container)
  const app = createApp(TodoGoalsView, { snapshot: snapshot() })
  app.use(pinia)
  app.mount(container)
  mountedApps.push(app)
  return { app, container }
}

function buttonWithText(container: ParentNode, text: string) {
  const button = [...container.querySelectorAll('button')]
    .find((candidate) => candidate.textContent?.trim() === text)
  if (!(button instanceof HTMLButtonElement)) throw new Error(`Missing button: ${text}`)
  return button
}

async function flushUi() {
  await Promise.resolve()
  await nextTick()
  await Promise.resolve()
}

describe('TodoGoalsView interactions', () => {
  it('keeps editing inline, restores focus on Escape, and reports a failed save', async () => {
    const store = useDashboardStore(pinia)
    store.todos = [todoItem({ id: 'editable', text: 'original' })]
    const update = vi.spyOn(store, 'updateTodo').mockImplementation(async () => {
      store.error = 'write rejected'
      return false
    })
    const { container } = mountView()

    buttonWithText(container, '编辑').click()
    await nextTick()
    let input = container.querySelector('[aria-label="编辑待办：original"]')
    expect(input).toBeInstanceOf(HTMLInputElement)
    expect(document.activeElement).toBe(input)

    input!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    await nextTick()
    expect(container.querySelector('[aria-label="编辑待办：original"]')).toBeNull()
    expect(document.activeElement).toBe(buttonWithText(container, '编辑'))

    buttonWithText(container, '编辑').click()
    await nextTick()
    input = container.querySelector('[aria-label="编辑待办：original"]')
    ;(input as HTMLInputElement).value = ''
    input!.dispatchEvent(new Event('input', { bubbles: true }))
    input!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    await flushUi()
    expect(container.textContent).toContain('待办内容不能为空')
    expect(document.activeElement).toBe(input)
    expect(update).not.toHaveBeenCalled()

    ;(input as HTMLInputElement).value = 'changed'
    input!.dispatchEvent(new Event('input', { bubbles: true }))
    input!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
    await flushUi()

    expect(update).toHaveBeenCalledWith(expect.objectContaining({ id: 'editable', text: 'changed' }))
    expect(container.querySelector('[role="alert"]')?.textContent).toContain('保存失败：write rejected')
    expect(container.querySelector('[aria-label="编辑待办：original"]')).toBe(input)
    expect(document.activeElement).toBe(input)
  })

  it('shows the completed count in an accessible confirmation and supports Escape', async () => {
    const store = useDashboardStore(pinia)
    store.todos = [
      todoItem({ id: 'done-1', text: 'done one', done: true }),
      todoItem({ id: 'done-2', text: 'done two', done: true }),
      todoItem({ id: 'open', text: 'open', done: false }),
    ]
    const clear = vi.spyOn(store, 'clearCompletedTodos').mockResolvedValue(true)
    const { container } = mountView()

    const trigger = buttonWithText(container, '清理已完成（2）')
    expect(trigger.disabled).toBe(false)
    trigger.click()
    await nextTick()

    const dialog = container.querySelector('[role="alertdialog"]')
    expect(dialog?.textContent).toContain('确认清理 2 项待办？')
    expect(document.activeElement).toBe(buttonWithText(container, '确认清理'))

    dialog!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
    await nextTick()
    expect(container.querySelector('[role="alertdialog"]')).toBeNull()
    expect(document.activeElement).toBe(trigger)

    trigger.click()
    await nextTick()
    buttonWithText(container, '确认清理').click()
    await flushUi()
    expect(clear).toHaveBeenCalledOnce()
    expect(container.textContent).toContain('已清理 2 项已完成待办')
  })

  it('keeps an undoable delete across panel teardown and restores the row on undo', async () => {
    const store = useDashboardStore(pinia)
    store.todos = [todoItem({ id: 'undo-row', text: 'restore this' })]
    const remove = vi.spyOn(store, 'deleteTodo').mockResolvedValue(true)
    const first = mountView()

    buttonWithText(first.container, '删除').click()
    await nextTick()
    expect(first.container.textContent).not.toContain('无截止日期')
    expect(first.container.textContent).toContain('已暂时移除“restore this”')

    first.app.unmount()
    mountedApps = mountedApps.filter((app) => app !== first.app)
    first.container.remove()
    const second = mountView()
    expect(second.container.textContent).toContain('已暂时移除“restore this”')

    buttonWithText(second.container, '撤销（5 秒）').click()
    await nextTick()
    expect(second.container.textContent).toContain('restore this')
    expect(second.container.textContent).toContain('无截止日期')
    expect(remove).not.toHaveBeenCalled()
  })

  it('disables completed cleanup when there is nothing to clear', () => {
    const store = useDashboardStore(pinia)
    store.todos = [todoItem({ done: false })]
    const { container } = mountView()

    expect(buttonWithText(container, '清理已完成（0）').disabled).toBe(true)
  })
})
