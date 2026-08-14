<script setup lang="ts">
type IconName =
  | 'activity'
  | 'alert'
  | 'check-square'
  | 'chevron'
  | 'circle'
  | 'clock'
  | 'compare'
  | 'edit'
  | 'folder'
  | 'globe'
  | 'layout'
  | 'list'
  | 'refresh'
  | 'settings'
  | 'sparkle'
  | 'sun'
  | 'terminal'
  | 'tool'

withDefaults(defineProps<{ name: IconName, size?: number }>(), { size: 16 })
</script>

<template>
  <svg
    class="ui-icon"
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <template v-if="name === 'refresh'">
      <path d="M20 11a8 8 0 1 0-2.34 5.66" />
      <path d="M20 4v7h-7" />
    </template>
    <template v-else-if="name === 'layout'">
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="M3 10h18M10 10v10" />
    </template>
    <template v-else-if="name === 'settings'">
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06-2.83 2.83-.06-.06a1.7 1.7 0 0 0-1.87-.34 1.7 1.7 0 0 0-1.04 1.55V21h-4v-.09A1.7 1.7 0 0 0 8.96 19.36a1.7 1.7 0 0 0-1.87.34l-.06.06-2.83-2.83.06-.06A1.7 1.7 0 0 0 4.6 15a1.7 1.7 0 0 0-1.55-1.04H3v-4h.05A1.7 1.7 0 0 0 4.6 9a1.7 1.7 0 0 0-.34-1.87L4.2 7.07l2.83-2.83.06.06A1.7 1.7 0 0 0 8.96 4.64 1.7 1.7 0 0 0 10 3.09V3h4v.09a1.7 1.7 0 0 0 1.04 1.55 1.7 1.7 0 0 0 1.87-.34l.06-.06 2.83 2.83-.06.06A1.7 1.7 0 0 0 19.4 9a1.7 1.7 0 0 0 1.55 1.04H21v4h-.05A1.7 1.7 0 0 0 19.4 15Z" />
    </template>
    <template v-else-if="name === 'check-square'">
      <rect x="3" y="3" width="18" height="18" rx="3" />
      <path d="m7 12 3 3 7-7" />
    </template>
    <template v-else-if="name === 'list'">
      <path d="M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01" />
    </template>
    <template v-else-if="name === 'activity'">
      <path d="M3 12h4l2.5-6 5 12 2.5-6h4" />
    </template>
    <template v-else-if="name === 'circle'">
      <circle cx="12" cy="12" r="7" />
    </template>
    <template v-else-if="name === 'clock'">
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </template>
    <template v-else-if="name === 'folder'">
      <path d="M3 6.5A2.5 2.5 0 0 1 5.5 4H10l2 2h6.5A2.5 2.5 0 0 1 21 8.5v8A2.5 2.5 0 0 1 18.5 19h-13A2.5 2.5 0 0 1 3 16.5Z" />
    </template>
    <template v-else-if="name === 'sparkle'">
      <path d="m12 3 1.4 4.1L17.5 8.5l-4.1 1.4L12 14l-1.4-4.1-4.1-1.4 4.1-1.4ZM18.5 14l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8Z" />
    </template>
    <template v-else-if="name === 'compare'">
      <path d="M7 7h13M17 4l3 3-3 3M17 17H4M7 14l-3 3 3 3" />
    </template>
    <template v-else-if="name === 'sun'">
      <circle cx="12" cy="12" r="3.5" />
      <path d="M12 2v2M12 20v2M4.93 4.93l1.42 1.42M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.42-1.41M17.66 6.34l1.41-1.41" />
    </template>
    <template v-else-if="name === 'terminal'">
      <rect x="3" y="5" width="18" height="14" rx="2" />
      <path d="m7 9 3 3-3 3M13 15h4" />
    </template>
    <template v-else-if="name === 'edit'">
      <path d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L8 18l-4 1 1-4Z" />
    </template>
    <template v-else-if="name === 'globe'">
      <circle cx="12" cy="12" r="9" />
      <path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18" />
    </template>
    <template v-else-if="name === 'tool'">
      <path d="M14.7 6.3a4 4 0 0 0-5-5L12 3.6 8.6 7 6.3 4.7a4 4 0 0 0 5 5L19 17.4a2.1 2.1 0 0 1-3 3l-7.7-7.7a4 4 0 0 0-5-5L6 10.4 9.4 7Z" />
    </template>
    <template v-else-if="name === 'alert'">
      <path d="M10.3 4.1 2.8 17a2 2 0 0 0 1.73 3h14.94a2 2 0 0 0 1.73-3L13.7 4.1a2 2 0 0 0-3.4 0Z" />
      <path d="M12 9v4M12 17h.01" />
    </template>
    <template v-else>
      <path d="m8 10 4 4 4-4" />
    </template>
  </svg>
</template>

<style scoped>
.ui-icon { display: block; flex: none; }
</style>
