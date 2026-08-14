import { computed, onBeforeUnmount, onMounted, ref, watchEffect } from 'vue'
import { useDashboardStore } from '../stores/dashboard'

/**
 * Resolves the effective theme from the saved setting plus, for `system`, the OS
 * preference, and mirrors it onto `<html data-theme>` for the global stylesheet.
 */
export function useThemePreference() {
  const store = useDashboardStore()
  const colorSchemeQuery = window.matchMedia('(prefers-color-scheme: light)')
  const systemPrefersLight = ref(colorSchemeQuery.matches)
  const isLightTheme = computed(() => store.settings?.theme === 'light'
    || (store.settings?.theme === 'system' && systemPrefersLight.value))

  function updateColorScheme(event: MediaQueryListEvent) {
    systemPrefersLight.value = event.matches
  }

  onMounted(() => {
    colorSchemeQuery.addEventListener('change', updateColorScheme)
  })
  onBeforeUnmount(() => {
    colorSchemeQuery.removeEventListener('change', updateColorScheme)
  })
  watchEffect(() => { document.documentElement.dataset.theme = isLightTheme.value ? 'light' : 'dark' })

  return { isLightTheme }
}
