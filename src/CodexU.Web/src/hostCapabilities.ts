export const HOST_CAPABILITY = Object.freeze({
  nativeDialogs: 'nativeDialogs',
  nativeNotifications: 'nativeNotifications',
  statusStripControl: 'statusStripControl',
  desktopMode: 'desktopMode',
  tray: 'tray',
  alwaysOnTop: 'alwaysOnTop',
  globalHotKey: 'globalHotKey',
  compactMode: 'compactMode',
  startupRegistration: 'startupRegistration',
} as const)

export type HostCapabilityName = typeof HOST_CAPABILITY[keyof typeof HOST_CAPABILITY]

// The browser demo emulates the legacy Windows host so every desktop setting can
// still be exercised by visual and interaction tests. Native hosts advertise only
// the capabilities they actually implement in app.initialize.
export const DEMO_HOST_CAPABILITIES: readonly string[] = Object.freeze([
  'usage',
  'runtime',
  'claudeCode',
  'combinedRuntime',
  'localOnly',
  'updates',
  'localData',
  'diagnostics',
  'rateCatalog',
  'todos',
  HOST_CAPABILITY.nativeDialogs,
  HOST_CAPABILITY.nativeNotifications,
  HOST_CAPABILITY.statusStripControl,
  HOST_CAPABILITY.desktopMode,
  HOST_CAPABILITY.tray,
  HOST_CAPABILITY.alwaysOnTop,
  HOST_CAPABILITY.globalHotKey,
  HOST_CAPABILITY.compactMode,
  HOST_CAPABILITY.startupRegistration,
])
