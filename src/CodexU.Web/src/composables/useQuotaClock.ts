import { onMounted, onUnmounted, ref, watch } from 'vue'

/** Update presentation at minute boundaries and at the reported instant of a reset. */
export function useQuotaClock(resetTimes: () => (string | null | undefined)[]) {
  const now = ref(Date.now())
  let timer: number | undefined
  function update() {
    window.clearTimeout(timer)
    if (document.hidden) return
    const current = Date.now()
    now.value = current
    let delay = 60_000 - current % 60_000
    for (const reset of resetTimes()) {
      const remaining = reset ? Date.parse(reset) - current : 0
      if (remaining > 0) delay = Math.min(delay, remaining)
    }
    timer = window.setTimeout(update, delay)
  }
  watch(resetTimes, update)
  onMounted(() => {
    update()
    document.addEventListener('visibilitychange', update)
    window.addEventListener('focus', update)
  })
  onUnmounted(() => {
    window.clearTimeout(timer)
    document.removeEventListener('visibilitychange', update)
    window.removeEventListener('focus', update)
  })
  return now
}
