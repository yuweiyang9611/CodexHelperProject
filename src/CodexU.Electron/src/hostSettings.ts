import { isRecord } from './protocol';

export const SUPPORTED_THEMES = ['light', 'dark', 'system'] as const;
export const SUPPORTED_GLOBAL_HOT_KEYS = [
  'Ctrl+U',
  'Ctrl+Shift+U',
  'Ctrl+Alt+U',
  'Ctrl+Shift+C',
  'Ctrl+Alt+C',
] as const;

export type HostTheme = typeof SUPPORTED_THEMES[number];
export type HostGlobalHotKey = typeof SUPPORTED_GLOBAL_HOT_KEYS[number];

export interface HostSettings {
  closeToTray: boolean;
  compactMode: boolean;
  theme: HostTheme;
  startAtLogin: boolean;
  globalHotKey: HostGlobalHotKey;
}

export interface WindowLayout {
  width: number;
  height: number;
  minimumWidth: number;
  minimumHeight: number;
}

export type NativeActivationEvent = 'second-instance' | 'activate';
export type NativeActivationAction = 'show' | 'ignore' | 'fail';

export const DEFAULT_HOST_SETTINGS: Readonly<HostSettings> = Object.freeze({
  closeToTray: true,
  compactMode: false,
  theme: 'dark',
  startAtLogin: false,
  globalHotKey: 'Ctrl+U',
});

const EXPANDED_LAYOUT: Readonly<WindowLayout> = Object.freeze({
  width: 1180,
  height: 780,
  minimumWidth: 900,
  minimumHeight: 620,
});

const COMPACT_LAYOUT: Readonly<WindowLayout> = Object.freeze({
  width: 940,
  height: 420,
  minimumWidth: 640,
  minimumHeight: 380,
});

export function parseHostSettings(value: unknown): HostSettings {
  if (!isRecord(value)) throw new TypeError('settings.get must return an object.');
  if (typeof value.closeToTray !== 'boolean') invalidField('closeToTray');
  if (typeof value.compactMode !== 'boolean') invalidField('compactMode');
  if (!isOneOf(value.theme, SUPPORTED_THEMES)) invalidField('theme');
  if (typeof value.startAtLogin !== 'boolean') invalidField('startAtLogin');
  if (!isOneOf(value.globalHotKey, SUPPORTED_GLOBAL_HOT_KEYS)) invalidField('globalHotKey');

  return {
    closeToTray: value.closeToTray,
    compactMode: value.compactMode,
    theme: value.theme,
    startAtLogin: value.startAtLogin,
    globalHotKey: value.globalHotKey,
  };
}

export function windowLayout(compactMode: boolean): Readonly<WindowLayout> {
  return compactMode ? COMPACT_LAYOUT : EXPANDED_LAYOUT;
}

export function shouldHideWindowOnClose(
  settings: HostSettings,
  shutdownStarted: boolean,
  trayAvailable: boolean,
): boolean {
  return settings.closeToTray && !shutdownStarted && trayAvailable;
}

export function shouldUpdateStartupRegistration(
  platform: NodeJS.Platform,
  isPackaged: boolean,
  smokeTest: boolean,
  currentValue: boolean,
  desiredValue: boolean,
): boolean {
  return platform === 'win32'
    && isPackaged
    && !smokeTest
    && currentValue !== desiredValue;
}

export function shouldSuppressHostEventInSmoke(smokeTest: boolean, method: string): boolean {
  return smokeTest && (
    method === 'settings.changed'
    || (method.startsWith('host.') && method !== 'host.webReady')
  );
}

export function nativeActivationAction(
  smokeTest: boolean,
  event: NativeActivationEvent,
): NativeActivationAction {
  if (!smokeTest) return 'show';
  return event === 'second-instance' ? 'fail' : 'ignore';
}

function isOneOf<T extends string>(value: unknown, allowed: readonly T[]): value is T {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value);
}

function invalidField(field: keyof HostSettings): never {
  throw new TypeError(`settings.${field} is missing or unsupported.`);
}
